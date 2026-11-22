# URL Health Monitor — Design Plan

A lightweight URL health monitor. It checks a list of URLs every 5 minutes, saves status and response time history, shows that history in a web UI, and sends email alerts when a URL goes down or comes back up.

**Stack:** Vue 3 (Vite) · ASP.NET Core (.NET 10 LTS) · EF Core · SQLite · MailKit (SMTP) · xUnit

> **Prerequisite:** the .NET SDK is not currently installed on this machine (`dotnet` isn't found). Install the **.NET 10 SDK (LTS)** before starting. Node v26 and npm 11 are already installed.

---

## 1. Architecture

```
┌──────────────┐   HTTP/JSON   ┌───────────────────────────────┐
│  Vue 3 SPA   │ ────────────▶ │ ASP.NET Core Web API          │
│ (Vite dev    │   /api/...    │  ├─ Endpoints                 │
│  server)     │               │  ├─ UrlCheckService (pure)    │
└──────────────┘               │  ├─ CheckScheduler            │
                               │  │   (BackgroundService, 5m)  │
                               │  ├─ AlertService              │──▶ SMTP ──▶ smtp4dev (local inbox UI)
                               │  └─ EF Core DbContext         │
                               └──────────────┬────────────────┘
                                              ▼
                                        SQLite file (monitor.db)
```

- **Scheduler:** a built-in `BackgroundService` with a `PeriodicTimer`. No Hangfire or Quartz, so there are no extra dependencies.
- **Checks:** the job runs every 5 minutes and checks all URLs in parallel, with a concurrency limit. Each check has a timeout, and every result is written as a row.
- **Alerts:** after each run, the system compares each URL's new state with its previous state. Only a **change** (UP→DOWN or DOWN→UP) sends an email to every saved recipient.
- **Local run:** one command, `npm start` from the repo root, launches the API, the Vite dev server and smtp4dev together. The Vite proxy sends `/api` to the backend, so there's no CORS setup.

---

## 2. Project File Layout

```
rosnet-url-monitor/
├─ README.md
├─ design.md
├─ dotnet-tools.json                 # local tool manifest: dotnet-ef 10.0.15, smtp4dev 3.15.0
├─ package.json                      # root scripts: `npm run setup`, `npm start` (API + Vite + smtp4dev)
├─ UrlMonitor.slnx                    # .NET 10's default (XML) solution format
├─ backend/
│  ├─ src/UrlMonitor.Api/
│  │  ├─ Program.cs                  # DI, EF, hosted service, endpoints, CORS
│  │  ├─ appsettings.json            # ConnectionString, CheckInterval, Timeout, Smtp, seed URLs
│  │  ├─ Data/
│  │  │  ├─ MonitorDbContext.cs
│  │  │  └─ Migrations/
│  │  ├─ Models/
│  │  │  ├─ MonitoredUrl.cs          # Id, Url, Name?, CreatedAt, IsActive, CurrentState, ConsecutiveFailures
│  │  │  ├─ UrlCheck.cs              # Id, MonitoredUrlId, CheckedAt, StatusCode?, ResponseTimeMs, IsUp, Error?
│  │  │  ├─ AlertRecipient.cs        # Id, Email, CreatedAt
│  │  │  └─ AlertEvent.cs            # Id, MonitoredUrlId, OccurredAt, Kind (Down/Recovered),
│  │  │                               #   Delivery (Sent/Failed/Suppressed/NoRecipients), Error?
│  │  ├─ Dtos/                       # UrlSummaryDto, CheckDto, CreateUrlRequest, StatsDto, RecipientDto
│  │  ├─ Endpoints/
│  │  │  ├─ UrlEndpoints.cs          # add / delete / list
│  │  │  ├─ CheckEndpoints.cs        # history / stats / manual "check now"
│  │  │  └─ AlertEndpoints.cs        # recipients CRUD, test email, alert history
│  │  ├─ Services/
│  │  │  ├─ IUrlChecker.cs / UrlChecker.cs         # one HTTP check → result (unit-testable)
│  │  │  ├─ ICheckRunner.cs / CheckRunner.cs       # checks all URLs, saves results, calls AlertService
│  │  │  ├─ IAlertService.cs / AlertService.cs     # state-change detection → AlertEvent → send
│  │  │  ├─ IEmailSender.cs / SmtpEmailSender.cs   # MailKit implementation
│  │  │  ├─ StatusRules.cs                         # up/down rule: 2xx, 401, 403 = up
│  │  │  └─ MonitorOptions.cs                      # "Monitor" config section
│  │  │  (time comes from .NET's built-in TimeProvider; tests use FakeTimeProvider)
│  │  └─ Background/
│  │     └─ CheckSchedulerService.cs               # PeriodicTimer loop → ICheckRunner
│  └─ tests/
│     ├─ UrlMonitor.UnitTests/       # xUnit: UrlChecker (fake HttpMessageHandler), validation,
│     │                              #        uptime math, AlertService state changes (fake IEmailSender)
│     └─ UrlMonitor.IntegrationTests/# WebApplicationFactory + temp SQLite:
│                                    #        add URL → run check → read history; DOWN → alert recorded
└─ frontend/
   ├─ package.json, vite.config.js   # proxy /api → http://localhost:5xxx
   └─ src/
      ├─ main.js, App.vue, router.js # nav: Dashboard | Alerts
      ├─ api/client.js               # fetch wrappers
      ├─ views/
      │  ├─ DashboardView.vue        # URL list, red/green chip, status code, response time, add/delete
      │  ├─ UrlDetailView.vue        # chart/list toggle, uptime/response-time toggle
      │  └─ AlertsView.vue           # add email, list of saved emails (delete), "send test", recent alerts
      └─ components/
         ├─ StatusChip.vue
         ├─ AddUrlForm.vue
         ├─ AddEmailForm.vue
         ├─ ToastNotifications.vue   # red/green pop-ups when polling sees a state change
         ├─ HistoryChart.vue         # Chart.js via vue-chartjs
         └─ HistoryTable.vue         # newest → oldest, paginated
```

### API surface

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/urls` | All URLs with their latest check (dashboard) |
| POST | `/api/urls` | Add a URL (validates it, rejects duplicates) |
| DELETE | `/api/urls/{id}` | Delete a URL and its history (cascade) |
| GET | `/api/urls/{id}/checks?from=&to=&page=&pageSize=` | History, newest first |
| GET | `/api/urls/{id}/stats?range=24h` | Uptime %, average and p95 response time, bucketed series for the chart |
| POST | `/api/urls/{id}/check` | Optional: run a check now, which helps demos and testing |
| GET | `/api/alerts/recipients` | List saved email addresses |
| POST | `/api/alerts/recipients` | Add an email (validates format, rejects duplicates case-insensitively) |
| DELETE | `/api/alerts/recipients/{id}` | Remove an email |
| POST | `/api/alerts/test` | Send a test email to all recipients (checks the SMTP setup) |
| GET | `/api/alerts/events?limit=50` | Recent alert history (what was sent and when, including failures) |

---

## 3. Alerts Design

### Is email easy to do locally?

**Yes.** Email works locally with no real mail account. The API sends over SMTP to a **local SMTP catcher**, which accepts every message and shows it in a web inbox:

- **smtp4dev** (recommended, since the .NET SDK is already required): pinned in the repo's tool manifest. Run it with `dotnet smtp4dev`. SMTP listens on `localhost:25` and the web inbox is at `http://localhost:5000`. Change its port if it clashes with the API.
- Alternatives: **Mailpit** (a single exe, or Docker).

Using a real provider later (Gmail, SendGrid, Office 365) only means changing the `Smtp` section in config. No code changes.

```json
"Smtp": {
  "Host": "localhost",
  "Port": 25,
  "UseSsl": false,
  "Username": "",
  "Password": "",
  "From": "url-monitor@localhost"
},
"Alerts": {
  "FailureThreshold": 2,
  "CooldownMinutes": 30,
  "SendRecoveryEmails": true
}
```

Because email works locally, email is the main alert channel. **In-browser toast pop-ups are added as well** (see question 32). They appear while the UI is open, when dashboard polling sees a URL change state.

### Alert logic

1. After each check, update the URL's `ConsecutiveFailures`: set it to 0 if the check is up, otherwise add 1.
2. **DOWN alert:** `CurrentState == Up` and `ConsecutiveFailures >= FailureThreshold` → set the state to `Down`, create an `AlertEvent(Down)` and send the email.
3. **RECOVERED alert:** `CurrentState == Down` and the check is up → set the state to `Up`, create an `AlertEvent(Recovered)` and send the email.
4. **Cooldown:** if an email for this URL was sent less than `CooldownMinutes` ago, the event is saved as `Suppressed` and no email is sent.
5. Otherwise nothing is sent. A URL that stays down gets **one** email, not one every 5 minutes.
6. Emails are sent **after** the check results are saved, and a failed send never fails the check run. The failure is saved in `AlertEvent.Error` and shown on the Alerts page.
7. One email goes to each recipient, or one email with the addresses in BCC, so recipients don't see each other's addresses.

**Email content:** the subject is `[DOWN] https://example.com` or `[RECOVERED] https://example.com`. The body has the URL, the time (UTC and local), the status code or error, the response time, and how long the URL was down (for recoveries).

### Alerts UI page (`/alerts`)

- A form to add an email, with format validation and an inline error for invalid or duplicate addresses.
- A list of saved emails with a delete button on each.
- A **"Send test email"** button.
- A **recent alerts** table: time, URL, Down/Recovered, and whether it was sent or failed.

---

## 4. Build Steps (in order)

1. **Install prerequisites:** the .NET 10 SDK and Node. The `dotnet-ef` (10.0.15) and smtp4dev (3.15.0) tools are pinned in the repo's local tool manifest (`dotnet-tools.json`). Run `dotnet tool restore` to install them. No global installs are needed. *(Done: SDK installed, git initialized, manifest created.)*
2. **Scaffold:** create the solution, the `webapi` project, two xUnit projects, and the Vite Vue app.
3. **Data layer:** write the models, the DbContext, an index on `(MonitoredUrlId, CheckedAt)`, a unique index on `AlertRecipient.Email`, cascade delete, and the initial migration. The schema is applied at startup with `Migrate()`.
4. **Checker:** build `UrlChecker` on `IHttpClientFactory`, timing each request with a `Stopwatch`. It handles timeouts and DNS/TLS errors, turning each one into a "down" result with an error message. Unit-test it with a fake handler.
5. **Runner and scheduler:** the runner checks URLs in parallel (`Parallel.ForEachAsync` with a concurrency cap) and saves results in one batch. The scheduler runs it once at startup, then every N minutes, where N comes from config.
6. **Alerts backend:** write `IEmailSender`/`SmtpEmailSender` with MailKit and `AlertService` for state-change detection. Hook `AlertService` into the runner. Unit-test the state machine (threshold, one alert per outage, recovery) with a fake sender.
7. **Endpoints:** add the URL CRUD, history/stats and alert recipient endpoints with DTOs and validation. Seed URLs from `appsettings.json` on first run (see question 30).
8. **Integration tests:**
   - POST a URL, trigger the runner directly instead of waiting 5 minutes, then GET the history and assert on it.
   - POST a recipient, point a URL at a stub that returns 500, run the runner `FailureThreshold` times, and assert that an `AlertEvent(Down)` exists and the fake sender received the email.
9. **Frontend:** build the API client, then the dashboard with polling every ~30–60 seconds, then the detail view with the chart/list toggles, then the Alerts page, then state-change toasts.
10. **Polish:** write the README with a run guide (API, frontend and smtp4dev), and add the root `package.json` with `npm run setup` and `npm start` (using `concurrently`).

---

## 5. Pitfalls and Open Questions

### Definitions you need to decide

1. **What counts as "up"?** The usual rule is 2xx/3xx = up, and 4xx/5xx, timeouts and network errors = down. Should a 401 or 403 count as up? Many health URLs sit behind auth.
   - **Answer:** Use the usual rule, plus **401 and 403 count as up**, because the server is responding. Because of answer 2, **3xx counts as down**. Final rule:
     - **Up:** 2xx, 401, 403
     - **Down:** 3xx, any other 4xx, 5xx, timeouts, DNS/TLS/connection errors
2. **Redirects:** should the checker follow them and record the final status, or record the 301/302 itself?
   - **Answer:** Don't follow redirects (`AllowAutoRedirect = false` on the HttpClient). Record the 301/302 status and **count it as down**. A redirect means the user should update the saved URL. The UI should show the redirect target (the `Location` header) in the error text so the user knows what to change it to.
3. **Timeout value:** I suggest 10 seconds. A timeout has no HTTP status, so the field can't be required, and the UI needs a way to show "timeout / DNS failure".
   - **Answer:** **10 seconds**, set in config (`Monitor:TimeoutSeconds`). `StatusCode` can be null. When it is, the UI shows the error type (Timeout, DNS failure, Connection refused, TLS error).
4. **Uptime %:** should this be the share of checks that succeeded (simple), or time-weighted? The two differ when checks are missed, for example while the app is stopped.
   - **Answer:** **Share of checks that succeeded**: `up checks / total checks` in the selected time range. This is the right fit for a simple local app.
5. **Chart time range:** last 24 hours, 7 days, or user-selectable? At 5-minute checks, 7 days is about 2,000 points per URL. Should the backend group them into hourly buckets for the chart?
   - **Answer:** The default is the **last 24 hours**, with user-selectable ranges. Suggested options: **1h, 6h, 24h, 7d, 30d**. The 7d and 30d ranges are grouped into buckets on the backend (hourly for 7d, every 6 hours for 30d) so the chart stays readable. Shorter ranges show every check.

### Technical pitfalls

6. **SQLite and `DateTimeOffset`:** EF Core's SQLite provider can't sort or filter on `DateTimeOffset`. Store UTC `DateTime`, or Unix-millisecond `long`, and convert in the UI.
   - **Answer:** Store all timestamps as **UTC `DateTime`**. The API returns ISO-8601 UTC strings ending in `Z`, and the Vue UI converts them to the browser's local time for display.
7. **SQLite writes from two places:** the background job and the API both write. That's fine at this scale, but turn on WAL mode and save each run in one batch to avoid "database is locked" errors.
   - **Answer:** Yes. Turn on **WAL mode** at startup (`PRAGMA journal_mode=WAL;`) and save each check run with a **single `SaveChangesAsync`**.
8. **Scoped vs. singleton:** a `BackgroundService` is a singleton, but `DbContext` is scoped. Each run has to create its own scope. This is a classic bug.
   - **Answer:** Fine for the short term. The scheduler creates a **new DI scope for each run** (`IServiceScopeFactory.CreateAsyncScope()`) and gets the `DbContext` and runner from that scope.
9. **Overlapping runs:** with many slow URLs, one run could last longer than 5 minutes. `PeriodicTimer` doesn't overlap runs (it skips ticks), so keep the per-request timeout well under the interval.
   - **Answer:** Yes. Use `PeriodicTimer`, which never overlaps runs. The 10-second per-request timeout is far below the 5-minute interval, and the concurrency cap keeps a full run short.
10. **App asleep means no data:** the checker only runs while the process does, so history will have gaps. Is that acceptable, or should the UI show gaps explicitly?
   - **Answer:** The **UI shows gaps** when the app was down. Any space between two checks longer than **2× the check interval** counts as a gap. The chart breaks the line there (a `null` point, so Chart.js doesn't draw across it), and the history list shows a "No data (monitor not running)" row covering that period. Gaps are not counted in uptime %.
11. **HEAD vs. GET:** HEAD is lighter, but some servers reject it with 405. Suggestion: GET, reading only the headers (`ResponseHeadersRead`).
   - **Answer:** Use **GET**.
12. **Response time:** measure time to first byte (headers received) or the full body download? Also, the first request to a host includes DNS and TLS setup, so it can spike.
   - **Answer:** Measure the **full body download**: the timer starts before the request and stops after the whole body has been read. The 10-second timeout covers the download too. Suggested safety cap: stop reading after ~5 MB, so one very large page can't hold up a run.
13. **Self-signed or invalid TLS certificates:** should those count as down? Suggestion: yes, with the error message saved.
   - **Answer:** Yes. An invalid or self-signed certificate **counts as down**, and the TLS error message is saved.
14. **URL validation:** only absolute `http`/`https` URLs, and duplicates are rejected. Also, letting users add any URL lets the server call internal addresses (SSRF). That's fine locally, but worth noting if this is ever deployed.
   - **Answer:** Fine as is for a local tool. Validation: absolute `http`/`https` URLs only, with duplicates rejected. No SSRF protection for now.
15. **Delete behavior:** hard delete with cascade (history is lost), or soft delete with `IsActive = false`? Suggestion: hard delete for simplicity, unless you want to keep history.
   - **Answer:** **Hard delete**. Deleting a URL also deletes its checks and alert events (cascade).
16. **Data retention:** about 288 rows per URL per day. Should a cleanup job delete rows older than N days? Not urgent for SQLite at small scale.
   - **Answer:** Not in scope for now. Possible **follow-up**: a retention cleanup job.
17. **Integration test against a live URL:** a real external URL makes the test flaky and dependent on the network. Options are a local stub server in the test (reliable), a real URL like `https://example.com` marked with a trait so it can be skipped, or both.
   - **Answer:** Use a **local stub server** started by the test. It serves fixed endpoints: `/ok` (200), `/error` (500), `/redirect` (302), `/auth` (401) and `/slow` (longer than the timeout). That makes the tests reliable and offline.
18. **Postgres migration later:** EF migrations are provider-specific, so the SQLite migrations won't carry over and you'd regenerate them for Postgres. Keeping types simple (no SQLite-only tricks) keeps the swap easy.
   - **Answer:** **Keep the types simple** (`int`, `string`, `bool`, `DateTime`, `long`), with no SQLite-specific SQL or column types, so a later move to Postgres is mostly a provider change and a regenerated migration.
19. **Ports and CORS:** a Vite proxy in dev avoids CORS. If you'd rather open the frontend against the API directly, a CORS policy is needed instead.
   - **Answer:** **No CORS for now.** In dev, the Vite proxy forwards `/api` to the backend.
20. **Windows specifics:** the `dotnet-ef` and `smtp4dev` tools need `%USERPROFILE%\.dotnet\tools` on PATH. The dev HTTPS certificate needs `dotnet dev-certs https --trust`, or run the API on plain HTTP locally.
   - **Answer:** The user will set up the dev certificate. The README will list the steps. Because the tools are local (from the manifest) and run as `dotnet ef` / `dotnet smtp4dev`, no PATH change is needed.

### Alert-specific pitfalls

21. **Alert spam / flapping:** a URL that keeps switching between up and down would send an email every 5 minutes. `FailureThreshold` (2 consecutive failures) helps. Do you also want a cooldown, such as at most one alert per URL per 30 minutes?
   - **Answer:** Yes, add a **cooldown**: at most one email per URL per `Alerts:CooldownMinutes`, default **30**. State changes during the cooldown are still saved as an `AlertEvent`, marked `Suppressed`, so the Alerts page still shows them, but no email is sent. This works together with the `FailureThreshold` of 2.
22. **Startup state:** after a restart, should the saved `CurrentState` be trusted (no duplicate alert), and should new URLs start as `Unknown` so the first check doesn't send a "recovered" email? Suggestion: yes to both.
   - **Answer:** Yes to both. `CurrentState` is saved in the database and trusted after a restart, so no duplicate alert is sent. New URLs start as `Unknown`. The first check sets them to Up or Down without an email, but Unknown → Down does send a DOWN alert once the failure threshold is reached.
23. **Port conflicts:** smtp4dev's web UI defaults to port 5000, which may clash with the API. Port 25 may be blocked or already in use on some Windows machines, so use port 2525 for SMTP if needed.
   - **Answer:** Yes. If port 25 is blocked or in use, run smtp4dev with `--smtpport 2525` and set `Smtp:Port` to 2525. Also move smtp4dev's web UI off port 5000 (for example `--urls http://localhost:5050`) so it doesn't clash with the API. The README will cover both.
24. **Real SMTP later:** Gmail and Office 365 need app passwords or OAuth, and TLS on port 587. Credentials must go in user-secrets or environment variables, **never** in `appsettings.json` in git.
   - **Answer:** **Follow-up.** Real SMTP provider support, with credentials in user-secrets or environment variables and TLS on port 587, comes later. For now, only local smtp4dev is used, which needs no credentials.
25. **Slow or failing SMTP:** sending must not block or fail the check run. Use a short SMTP timeout and record the failure. Optionally, retry failed sends on the next run.
   - **Answer:** Use a **short SMTP timeout** (10 seconds). On failure, save the error on the `AlertEvent` (`Sent = false`, `Error = ...`) and carry on with the run. **Retrying failed sends is a follow-up.**
26. **Recipient scope:** do all recipients get alerts for all URLs (simple), or should each email subscribe to specific URLs? Suggestion: all-for-all for now.
   - **Answer:** **All recipients get alerts for all URLs.** Letting each email subscribe to specific URLs is a **follow-up**.
27. **Email validation:** use `MailAddress`/`[EmailAddress]` for format only. There's no confirmation or double opt-in step, which is fine for a local tool.
   - **Answer:** Yes. Check the format only, with **no confirmation or double opt-in**. If the app is ever deployed beyond local use, adding email confirmation is a possible **follow-up**.
28. **Deleting a URL that is down:** its alert history goes with it (cascade). Is that acceptable, or should alert events be kept?
   - **Answer:** **Cascade is fine.** Deleting a URL also deletes its alert events.

### Scope questions

29. Should the check interval be a single global value (5 minutes, set in config), or settable per URL?
   - **Answer:** A **single global value**, `Monitor:CheckIntervalMinutes` in config (default 5). Setting the interval per URL is a **follow-up**.
30. Should URLs also be seedable from a config file, in addition to the UI form?
   - **Answer:** Yes. URLs can also be **seeded** from `Monitor:SeedUrls` in `appsettings.json`. At startup, any seed URL not already in the database is added. To keep a deleted seed URL from coming back on every restart, seeding only runs **when the database has no URLs** (first run, or after the database file is deleted). After that, the UI and API manage the list.
31. Do you want a single-command start (the API serves the built Vue app), or are separate terminals fine?
   - **Answer:** **Single command start.** A root `package.json` provides `npm start`, which uses `concurrently` to launch the API (`dotnet run`), the Vite dev server and smtp4dev together, with labelled output in one terminal. `npm run setup` runs the one-time steps: `npm install`, `dotnet restore` and `dotnet tool restore`.
32. Do you want in-browser pop-up/toast notifications **in addition to** email (optional extra)?
   - **Answer:** Yes, add **in-browser toasts as well as email**. When the dashboard's polling sees a URL change state (Up → Down or Down → Up) compared with the previous poll, it shows a red or green toast in the corner. Toasts only appear while the UI is open. They don't use the cooldown, because they're cheap and not sent anywhere.

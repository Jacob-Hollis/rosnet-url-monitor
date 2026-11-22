# What I built

## Url Monitor

A lightweight URL health monitor. It checks a list of URLs every 5 minutes, records the HTTP status and
response time of every check in SQLite, shows current health and history in a web UI, and emails you
when a URL goes down or comes back.

- **Backend:** ASP.NET Core (.NET 10) + EF Core + SQLite, with a background job for the checks
- **Frontend:** Vue 3 + Vite + Chart.js
- **Email:** MailKit over SMTP, to [smtp4dev](https://github.com/rnwood/smtp4dev) locally (a local inbox, no real email account needed)

The full design, including every decision and the reasoning behind it, is in [design.md](design.md).

## Quick start

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js](https://nodejs.org/) 20.19+ (or 22.12+).

```sh
npm run setup   # once: installs npm packages, the pinned dotnet tools (dotnet-ef, smtp4dev), NuGet packages
npm start       # starts the API, the web UI and the local mail inbox together
```

Then open:

| What | URL |
|---|---|
| **Web UI** | http://localhost:5173 |
| Email inbox (smtp4dev) | http://localhost:5050 |
| API | http://localhost:5265/api/urls |
| OpenAPI document | http://localhost:5265/openapi/v1.json |

Press **Ctrl+C** to stop everything.

On first run the database (`backend/src/UrlMonitor.Api/monitor.db`) is created and three example URLs
are added: two healthy ones and one that fails on purpose (`does-not-exist.invalid`), so you can see
both states straight away. Delete them in the UI if you don't want them.

### Try the alerts

1. Go to **Alerts** and add any email address (it only goes to the local inbox), then click **Send test email**.
2. Open http://localhost:5050 to see it.
3. A URL is marked down after **2 failed checks in a row**. To see a DOWN email without waiting 5 minutes,
   click **Check now** on the `does-not-exist.invalid` row on the dashboard.

## Running the tests

```sh
npm test        # or: dotnet test UrlMonitor.slnx
```

- **Unit tests** (`backend/tests/UrlMonitor.UnitTests`): checker, alert rules, stats, validation, runner and
  scheduler. They use fakes and a fake clock, so they run in about a second with no network.
- **Integration tests** (`backend/tests/UrlMonitor.IntegrationTests`): the real API over HTTP with a real
  SQLite file, checking a local stub server (OK, 500, redirect, 401, slow and toggle pages) end to end,
  including the alert emails (captured by a fake sender). No UI, no internet.

## How it works

- **Checks:** every URL is checked at startup and then every 5 minutes, up to 10 at a time. A check is a
  GET request that does not follow redirects, timed until the whole body is downloaded (10 second timeout).
- **Up or down:** 2xx, 401 and 403 count as **up**. Everything else counts as **down**: 3xx redirects
  (update the URL instead; the error shows where it redirects to), other 4xx, 5xx, timeouts, and DNS,
  connection and TLS errors.
- **History:** every check is stored. Uptime % is the share of checks that were up. Response-time averages
  only include checks that got a response, so timeouts don't skew them. When the app wasn't running,
  the chart line breaks and the list shows a "No data" row.
- **Alerts:** a URL goes **DOWN** after 2 failed checks in a row, and **RECOVERED** when it's up again.
  Each change emails every saved address, with at most one email per URL every 30 minutes; changes inside
  that window are still listed on the Alerts page. While the UI is open you also get a pop-up when a URL
  changes state.

## Configuration

Settings are in `backend/src/UrlMonitor.Api/appsettings.json` (and `appsettings.Development.json`, used by
`npm start`).

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Monitor` | `Data Source=monitor.db` | SQLite database file |
| `Monitor:CheckIntervalMinutes` | `5` | How often every URL is checked |
| `Monitor:TimeoutSeconds` | `10` | Per-check timeout (connect, headers and body) |
| `Monitor:MaxBodyBytes` | `5242880` | Stop reading a response body after 5 MB |
| `Monitor:MaxConcurrency` | `10` | URLs checked at the same time |
| `Monitor:SeedUrls` | 3 examples (Development) | URLs added when the database has no URLs |
| `Alerts:FailureThreshold` | `2` | Failed checks in a row before a URL is DOWN |
| `Alerts:CooldownMinutes` | `30` | At most one email per URL in this window |
| `Alerts:SendRecoveryEmails` | `true` | Email when a URL recovers |
| `Smtp:Host` / `Smtp:Port` | `localhost` / `2525` | Mail server (smtp4dev locally) |
| `Smtp:Security` | `None` | `None`, `StartTls` (port 587), `SslOnConnect` (port 465) or `Auto` |
| `Smtp:Username` / `Smtp:Password` | empty | Leave empty for no login |
| `Smtp:From` | `url-monitor@localhost` | Sender address |

Any setting can also be set with an environment variable, using `__` for `:` (for example
`Monitor__CheckIntervalMinutes=1`). Never put real SMTP passwords in `appsettings.json`; use
`dotnet user-secrets` or environment variables instead.

## API

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/urls` | All URLs with latest check, state and 24h uptime |
| GET | `/api/urls/{id}` | One URL |
| POST | `/api/urls` | Add a URL: `{ "url": "https://…", "name": "optional" }` |
| DELETE | `/api/urls/{id}` | Delete a URL and its history |
| GET | `/api/urls/{id}/checks?from=&page=&pageSize=` | Check history, newest first |
| GET | `/api/urls/{id}/stats?range=1h\|6h\|24h\|7d\|30d` | Uptime, response times and chart series |
| POST | `/api/urls/{id}/check` | Check a URL now |
| GET/POST | `/api/alerts/recipients` | List / add alert emails: `{ "email": "…" }` |
| DELETE | `/api/alerts/recipients/{id}` | Remove an alert email |
| POST | `/api/alerts/test` | Send a test email to everyone |
| GET | `/api/alerts/events?limit=50` | Recent alerts |

Errors use the standard problem details format (`400` validation, `404` not found, `409` duplicate,
`502` email failed).

## Project layout

```
backend/src/UrlMonitor.Api/        ASP.NET Core API, background checker, alerts, EF Core + migrations
backend/tests/UrlMonitor.UnitTests/
backend/tests/UrlMonitor.IntegrationTests/
frontend/                          Vue 3 app (dashboard, URL detail, alerts)
dotnet-tools.json                  pinned dotnet-ef and smtp4dev versions
package.json                       root scripts: setup, start, test, build
design.md                          design plan and decisions
```

Run the parts separately if you prefer: `npm run start:api`, `npm run start:web`, `npm run start:mail`.

## Troubleshooting

- **A port is already in use** (5265, 5173, 5050 or 2525): stop whatever is using it, or change the port in
  `backend/src/UrlMonitor.Api/Properties/launchSettings.json` (API; also update `frontend/vite.config.js`),
  `frontend/vite.config.js` (UI), or the `start:mail` script and `Smtp:Port` (mail).
- **"API unreachable" in the UI:** the API isn't running or is still starting. Check the `api` lines in the `npm start` output.
- **Start over with an empty database:** stop the app and delete `backend/src/UrlMonitor.Api/monitor.db*`.
  The example URLs are added again on the next start.
- **Old emails in smtp4dev:** smtp4dev keeps its messages between runs; delete them in its web UI.
- **`dotnet smtp4dev` or `dotnet ef` not found:** run `dotnet tool restore` (part of `npm run setup`).
- **Changing the database schema:** edit the models, then
  `dotnet ef migrations add <Name> --project backend/src/UrlMonitor.Api --output-dir Data/Migrations`.
  Migrations are applied automatically at startup.
- **HTTPS:** the app runs over plain HTTP locally, so no development certificate is needed.
- **"400 Bad Request – Invalid Hostname"** when opening the API by another name (e.g. a LAN IP): the API only
  answers to `localhost`, `127.0.0.1` and `[::1]`, which blocks DNS-rebinding attacks. Add the name to
  `AllowedHosts` in `appsettings.json` (semicolon-separated) only if you really need it, since the API has no login.
- **"403 Cross-site requests that change data are not allowed":** the API rejects data-changing requests
  that a browser marks as coming from another website, so a page you visit can't, say, send test emails or
  delete URLs. The UI, curl and scripts are unaffected.


# The Debrief

30 minutes was allocated to design, 1 hour to coding, and 1 hour to review and testing. A small amount of extra time for reviewing this readme and adding context.

## Backend design


## Frontend design


## Testing approach

I decided to use mostly unit tests and some integration tests. A fair amount of unit tests were cut out to keep it lean. No automated UI test were used, instead I did manual UI testing (and in real scenarios I would also record a demo of my UI testing for future reference, for QA engineers, and to catch issues I didn't see before). UI test would be a follow up as noted above.

## AI Usage

I used Claude Code exclusively for design and coding.

## Possible follow-ups and other improvements that were cut

- **Retention cleanup of old checks** 
- **per-URL check intervals**
- **per-URL alert subscriptions**
- **real SMTP**
- **provider setup** 
- **retrying failed emails** 
- **email confirmation (for non-local use)**
- **Postgres (mostly a provider change plus regenerated migrations)**
- **UI automated tests, for example through Playwright**


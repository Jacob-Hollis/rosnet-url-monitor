using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using UrlMonitor.Api.Dtos;
using UrlMonitor.Api.Models;

namespace UrlMonitor.IntegrationTests;

/// <summary>
/// End-to-end tests (no UI): the real API over HTTP, a real SQLite file, real HTTP checks
/// against a local stub server, and the alert flow with a fake email sender.
/// </summary>
public sealed class MonitorEndToEndTests(StubServer stub) : IClassFixture<StubServer>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private MonitorAppFactory _app = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync() => StartAppAsync();

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>(Re)starts the app with extra settings; the default app is started before every test.</summary>
    private async Task StartAppAsync(params (string Key, string Value)[] settings)
    {
        if (_app is not null)
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }

        _app = new MonitorAppFactory();
        foreach (var (key, value) in settings) _app.Settings[key] = value;
        _client = _app.CreateClient();
    }

    // ---- helpers -------------------------------------------------------------------------

    private async Task<UrlSummaryDto> AddUrlAsync(string url, string? name = null)
    {
        var response = await _client.PostAsJsonAsync("/api/urls", new CreateUrlRequest(url, name), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UrlSummaryDto>(Json))!;
    }

    private async Task AddRecipientAsync(string email)
    {
        var response = await _client.PostAsJsonAsync("/api/alerts/recipients", new CreateRecipientRequest(email), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<T> GetAsync<T>(string path) => (await _client.GetFromJsonAsync<T>(path, Json))!;

    private Task<List<UrlSummaryDto>> UrlsAsync() => GetAsync<List<UrlSummaryDto>>("/api/urls");

    private Task<List<AlertEventDto>> AlertEventsAsync() => GetAsync<List<AlertEventDto>>("/api/alerts/events");

    // ---- the main end-to-end flow ----------------------------------------------------------

    [Fact]
    public async Task Add_url_run_scheduled_check_and_read_history()
    {
        var added = await AddUrlAsync(stub.Url("/ok"), "Stub OK");
        Assert.Equal(UrlState.Unknown, added.CurrentState);
        Assert.Null(added.LatestCheck);

        await _app.RunScheduledCheckAsync();

        // Dashboard view
        var summary = Assert.Single(await UrlsAsync());
        Assert.Equal(added.Id, summary.Id);
        Assert.Equal("Stub OK", summary.Name);
        Assert.Equal(UrlState.Up, summary.CurrentState);
        Assert.NotNull(summary.LatestCheck);
        Assert.True(summary.LatestCheck.IsUp);
        Assert.Equal(200, summary.LatestCheck.StatusCode);
        Assert.InRange(summary.LatestCheck.ResponseTimeMs, 0, 1000);
        Assert.Equal(DateTimeKind.Utc, summary.LatestCheck.CheckedAt.Kind);
        Assert.Equal(100.0, summary.Uptime24hPercent);

        // History list
        await _app.RunScheduledCheckAsync();
        var page = await GetAsync<CheckPageDto>($"/api/urls/{added.Id}/checks");
        Assert.Equal(2, page.TotalCount);
        Assert.True(page.Items[0].CheckedAt >= page.Items[1].CheckedAt, "newest first");
        Assert.Equal(600, page.GapThresholdSeconds);

        // Chart data
        var stats = await GetAsync<StatsDto>($"/api/urls/{added.Id}/stats?range=1h");
        Assert.Equal(2, stats.TotalChecks);
        Assert.Equal(100.0, stats.UptimePercent);
        Assert.Equal(2, stats.Series.Count);
        Assert.NotNull(stats.AvgResponseMs);
    }

    [Fact]
    public async Task Real_http_outcomes_are_classified_correctly()
    {
        var ok = await AddUrlAsync(stub.Url("/ok"));
        var error = await AddUrlAsync(stub.Url("/error"));
        var redirect = await AddUrlAsync(stub.Url("/redirect"));
        var auth = await AddUrlAsync(stub.Url("/auth"));
        var slow = await AddUrlAsync(stub.Url("/slow"));

        await _app.RunScheduledCheckAsync();

        var byId = (await UrlsAsync()).ToDictionary(u => u.Id, u => u.LatestCheck!);

        Assert.True(byId[ok.Id].IsUp);
        Assert.Equal(200, byId[ok.Id].StatusCode);

        Assert.False(byId[error.Id].IsUp);
        Assert.Equal(500, byId[error.Id].StatusCode);
        Assert.StartsWith("HTTP 500", byId[error.Id].Error);

        // Redirects are recorded, not followed, and the error says where to.
        Assert.False(byId[redirect.Id].IsUp);
        Assert.Equal(302, byId[redirect.Id].StatusCode);
        Assert.Contains($"redirects to {stub.Url("/ok")}", byId[redirect.Id].Error);

        // 401 counts as up: the server is responding.
        Assert.True(byId[auth.Id].IsUp);
        Assert.Equal(401, byId[auth.Id].StatusCode);

        Assert.False(byId[slow.Id].IsUp);
        Assert.Null(byId[slow.Id].StatusCode);
        Assert.Equal("Timeout after 1s", byId[slow.Id].Error);
        Assert.InRange(byId[slow.Id].ResponseTimeMs, 900, 5000);
    }

    [Fact]
    public async Task Connection_refused_is_down_with_no_status()
    {
        // Windows retries a refused localhost connection for ~2s before failing, so allow longer
        // than the default 1s test timeout to see the real "refused" error rather than a timeout.
        await StartAppAsync(("Monitor:TimeoutSeconds", "5"));
        var url = await AddUrlAsync($"http://127.0.0.1:{GetClosedPort()}/");

        await _app.RunScheduledCheckAsync();

        var check = Assert.Single(await UrlsAsync(), u => u.Id == url.Id).LatestCheck!;
        Assert.False(check.IsUp);
        Assert.Null(check.StatusCode);
        Assert.StartsWith("Connection failed", check.Error);
    }

    /// <summary>A localhost port nothing is listening on: bind a free port, then release it.</summary>
    private static int GetClosedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Deleting_a_url_while_check_now_runs_returns_404_not_500()
    {
        var url = await AddUrlAsync(stub.Url("/slow")); // the check takes the full 1s timeout

        var checkNow = _client.PostAsync($"/api/urls/{url.Id}/check", null);
        await Task.Delay(300); // the check is now waiting on the slow page
        var delete = await _client.DeleteAsync($"/api/urls/{url.Id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await checkNow).StatusCode);
    }

    [Fact]
    public async Task Check_now_endpoint_checks_and_saves()
    {
        var url = await AddUrlAsync(stub.Url("/ok"));

        var response = await _client.PostAsync($"/api/urls/{url.Id}/check", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var check = (await response.Content.ReadFromJsonAsync<CheckDto>(Json))!;
        Assert.True(check.IsUp);
        Assert.Equal(1, (await GetAsync<CheckPageDto>($"/api/urls/{url.Id}/checks")).TotalCount);
    }

    // ---- alerts ----------------------------------------------------------------------------

    [Fact]
    public async Task Url_going_down_sends_one_down_email()
    {
        await AddRecipientAsync("ops@example.com");
        var url = await AddUrlAsync(stub.Url("/error"));

        await _app.RunScheduledCheckAsync(); // failure 1: below threshold
        Assert.Empty(_app.Email.Sent);
        Assert.Empty(await AlertEventsAsync());

        await _app.RunScheduledCheckAsync(); // failure 2: DOWN
        await _app.RunScheduledCheckAsync(); // still down: no new alert

        var email = Assert.Single(_app.Email.Sent);
        Assert.Equal($"[DOWN] {url.Url}", email.Subject);
        Assert.Equal(["ops@example.com"], email.Recipients);
        Assert.Contains("HTTP 500", email.Body);

        var alert = Assert.Single(await AlertEventsAsync());
        Assert.Equal(AlertKind.Down, alert.Kind);
        Assert.Equal(AlertDelivery.Sent, alert.Delivery);
        Assert.Equal(url.Url, alert.Url);

        Assert.Equal(UrlState.Down, Assert.Single(await UrlsAsync()).CurrentState);
    }

    [Fact]
    public async Task Url_coming_back_sends_recovered_email()
    {
        await StartAppAsync(("Alerts:CooldownMinutes", "0"));
        await AddRecipientAsync("ops@example.com");
        await AddUrlAsync(stub.Url("/toggle"));

        try
        {
            stub.ToggleIsUp = true;
            await _app.RunScheduledCheckAsync();
            stub.ToggleIsUp = false;
            await _app.RunScheduledCheckAsync();
            await _app.RunScheduledCheckAsync();
            stub.ToggleIsUp = true;
            await _app.RunScheduledCheckAsync();
        }
        finally
        {
            stub.ToggleIsUp = true;
        }

        Assert.Equal(
            ["[DOWN] " + stub.Url("/toggle"), "[RECOVERED] " + stub.Url("/toggle")],
            _app.Email.Sent.Select(e => e.Subject));
        Assert.Contains("Outage length:", _app.Email.Sent[1].Body);

        var events = await AlertEventsAsync(); // newest first
        Assert.Equal([AlertKind.Recovered, AlertKind.Down], events.Select(e => e.Kind));
        Assert.All(events, e => Assert.Equal(AlertDelivery.Sent, e.Delivery));
    }

    [Fact]
    public async Task Email_failure_is_recorded_and_checks_still_saved()
    {
        await AddRecipientAsync("ops@example.com");
        var url = await AddUrlAsync(stub.Url("/error"));
        _app.Email.FailWith = new IOException("SMTP server unreachable");

        await _app.RunScheduledCheckAsync();
        await _app.RunScheduledCheckAsync();

        var alert = Assert.Single(await AlertEventsAsync());
        Assert.Equal(AlertDelivery.Failed, alert.Delivery);
        Assert.Equal("SMTP server unreachable", alert.Error);
        Assert.Equal(2, (await GetAsync<CheckPageDto>($"/api/urls/{url.Id}/checks")).TotalCount);
    }

    [Fact]
    public async Task Test_email_endpoint()
    {
        var noRecipients = await _client.PostAsync("/api/alerts/test", null);
        Assert.Equal(HttpStatusCode.BadRequest, noRecipients.StatusCode);

        await AddRecipientAsync("ops@example.com");
        var ok = await _client.PostAsync("/api/alerts/test", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.StartsWith("[TEST]", Assert.Single(_app.Email.Sent).Subject);

        _app.Email.FailWith = new IOException("Connection refused");
        var failed = await _client.PostAsync("/api/alerts/test", null);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("Connection refused", await failed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Recipients_can_be_added_listed_and_removed()
    {
        await AddRecipientAsync("  Ops@Example.com ");

        var duplicate = await _client.PostAsJsonAsync("/api/alerts/recipients", new CreateRecipientRequest("ops@example.com"), Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var invalid = await _client.PostAsJsonAsync("/api/alerts/recipients", new CreateRecipientRequest("nope"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var recipient = Assert.Single(await GetAsync<List<RecipientDto>>("/api/alerts/recipients"));
        Assert.Equal("ops@example.com", recipient.Email);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/alerts/recipients/{recipient.Id}")).StatusCode);
        Assert.Empty(await GetAsync<List<RecipientDto>>("/api/alerts/recipients"));
    }

    // ---- URL management and errors -----------------------------------------------------------

    [Fact]
    public async Task Deleting_a_url_removes_it_and_its_history()
    {
        var url = await AddUrlAsync(stub.Url("/ok"));
        await _app.RunScheduledCheckAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/urls/{url.Id}")).StatusCode);

        Assert.Empty(await UrlsAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/urls/{url.Id}/checks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/urls/{url.Id}")).StatusCode);
    }

    [Fact]
    public async Task Bad_requests_get_clear_errors()
    {
        await AddUrlAsync(stub.Url("/ok"));

        var invalid = await _client.PostAsJsonAsync("/api/urls", new CreateUrlRequest("example.com", null), Json);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("absolute http", await invalid.Content.ReadAsStringAsync());

        var duplicate = await _client.PostAsJsonAsync("/api/urls", new CreateUrlRequest(stub.Url("/ok"), null), Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/urls/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/urls/999/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/urls/999/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/urls/1/stats?range=2h")).StatusCode);
    }

    [Fact]
    public async Task Seed_urls_are_added_on_first_run_only()
    {
        await StartAppAsync(("Monitor:SeedUrls:0", stub.Url("/ok")), ("Monitor:SeedUrls:1", "not a url"));

        var seeded = Assert.Single(await UrlsAsync());
        Assert.Equal(stub.Url("/ok"), seeded.Url);
    }
}

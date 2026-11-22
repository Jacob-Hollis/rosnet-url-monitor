using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.UnitTests.Services.Alerts;

/// <summary>
/// The whole alert flow through the real CheckRunner and AlertService, with in-memory SQLite,
/// a scripted URL checker, a fake clock and a fake email sender.
/// </summary>
public sealed class AlertFlowTests : IAsyncLifetime
{
    private TestSqliteDb _db = null!;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeEmailSender _email = new();
    private readonly AlertsOptions _alertOptions = new() { FailureThreshold = 2, CooldownMinutes = 30 };
    private bool _siteIsUp = true;
    private int _urlId;

    public async Task InitializeAsync()
    {
        _db = await TestSqliteDb.CreateAsync();
        await using var context = _db.CreateContext();
        var url = new MonitoredUrl { Url = "https://example.com", CreatedAt = _time.GetUtcNow().UtcDateTime };
        context.MonitoredUrls.Add(url);
        await context.SaveChangesAsync();
        _urlId = url.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task AddRecipientsAsync(params string[] emails)
    {
        await using var context = _db.CreateContext();
        context.AlertRecipients.AddRange(emails.Select(e => new AlertRecipient { Email = e, CreatedAt = DateTime.UtcNow }));
        await context.SaveChangesAsync();
    }

    /// <summary>Runs one scheduled check, then moves the clock forward 5 minutes.</summary>
    private async Task RunCheckAsync()
    {
        await using var context = _db.CreateContext();
        var alertService = new AlertService(
            context, _email, Options.Create(_alertOptions), _time, NullLogger<AlertService>.Instance)
        {
            LocalZone = TimeZoneInfo.Utc,
        };
        var runner = new CheckRunner(
            context, new ScriptedChecker(this), alertService, Options.Create(new MonitorOptions()), NullLogger<CheckRunner>.Instance);

        await runner.RunAllAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
    }

    private async Task RunChecksAsync(string sequence)
    {
        foreach (var c in sequence)
        {
            _siteIsUp = c == 'U';
            await RunCheckAsync();
        }
    }

    private async Task<List<AlertEvent>> EventsAsync()
    {
        await using var context = _db.CreateContext();
        return await context.AlertEvents.OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<MonitoredUrl> UrlAsync()
    {
        await using var context = _db.CreateContext();
        return await context.MonitoredUrls.SingleAsync(u => u.Id == _urlId);
    }

    // The basic DOWN email, "one email while it stays down" and "email failure is recorded" are
    // covered end to end by the integration tests; these cover the timing rules they can't reach.

    [Fact]
    public async Task Recovery_after_cooldown_is_emailed_with_outage_length()
    {
        await AddRecipientsAsync("ops@example.com");

        // Down at 12:10, stays down 40 minutes (past the 30 minute cooldown), recovers at 12:50.
        await RunChecksAsync("UDD" + new string('D', 7) + "U");

        Assert.Equal(2, _email.Sent.Count);
        var recovered = _email.Sent[1];
        Assert.Equal("[RECOVERED] https://example.com", recovered.Subject);
        Assert.Contains("Outage length: 40m 0s", recovered.Body);

        var events = await EventsAsync();
        Assert.Equal([AlertKind.Down, AlertKind.Recovered], events.Select(e => e.Kind));
        Assert.All(events, e => Assert.Equal(AlertDelivery.Sent, e.Delivery));
        Assert.Equal(UrlState.Up, (await UrlAsync()).CurrentState);
    }

    [Fact]
    public async Task Flapping_within_cooldown_is_recorded_but_not_emailed()
    {
        await AddRecipientsAsync("ops@example.com");

        // Down, recovered, down again: all within 30 minutes.
        await RunChecksAsync("UDDUDD");

        Assert.Single(_email.Sent);
        var events = await EventsAsync();
        Assert.Equal([AlertKind.Down, AlertKind.Recovered, AlertKind.Down], events.Select(e => e.Kind));
        Assert.Equal(
            [AlertDelivery.Sent, AlertDelivery.Suppressed, AlertDelivery.Suppressed],
            events.Select(e => e.Delivery));
    }

    [Fact]
    public async Task Failed_email_does_not_start_a_cooldown()
    {
        await AddRecipientsAsync("ops@example.com");
        _email.FailWith = new IOException("smtp down");
        await RunChecksAsync("UDD");

        _email.FailWith = null;
        await RunChecksAsync("U");

        // The recovery is emailed: only a Sent email starts the cooldown.
        Assert.Equal("[RECOVERED] https://example.com", Assert.Single(_email.Sent).Subject);
    }

    [Fact]
    public async Task No_recipients_is_recorded()
    {
        await RunChecksAsync("UDD");

        Assert.Empty(_email.Sent);
        Assert.Equal(AlertDelivery.NoRecipients, Assert.Single(await EventsAsync()).Delivery);
    }

    private sealed class ScriptedChecker(AlertFlowTests test) : IUrlChecker
    {
        public Task<CheckResult> CheckAsync(string url, CancellationToken cancellationToken = default)
        {
            var now = test._time.GetUtcNow().UtcDateTime;
            return Task.FromResult(test._siteIsUp
                ? new CheckResult(now, 200, 50, true, null)
                : new CheckResult(now, 503, 50, false, "HTTP 503 Service Unavailable"));
        }
    }
}

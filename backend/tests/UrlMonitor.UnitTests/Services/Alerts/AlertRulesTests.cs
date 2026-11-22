using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.UnitTests.Services.Alerts;

public class AlertRulesTests
{
    private static MonitoredUrl Url(UrlState state = UrlState.Unknown) =>
        new() { Url = "https://example.com", CurrentState = state };

    /// <summary>Applies a sequence of check results ('U' = up, 'D' = down) and returns the alerts raised.</summary>
    private static List<AlertKind?> Apply(MonitoredUrl url, string sequence, int threshold = 2) =>
        sequence.Select(c => AlertRules.ApplyCheck(url, c == 'U', threshold)).ToList();

    [Fact]
    public void Full_outage_cycle()
    {
        // New URL up (silent), 1 failure (below threshold), 2nd failure (DOWN), still down (silent),
        // back up (RECOVERED), then down again (DOWN).
        var url = Url();
        Assert.Equal(
            [null, null, AlertKind.Down, null, AlertKind.Recovered, null, AlertKind.Down],
            Apply(url, "UDDDUDD"));
        Assert.Equal(UrlState.Down, url.CurrentState);
        Assert.Equal(2, url.ConsecutiveFailures);
    }

    [Fact]
    public void New_url_that_fails_raises_down_at_threshold()
    {
        var url = Url();
        Assert.Equal([null, AlertKind.Down], Apply(url, "DD"));
    }

    [Fact]
    public void Failure_streak_resets_on_success()
    {
        var url = Url(UrlState.Up);
        Assert.Equal([null, null, null, null], Apply(url, "DUDU"));
        Assert.Equal(0, url.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)] // treated as 1
    public void Low_threshold_alerts_on_first_failure(int threshold)
    {
        Assert.Equal([AlertKind.Down], Apply(Url(UrlState.Up), "D", threshold));
    }

    [Theory]
    [InlineData(AlertKind.Down, false, true, AlertDelivery.Pending)]
    [InlineData(AlertKind.Recovered, false, true, AlertDelivery.Pending)]
    [InlineData(AlertKind.Down, true, true, AlertDelivery.Suppressed)]      // cooldown
    [InlineData(AlertKind.Recovered, false, false, AlertDelivery.Suppressed)] // recovery emails off
    [InlineData(AlertKind.Down, false, false, AlertDelivery.Pending)]       // ...doesn't affect DOWN
    public void Initial_delivery(AlertKind kind, bool inCooldown, bool sendRecovery, AlertDelivery expected)
    {
        var options = new AlertsOptions { SendRecoveryEmails = sendRecovery };
        Assert.Equal(expected, AlertRules.InitialDelivery(kind, inCooldown, options));
    }
}

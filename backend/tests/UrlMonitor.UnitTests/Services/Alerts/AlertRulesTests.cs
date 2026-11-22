using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.UnitTests.Services.Alerts;

public class AlertRulesTests
{
    private const int Threshold = 2;

    private static MonitoredUrl Url(UrlState state = UrlState.Unknown, int failures = 0) =>
        new() { Url = "https://example.com", CurrentState = state, ConsecutiveFailures = failures };

    /// <summary>Applies a sequence of check results ('U' = up, 'D' = down) and returns the alerts raised.</summary>
    private static List<AlertKind?> Apply(MonitoredUrl url, string sequence, int threshold = Threshold) =>
        sequence.Select(c => AlertRules.ApplyCheck(url, c == 'U', threshold)).ToList();

    [Fact]
    public void New_url_that_is_up_becomes_up_silently()
    {
        var url = Url();
        Assert.Equal([null], Apply(url, "U"));
        Assert.Equal(UrlState.Up, url.CurrentState);
    }

    [Fact]
    public void One_failure_is_below_threshold()
    {
        var url = Url(UrlState.Up);
        Assert.Equal([null], Apply(url, "D"));
        Assert.Equal(UrlState.Up, url.CurrentState);
        Assert.Equal(1, url.ConsecutiveFailures);
    }

    [Fact]
    public void Reaching_threshold_raises_down_once()
    {
        var url = Url(UrlState.Up);
        Assert.Equal([null, AlertKind.Down, null, null], Apply(url, "DDDD"));
        Assert.Equal(UrlState.Down, url.CurrentState);
        Assert.Equal(4, url.ConsecutiveFailures);
    }

    [Fact]
    public void New_url_that_fails_raises_down_at_threshold()
    {
        var url = Url();
        Assert.Equal([null, AlertKind.Down], Apply(url, "DD"));
        Assert.Equal(UrlState.Down, url.CurrentState);
    }

    [Fact]
    public void Failure_streak_resets_on_success()
    {
        var url = Url(UrlState.Up);
        Assert.Equal([null, null, null, null], Apply(url, "DUDU"));
        Assert.Equal(UrlState.Up, url.CurrentState);
        Assert.Equal(0, url.ConsecutiveFailures);
    }

    [Fact]
    public void Down_url_that_comes_back_raises_recovered()
    {
        var url = Url(UrlState.Down, failures: 5);
        Assert.Equal([AlertKind.Recovered, null], Apply(url, "UU"));
        Assert.Equal(UrlState.Up, url.CurrentState);
        Assert.Equal(0, url.ConsecutiveFailures);
    }

    [Fact]
    public void Full_outage_cycle()
    {
        var url = Url();
        Assert.Equal(
            [null, null, AlertKind.Down, null, AlertKind.Recovered, null, AlertKind.Down],
            Apply(url, "UDDDUDD"));
    }

    [Fact]
    public void Threshold_of_one_alerts_on_first_failure()
    {
        var url = Url(UrlState.Up);
        Assert.Equal([AlertKind.Down], Apply(url, "D", threshold: 1));
    }

    [Fact]
    public void Threshold_below_one_is_treated_as_one()
    {
        var url = Url(UrlState.Up);
        Assert.Equal([AlertKind.Down], Apply(url, "D", threshold: 0));
    }

    [Theory]
    [InlineData(AlertKind.Down, false, true, AlertDelivery.Pending)]
    [InlineData(AlertKind.Recovered, false, true, AlertDelivery.Pending)]
    [InlineData(AlertKind.Down, true, true, AlertDelivery.Suppressed)]
    [InlineData(AlertKind.Recovered, true, true, AlertDelivery.Suppressed)]
    [InlineData(AlertKind.Recovered, false, false, AlertDelivery.Suppressed)]
    [InlineData(AlertKind.Down, false, false, AlertDelivery.Pending)]
    public void Initial_delivery(AlertKind kind, bool inCooldown, bool sendRecovery, AlertDelivery expected)
    {
        var options = new AlertsOptions { SendRecoveryEmails = sendRecovery };
        Assert.Equal(expected, AlertRules.InitialDelivery(kind, inCooldown, options));
    }
}

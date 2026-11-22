using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.UnitTests.Services.Alerts;

public class AlertEmailComposerTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone("Test-5", TimeSpan.FromHours(-5), "Test-5", "Test-5");
    private static readonly DateTime At = new(2026, 11, 22, 18, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Down_email()
    {
        var url = new MonitoredUrl { Url = "https://example.com", ConsecutiveFailures = 2 };
        var check = new UrlCheck { CheckedAt = At, StatusCode = 503, ResponseTimeMs = 87, Error = "HTTP 503 Service Unavailable" };

        var email = AlertEmailComposer.Compose(AlertKind.Down, url, check, null, Eastern);

        Assert.Equal("[DOWN] https://example.com", email.Subject);
        Assert.Contains("https://example.com is DOWN.", email.Body);
        Assert.Contains("2026-11-22 18:30:00 UTC (2026-11-22 13:30:00 local)", email.Body);
        Assert.Contains("Status:        503", email.Body);
        Assert.Contains("HTTP 503 Service Unavailable", email.Body);
        Assert.Contains("87 ms", email.Body);
        Assert.Contains("2 in a row", email.Body);
    }

    [Fact]
    public void Down_email_with_no_response()
    {
        var url = new MonitoredUrl { Url = "https://example.com" };
        var check = new UrlCheck { CheckedAt = At, StatusCode = null, ResponseTimeMs = 10000, Error = "Timeout after 10s" };

        var email = AlertEmailComposer.Compose(AlertKind.Down, url, check, null, Eastern);

        Assert.Contains("Status:        no response", email.Body);
        Assert.Contains("Timeout after 10s", email.Body);
    }

    [Fact]
    public void Recovered_email_shows_outage_length_and_uses_name_in_subject()
    {
        var url = new MonitoredUrl { Url = "https://example.com", Name = "Main site" };
        var check = new UrlCheck { CheckedAt = At, StatusCode = 200, ResponseTimeMs = 120 };

        var email = AlertEmailComposer.Compose(AlertKind.Recovered, url, check, At.AddMinutes(-75), Eastern);

        Assert.Equal("[RECOVERED] Main site", email.Subject);
        Assert.Contains("has RECOVERED", email.Body);
        Assert.Contains("Down since:    2026-11-22 17:15:00 UTC", email.Body);
        Assert.Contains("Outage length: 1h 15m", email.Body);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(10 * 60 + 5, "10m 5s")]
    [InlineData(3 * 3600 + 20 * 60, "3h 20m")]
    [InlineData(-5, "0s")]
    public void Durations(int seconds, string expected) =>
        Assert.Equal(expected, AlertEmailComposer.FormatDuration(TimeSpan.FromSeconds(seconds)));
}

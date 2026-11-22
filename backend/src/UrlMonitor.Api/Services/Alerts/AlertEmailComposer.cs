using System.Globalization;
using System.Text;
using UrlMonitor.Api.Models;

namespace UrlMonitor.Api.Services.Alerts;

public record AlertEmail(string Subject, string Body);

/// <summary>Builds the plain-text alert emails.</summary>
public static class AlertEmailComposer
{
    /// <param name="downSince">For recoveries: when the URL went down, to show the outage length.</param>
    public static AlertEmail Compose(
        AlertKind kind, MonitoredUrl url, UrlCheck check, DateTime? downSince, TimeZoneInfo localZone)
    {
        var label = kind == AlertKind.Down ? "DOWN" : "RECOVERED";
        var subject = $"[{label}] {url.Name ?? url.Url}";

        var body = new StringBuilder();
        body.AppendLine(kind == AlertKind.Down
            ? $"{url.Url} is DOWN."
            : $"{url.Url} has RECOVERED and is up again.");
        body.AppendLine();
        if (url.Name is not null) body.AppendLine($"Name:          {url.Name}");
        body.AppendLine($"URL:           {url.Url}");
        body.AppendLine($"Time:          {FormatTime(check.CheckedAt, localZone)}");
        body.AppendLine($"Status:        {(check.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "no response")}");
        if (check.Error is not null) body.AppendLine($"Error:         {check.Error}");
        body.AppendLine($"Response time: {check.ResponseTimeMs} ms");

        if (kind == AlertKind.Down)
        {
            body.AppendLine($"Failed checks: {url.ConsecutiveFailures} in a row");
        }
        else if (downSince is { } since)
        {
            body.AppendLine($"Down since:    {FormatTime(since, localZone)}");
            body.AppendLine($"Outage length: {FormatDuration(check.CheckedAt - since)}");
        }

        return new AlertEmail(subject, body.ToString());
    }

    public static AlertEmail ComposeTest(DateTime now, TimeZoneInfo localZone) => new(
        "[TEST] URL Monitor email alerts",
        $"This is a test email from URL Monitor, sent {FormatTime(now, localZone)}.{Environment.NewLine}" +
        $"If you received it, alert emails are working.{Environment.NewLine}");

    public static string FormatTime(DateTime utc, TimeZoneInfo localZone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, localZone);
        return $"{utc:yyyy-MM-dd HH:mm:ss} UTC ({local:yyyy-MM-dd HH:mm:ss} local)";
    }

    public static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
        return $"{d.Seconds}s";
    }
}

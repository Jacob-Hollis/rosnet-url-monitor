namespace UrlMonitor.Api.Models;

/// <summary>The result of a single check of a URL.</summary>
public class UrlCheck
{
    public long Id { get; set; }

    public int MonitoredUrlId { get; set; }

    public MonitoredUrl? MonitoredUrl { get; set; }

    /// <summary>When the check started (UTC).</summary>
    public DateTime CheckedAt { get; set; }

    /// <summary>HTTP status code; null when no response was received (timeout, DNS, TLS, connection error).</summary>
    public int? StatusCode { get; set; }

    /// <summary>Time until the full body was read, or until the request failed, in milliseconds.</summary>
    public int ResponseTimeMs { get; set; }

    public bool IsUp { get; set; }

    /// <summary>Error description, e.g. "Timeout", "DNS failure", or "Redirect to https://...".</summary>
    public string? Error { get; set; }
}

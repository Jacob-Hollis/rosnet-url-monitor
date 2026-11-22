namespace UrlMonitor.Api.Models;

/// <summary>A URL being monitored.</summary>
public class MonitoredUrl
{
    public int Id { get; set; }

    /// <summary>Absolute http/https URL. Unique.</summary>
    public required string Url { get; set; }

    /// <summary>Optional display name.</summary>
    public string? Name { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Alerting state (not just the last check's result): changes only once the failure threshold is reached.</summary>
    public UrlState CurrentState { get; set; } = UrlState.Unknown;

    /// <summary>Number of consecutive failed checks; reset to 0 on a successful check.</summary>
    public int ConsecutiveFailures { get; set; }

    public List<UrlCheck> Checks { get; set; } = [];

    public List<AlertEvent> AlertEvents { get; set; } = [];
}

public enum UrlState
{
    Unknown,
    Up,
    Down,
}

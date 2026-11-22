namespace UrlMonitor.Api.Services;

/// <summary>Settings from the "Monitor" section of appsettings.json.</summary>
public class MonitorOptions
{
    public const string SectionName = "Monitor";

    /// <summary>How often every URL is checked (global for now; per-URL is a follow-up).</summary>
    public double CheckIntervalMinutes { get; set; } = 5;

    /// <summary>Per-request timeout, covering connect, headers and body download.</summary>
    public double TimeoutSeconds { get; set; } = 10;

    /// <summary>Stop reading a response body after this many bytes, so one huge page can't hold up a run.</summary>
    public long MaxBodyBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Maximum number of URLs checked at the same time.</summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>URLs added on first run, when the database has no URLs.</summary>
    public List<string> SeedUrls { get; set; } = [];

    public TimeSpan CheckInterval => TimeSpan.FromMinutes(CheckIntervalMinutes);
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}

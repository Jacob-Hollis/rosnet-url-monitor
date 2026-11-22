namespace UrlMonitor.Api.Services;

public interface IUrlChecker
{
    /// <summary>
    /// Checks one URL. Never throws for network or HTTP problems; those become a "down" result.
    /// Only throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled
    /// (e.g. the app is shutting down).
    /// </summary>
    Task<CheckResult> CheckAsync(string url, CancellationToken cancellationToken = default);
}

/// <param name="CheckedAt">When the check started (UTC).</param>
/// <param name="StatusCode">Null when no response was received.</param>
/// <param name="ResponseTimeMs">Time until the body was fully read, or until the request failed.</param>
/// <param name="IsUp">Result of <see cref="StatusRules.IsUp"/>; false for any error.</param>
/// <param name="Error">Why the check is down; null when up.</param>
public record CheckResult(DateTime CheckedAt, int? StatusCode, int ResponseTimeMs, bool IsUp, string? Error);

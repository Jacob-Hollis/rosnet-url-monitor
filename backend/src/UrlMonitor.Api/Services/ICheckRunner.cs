using UrlMonitor.Api.Models;

namespace UrlMonitor.Api.Services;

public interface ICheckRunner
{
    /// <summary>Checks every active URL in parallel and saves the results in one batch.</summary>
    Task<IReadOnlyList<UrlCheck>> RunAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks a single URL now ("check now" button). Returns null if the URL doesn't exist.</summary>
    Task<UrlCheck?> RunOneAsync(int monitoredUrlId, CancellationToken cancellationToken = default);
}

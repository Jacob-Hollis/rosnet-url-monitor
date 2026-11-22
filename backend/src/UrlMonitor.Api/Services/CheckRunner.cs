using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.Api.Services;

/// <summary>
/// Runs checks, updates alert state and saves, then queues alert emails. Scoped: it uses the run's DbContext.
/// <list type="number">
/// <item>The HTTP checks run in parallel with no lock held (they can take up to the timeout).</item>
/// <item>Under <see cref="CheckStateLock"/>, each URL's current state is re-read from the database,
/// the results are applied, and everything is saved together. This keeps concurrent runs
/// (scheduler + "check now") from overwriting each other or raising the same alert twice,
/// and skips URLs deleted while their check was running.</item>
/// <item>Alerts are queued for <see cref="Background.AlertDeliveryService"/>; nothing waits for SMTP.</item>
/// </list>
/// </summary>
public class CheckRunner(
    MonitorDbContext db,
    IUrlChecker checker,
    IAlertService alertService,
    IAlertQueue alertQueue,
    CheckStateLock stateLock,
    IOptions<MonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<CheckRunner> logger) : ICheckRunner
{
    private const int MaxErrorLength = 1000;

    private sealed record Target(int Id, string Url);

    public async Task<IReadOnlyList<UrlCheck>> RunAllAsync(CancellationToken cancellationToken = default)
    {
        var targets = await db.MonitoredUrls
            .AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.Id)
            .Select(u => new Target(u.Id, u.Url))
            .ToListAsync(cancellationToken);

        if (targets.Count == 0) return [];

        var results = new CheckResult[targets.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrency),
                CancellationToken = cancellationToken,
            },
            async (i, ct) => results[i] = await SafeCheckAsync(targets[i], ct));

        var checks = await SaveResultsAsync(targets.Select((t, i) => (t.Id, results[i])).ToList(), cancellationToken);

        logger.LogInformation(
            "Checked {Count} URLs: {Up} up, {Down} down",
            checks.Count, checks.Count(c => c.IsUp), checks.Count(c => !c.IsUp));

        return checks;
    }

    public async Task<UrlCheck?> RunOneAsync(int monitoredUrlId, CancellationToken cancellationToken = default)
    {
        var target = await db.MonitoredUrls
            .AsNoTracking()
            .Where(u => u.Id == monitoredUrlId)
            .Select(u => new Target(u.Id, u.Url))
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null) return null;

        var result = await SafeCheckAsync(target, cancellationToken);

        // Null if the URL was deleted while it was being checked.
        return (await SaveResultsAsync([(target.Id, result)], cancellationToken)).SingleOrDefault();
    }

    /// <summary>
    /// Applies check results to fresh URL state under the lock, saves checks, state and alert events together,
    /// then queues the alerts for emailing. Results for URLs that no longer exist are dropped.
    /// </summary>
    private async Task<List<UrlCheck>> SaveResultsAsync(
        IReadOnlyList<(int UrlId, CheckResult Result)> results, CancellationToken cancellationToken)
    {
        IReadOnlyList<RaisedAlert> alerts;
        List<UrlCheck> checks;

        using (await stateLock.AcquireAsync(cancellationToken))
        {
            // Read now, not before the HTTP checks: another run may have updated these URLs meanwhile.
            // The context hasn't tracked any MonitoredUrl yet, so these are fresh values from the database.
            var ids = results.Select(r => r.UrlId).ToList();
            var urls = await db.MonitoredUrls
                .Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, cancellationToken);

            var applied = results
                .Where(r => urls.ContainsKey(r.UrlId))
                .Select(r => (Url: urls[r.UrlId], Check: ToEntity(r.UrlId, r.Result)))
                .ToList();

            checks = applied.Select(a => a.Check).ToList();
            db.UrlChecks.AddRange(checks);
            alerts = await alertService.EvaluateAsync(applied, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
        }

        alertQueue.Enqueue(alerts.Where(a => a.Event.Delivery == AlertDelivery.Pending).Select(a => a.Event.Id));
        return checks;
    }

    /// <summary>
    /// Checks one URL. The checker turns known network/HTTP problems into "down" results itself; anything
    /// unexpected is recorded as a down check for this URL only, instead of aborting the whole run
    /// (Parallel.ForEachAsync stops everything on the first exception, and no results would be saved).
    /// </summary>
    private async Task<CheckResult> SafeCheckAsync(Target target, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            return await checker.CheckAsync(target.Url, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // App shutting down.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error checking {Url}", target.Url);
            var error = $"Check failed: {ex.Message}";
            return new CheckResult(
                startedAt, null, 0, false, error.Length <= MaxErrorLength ? error : error[..MaxErrorLength]);
        }
    }

    private static UrlCheck ToEntity(int urlId, CheckResult result) => new()
    {
        MonitoredUrlId = urlId,
        CheckedAt = result.CheckedAt,
        StatusCode = result.StatusCode,
        ResponseTimeMs = result.ResponseTimeMs,
        IsUp = result.IsUp,
        Error = result.Error,
    };
}

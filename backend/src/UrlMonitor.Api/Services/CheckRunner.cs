using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.Api.Services;

/// <summary>
/// Runs checks, updates alert state, saves, then sends alert emails. Scoped: it uses the run's DbContext.
/// The DbContext is never used from the parallel part; only the HTTP checks run concurrently.
/// </summary>
public class CheckRunner(
    MonitorDbContext db,
    IUrlChecker checker,
    IAlertService alertService,
    IOptions<MonitorOptions> options,
    ILogger<CheckRunner> logger) : ICheckRunner
{
    public async Task<IReadOnlyList<UrlCheck>> RunAllAsync(CancellationToken cancellationToken = default)
    {
        var urls = await db.MonitoredUrls
            .Where(u => u.IsActive)
            .OrderBy(u => u.Id)
            .ToListAsync(cancellationToken);

        if (urls.Count == 0) return [];

        var results = new CheckResult[urls.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, urls.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrency),
                CancellationToken = cancellationToken,
            },
            async (i, ct) => results[i] = await checker.CheckAsync(urls[i].Url, ct));

        // A URL may have been deleted while its check was running; saving a check (or a state
        // update) for it would fail the whole batch, so drop those results.
        var ids = urls.Select(u => u.Id).ToList();
        var stillExisting = (await db.MonitoredUrls
                .Where(u => ids.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var saved = new List<(MonitoredUrl Url, UrlCheck Check)>(urls.Count);
        for (var i = 0; i < urls.Count; i++)
        {
            if (!stillExisting.Contains(urls[i].Id))
            {
                db.Entry(urls[i]).State = EntityState.Detached;
                continue;
            }

            saved.Add((urls[i], ToEntity(urls[i], results[i])));
        }

        var checks = await SaveAndAlertAsync(saved, cancellationToken);

        logger.LogInformation(
            "Checked {Count} URLs: {Up} up, {Down} down",
            checks.Count, checks.Count(c => c.IsUp), checks.Count(c => !c.IsUp));

        return checks;
    }

    public async Task<UrlCheck?> RunOneAsync(int monitoredUrlId, CancellationToken cancellationToken = default)
    {
        var url = await db.MonitoredUrls.FindAsync([monitoredUrlId], cancellationToken);
        if (url is null) return null;

        var check = ToEntity(url, await checker.CheckAsync(url.Url, cancellationToken));
        await SaveAndAlertAsync([(url, check)], cancellationToken);
        return check;
    }

    /// <summary>
    /// Saves checks, URL state changes and alert events together, then emails the alerts.
    /// Emails go out only after the results are safely saved, and email problems never fail the run.
    /// </summary>
    private async Task<List<UrlCheck>> SaveAndAlertAsync(
        IReadOnlyList<(MonitoredUrl Url, UrlCheck Check)> results, CancellationToken cancellationToken)
    {
        db.UrlChecks.AddRange(results.Select(r => r.Check));
        var alerts = await alertService.EvaluateAsync(results, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        await alertService.DeliverAsync(alerts, cancellationToken);

        return results.Select(r => r.Check).ToList();
    }

    private static UrlCheck ToEntity(MonitoredUrl url, CheckResult result) => new()
    {
        MonitoredUrlId = url.Id,
        CheckedAt = result.CheckedAt,
        StatusCode = result.StatusCode,
        ResponseTimeMs = result.ResponseTimeMs,
        IsUp = result.IsUp,
        Error = result.Error,
    };
}

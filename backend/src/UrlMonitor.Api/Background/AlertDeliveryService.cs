using Microsoft.EntityFrameworkCore;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.Api.Background;

/// <summary>
/// Emails queued alerts in the background, so check runs and "check now" never wait for SMTP.
/// On startup it also picks up alerts that were saved but not emailed before the app last stopped.
/// </summary>
public class AlertDeliveryService(
    IServiceScopeFactory scopeFactory,
    IAlertQueue queue,
    TimeProvider timeProvider,
    ILogger<AlertDeliveryService> logger) : BackgroundService
{
    /// <summary>Unsent alerts older than this at startup are marked Failed rather than emailed late.</summary>
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromHours(1);

    public const string StaleError = "Not emailed: the app stopped before this alert could be sent.";

    public const string InterruptedError =
        "Unknown: the app stopped (or hit a database error) while sending this email, so it may or may not have been sent.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverPendingAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await queue.DequeueBatchAsync(stoppingToken);
                if (batch.Count == 0) continue;

                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IAlertService>().DeliverAsync(batch, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Alert delivery failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is shutting down; anything unsent stays Pending and is picked up on next start.
        }
    }

    /// <summary>
    /// At startup: alerts left Sending may already have been emailed, so they're marked Failed (never sent twice).
    /// Recent Pending alerts are re-queued; older ones are marked Failed so stale alerts aren't emailed hours late.
    /// </summary>
    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - MaxPendingAge;

        await db.AlertEvents
            .Where(e => e.Delivery == AlertDelivery.Sending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Delivery, AlertDelivery.Failed)
                .SetProperty(e => e.Error, InterruptedError), cancellationToken);

        await db.AlertEvents
            .Where(e => e.Delivery == AlertDelivery.Pending && e.OccurredAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Delivery, AlertDelivery.Failed)
                .SetProperty(e => e.Error, StaleError), cancellationToken);

        var pending = await db.AlertEvents
            .Where(e => e.Delivery == AlertDelivery.Pending)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        if (pending.Count > 0)
        {
            logger.LogInformation("Re-queued {Count} alerts that were not emailed before the last shutdown", pending.Count);
            queue.Enqueue(pending);
        }
    }
}

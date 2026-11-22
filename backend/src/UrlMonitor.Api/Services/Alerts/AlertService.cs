using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Models;

namespace UrlMonitor.Api.Services.Alerts;

/// <summary>An alert raised by a check, waiting to be saved and delivered.</summary>
public record RaisedAlert(AlertEvent Event, MonitoredUrl Url, UrlCheck Check);

public interface IAlertService
{
    /// <summary>
    /// Updates each URL's failure count and state from its new check, and adds an
    /// <see cref="AlertEvent"/> to the DbContext for every state change. Does not save.
    /// </summary>
    Task<IReadOnlyList<RaisedAlert>> EvaluateAsync(
        IReadOnlyList<(MonitoredUrl Url, UrlCheck Check)> results, CancellationToken cancellationToken = default);

    /// <summary>
    /// Emails the Pending alerts (call after the checks and events are saved) and saves each one's outcome.
    /// Never throws for email problems: failures are recorded on the event.
    /// </summary>
    Task DeliverAsync(IReadOnlyList<RaisedAlert> alerts, CancellationToken cancellationToken = default);
}

public class AlertService(
    MonitorDbContext db,
    IEmailSender emailSender,
    IOptions<AlertsOptions> options,
    TimeProvider timeProvider,
    ILogger<AlertService> logger) : IAlertService
{
    private const int MaxErrorLength = 1000;

    private readonly AlertsOptions _options = options.Value;

    /// <summary>Time zone for the "local" time in emails. Overridable for tests.</summary>
    public TimeZoneInfo LocalZone { get; init; } = TimeZoneInfo.Local;

    public async Task<IReadOnlyList<RaisedAlert>> EvaluateAsync(
        IReadOnlyList<(MonitoredUrl Url, UrlCheck Check)> results, CancellationToken cancellationToken = default)
    {
        var raised = new List<(MonitoredUrl Url, UrlCheck Check, AlertKind Kind)>();
        foreach (var (url, check) in results)
        {
            if (AlertRules.ApplyCheck(url, check.IsUp, _options.FailureThreshold) is { } kind)
            {
                raised.Add((url, check, kind));
            }
        }

        if (raised.Count == 0) return [];

        // URLs that already had an email sent within the cooldown window.
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - _options.Cooldown;
        var ids = raised.Select(r => r.Url.Id).ToList();
        var inCooldown = (await db.AlertEvents
                .Where(e => ids.Contains(e.MonitoredUrlId)
                            && e.Delivery == AlertDelivery.Sent
                            && e.OccurredAt > cutoff)
                .Select(e => e.MonitoredUrlId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var alerts = new List<RaisedAlert>(raised.Count);
        foreach (var (url, check, kind) in raised)
        {
            var alertEvent = new AlertEvent
            {
                MonitoredUrlId = url.Id,
                OccurredAt = check.CheckedAt,
                Kind = kind,
                Delivery = AlertRules.InitialDelivery(kind, inCooldown.Contains(url.Id), _options),
            };
            db.AlertEvents.Add(alertEvent);
            alerts.Add(new RaisedAlert(alertEvent, url, check));

            logger.LogInformation("{Url} is {Kind} ({Delivery})", url.Url, kind, alertEvent.Delivery);
        }

        return alerts;
    }

    public async Task DeliverAsync(IReadOnlyList<RaisedAlert> alerts, CancellationToken cancellationToken = default)
    {
        var pending = alerts.Where(a => a.Event.Delivery == AlertDelivery.Pending).ToList();
        if (pending.Count == 0) return;

        try
        {
            var recipients = await db.AlertRecipients
                .OrderBy(r => r.Id)
                .Select(r => r.Email)
                .ToListAsync(cancellationToken);

            foreach (var alert in pending)
            {
                if (recipients.Count == 0)
                {
                    alert.Event.Delivery = AlertDelivery.NoRecipients;
                    continue;
                }

                await SendOneAsync(alert, recipients, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: unsent alerts stay Pending.
        }
        catch (Exception ex)
        {
            // E.g. the database is locked. The checks are already saved; don't fail the run.
            logger.LogError(ex, "Failed to deliver alerts");
        }
    }

    private async Task SendOneAsync(RaisedAlert alert, List<string> recipients, CancellationToken cancellationToken)
    {
        try
        {
            var downSince = alert.Event.Kind == AlertKind.Recovered
                ? await FindDownSinceAsync(alert, cancellationToken)
                : null;

            var email = AlertEmailComposer.Compose(alert.Event.Kind, alert.Url, alert.Check, downSince, LocalZone);
            await emailSender.SendAsync(recipients, email.Subject, email.Body, cancellationToken);

            alert.Event.Delivery = AlertDelivery.Sent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            alert.Event.Delivery = AlertDelivery.Failed;
            alert.Event.Error = ex.Message.Length <= MaxErrorLength ? ex.Message : ex.Message[..MaxErrorLength];
            logger.LogWarning(ex, "Failed to send {Kind} alert for {Url}", alert.Event.Kind, alert.Url.Url);
        }
    }

    /// <summary>When the outage that just ended started: the latest Down event before this recovery.</summary>
    private Task<DateTime?> FindDownSinceAsync(RaisedAlert alert, CancellationToken cancellationToken) =>
        db.AlertEvents
            .Where(e => e.MonitoredUrlId == alert.Url.Id
                        && e.Kind == AlertKind.Down
                        && e.OccurredAt <= alert.Event.OccurredAt)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => (DateTime?)e.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
}

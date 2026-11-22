using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Models;

namespace UrlMonitor.Api.Services.Alerts;

/// <summary>An alert raised by a check (not yet saved).</summary>
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
    /// Emails the given alert events that are still Pending, and saves each one's outcome.
    /// Never throws for email problems: failures are recorded on the event.
    /// </summary>
    Task DeliverAsync(IReadOnlyCollection<long> alertEventIds, CancellationToken cancellationToken = default);
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

        // URLs with an email sent, queued or being sent within the cooldown window. Pending and Sending count
        // too: emails go out in the background, so a recent alert may not be marked Sent yet.
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - _options.Cooldown;
        var ids = raised.Select(r => r.Url.Id).ToList();
        var inCooldown = (await db.AlertEvents
                .Where(e => ids.Contains(e.MonitoredUrlId)
                            && (e.Delivery == AlertDelivery.Sent
                                || e.Delivery == AlertDelivery.Pending
                                || e.Delivery == AlertDelivery.Sending)
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

    public async Task DeliverAsync(IReadOnlyCollection<long> alertEventIds, CancellationToken cancellationToken = default)
    {
        if (alertEventIds.Count == 0) return;

        // Only still-Pending events: an id can be queued twice (e.g. re-queued at startup).
        var events = await db.AlertEvents
            .Include(e => e.MonitoredUrl)
            .Where(e => alertEventIds.Contains(e.Id) && e.Delivery == AlertDelivery.Pending)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken);
        if (events.Count == 0) return;

        var recipients = await db.AlertRecipients
            .OrderBy(r => r.Id)
            .Select(r => r.Email)
            .ToListAsync(cancellationToken);

        if (recipients.Count == 0)
        {
            foreach (var alertEvent in events) alertEvent.Delivery = AlertDelivery.NoRecipients;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        foreach (var alertEvent in events)
        {
            // Saved before contacting SMTP: if this fails, nothing was sent and the alert stays Pending.
            // If the outcome below can't be saved, the alert stays Sending and is marked Failed at the next
            // start (AlertDeliveryService) rather than emailed a second time.
            alertEvent.Delivery = AlertDelivery.Sending;
            await db.SaveChangesAsync(cancellationToken);

            await SendOneAsync(alertEvent, recipients, cancellationToken);
            await SaveOutcomeAsync(alertEvent);
        }
    }

    /// <summary>
    /// Records Sent/Failed right after each email, even during shutdown, so earlier outcomes aren't lost if a
    /// later one fails. On a database error the change stays pending in the DbContext and is retried by the next save.
    /// </summary>
    private async Task SaveOutcomeAsync(AlertEvent alertEvent)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Could not record that alert {AlertId} was {Delivery}; it stays Sending and will be marked Failed at the next start, not emailed again",
                alertEvent.Id, alertEvent.Delivery);
        }
    }

    private async Task SendOneAsync(AlertEvent alertEvent, List<string> recipients, CancellationToken cancellationToken)
    {
        var url = alertEvent.MonitoredUrl!;
        try
        {
            var check = await FindCheckAsync(alertEvent, cancellationToken);
            var downSince = alertEvent.Kind == AlertKind.Recovered
                ? await FindDownSinceAsync(alertEvent, cancellationToken)
                : null;

            var email = AlertEmailComposer.Compose(alertEvent.Kind, url, check, downSince, LocalZone);
            await emailSender.SendAsync(recipients, email.Subject, email.Body, cancellationToken);

            alertEvent.Delivery = AlertDelivery.Sent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            alertEvent.Delivery = AlertDelivery.Failed;
            alertEvent.Error = ex.Message.Length <= MaxErrorLength ? ex.Message : ex.Message[..MaxErrorLength];
            logger.LogWarning(ex, "Failed to send {Kind} alert for {Url}", alertEvent.Kind, url.Url);
        }
    }

    /// <summary>The check that raised the alert (an alert's time is its check's time).</summary>
    private async Task<UrlCheck> FindCheckAsync(AlertEvent alertEvent, CancellationToken cancellationToken) =>
        await db.UrlChecks
            .AsNoTracking()
            .Where(c => c.MonitoredUrlId == alertEvent.MonitoredUrlId && c.CheckedAt == alertEvent.OccurredAt)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken)
        ?? new UrlCheck { MonitoredUrlId = alertEvent.MonitoredUrlId, CheckedAt = alertEvent.OccurredAt };

    /// <summary>When the outage that just ended started: the latest Down event before this recovery.</summary>
    private Task<DateTime?> FindDownSinceAsync(AlertEvent alertEvent, CancellationToken cancellationToken) =>
        db.AlertEvents
            .Where(e => e.MonitoredUrlId == alertEvent.MonitoredUrlId
                        && e.Kind == AlertKind.Down
                        && e.OccurredAt <= alertEvent.OccurredAt)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => (DateTime?)e.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
}

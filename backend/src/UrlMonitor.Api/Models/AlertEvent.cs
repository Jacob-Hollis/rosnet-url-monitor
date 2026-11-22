namespace UrlMonitor.Api.Models;

/// <summary>A state change of a URL (went down / recovered) and what happened to its email.</summary>
public class AlertEvent
{
    public long Id { get; set; }

    public int MonitoredUrlId { get; set; }

    public MonitoredUrl? MonitoredUrl { get; set; }

    public DateTime OccurredAt { get; set; }

    public AlertKind Kind { get; set; }

    public AlertDelivery Delivery { get; set; }

    /// <summary>SMTP error message when <see cref="Delivery"/> is <see cref="AlertDelivery.Failed"/>.</summary>
    public string? Error { get; set; }
}

public enum AlertKind
{
    Down,
    Recovered,
}

public enum AlertDelivery
{
    /// <summary>Saved and queued; the email hasn't been attempted yet.</summary>
    Pending,

    /// <summary>
    /// The email is being sent right now. Saved before contacting the SMTP server, so if the outcome can't be
    /// recorded (app stopped, database error) the alert is marked Failed at the next start, never emailed twice.
    /// </summary>
    Sending,

    /// <summary>Email sent to all recipients.</summary>
    Sent,

    /// <summary>Sending failed; see <see cref="AlertEvent.Error"/>.</summary>
    Failed,

    /// <summary>Not sent: the URL is within its alert cooldown, or recovery emails are turned off.</summary>
    Suppressed,

    /// <summary>Not sent because no recipients are configured.</summary>
    NoRecipients,
}

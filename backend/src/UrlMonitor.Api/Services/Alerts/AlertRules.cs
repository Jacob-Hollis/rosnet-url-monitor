using UrlMonitor.Api.Models;

namespace UrlMonitor.Api.Services.Alerts;

/// <summary>
/// The alert state machine (design.md section 3). Pure functions, so every rule is unit-testable.
/// </summary>
public static class AlertRules
{
    /// <summary>
    /// Updates <paramref name="url"/>'s failure count and state for one check result,
    /// and returns the alert to raise, if the state changed.
    /// <list type="bullet">
    /// <item>Up check: failures reset; Down → Up raises Recovered; Unknown → Up is silent.</item>
    /// <item>Failed check: failures + 1; reaching the threshold from Up or Unknown raises Down.
    /// A URL that stays down raises nothing more.</item>
    /// </list>
    /// </summary>
    public static AlertKind? ApplyCheck(MonitoredUrl url, bool isUp, int failureThreshold)
    {
        if (isUp)
        {
            url.ConsecutiveFailures = 0;
            var previous = url.CurrentState;
            url.CurrentState = UrlState.Up;
            return previous == UrlState.Down ? AlertKind.Recovered : null;
        }

        url.ConsecutiveFailures++;
        if (url.CurrentState != UrlState.Down && url.ConsecutiveFailures >= Math.Max(1, failureThreshold))
        {
            url.CurrentState = UrlState.Down;
            return AlertKind.Down;
        }

        return null;
    }

    /// <summary>
    /// What to do with a new alert's email: Pending (send it) or Suppressed (record only).
    /// </summary>
    /// <param name="emailSentWithinCooldown">An email for this URL was sent within the cooldown window.</param>
    public static AlertDelivery InitialDelivery(AlertKind kind, bool emailSentWithinCooldown, AlertsOptions options)
    {
        if (kind == AlertKind.Recovered && !options.SendRecoveryEmails) return AlertDelivery.Suppressed;
        if (emailSentWithinCooldown) return AlertDelivery.Suppressed;
        return AlertDelivery.Pending;
    }
}

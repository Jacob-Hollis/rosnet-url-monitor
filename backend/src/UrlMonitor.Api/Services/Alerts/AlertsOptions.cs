using MailKit.Security;

namespace UrlMonitor.Api.Services.Alerts;

/// <summary>Settings from the "Alerts" section of appsettings.json.</summary>
public class AlertsOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Consecutive failed checks before a URL counts as down and a DOWN alert fires.</summary>
    public int FailureThreshold { get; set; } = 2;

    /// <summary>At most one email per URL within this many minutes; alerts inside the window are recorded as Suppressed.</summary>
    public double CooldownMinutes { get; set; } = 30;

    public bool SendRecoveryEmails { get; set; } = true;

    public TimeSpan Cooldown => TimeSpan.FromMinutes(CooldownMinutes);
}

/// <summary>Settings from the "Smtp" section. Defaults point at a local smtp4dev.</summary>
public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 25;

    /// <summary>None (smtp4dev), StartTls (port 587), SslOnConnect (port 465), or Auto.</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.None;

    /// <summary>Leave empty for no authentication. Real credentials belong in user-secrets/env vars, never in git.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = "url-monitor@localhost";

    public double TimeoutSeconds { get; set; } = 10;
}

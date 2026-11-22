namespace UrlMonitor.Api.Services.Alerts;

public interface IEmailSender
{
    /// <summary>Sends one email to all <paramref name="recipients"/> (as BCC, so they don't see each other).</summary>
    Task SendAsync(IReadOnlyCollection<string> recipients, string subject, string body, CancellationToken cancellationToken = default);
}

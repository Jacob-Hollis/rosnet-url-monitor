using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace UrlMonitor.Api.Services.Alerts;

/// <summary>Sends email over SMTP with MailKit. Locally this goes to smtp4dev.</summary>
public class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(
        IReadOnlyCollection<string> recipients, string subject, string body, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.From));
        // Recipients go in BCC; the visible To is the sender itself.
        message.To.Add(MailboxAddress.Parse(settings.From));
        foreach (var recipient in recipients)
        {
            message.Bcc.Add(MailboxAddress.Parse(recipient));
        }
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        // Short timeout: a slow or unreachable SMTP server must not hold up the check run.
        using var client = new SmtpClient { Timeout = (int)TimeSpan.FromSeconds(settings.TimeoutSeconds).TotalMilliseconds };
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security, cancellationToken);
        if (!string.IsNullOrEmpty(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? "", cancellationToken);
        }
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}

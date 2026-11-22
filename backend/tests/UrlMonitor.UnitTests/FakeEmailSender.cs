using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.UnitTests;

/// <summary>Records sent emails instead of sending them; can be told to fail.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public record SentEmail(IReadOnlyCollection<string> Recipients, string Subject, string Body);

    public List<SentEmail> Sent { get; } = [];

    /// <summary>When set, SendAsync throws this instead of recording.</summary>
    public Exception? FailWith { get; set; }

    public Task SendAsync(IReadOnlyCollection<string> recipients, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        Sent.Add(new SentEmail(recipients.ToList(), subject, body));
        return Task.CompletedTask;
    }
}

namespace UrlMonitor.Api.Models;

/// <summary>An email address that receives alerts for all URLs.</summary>
public class AlertRecipient
{
    public int Id { get; set; }

    /// <summary>Stored trimmed and lower-cased so the unique index is case-insensitive.</summary>
    public required string Email { get; set; }

    public DateTime CreatedAt { get; set; }
}

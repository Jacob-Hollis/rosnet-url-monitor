using UrlMonitor.Api.Models;

namespace UrlMonitor.Api.Dtos;

public record CreateRecipientRequest(string? Email);

public record RecipientDto(int Id, string Email, DateTime CreatedAt);

public record AlertEventDto(
    long Id,
    int MonitoredUrlId,
    string Url,
    string? UrlName,
    DateTime OccurredAt,
    AlertKind Kind,
    AlertDelivery Delivery,
    string? Error);

public record TestEmailResultDto(int RecipientCount);

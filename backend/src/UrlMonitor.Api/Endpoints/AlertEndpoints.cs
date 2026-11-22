using Microsoft.EntityFrameworkCore;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Dtos;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.Api.Endpoints;

/// <summary>/api/alerts: alert recipients, test email and recent alert history.</summary>
public static class AlertEndpoints
{
    public const int DefaultEventLimit = 50;
    public const int MaxEventLimit = 500;

    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alerts").WithTags("Alerts");

        group.MapGet("/recipients", ListRecipientsAsync).WithSummary("Saved alert email addresses");
        group.MapPost("/recipients", CreateRecipientAsync).WithSummary("Add an alert email address");
        group.MapDelete("/recipients/{id:int}", DeleteRecipientAsync).WithSummary("Remove an alert email address");
        group.MapPost("/test", SendTestAsync).WithSummary("Send a test email to all recipients");
        group.MapGet("/events", ListEventsAsync).WithSummary("Recent alerts, newest first");

        return app;
    }

    private static async Task<IResult> ListRecipientsAsync(MonitorDbContext db, CancellationToken ct)
    {
        var recipients = await db.AlertRecipients
            .AsNoTracking()
            .OrderBy(r => r.Id)
            .Select(r => new RecipientDto(r.Id, r.Email, r.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(recipients);
    }

    private static async Task<IResult> CreateRecipientAsync(
        CreateRecipientRequest request, MonitorDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!InputValidation.TryNormalizeEmail(request.Email, out var email, out var error))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["email"] = [error] });
        }

        if (await db.AlertRecipients.AnyAsync(r => r.Email == email, ct)) return Duplicate(email);

        var recipient = new AlertRecipient { Email = email, CreatedAt = time.GetUtcNow().UtcDateTime };
        db.AlertRecipients.Add(recipient);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Duplicate(email);
        }

        return TypedResults.Created(
            $"/api/alerts/recipients/{recipient.Id}",
            new RecipientDto(recipient.Id, recipient.Email, recipient.CreatedAt));
    }

    private static async Task<IResult> DeleteRecipientAsync(int id, MonitorDbContext db, CancellationToken ct)
    {
        var deleted = await db.AlertRecipients.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0
            ? TypedResults.Problem($"Recipient {id} not found.", statusCode: StatusCodes.Status404NotFound)
            : TypedResults.NoContent();
    }

    private static async Task<IResult> SendTestAsync(
        MonitorDbContext db, IEmailSender sender, TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var recipients = await db.AlertRecipients.OrderBy(r => r.Id).Select(r => r.Email).ToListAsync(ct);
        if (recipients.Count == 0)
        {
            return TypedResults.Problem(
                "Add at least one email address before sending a test.", statusCode: StatusCodes.Status400BadRequest);
        }

        var email = AlertEmailComposer.ComposeTest(time.GetUtcNow().UtcDateTime, TimeZoneInfo.Local);
        try
        {
            await sender.SendAsync(recipients, email.Subject, email.Body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger(typeof(AlertEndpoints)).LogWarning(ex, "Test email failed");
            return TypedResults.Problem(
                $"Could not send email: {ex.Message}",
                title: "Email failed",
                statusCode: StatusCodes.Status502BadGateway);
        }

        return TypedResults.Ok(new TestEmailResultDto(recipients.Count));
    }

    private static async Task<IResult> ListEventsAsync(int? limit, MonitorDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultEventLimit, 1, MaxEventLimit);
        var events = await db.AlertEvents
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(take)
            .Select(e => new AlertEventDto(
                e.Id, e.MonitoredUrlId, e.MonitoredUrl!.Url, e.MonitoredUrl.Name,
                e.OccurredAt, e.Kind, e.Delivery, e.Error))
            .ToListAsync(ct);
        return TypedResults.Ok(events);
    }

    private static IResult Duplicate(string email) =>
        TypedResults.Problem($"{email} is already on the list.", statusCode: StatusCodes.Status409Conflict);
}

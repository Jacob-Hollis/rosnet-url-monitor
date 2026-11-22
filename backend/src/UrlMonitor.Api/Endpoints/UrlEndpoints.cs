using Microsoft.EntityFrameworkCore;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Dtos;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services;

namespace UrlMonitor.Api.Endpoints;

/// <summary>/api/urls: list, get, add and delete monitored URLs.</summary>
public static class UrlEndpoints
{
    public static IEndpointRouteBuilder MapUrlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/urls").WithTags("URLs");

        group.MapGet("/", ListAsync).WithSummary("All URLs with their latest check (dashboard)");
        group.MapGet("/{id:int}", GetAsync).WithSummary("One URL with its latest check");
        group.MapPost("/", CreateAsync).WithSummary("Add a URL");
        group.MapDelete("/{id:int}", DeleteAsync).WithSummary("Delete a URL and its history");

        return app;
    }

    private static async Task<IResult> ListAsync(MonitorDbContext db, TimeProvider time, CancellationToken ct)
    {
        var summaries = await QuerySummaries(db, time).ToListAsync(ct);
        return TypedResults.Ok(summaries.Select(ToDto));
    }

    private static async Task<IResult> GetAsync(int id, MonitorDbContext db, TimeProvider time, CancellationToken ct)
    {
        var summary = await QuerySummaries(db, time, id).FirstOrDefaultAsync(ct);
        return summary is null ? NotFound(id) : TypedResults.Ok(ToDto(summary));
    }

    private static async Task<IResult> CreateAsync(
        CreateUrlRequest request, MonitorDbContext db, TimeProvider time, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!InputValidation.TryNormalizeUrl(request.Url, out var url, out var urlError)) errors["url"] = [urlError];
        if (!InputValidation.TryNormalizeName(request.Name, out var name, out var nameError)) errors["name"] = [nameError];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        if (await db.MonitoredUrls.AnyAsync(u => u.Url == url, ct)) return Duplicate(url);

        var entity = new MonitoredUrl { Url = url, Name = name, CreatedAt = time.GetUtcNow().UtcDateTime };
        db.MonitoredUrls.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another request adding the same URL (unique index).
            return Duplicate(url);
        }

        var dto = ToDto(new Summary(entity, null, 0, 0));
        return TypedResults.Created($"/api/urls/{entity.Id}", dto);
    }

    private static async Task<IResult> DeleteAsync(int id, MonitorDbContext db, CancellationToken ct)
    {
        // Hard delete; the database cascades to checks and alert events (design.md questions 15 and 28).
        var deleted = await db.MonitoredUrls.Where(u => u.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound(id) : TypedResults.NoContent();
    }

    private sealed record Summary(MonitoredUrl Url, UrlCheck? Latest, int Checks24h, int Up24h);

    /// <param name="id">Only this URL. Filtering must happen before the projection, which EF can't filter on.</param>
    private static IQueryable<Summary> QuerySummaries(MonitorDbContext db, TimeProvider time, int? id = null)
    {
        var since = time.GetUtcNow().UtcDateTime.AddHours(-24);
        var urls = db.MonitoredUrls.AsNoTracking();
        if (id is not null) urls = urls.Where(u => u.Id == id);

        return urls
            .OrderBy(u => u.Id)
            .Select(u => new Summary(
                u,
                u.Checks.OrderByDescending(c => c.CheckedAt).FirstOrDefault(),
                u.Checks.Count(c => c.CheckedAt >= since),
                u.Checks.Count(c => c.CheckedAt >= since && c.IsUp)));
    }

    private static UrlSummaryDto ToDto(Summary s) => new(
        s.Url.Id,
        s.Url.Url,
        s.Url.Name,
        s.Url.CreatedAt,
        s.Url.IsActive,
        s.Url.CurrentState,
        s.Latest is null ? null : CheckEndpoints.ToDto(s.Latest),
        s.Checks24h == 0 ? null : Math.Round(100.0 * s.Up24h / s.Checks24h, 1));

    internal static IResult NotFound(int id) =>
        TypedResults.Problem($"URL {id} not found.", statusCode: StatusCodes.Status404NotFound);

    private static IResult Duplicate(string url) =>
        TypedResults.Problem($"{url} is already being monitored.", statusCode: StatusCodes.Status409Conflict);
}

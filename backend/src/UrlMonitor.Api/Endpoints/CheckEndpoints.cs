using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Dtos;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services;

namespace UrlMonitor.Api.Endpoints;

/// <summary>/api/urls/{id}/checks, /stats and /check: history, chart data and "check now".</summary>
public static class CheckEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    public static IEndpointRouteBuilder MapCheckEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/urls/{id:int}").WithTags("Checks");

        group.MapGet("/checks", ListChecksAsync).WithSummary("Check history, newest first, paged");
        group.MapGet("/stats", GetStatsAsync).WithSummary("Uptime, response times and chart series for a range (1h, 6h, 24h, 7d, 30d)");
        group.MapPost("/check", CheckNowAsync).WithSummary("Check this URL now");

        return app;
    }

    private static async Task<IResult> ListChecksAsync(
        int id,
        DateTime? from,
        DateTime? to,
        int? page,
        int? pageSize,
        MonitorDbContext db,
        IOptions<MonitorOptions> options,
        CancellationToken ct)
    {
        if (!await db.MonitoredUrls.AnyAsync(u => u.Id == id, ct)) return UrlEndpoints.NotFound(id);

        var pageNumber = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

        var query = db.UrlChecks.AsNoTracking().Where(c => c.MonitoredUrlId == id);
        if (from is { } f) query = query.Where(c => c.CheckedAt >= f.ToUniversalTime());
        if (to is { } t) query = query.Where(c => c.CheckedAt <= t.ToUniversalTime());

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(c => c.CheckedAt)
            .ThenByDescending(c => c.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return TypedResults.Ok(new CheckPageDto(
            items.Select(ToDto).ToList(),
            pageNumber,
            size,
            total,
            (int)GapThreshold(options.Value).TotalSeconds));
    }

    private static async Task<IResult> GetStatsAsync(
        int id,
        string? range,
        MonitorDbContext db,
        IOptions<MonitorOptions> options,
        TimeProvider time,
        CancellationToken ct)
    {
        var statsRange = StatsRange.Parse(range);
        if (statsRange is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["range"] = [$"Range must be one of: {string.Join(", ", StatsRange.All.Select(r => r.Name))}."],
            });
        }

        if (!await db.MonitoredUrls.AnyAsync(u => u.Id == id, ct)) return UrlEndpoints.NotFound(id);

        var to = time.GetUtcNow().UtcDateTime;
        var from = to - statsRange.Length;

        var checks = await db.UrlChecks
            .AsNoTracking()
            .Where(c => c.MonitoredUrlId == id && c.CheckedAt >= from && c.CheckedAt <= to)
            .OrderBy(c => c.CheckedAt)
            .Select(c => new CheckPoint(c.CheckedAt, c.IsUp, c.ResponseTimeMs, c.StatusCode))
            .ToListAsync(ct);

        return TypedResults.Ok(HistoryStats.Build(checks, statsRange, from, to, GapThreshold(options.Value)));
    }

    private static async Task<IResult> CheckNowAsync(int id, ICheckRunner runner, CancellationToken ct)
    {
        var check = await runner.RunOneAsync(id, ct);
        return check is null ? UrlEndpoints.NotFound(id) : TypedResults.Ok(ToDto(check));
    }

    /// <summary>Checks further apart than twice the interval mean the monitor wasn't running (design.md question 10).</summary>
    public static TimeSpan GapThreshold(MonitorOptions options) => options.CheckInterval * 2;

    internal static CheckDto ToDto(UrlCheck c) =>
        new(c.Id, c.CheckedAt, c.StatusCode, c.ResponseTimeMs, c.IsUp, c.Error);
}

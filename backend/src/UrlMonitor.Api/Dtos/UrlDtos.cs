using UrlMonitor.Api.Models;

namespace UrlMonitor.Api.Dtos;

public record CreateUrlRequest(string? Url, string? Name);

public record CheckDto(
    long Id,
    DateTime CheckedAt,
    int? StatusCode,
    int ResponseTimeMs,
    bool IsUp,
    string? Error);

/// <summary>A URL with its most recent check, for the dashboard.</summary>
public record UrlSummaryDto(
    int Id,
    string Url,
    string? Name,
    DateTime CreatedAt,
    bool IsActive,
    UrlState CurrentState,
    CheckDto? LatestCheck,
    // Share of checks in the last 24 hours that were up; null with no checks.
    double? Uptime24hPercent);

public record CheckPageDto(
    IReadOnlyList<CheckDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    // Two checks further apart than this have a gap (monitor not running) between them.
    int GapThresholdSeconds);

/// <summary>
/// One point on the history chart. For short ranges each point is one check; for 7d/30d it is a bucket.
/// A point with <see cref="Checks"/> = 0 is a gap (no data): the chart breaks its line there.
/// </summary>
public record SeriesPointDto(
    DateTime Time,
    double? UptimePercent,
    double? AvgResponseMs,
    int Checks,
    int? StatusCode);

public record StatsDto(
    string Range,
    DateTime From,
    DateTime To,
    // Bucket size in seconds; null when every check is its own point.
    int? BucketSeconds,
    int TotalChecks,
    int UpChecks,
    double? UptimePercent,
    double? AvgResponseMs,
    int? P95ResponseMs,
    IReadOnlyList<SeriesPointDto> Series);

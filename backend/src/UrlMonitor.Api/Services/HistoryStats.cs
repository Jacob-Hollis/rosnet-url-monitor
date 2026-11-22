using UrlMonitor.Api.Dtos;

namespace UrlMonitor.Api.Services;

/// <summary>The fields of a check needed for stats.</summary>
public record CheckPoint(DateTime CheckedAt, bool IsUp, int ResponseTimeMs, int? StatusCode);

/// <summary>A chart range: how far back, and the bucket size (null = one point per check).</summary>
public record StatsRange(string Name, TimeSpan Length, TimeSpan? Bucket)
{
    public static readonly StatsRange Default = new("24h", TimeSpan.FromHours(24), null);

    public static readonly IReadOnlyList<StatsRange> All =
    [
        new("1h", TimeSpan.FromHours(1), null),
        new("6h", TimeSpan.FromHours(6), null),
        Default,
        new("7d", TimeSpan.FromDays(7), TimeSpan.FromHours(1)),
        new("30d", TimeSpan.FromDays(30), TimeSpan.FromHours(6)),
    ];

    public static StatsRange? Parse(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? Default
            : All.FirstOrDefault(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Uptime and response time stats plus the chart series (design.md questions 4, 5 and 10).</summary>
public static class HistoryStats
{
    /// <param name="checks">Checks in [from, to], oldest first.</param>
    /// <param name="gapThreshold">Checks further apart than this have a gap point inserted between them.</param>
    public static StatsDto Build(
        IReadOnlyList<CheckPoint> checks, StatsRange range, DateTime from, DateTime to, TimeSpan gapThreshold)
    {
        var total = checks.Count;
        var up = checks.Count(c => c.IsUp);

        // Response time only counts checks that got a response, so a 10s timeout doesn't skew the average.
        var responded = checks.Where(c => c.StatusCode is not null).Select(c => c.ResponseTimeMs).ToList();

        var series = range.Bucket is { } bucket
            ? BucketSeries(checks, from, to, bucket)
            : RawSeries(checks, gapThreshold);

        return new StatsDto(
            range.Name,
            from,
            to,
            range.Bucket is { } b ? (int)b.TotalSeconds : null,
            total,
            up,
            total == 0 ? null : Round(100.0 * up / total),
            responded.Count == 0 ? null : Round(responded.Average()),
            Percentile95(responded),
            series);
    }

    /// <summary>One point per check, with a gap point wherever the monitor wasn't running.</summary>
    private static List<SeriesPointDto> RawSeries(IReadOnlyList<CheckPoint> checks, TimeSpan gapThreshold)
    {
        var series = new List<SeriesPointDto>(checks.Count);
        for (var i = 0; i < checks.Count; i++)
        {
            var c = checks[i];
            if (i > 0 && c.CheckedAt - checks[i - 1].CheckedAt > gapThreshold)
            {
                var midpoint = checks[i - 1].CheckedAt + (c.CheckedAt - checks[i - 1].CheckedAt) / 2;
                series.Add(Gap(midpoint));
            }

            series.Add(new SeriesPointDto(
                c.CheckedAt,
                c.IsUp ? 100 : 0,
                c.StatusCode is null ? null : c.ResponseTimeMs,
                1,
                c.StatusCode));
        }
        return series;
    }

    /// <summary>Fixed-size buckets from <paramref name="from"/> to <paramref name="to"/>; an empty bucket is a gap.</summary>
    private static List<SeriesPointDto> BucketSeries(IReadOnlyList<CheckPoint> checks, DateTime from, DateTime to, TimeSpan bucket)
    {
        // Align buckets to whole multiples of the bucket size (e.g. on the hour) so they don't shift on every refresh.
        var start = new DateTime(from.Ticks - from.Ticks % bucket.Ticks, DateTimeKind.Utc);
        var count = (int)Math.Ceiling((to - start) / bucket);
        var groups = checks
            .GroupBy(c => (int)((c.CheckedAt - start) / bucket))
            .ToDictionary(g => g.Key, g => g.ToList());

        var series = new List<SeriesPointDto>(count);
        for (var i = 0; i < count; i++)
        {
            var time = start + bucket * i;
            if (!groups.TryGetValue(i, out var inBucket))
            {
                series.Add(Gap(time));
                continue;
            }

            var responded = inBucket.Where(c => c.StatusCode is not null).ToList();
            series.Add(new SeriesPointDto(
                time,
                Round(100.0 * inBucket.Count(c => c.IsUp) / inBucket.Count),
                responded.Count == 0 ? null : Round(responded.Average(c => c.ResponseTimeMs)),
                inBucket.Count,
                null));
        }
        return series;
    }

    private static SeriesPointDto Gap(DateTime time) => new(time, null, null, 0, null);

    /// <summary>Nearest-rank 95th percentile.</summary>
    private static int? Percentile95(List<int> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        var rank = (int)Math.Ceiling(0.95 * values.Count);
        return values[Math.Clamp(rank - 1, 0, values.Count - 1)];
    }

    private static double Round(double value) => Math.Round(value, 1);
}

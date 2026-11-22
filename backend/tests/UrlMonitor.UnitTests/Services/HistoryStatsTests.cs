using UrlMonitor.Api.Services;

namespace UrlMonitor.UnitTests.Services;

public class HistoryStatsTests
{
    private static readonly DateTime To = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);

    private static StatsRange Range(string name) => StatsRange.Parse(name)!;

    private static CheckPoint Up(DateTime at, int ms = 100) => new(at, true, ms, 200);
    private static CheckPoint Down(DateTime at, int ms = 100, int? status = 500) => new(at, false, ms, status);

    [Theory]
    [InlineData(null, "24h")]
    [InlineData("", "24h")]
    [InlineData("1h", "1h")]
    [InlineData("7D", "7d")]
    [InlineData(" 30d ", "30d")]
    public void Parses_ranges(string? input, string expected) => Assert.Equal(expected, StatsRange.Parse(input)!.Name);

    [Theory]
    [InlineData("2h")]
    [InlineData("week")]
    public void Rejects_unknown_ranges(string input) => Assert.Null(StatsRange.Parse(input));

    [Fact]
    public void Empty_history_has_null_stats()
    {
        var stats = HistoryStats.Build([], Range("24h"), To.AddHours(-24), To, Gap);

        Assert.Equal(0, stats.TotalChecks);
        Assert.Null(stats.UptimePercent);
        Assert.Null(stats.AvgResponseMs);
        Assert.Null(stats.P95ResponseMs);
        Assert.Empty(stats.Series);
    }

    [Fact]
    public void Uptime_is_share_of_checks_up()
    {
        var start = To.AddHours(-1);
        var checks = Enumerable.Range(0, 4)
            .Select(i => i == 2 ? Down(start.AddMinutes(5 * i)) : Up(start.AddMinutes(5 * i)))
            .ToList();

        var stats = HistoryStats.Build(checks, Range("1h"), start, To, Gap);

        Assert.Equal(4, stats.TotalChecks);
        Assert.Equal(3, stats.UpChecks);
        Assert.Equal(75.0, stats.UptimePercent);
    }

    [Fact]
    public void Response_time_ignores_checks_with_no_response()
    {
        var start = To.AddHours(-1);
        CheckPoint[] checks =
        [
            Up(start, 100),
            Up(start.AddMinutes(5), 300),
            Down(start.AddMinutes(10), 10_000, status: null), // timeout
        ];

        var stats = HistoryStats.Build(checks, Range("1h"), start, To, Gap);

        Assert.Equal(200.0, stats.AvgResponseMs);
        Assert.Equal(300, stats.P95ResponseMs);
        Assert.Null(stats.Series[2].AvgResponseMs);
        Assert.Equal(0, stats.Series[2].UptimePercent);
    }

    [Fact]
    public void P95_uses_nearest_rank()
    {
        var start = To.AddHours(-6);
        var checks = Enumerable.Range(1, 20).Select(i => Up(start.AddMinutes(5 * i), ms: i * 10)).ToList();

        var stats = HistoryStats.Build(checks, Range("6h"), start, To, Gap);

        Assert.Equal(190, stats.P95ResponseMs); // 19th of 20 values
    }

    [Fact]
    public void Raw_series_has_one_point_per_check_and_gap_points_where_monitor_was_off()
    {
        var start = To.AddHours(-1);
        CheckPoint[] checks =
        [
            Up(start),
            Up(start.AddMinutes(5)),
            Up(start.AddMinutes(35)), // 30 minute gap: app was stopped
            Down(start.AddMinutes(40)),
        ];

        var stats = HistoryStats.Build(checks, Range("1h"), start, To, Gap);

        Assert.Null(stats.BucketSeconds);
        Assert.Equal(5, stats.Series.Count);
        var gap = stats.Series[2];
        Assert.Equal(0, gap.Checks);
        Assert.Null(gap.UptimePercent);
        Assert.Equal(start.AddMinutes(20), gap.Time);
        Assert.Equal([100.0, 100.0, null, 100.0, 0.0], stats.Series.Select(p => p.UptimePercent));
    }

    [Fact]
    public void Checks_exactly_at_gap_threshold_are_not_a_gap()
    {
        var start = To.AddHours(-1);
        var stats = HistoryStats.Build([Up(start), Up(start.AddMinutes(10))], Range("1h"), start, To, Gap);

        Assert.Equal(2, stats.Series.Count);
    }

    [Fact]
    public void Seven_day_range_uses_hourly_buckets_with_empty_buckets_as_gaps()
    {
        var from = To.AddDays(-7);
        var hour = from.AddHours(10);
        CheckPoint[] checks =
        [
            Up(hour.AddMinutes(0), 100),
            Up(hour.AddMinutes(5), 200),
            Down(hour.AddMinutes(10), 300),
            Up(hour.AddMinutes(15), 400),
        ];

        var stats = HistoryStats.Build(checks, Range("7d"), from, To, Gap);

        Assert.Equal(3600, stats.BucketSeconds);
        Assert.Equal(7 * 24, stats.Series.Count);

        var bucket = stats.Series.Single(p => p.Checks > 0);
        Assert.Equal(hour, bucket.Time);
        Assert.Equal(4, bucket.Checks);
        Assert.Equal(75.0, bucket.UptimePercent);
        Assert.Equal(250.0, bucket.AvgResponseMs);

        Assert.All(stats.Series.Where(p => p.Checks == 0), p => Assert.Null(p.UptimePercent));
    }

    [Fact]
    public void Buckets_are_aligned_to_the_bucket_size()
    {
        var to = new DateTime(2026, 11, 22, 12, 37, 0, DateTimeKind.Utc);
        var from = to.AddDays(-30);

        var stats = HistoryStats.Build([Up(to.AddMinutes(-1))], Range("30d"), from, to, Gap);

        Assert.Equal(6 * 3600, stats.BucketSeconds);
        Assert.All(stats.Series, p => Assert.Equal(0, p.Time.Hour % 6));
        Assert.All(stats.Series, p => Assert.Equal(0, p.Time.Minute));
        Assert.Equal(1, stats.Series[^1].Checks);
    }
}

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.UnitTests.Services;

public sealed class CheckRunnerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private TestSqliteDb _db = null!;
    private readonly MonitorOptions _options = new() { MaxConcurrency = 10 };
    private readonly CheckStateLock _stateLock = new(); // shared, like the app's singleton
    private readonly AlertQueue _queue = new();

    public async Task InitializeAsync() => _db = await TestSqliteDb.CreateAsync();
    public async Task DisposeAsync() => await _db.DisposeAsync();

    private CheckRunner CreateRunner(Api.Data.MonitorDbContext context, IUrlChecker checker) =>
        new(context, checker, NewAlertService(context), _queue, _stateLock, Options.Create(_options),
            TimeProvider.System, NullLogger<CheckRunner>.Instance);

    private static AlertService NewAlertService(Api.Data.MonitorDbContext context) => new(
        context, new FakeEmailSender(), Options.Create(new AlertsOptions()), TimeProvider.System, NullLogger<AlertService>.Instance);

    private async Task<List<int>> AddUrlsAsync(params (string Url, bool Active)[] urls)
    {
        await using var context = _db.CreateContext();
        var entities = urls.Select(u => new MonitoredUrl { Url = u.Url, IsActive = u.Active, CreatedAt = Now }).ToList();
        context.MonitoredUrls.AddRange(entities);
        await context.SaveChangesAsync();
        return entities.Select(e => e.Id).ToList();
    }

    [Fact]
    public async Task RunAll_saves_one_check_per_active_url()
    {
        await AddUrlsAsync(("https://up.test", true), ("https://down.test", true), ("https://paused.test", false));
        var checker = new FakeChecker(url => url.Contains("down")
            ? new CheckResult(Now, 500, 40, false, "HTTP 500")
            : new CheckResult(Now, 200, 25, true, null));

        await using (var context = _db.CreateContext())
        {
            var checks = await CreateRunner(context, checker).RunAllAsync();
            Assert.Equal(2, checks.Count);
        }

        await using var verify = _db.CreateContext();
        var saved = await verify.UrlChecks.Include(c => c.MonitoredUrl).ToListAsync();
        Assert.Equal(2, saved.Count);

        var up = saved.Single(c => c.MonitoredUrl!.Url == "https://up.test");
        Assert.True(up.IsUp);
        Assert.Equal(200, up.StatusCode);
        Assert.Equal(25, up.ResponseTimeMs);
        Assert.Equal(Now, up.CheckedAt);

        var down = saved.Single(c => c.MonitoredUrl!.Url == "https://down.test");
        Assert.False(down.IsUp);
        Assert.Equal("HTTP 500", down.Error);

        Assert.DoesNotContain(checker.Checked, u => u == "https://paused.test");
    }

    [Fact]
    public async Task RunAll_respects_max_concurrency()
    {
        _options.MaxConcurrency = 3;
        await AddUrlsAsync(Enumerable.Range(0, 12).Select(i => ($"https://site{i}.test", true)).ToArray());

        var current = 0;
        var max = 0;
        var checker = new FakeChecker(async _ =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref max, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref current);
            return new CheckResult(Now, 200, 1, true, null);
        });

        await using var context = _db.CreateContext();
        var checks = await CreateRunner(context, checker).RunAllAsync();

        Assert.Equal(12, checks.Count);
        Assert.InRange(max, 1, 3);
    }

    [Fact]
    public async Task RunAll_skips_urls_deleted_while_checking()
    {
        var ids = await AddUrlsAsync(("https://kept.test", true), ("https://deleted.test", true));
        var checker = new FakeChecker(async url =>
        {
            if (url == "https://deleted.test")
            {
                // Simulates the user deleting the URL through the API mid-run.
                await using var other = _db.CreateContext();
                await other.MonitoredUrls.Where(u => u.Id == ids[1]).ExecuteDeleteAsync();
            }
            return new CheckResult(Now, 200, 1, true, null);
        });
        _options.MaxConcurrency = 1;

        await using (var context = _db.CreateContext())
        {
            var checks = await CreateRunner(context, checker).RunAllAsync();
            Assert.Single(checks);
        }

        await using var verify = _db.CreateContext();
        Assert.Equal(ids[0], (await verify.UrlChecks.SingleAsync()).MonitoredUrlId);
    }

    [Fact]
    public async Task Unexpected_checker_error_is_recorded_for_that_url_only()
    {
        await AddUrlsAsync(("https://broken.test", true), ("https://fine.test", true));
        var checker = new FakeChecker(url => url == "https://broken.test"
            ? throw new ArgumentException("Something unexpected")
            : new CheckResult(Now, 200, 1, true, null));

        await using (var context = _db.CreateContext())
        {
            Assert.Equal(2, (await CreateRunner(context, checker).RunAllAsync()).Count);
        }

        await using var verify = _db.CreateContext();
        var saved = await verify.UrlChecks.Include(c => c.MonitoredUrl).ToListAsync();
        Assert.True(saved.Single(c => c.MonitoredUrl!.Url == "https://fine.test").IsUp);
        var broken = saved.Single(c => c.MonitoredUrl!.Url == "https://broken.test");
        Assert.False(broken.IsUp);
        Assert.Null(broken.StatusCode);
        Assert.Equal("Check failed: Something unexpected", broken.Error);
    }

    [Fact]
    public async Task RunOne_returns_null_when_url_is_deleted_during_its_check()
    {
        var ids = await AddUrlsAsync(("https://deleted.test", true));
        var checker = new FakeChecker(async _ =>
        {
            await using var other = _db.CreateContext();
            await other.MonitoredUrls.ExecuteDeleteAsync();
            return new CheckResult(Now, 200, 1, true, null);
        });

        await using (var context = _db.CreateContext())
        {
            Assert.Null(await CreateRunner(context, checker).RunOneAsync(ids[0]));
        }

        await using var verify = _db.CreateContext();
        Assert.Equal(0, await verify.UrlChecks.CountAsync());
    }

    [Fact]
    public async Task Concurrent_runs_apply_failures_in_turn_and_alert_once()
    {
        // Up with 1 failure; threshold is 2. A scheduled run and a "check now" both see a failure.
        var ids = await AddUrlsAsync(("https://flaky.test", true));
        await using (var setup = _db.CreateContext())
        {
            var url = await setup.MonitoredUrls.SingleAsync();
            url.CurrentState = UrlState.Up;
            url.ConsecutiveFailures = 1;
            await setup.SaveChangesAsync();
        }

        // Hold both runs inside their HTTP check at the same time, then release them together.
        var release = new TaskCompletionSource();
        var entered = new SemaphoreSlim(0);
        var checker = new FakeChecker(async _ =>
        {
            entered.Release();
            await release.Task;
            return new CheckResult(Now, 500, 1, false, "HTTP 500");
        });

        await using var scheduledContext = _db.CreateContext();
        await using var checkNowContext = _db.CreateContext();
        var scheduled = CreateRunner(scheduledContext, checker).RunAllAsync();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        var checkNow = CreateRunner(checkNowContext, checker).RunOneAsync(ids[0]);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        release.SetResult();
        await Task.WhenAll(scheduled, checkNow);

        await using var verify = _db.CreateContext();
        var saved = await verify.MonitoredUrls.SingleAsync();
        Assert.Equal(3, saved.ConsecutiveFailures);          // 1 + 2: neither update was lost
        Assert.Equal(UrlState.Down, saved.CurrentState);
        Assert.Single(await verify.AlertEvents.ToListAsync()); // one DOWN alert, not two
        Assert.Equal(2, await verify.UrlChecks.CountAsync());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    private sealed class FakeChecker : IUrlChecker
    {
        private readonly Func<string, Task<CheckResult>> _check;

        public FakeChecker(Func<string, CheckResult> check) => _check = url => Task.FromResult(check(url));
        public FakeChecker(Func<string, Task<CheckResult>> check) => _check = check;

        private readonly ConcurrentQueue<string> _checked = new();
        public IReadOnlyList<string> Checked => _checked.ToList();

        public Task<CheckResult> CheckAsync(string url, CancellationToken cancellationToken = default)
        {
            _checked.Enqueue(url);
            return _check(url);
        }
    }
}

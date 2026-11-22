using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services;

namespace UrlMonitor.UnitTests.Services;

public sealed class CheckRunnerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private TestSqliteDb _db = null!;
    private readonly MonitorOptions _options = new() { MaxConcurrency = 10 };

    public async Task InitializeAsync() => _db = await TestSqliteDb.CreateAsync();
    public async Task DisposeAsync() => await _db.DisposeAsync();

    private CheckRunner CreateRunner(Api.Data.MonitorDbContext context, IUrlChecker checker) =>
        new(context, checker, Options.Create(_options), NullLogger<CheckRunner>.Instance);

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
    public async Task RunAll_with_no_urls_does_nothing()
    {
        await using var context = _db.CreateContext();
        var checks = await CreateRunner(context, FakeChecker.NeverCalled).RunAllAsync();
        Assert.Empty(checks);
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
    public async Task RunOne_checks_and_saves_a_single_url()
    {
        var ids = await AddUrlsAsync(("https://one.test", true), ("https://two.test", true));
        var checker = new FakeChecker(_ => new CheckResult(Now, 200, 10, true, null));

        await using (var context = _db.CreateContext())
        {
            var check = await CreateRunner(context, checker).RunOneAsync(ids[1]);
            Assert.NotNull(check);
            Assert.Equal(ids[1], check.MonitoredUrlId);
        }

        Assert.Equal(["https://two.test"], checker.Checked);
        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.UrlChecks.CountAsync());
    }

    [Fact]
    public async Task RunOne_returns_null_for_unknown_url()
    {
        await using var context = _db.CreateContext();
        var check = await CreateRunner(context, FakeChecker.NeverCalled).RunOneAsync(999);
        Assert.Null(check);
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

        public static FakeChecker NeverCalled => new(_ => Task.FromException<CheckResult>(new UnreachableException()));

        private readonly ConcurrentQueue<string> _checked = new();
        public IReadOnlyList<string> Checked => _checked.ToList();

        public Task<CheckResult> CheckAsync(string url, CancellationToken cancellationToken = default)
        {
            _checked.Enqueue(url);
            return _check(url);
        }
    }
}

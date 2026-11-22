using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UrlMonitor.Api.Background;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services;

namespace UrlMonitor.UnitTests.Background;

public sealed class CheckSchedulerServiceTests : IAsyncDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeRunner _runner = new();
    private readonly ServiceProvider _services;
    private readonly CheckSchedulerService _scheduler;

    public CheckSchedulerServiceTests()
    {
        _services = new ServiceCollection()
            .AddScoped<ICheckRunner>(_ => _runner)
            .BuildServiceProvider();

        _scheduler = new CheckSchedulerService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MonitorOptions { CheckIntervalMinutes = 5 }),
            _time,
            NullLogger<CheckSchedulerService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _scheduler.StopAsync(CancellationToken.None);
        _scheduler.Dispose();
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task Runs_at_startup_then_every_interval()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        await _runner.WaitForRunAsync(); // startup run

        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(_runner.HasPendingRun, "should not run before the interval");

        _time.Advance(TimeSpan.FromMinutes(1));
        await _runner.WaitForRunAsync(); // 5 minutes

        _time.Advance(TimeSpan.FromMinutes(5));
        await _runner.WaitForRunAsync(); // 10 minutes

        Assert.Equal(3, _runner.Runs);
    }

    [Fact]
    public async Task A_failed_run_does_not_stop_later_runs()
    {
        _runner.FailNextRun = true;

        await _scheduler.StartAsync(CancellationToken.None);
        await _runner.WaitForRunAsync(); // startup run throws

        _time.Advance(TimeSpan.FromMinutes(5));
        await _runner.WaitForRunAsync(); // still runs

        Assert.Equal(2, _runner.Runs);
    }

    [Fact]
    public async Task Stops_cleanly_on_shutdown()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        await _runner.WaitForRunAsync();

        await _scheduler.StopAsync(CancellationToken.None);

        Assert.True(_scheduler.ExecuteTask!.IsCompletedSuccessfully);
    }

    private sealed class FakeRunner : ICheckRunner
    {
        private readonly Channel<int> _runs = Channel.CreateUnbounded<int>();
        private int _count;

        public bool FailNextRun { get; set; }
        public int Runs => Volatile.Read(ref _count);
        public bool HasPendingRun => _runs.Reader.Count > 0;

        public Task WaitForRunAsync() => _runs.Reader.ReadAsync().AsTask().WaitAsync(WaitLimit);

        public Task<IReadOnlyList<UrlCheck>> RunAllAsync(CancellationToken cancellationToken = default)
        {
            _runs.Writer.TryWrite(Interlocked.Increment(ref _count));
            if (FailNextRun)
            {
                FailNextRun = false;
                throw new InvalidOperationException("database is locked");
            }
            return Task.FromResult<IReadOnlyList<UrlCheck>>([]);
        }

        public Task<UrlCheck?> RunOneAsync(int monitoredUrlId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

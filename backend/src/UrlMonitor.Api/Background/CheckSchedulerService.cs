using Microsoft.Extensions.Options;
using UrlMonitor.Api.Services;

namespace UrlMonitor.Api.Background;

/// <summary>
/// Runs all checks once at startup, then every <see cref="MonitorOptions.CheckIntervalMinutes"/>.
/// PeriodicTimer never overlaps runs: if a run takes longer than the interval, missed ticks are skipped.
/// </summary>
public class CheckSchedulerService(
    IServiceScopeFactory scopeFactory,
    IOptions<MonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<CheckSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.CheckInterval;
        logger.LogInformation("URL checks scheduled every {Interval}", interval);

        // Created before the first run so the interval is measured from startup.
        using var timer = new PeriodicTimer(interval, timeProvider);

        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            // BackgroundService is a singleton but DbContext is scoped: each run gets its own scope.
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<ICheckRunner>();
            await runner.RunAllAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad run (e.g. a locked database) must not stop future runs.
            logger.LogError(ex, "Check run failed");
        }
    }
}

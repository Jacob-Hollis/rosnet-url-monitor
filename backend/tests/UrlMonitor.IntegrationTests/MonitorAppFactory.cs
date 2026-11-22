using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using UrlMonitor.Api.Background;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services;
using UrlMonitor.Api.Services.Alerts;

// WebApplicationFactory hooks into host construction process-wide, so the stub server's host and
// the app's host must never be built at the same time: run integration tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UrlMonitor.IntegrationTests;

/// <summary>
/// The real API (all real services, real SQLite file, real HttpClient) with three test changes:
/// a temp database, no background scheduler (tests trigger runs directly), and a fake email sender.
/// </summary>
public sealed class MonitorAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"urlmonitor-test-{Guid.NewGuid():N}.db");

    public FakeEmailSender Email { get; } = new();

    /// <summary>Extra config, e.g. ("Alerts:CooldownMinutes", "0"). Set before first use.</summary>
    public Dictionary<string, string> Settings { get; } = new()
    {
        ["Monitor:TimeoutSeconds"] = "1",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development", so the dev seed URLs aren't added.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Monitor", $"Data Source={_dbPath}");
        builder.UseSetting("Monitor:SchedulerEnabled", "false");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            // Belt and braces: make sure no scheduler runs, whatever the config says.
            // The alert delivery worker stays: it's what sends the (fake) emails.
            var scheduler = services.Where(d => d.ImplementationType == typeof(CheckSchedulerService)).ToList();
            foreach (var descriptor in scheduler) services.Remove(descriptor);

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
        });
    }

    /// <summary>
    /// Runs one scheduled check of every URL, exactly as the 5-minute job does, then waits for the
    /// background worker to finish emailing any alerts it raised.
    /// </summary>
    public async Task RunScheduledCheckAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICheckRunner>().RunAllAsync();
        }
        await WaitForAlertDeliveryAsync();
    }

    /// <summary>Waits until no alert is still waiting to be emailed.</summary>
    public async Task WaitForAlertDeliveryAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
            if (!await db.AlertEvents.AnyAsync(e => e.Delivery == AlertDelivery.Pending)) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Alerts were still Pending after 10s.");
            await Task.Delay(20);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}

/// <summary>Records emails instead of sending them; can be told to fail.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public record SentEmail(IReadOnlyCollection<string> Recipients, string Subject, string Body);

    private readonly List<SentEmail> _sent = [];

    public IReadOnlyList<SentEmail> Sent { get { lock (_sent) return _sent.ToList(); } }

    public Exception? FailWith { get; set; }

    public Task SendAsync(IReadOnlyCollection<string> recipients, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        lock (_sent) _sent.Add(new SentEmail(recipients.ToList(), subject, body));
        return Task.CompletedTask;
    }
}

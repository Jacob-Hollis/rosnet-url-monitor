using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using UrlMonitor.Api.Background;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services.Alerts;

namespace UrlMonitor.UnitTests.Background;

public sealed class AlertDeliveryServiceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private TestSqliteDb _db = null!;

    public async Task InitializeAsync() => _db = await TestSqliteDb.CreateAsync();
    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Startup_requeues_recent_unsent_alerts_and_fails_stale_ones()
    {
        long staleId, recentId;
        await using (var context = _db.CreateContext())
        {
            var url = new MonitoredUrl { Url = "https://example.com", CreatedAt = Now };
            var stale = new AlertEvent { OccurredAt = Now.AddHours(-2), Kind = AlertKind.Down, Delivery = AlertDelivery.Pending };
            var recent = new AlertEvent { OccurredAt = Now.AddMinutes(-10), Kind = AlertKind.Recovered, Delivery = AlertDelivery.Pending };
            var sent = new AlertEvent { OccurredAt = Now.AddMinutes(-5), Kind = AlertKind.Down, Delivery = AlertDelivery.Sent };
            url.AlertEvents.AddRange([stale, recent, sent]);
            context.MonitoredUrls.Add(url);
            await context.SaveChangesAsync();
            (staleId, recentId) = (stale.Id, recent.Id);
        }

        await using var services = new ServiceCollection()
            .AddScoped<MonitorDbContext>(_ => _db.CreateContext())
            .BuildServiceProvider();
        var queue = new AlertQueue();
        var service = new AlertDeliveryService(
            services.GetRequiredService<IServiceScopeFactory>(),
            queue,
            new FakeTimeProvider(new DateTimeOffset(Now)),
            NullLogger<AlertDeliveryService>.Instance);

        await service.RecoverPendingAsync(CancellationToken.None);

        Assert.Equal([recentId], queue.DequeueAvailable());

        await using var verify = _db.CreateContext();
        var staleEvent = await verify.AlertEvents.SingleAsync(e => e.Id == staleId);
        Assert.Equal(AlertDelivery.Failed, staleEvent.Delivery);
        Assert.Equal(AlertDeliveryService.StaleError, staleEvent.Error);
    }
}

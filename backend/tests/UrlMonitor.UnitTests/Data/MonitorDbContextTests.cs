using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Models;

namespace UrlMonitor.UnitTests.Data;

/// <summary>
/// Data layer tests against a real (in-memory) SQLite database with the real migrations applied.
/// </summary>
public sealed class MonitorDbContextTests : IAsyncLifetime
{
    // An in-memory SQLite database lives as long as its connection is open.
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private MonitorDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MonitorDbContext>().UseSqlite(_connection).Options);

    [Fact]
    public async Task DateTimes_are_read_back_as_utc()
    {
        var checkedAt = new DateTime(2026, 11, 22, 18, 30, 0, DateTimeKind.Utc);
        await using (var db = CreateContext())
        {
            var url = NewUrl("https://example.com");
            url.Checks.Add(new UrlCheck { CheckedAt = checkedAt, StatusCode = 200, ResponseTimeMs = 120, IsUp = true });
            db.MonitoredUrls.Add(url);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var check = await db.UrlChecks.SingleAsync();
            Assert.Equal(DateTimeKind.Utc, check.CheckedAt.Kind);
            Assert.Equal(checkedAt, check.CheckedAt);
        }
    }

    [Fact]
    public async Task DateTime_queries_filter_and_sort_correctly()
    {
        var start = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = CreateContext())
        {
            var url = NewUrl("https://example.com");
            for (var i = 0; i < 5; i++)
            {
                url.Checks.Add(new UrlCheck { CheckedAt = start.AddMinutes(5 * i), StatusCode = 200, IsUp = true });
            }
            db.MonitoredUrls.Add(url);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var from = start.AddMinutes(10);
            var times = await db.UrlChecks
                .Where(c => c.CheckedAt >= from)
                .OrderByDescending(c => c.CheckedAt)
                .Select(c => c.CheckedAt)
                .ToListAsync();

            Assert.Equal([start.AddMinutes(20), start.AddMinutes(15), start.AddMinutes(10)], times);
        }
    }

    [Fact]
    public async Task Deleting_a_url_deletes_its_checks_and_alert_events()
    {
        await using (var db = CreateContext())
        {
            var url = NewUrl("https://example.com");
            url.Checks.Add(new UrlCheck { CheckedAt = DateTime.UtcNow, StatusCode = 500, IsUp = false });
            url.AlertEvents.Add(new AlertEvent { OccurredAt = DateTime.UtcNow, Kind = AlertKind.Down, Delivery = AlertDelivery.Sent });
            db.MonitoredUrls.Add(url);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            // Delete without loading children, so the database's ON DELETE CASCADE does the work.
            await db.MonitoredUrls.ExecuteDeleteAsync();

            Assert.Equal(0, await db.UrlChecks.CountAsync());
            Assert.Equal(0, await db.AlertEvents.CountAsync());
        }
    }

    [Fact]
    public async Task Duplicate_url_is_rejected()
    {
        await using var db = CreateContext();
        db.MonitoredUrls.Add(NewUrl("https://example.com"));
        await db.SaveChangesAsync();

        db.MonitoredUrls.Add(NewUrl("https://example.com"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Duplicate_recipient_email_is_rejected()
    {
        await using var db = CreateContext();
        db.AlertRecipients.Add(new AlertRecipient { Email = "ops@example.com", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        db.AlertRecipients.Add(new AlertRecipient { Email = "ops@example.com", CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Enums_are_stored_as_strings()
    {
        await using (var db = CreateContext())
        {
            var url = NewUrl("https://example.com");
            url.CurrentState = UrlState.Down;
            db.MonitoredUrls.Add(url);
            await db.SaveChangesAsync();
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT CurrentState FROM MonitoredUrls";
        Assert.Equal("Down", await command.ExecuteScalarAsync());
    }

    private static MonitoredUrl NewUrl(string url) => new() { Url = url, CreatedAt = DateTime.UtcNow };
}

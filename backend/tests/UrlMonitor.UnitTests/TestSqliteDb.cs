using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UrlMonitor.Api.Data;

namespace UrlMonitor.UnitTests;

/// <summary>An in-memory SQLite database with the real migrations applied; lives until disposed.</summary>
public sealed class TestSqliteDb : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private TestSqliteDb() { }

    public static async Task<TestSqliteDb> CreateAsync()
    {
        var db = new TestSqliteDb();
        await db._connection.OpenAsync();
        await using var context = db.CreateContext();
        await context.Database.MigrateAsync();
        return db;
    }

    public MonitorDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MonitorDbContext>().UseSqlite(_connection).Options);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

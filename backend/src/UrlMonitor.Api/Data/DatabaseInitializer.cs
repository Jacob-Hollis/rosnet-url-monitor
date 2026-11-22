using Microsoft.EntityFrameworkCore;

namespace UrlMonitor.Api.Data;

public static class DatabaseInitializer
{
    /// <summary>
    /// Applies pending migrations and enables SQLite WAL mode so the background checker
    /// and API requests can read and write at the same time without "database is locked" errors.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

        await db.Database.MigrateAsync();

        if (db.Database.IsSqlite())
        {
            // WAL mode is stored in the database file, so this only changes anything the first time.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }
    }
}

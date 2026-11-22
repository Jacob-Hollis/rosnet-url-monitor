using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlMonitor.Api.Models;
using UrlMonitor.Api.Services;

namespace UrlMonitor.Api.Data;

public static class DatabaseInitializer
{
    /// <summary>
    /// Applies pending migrations, enables SQLite WAL mode, and seeds URLs on first run.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MonitorDbContext>();

        await db.Database.MigrateAsync();

        if (db.Database.IsSqlite())
        {
            // Lets the background checker and API requests read and write at the same time
            // without "database is locked" errors. Stored in the file, so this only matters once.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }

        var seeded = await SeedUrlsAsync(
            db,
            services.GetRequiredService<IOptions<MonitorOptions>>().Value.SeedUrls,
            services.GetRequiredService<TimeProvider>());

        if (seeded > 0)
        {
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(DatabaseInitializer))
                .LogInformation("Seeded {Count} URLs from Monitor:SeedUrls", seeded);
        }
    }

    /// <summary>
    /// Adds the configured seed URLs, but only when the database has no URLs at all (first run),
    /// so a seed URL the user deleted doesn't come back on every restart (design.md question 30).
    /// Invalid and duplicate entries are skipped.
    /// </summary>
    public static async Task<int> SeedUrlsAsync(MonitorDbContext db, IEnumerable<string> seedUrls, TimeProvider time)
    {
        if (await db.MonitoredUrls.AnyAsync()) return 0;

        var now = time.GetUtcNow().UtcDateTime;
        var urls = seedUrls
            .Select(s => InputValidation.TryNormalizeUrl(s, out var url, out _) ? url : null)
            .OfType<string>()
            .Distinct()
            .ToList();

        db.MonitoredUrls.AddRange(urls.Select(u => new MonitoredUrl { Url = u, CreatedAt = now }));
        await db.SaveChangesAsync();
        return urls.Count;
    }
}

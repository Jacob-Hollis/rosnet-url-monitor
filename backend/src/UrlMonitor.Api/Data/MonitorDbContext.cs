using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UrlMonitor.Api.Models;

namespace UrlMonitor.Api.Data;

public class MonitorDbContext(DbContextOptions<MonitorDbContext> options) : DbContext(options)
{
    public DbSet<MonitoredUrl> MonitoredUrls => Set<MonitoredUrl>();
    public DbSet<UrlCheck> UrlChecks => Set<UrlCheck>();
    public DbSet<AlertRecipient> AlertRecipients => Set<AlertRecipient>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All timestamps are UTC. SQLite has no time zone info, so values read back would be
        // DateTimeKind.Unspecified and serialize without a "Z"; this converter marks them as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

        // Store enums as readable strings (also portable to Postgres).
        configurationBuilder.Properties<UrlState>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<AlertKind>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<AlertDelivery>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MonitoredUrl>(e =>
        {
            e.Property(u => u.Url).HasMaxLength(2048);
            e.Property(u => u.Name).HasMaxLength(200);
            e.HasIndex(u => u.Url).IsUnique();

            e.HasMany(u => u.Checks)
                .WithOne(c => c.MonitoredUrl)
                .HasForeignKey(c => c.MonitoredUrlId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(u => u.AlertEvents)
                .WithOne(a => a.MonitoredUrl)
                .HasForeignKey(a => a.MonitoredUrlId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UrlCheck>(e =>
        {
            e.Property(c => c.Error).HasMaxLength(1000);
            // History and latest-check queries filter by URL and sort by time.
            e.HasIndex(c => new { c.MonitoredUrlId, c.CheckedAt });
        });

        modelBuilder.Entity<AlertRecipient>(e =>
        {
            e.Property(r => r.Email).HasMaxLength(320);
            e.HasIndex(r => r.Email).IsUnique();
        });

        modelBuilder.Entity<AlertEvent>(e =>
        {
            e.Property(a => a.Error).HasMaxLength(1000);
            // Cooldown lookups (per URL, latest first) and the recent alerts list.
            e.HasIndex(a => new { a.MonitoredUrlId, a.OccurredAt });
            e.HasIndex(a => a.OccurredAt);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

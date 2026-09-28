using Microsoft.EntityFrameworkCore;

namespace Wallet.NotificationWorker.Data;

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // EF looks for a property called "Id" by default, so name the key explicitly
        b.Entity<ProcessedEvent>(e => e.HasKey(p => p.EventId));

        b.Entity<Notification>(e =>
        {
            e.Property(n => n.Message).HasMaxLength(500);
            e.HasIndex(n => n.UserId);
        });
    }
}

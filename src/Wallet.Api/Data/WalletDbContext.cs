using Microsoft.EntityFrameworkCore;

namespace Wallet.Api.Data;

public sealed class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<Posting> Postings => Set<Posting>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(254);
        });

        b.Entity<Account>(e =>
        {
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(a => a.Currency).HasMaxLength(3).IsFixedLength();
            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId);
            e.HasIndex(a => a.UserId).IsUnique().HasFilter("kind = 'UserWallet'");
            e.ToTable(t => t.HasCheckConstraint(
                "ck_accounts_user_wallet_non_negative",
                "kind <> 'UserWallet' OR balance >= 0"
            ));

            e.HasData(new Account
            {
                Id = SystemAccounts.TopupSourceId,
                Kind = AccountKind.SystemTopupSource,
                Currency = "MYR",
                Balance = 0,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        });

        b.Entity<JournalEntry>(e =>
        {
            e.Property(j => j.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(j => j.IdempotencyKey).HasMaxLength(100);
            e.Property(j => j.Description).HasMaxLength(200);
            e.HasIndex(j => new {j.InitiatedByUserId, j.IdempotencyKey}).IsUnique();
        });

        b.Entity<Posting>(e =>
        {
            e.Property(p => p.Id).UseIdentityAlwaysColumn();
            e.HasOne(p => p.JournalEntry).WithMany(j => j.Postings).HasForeignKey(p => p.JournalEntryId);
            e.HasOne<Account>().WithMany().HasForeignKey(p => p.AccountId);
            e.HasIndex(p => new { p.AccountId, p.Id});
            e.ToTable(t => t.HasCheckConstraint("ck_postings_amount_non_zero", "amount <> 0"));
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.Property(o => o.Payload).HasColumnType("jsonb");
            e.HasIndex(o => o.OccurredAt).HasFilter("published_at IS NULL");
        });
    }
}
using Microsoft.EntityFrameworkCore;
using Wallet.Api.Data;

namespace Wallet.Api.Ledger;

public sealed record PostingRequest(Guid AccountId, long Amount);

public sealed class LedgerService(WalletDbContext db){
    public async Task<JournalEntry> PostAsync(
        JournalType type, Guid initiatedByUserId, string idempotencyKey, string? description,
        IReadOnlyList<PostingRequest> postings, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("LedgerService.PostAsync must run inside a transaction.");
        if (postings.Count < 2 || postings.Any(p => p.Amount == 0) || postings.Sum(p => p.Amount) != 0)
            throw new UnbalancedJournalException();

        var ids = postings.Select(p => p.AccountId).Distinct().ToArray();
        var accounts = await db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
            .ToDictionaryAsync(a => a.Id, ct);

        if (accounts.Count != ids.Length)
            throw new AccountNotFoundException();
        
        var now = DateTimeOffset.UtcNow;
        var journal = new JournalEntry
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            InitiatedByUserId = initiatedByUserId,
            IdempotencyKey = idempotencyKey,
            Description = description,
            CreatedAt = now,
        };

        foreach (var p in postings)
        {
            var account = accounts[p.AccountId];
            account.Balance += p.Amount;

            if (account.Kind == AccountKind.UserWallet && account.Balance < 0)
                throw new InsufficientFundsException();

            journal.Postings.Add(new Posting
            {
                AccountId = account.Id,
                Amount = p.Amount,
                BalanceAfter = account.Balance,
                CreatedAt = now,
            });
        }

        db.JournalEntries.Add(journal);
        return journal;
    }
}
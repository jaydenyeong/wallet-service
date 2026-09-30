using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wallet.Api.Auth;
using Wallet.Api.Data;
using Wallet.Api.Ledger;
using Wallet.Contracts;

namespace Wallet.Api.Wallets;

public sealed record TopupRequest(long Amount);
public sealed record MoneyMovementResponse(Guid TransactionId, long Amount, long Balance);
public sealed record TransferRequest(string ToEmail, long Amount, string? Note);

public sealed record BalanceResponse(Guid AccountId, string Currency, long Balance);
public sealed record TransactionItem(long Id, Guid TransactionId, string Type, long Amount, long BalanceAfter,
                                    string? Counterparty, string? Note, DateTimeOffset CreatedAt);

public sealed record TransactionPage(IReadOnlyList<TransactionItem> Items, long? NextCursor);

public static class WalletEndpoints
{
    public const long MaxTopup = 500_000;
    public const long MaxTransfer = 1_000_000;
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wallet").RequireAuthorization().WithTags("Wallet");
        group.MapPost("/topups", Topup);
        group.MapPost("/transfers", Transfer);
        group.MapGet("/", GetBalance);
        group.MapGet("/transactions", GetTransactions);
        return app;
    }

    private static async Task<IResult> Topup(
        TopupRequest req,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal principal, WalletDbContext db, LedgerService ledger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            return Results.Problem(statusCode: 400, title: "A valid Idempotency-Key header is required");
        if (req.Amount <= 0 || req.Amount > MaxTopup)
            return Results.Problem(statusCode: 400, title: $"Amount must be between 1 and {MaxTopup} sen");

        var userId = principal.GetUserId();
        var walletId = await GetWalletIdAsync(db, userId, ct);

        var existing = await FindReplayAsync(db, userId, idempotencyKey, walletId, ct);
        if (existing is not null) return Results.Ok(existing);

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var journal = await ledger.PostAsync(
                JournalType.Topup, userId, idempotencyKey, "Top-up",
                [new(SystemAccounts.TopupSourceId, -req.Amount), new(walletId, req.Amount)], ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var balance = journal.Postings.Single(p => p.AccountId == walletId).BalanceAfter;
            return Results.Created("/api/wallet", new MoneyMovementResponse(journal.Id, req.Amount, balance));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            var replay = await FindReplayAsync(db, userId, idempotencyKey, walletId, ct);
            if (replay is not null) return Results.Ok(replay);
            throw;
        }
    }
    private static async Task<IResult> Transfer(
        TransferRequest req,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal principal, WalletDbContext db, LedgerService ledger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            return Results.Problem(statusCode: 400, title: "A valid Idempotency-Key header is required");
        if (req.Amount <= 0 || req.Amount > MaxTransfer)
            return Results.Problem(statusCode: 400, title: $"Amount must be between 1 and {MaxTransfer} sen");
        if (req.Note is { Length: > 140 })
            return Results.Problem(statusCode: 400, title: "Note must be at most 140 characters");

        var senderId = principal.GetUserId();
        var toEmail = (req.ToEmail ?? "").Trim().ToLowerInvariant();

        var sender = await db.Accounts.AsNoTracking()
            .Where(a => a.UserId == senderId && a.Kind == AccountKind.UserWallet)
            .Join(db.Users, a => a.UserId, u => (Guid?)u.Id, (a, u) => new { WalletId = a.Id, u.Email })
            .SingleAsync(ct);

        var recipient = await db.Users.AsNoTracking()
            .Where(u => u.Email == toEmail)
            .Join(db.Accounts.Where(a => a.Kind == AccountKind.UserWallet),
                u => (Guid?)u.Id, a => a.UserId, (u, a) => new { UserId = u.Id, WalletId = a.Id, u.Email })
            .SingleOrDefaultAsync(ct);

        if (recipient is null)
            return Results.Problem(statusCode: 404, title: "Recipient not found");
        if (recipient.UserId == senderId)
            return Results.Problem(statusCode: 400, title: "You cannot transfer to yourself");

        var existing = await FindReplayAsync(db, senderId, idempotencyKey, sender.WalletId, ct);
        if (existing is not null) return Results.Ok(existing);

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var journal = await ledger.PostAsync(
                JournalType.Transfer, senderId, idempotencyKey, req.Note,
                [new(sender.WalletId, -req.Amount), new(recipient.WalletId, req.Amount)], ct);

            var evt = new TransferCompleted(
                EventId: Guid.CreateVersion7(),
                TransferId: journal.Id,
                FromUserId: senderId,
                ToUserId: recipient.UserId,
                FromEmail: sender.Email,
                ToEmail: recipient.Email,
                Amount: req.Amount,
                Currency: "MYR",
                Note: req.Note,
                OccuredAt: journal.CreatedAt
            );

            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = evt.EventId,
                Topic = Topics.Transfers,
                Key = sender.WalletId.ToString(),
                Type = nameof(TransferCompleted),
                Payload = JsonSerializer.Serialize(evt, JsonSerializerOptions.Web),
                OccurredAt = evt.OccuredAt,
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var balance = journal.Postings.Single(p => p.AccountId == sender.WalletId).BalanceAfter;
            return Results.Created("/api/wallet", new MoneyMovementResponse(journal.Id, req.Amount, balance));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            var replay = await FindReplayAsync(db, senderId, idempotencyKey, sender.WalletId, ct);
            if (replay is not null) return Results.Ok(replay);
            throw;
        }
    }

    private static async Task<IResult> GetBalance(ClaimsPrincipal principal, WalletDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var wallet = await db.Accounts.AsNoTracking()
            .Where(a => a.UserId == userId && a.Kind == AccountKind.UserWallet)
            .Select(a => new BalanceResponse(a.Id, a.Currency, a.Balance))
            .SingleAsync(ct);
        return Results.Ok(wallet);
    }

    private static async Task<IResult> GetTransactions(
        ClaimsPrincipal principal, WalletDbContext db, CancellationToken ct,
        int limit = 20, long? before = null)
    {
        limit = Math.Clamp(limit, 1, 100);
        var walletId = await GetWalletIdAsync(db, principal.GetUserId(), ct);

        var query = db.Postings.AsNoTracking().Where(p => p.AccountId == walletId);
        if (before is not null) query = query.Where(p => p.Id < before);

        var rows = await query
            .OrderByDescending(p => p.Id)
            .Take(limit + 1)
            .Select(p => new TransactionItem(
                p.Id,
                p.JournalEntryId,
                p.JournalEntry.Type.ToString(),
                p.Amount,
                p.BalanceAfter,
                (from o in db.Postings
                 join a in db.Accounts on o.AccountId equals a.Id
                 join u in db.Users on a.UserId equals (Guid?)u.Id
                 where o.JournalEntryId == p.JournalEntryId && o.AccountId != p.AccountId
                 select u.Email).FirstOrDefault(),
                p.JournalEntry.Description,
                p.CreatedAt
            )).ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var items = rows.Take(limit).ToList();
        return Results.Ok(new TransactionPage(items, hasMore ? items[^1].Id : null));
    }

    internal static Task<Guid> GetWalletIdAsync(WalletDbContext db, Guid userId, CancellationToken ct) =>
        db.Accounts.AsNoTracking()
            .Where(a => a.UserId == userId && a.Kind == AccountKind.UserWallet)
            .Select(a => a.Id)
            .SingleAsync(ct);

    internal static async Task<MoneyMovementResponse?> FindReplayAsync(
        WalletDbContext db, Guid userId, string key, Guid walletId, CancellationToken ct) =>
        await db.Postings.AsNoTracking()
            .Where(p => p.AccountId == walletId
                        && p.JournalEntry.InitiatedByUserId == userId
                        && p.JournalEntry.IdempotencyKey == key)
            .Select(p => new MoneyMovementResponse(p.JournalEntryId, Math.Abs(p.Amount), p.BalanceAfter))
            .SingleOrDefaultAsync(ct);
}
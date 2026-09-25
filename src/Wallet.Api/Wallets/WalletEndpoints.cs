using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wallet.Api.Auth;
using Wallet.Api.Data;
using Wallet.Api.Ledger;

namespace Wallet.Api.Wallets;

public sealed record TopupRequest(long Amount);
public sealed record MoneyMovementResponse(Guid TransactionId, long Amount, long Balance);

public static class WalletEndpoints
{
    public const long MaxTopup = 500_000;
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wallet").RequireAuthorization().WithTags("Wallet");
        group.MapPost("/topups", Topup);
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException {SqlState: PostgresErrorCodes.UniqueViolation})
        {
            db.ChangeTracker.Clear();
            var replay = await FindReplayAsync(db, userId, idempotencyKey, walletId, ct);
            if (replay is not null) return Results.Ok(replay);
            throw;
        }
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
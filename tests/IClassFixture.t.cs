using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wallet.Api.Data;
using Wallet.Api.Wallets;
using Wallet.Contracts;
using Xunit;

namespace Wallet.Api.Tests;

// One Postgres container (ApiFactory) is shared by every test in this class.
// Tests inside one class run one after another, so they don't interfere.
// Each test creates its own users with random emails, so no DB cleanup is needed.
public sealed class WalletApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // ─────────────────────────── Auth ───────────────────────────

    [Fact]
    public async Task Register_creates_wallet_with_zero_balance()
    {
        var (alice, _) = await TestHelpers.CreateUserAsync(factory);

        Assert.Equal(0, await TestHelpers.GetBalanceAsync(alice));
    }

    [Fact]
    public async Task Register_duplicate_email_returns_409()
    {
        var client = factory.CreateClient();
        var body = new { email = $"{Guid.NewGuid():N}@test.local", password = "Passw0rd!" };

        var first = await client.PostAsJsonAsync("/api/auth/register", body);
        var second = await client.PostAsJsonAsync("/api/auth/register", body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_without_token_returns_401()
    {
        var client = factory.CreateClient(); // no Bearer token

        var response = await client.GetAsync("/api/wallet");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─────────────────────────── Top-up ───────────────────────────

    [Fact]
    public async Task Topup_increases_balance()
    {
        var (alice, _) = await TestHelpers.CreateUserAsync(factory);

        var response = await TestHelpers.TopupAsync(alice, 5_000);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(5_000, await TestHelpers.GetBalanceAsync(alice));
    }

    [Fact]
    public async Task Topup_with_same_idempotency_key_is_applied_once()
    {
        var (alice, _) = await TestHelpers.CreateUserAsync(factory);
        var key = Guid.NewGuid().ToString();

        var first = await TestHelpers.TopupAsync(alice, 5_000, key);
        var second = await TestHelpers.TopupAsync(alice, 5_000, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<MoneyMovementResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<MoneyMovementResponse>();
        Assert.Equal(firstBody!.TransactionId, secondBody!.TransactionId);

        Assert.Equal(5_000, await TestHelpers.GetBalanceAsync(alice)); // not 10_000
    }

    // ─────────────────────────── Transfer ───────────────────────────

    [Fact]
    public async Task Transfer_moves_money_between_users()
    {
        var (alice, _) = await TestHelpers.CreateUserAsync(factory);
        var (bob, bobEmail) = await TestHelpers.CreateUserAsync(factory);
        (await TestHelpers.TopupAsync(alice, 5_000)).EnsureSuccessStatusCode();

        var response = await TestHelpers.TransferAsync(alice, bobEmail, 2_000);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(3_000, await TestHelpers.GetBalanceAsync(alice));
        Assert.Equal(2_000, await TestHelpers.GetBalanceAsync(bob));
    }

    [Fact]
    public async Task Transfer_with_insufficient_funds_returns_422_and_changes_nothing()
    {
        var (alice, _) = await TestHelpers.CreateUserAsync(factory);
        var (bob, bobEmail) = await TestHelpers.CreateUserAsync(factory);
        (await TestHelpers.TopupAsync(alice, 500)).EnsureSuccessStatusCode();

        var response = await TestHelpers.TransferAsync(alice, bobEmail, 1_000);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(500, await TestHelpers.GetBalanceAsync(alice));
        Assert.Equal(0, await TestHelpers.GetBalanceAsync(bob));
    }

    [Fact]
    public async Task Transfer_to_self_returns_400()
    {
        var (alice, aliceEmail) = await TestHelpers.CreateUserAsync(factory);
        (await TestHelpers.TopupAsync(alice, 1_000)).EnsureSuccessStatusCode();

        var response = await TestHelpers.TransferAsync(alice, aliceEmail, 100);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1_000, await TestHelpers.GetBalanceAsync(alice));
    }

    [Fact]
    public async Task Transfer_writes_exactly_one_outbox_message()
    {
        var (alice, _) = await TestHelpers.CreateUserAsync(factory);
        var (_, bobEmail) = await TestHelpers.CreateUserAsync(factory);
        (await TestHelpers.TopupAsync(alice, 5_000)).EnsureSuccessStatusCode();
        (await TestHelpers.TransferAsync(alice, bobEmail, 2_000)).EnsureSuccessStatusCode();

        // The outbox row's Key is the sender's wallet id, so use it to find this test's row.
        var wallet = await alice.GetFromJsonAsync<BalanceResponse>("/api/wallet");
        var walletKey = wallet!.AccountId.ToString();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        var messages = await db.OutboxMessages.AsNoTracking()
            .Where(o => o.Key == walletKey)
            .ToListAsync();

        var msg = Assert.Single(messages); // top-ups don't write outbox rows, so exactly one
        Assert.Equal(nameof(TransferCompleted), msg.Type);
        Assert.Null(msg.PublishedAt); // the publisher is disabled in tests

        var evt = JsonSerializer.Deserialize<TransferCompleted>(msg.Payload, JsonSerializerOptions.Web)!;
        Assert.Equal(2_000, evt.Amount);
        Assert.Equal(bobEmail, evt.ToEmail);
        Assert.Equal("MYR", evt.Currency);
    }

    // ─────────────────────────── The showcase tests ───────────────────────────

    [Fact]
    public async Task Concurrent_transfers_never_overdraw()
    {
        var (alice, _) = await TestHelpers.CreateUserAsync(factory);
        var (bob, bobEmail) = await TestHelpers.CreateUserAsync(factory);
        (await TestHelpers.TopupAsync(alice, 1_000)).EnsureSuccessStatusCode(); // RM 10.00

        // 20 parallel transfers of RM 1.00 each; only 10 can possibly succeed
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => TestHelpers.TransferAsync(alice, bobEmail, 100)));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(0, await TestHelpers.GetBalanceAsync(alice));
        Assert.Equal(1_000, await TestHelpers.GetBalanceAsync(bob));
    }

    [Fact]
    public async Task Ledger_invariants_hold()
    {
        // Arrange: some activity across a few users
        var (alice, _) = await TestHelpers.CreateUserAsync(factory);
        var (bob, bobEmail) = await TestHelpers.CreateUserAsync(factory);
        var (_, carolEmail) = await TestHelpers.CreateUserAsync(factory);

        (await TestHelpers.TopupAsync(alice, 10_000)).EnsureSuccessStatusCode();
        (await TestHelpers.TopupAsync(bob, 3_000)).EnsureSuccessStatusCode();
        (await TestHelpers.TransferAsync(alice, bobEmail, 2_500)).EnsureSuccessStatusCode();
        (await TestHelpers.TransferAsync(bob, carolEmail, 4_000)).EnsureSuccessStatusCode();
        (await TestHelpers.TransferAsync(alice, carolEmail, 1_000)).EnsureSuccessStatusCode();

        // Assert against the whole database (includes every earlier test's data too)
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();

        // 1. every journal entry balances
        var unbalanced = await db.Postings
            .GroupBy(p => p.JournalEntryId)
            .Where(g => g.Sum(p => p.Amount) != 0)
            .CountAsync();
        Assert.Equal(0, unbalanced);

        // 2. cached balances match the postings
        var mismatched = await db.Accounts
            .Where(a => a.Balance != (db.Postings.Where(p => p.AccountId == a.Id).Sum(p => (long?)p.Amount) ?? 0))
            .CountAsync();
        Assert.Equal(0, mismatched);

        // 3. no user wallet is negative
        Assert.False(await db.Accounts.AnyAsync(a => a.Kind == AccountKind.UserWallet && a.Balance < 0));

        // 4. the whole ledger sums to zero
        Assert.Equal(0, await db.Postings.SumAsync(p => p.Amount));
    }
}
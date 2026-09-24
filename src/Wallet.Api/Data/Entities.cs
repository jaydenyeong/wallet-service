namespace Wallet.Api.Data;

public sealed class User
{
    public Guid Id {get; set;}
    public string Email {get; set;} = "";
    public string PasswordHash {get; set;} = "";
    public DateTimeOffset CreatedAt {get; set;}
}

public enum AccountKind {UserWallet, SystemTopupSource}

public sealed class Account
{
    public Guid Id {get; set;}
    public Guid? UserId {get; set;}
    public AccountKind Kind {get; set;}
    public string Currency {get; set;} = "MYR";
    public long Balance {get; set;}
    public DateTimeOffset CreatedAt {get; set;}
}

public enum JournalType {Topup, Transfer}

public sealed class JournalEntry
{
    public Guid Id {get; set;}
    public JournalType Type {get; set;}
    public Guid InitiatedByUserId {get; set;}
    public string IdempotencyKey {get; set;} = "";
    public string? Description {get; set;}
    public DateTimeOffset CreatedAt {get; set;}
    public List<Posting> Postings {get; set;} = [];
}

public sealed class Posting
{
    public long Id {get; set;}
    public Guid JournalEntryId {get; set;}
    public JournalEntry JournalEntry {get; set;} = null!;
    public Guid AccountId {get; set;}
    public long Amount {get; set;}
    public long BalanceAfter {get; set;}
    public DateTimeOffset CreatedAt {get; set;}
}

public sealed class OutboxMessage
{
    public Guid Id {get; set;}
    public string Topic {get; set;} = "";
    public string Key {get; set;} = "";
    public string Type {get; set;} = "";
    public string Payload {get; set;} = "";
    public DateTimeOffset OccurredAt {get; set;}
    public DateTimeOffset? PublishedAt {get; set;}
    public int Attempts {get; set;}
    public string? LastError {get; set;}
}

public static class SystemAccounts
{
    public static readonly Guid TopupSourceId = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
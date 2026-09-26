namespace Wallet.Contracts;

public static class Topics
{
    public const string Transfers = "wallet.transfers";
}

public sealed record TransferCompleted(
    Guid EventId,
    Guid TransferId,
    Guid FromUserId,
    Guid ToUserId,
    string FromEmail,
    string ToEmail,
    long Amount,
    string Currency,
    string? Note,
    DateTimeOffset OccuredAt
);
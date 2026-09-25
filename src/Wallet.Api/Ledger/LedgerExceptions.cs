namespace Wallet.Api.Ledger;

public sealed class InsufficientFundsException() : Exception("Insufficient funds.");
public sealed class AccountNotFoundException() : Exception("Account not found.");
public sealed class UnbalancedJournalException() : Exception("Postings must sum to zero.");
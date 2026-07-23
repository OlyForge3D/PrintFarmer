using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Tracks a user's print credit balance. Credits are deducted when jobs complete
/// and can be manually credited or debited by administrators.
/// </summary>
public class UserBalance
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>
    /// Current balance amount in the configured currency.
    /// </summary>
    public decimal BalanceAmount { get; set; }

    /// <summary>
    /// ISO 4217 currency code (e.g., "USD", "EUR").
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    public DateTime LastUpdated { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Immutable ledger entry recording every credit/debit against a user balance.
/// </summary>
public class BalanceTransaction
{
    public Guid Id { get; set; }

    public Guid UserBalanceId { get; set; }

    public UserBalance? UserBalance { get; set; }

    /// <summary>
    /// Positive for credits, negative for debits.
    /// </summary>
    public decimal Amount { get; set; }

    public BalanceTransactionType TransactionType { get; set; }

    /// <summary>
    /// Optional reference to the print job that triggered this transaction.
    /// </summary>
    public Guid? PrintJobId { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Who initiated this transaction (admin user ID or "system").
    /// </summary>
    [MaxLength(200)]
    public string? PerformedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum BalanceTransactionType
{
    /// <summary>Manual credit added by admin.</summary>
    Credit = 0,

    /// <summary>Manual debit by admin.</summary>
    Debit = 1,

    /// <summary>Automatic deduction on job completion.</summary>
    JobDeduction = 2,

    /// <summary>Refund from cancelled job.</summary>
    JobRefund = 3
}

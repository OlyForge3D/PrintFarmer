using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.PrintQuotas;

/// <summary>
/// Service for managing print quotas and user balances.
/// Provides check-before-submit, deduct-on-complete, and refund-on-cancel operations.
/// </summary>
public interface IPrintQuotaService
{
    // ── Quota CRUD ──────────────────────────────────────────────────────
    Task<PrintQuota[]> GetQuotasForUserAsync(Guid userId, CancellationToken ct = default);

    Task<PrintQuota[]> GetQuotasForGroupAsync(string groupName, CancellationToken ct = default);

    Task<PrintQuota[]> GetAllQuotasAsync(CancellationToken ct = default);

    Task<PrintQuota?> GetQuotaByIdAsync(Guid quotaId, CancellationToken ct = default);

    Task<PrintQuota> CreateQuotaAsync(PrintQuota quota, CancellationToken ct = default);

    Task<PrintQuota?> UpdateQuotaAsync(Guid quotaId, decimal? limitAmount, QuotaPeriodType? periodType, bool? isActive, string? notes, CancellationToken ct = default);

    Task<bool> DeleteQuotaAsync(Guid quotaId, CancellationToken ct = default);

    // ── Quota enforcement ───────────────────────────────────────────────

    /// <summary>
    /// Checks whether the user can submit a job with the given estimated
    /// cost/count/weight without exceeding any active quota.
    /// </summary>
    Task<QuotaCheckResult> CheckQuotaAsync(Guid userId, decimal estimatedCost, int jobCount, double estimatedWeightGrams, CancellationToken ct = default);

    /// <summary>Deducts usage from all applicable quotas after job completion.</summary>
    Task DeductQuotaUsageAsync(Guid userId, decimal actualCost, double actualWeightGrams, CancellationToken ct = default);

    /// <summary>Refunds usage when a job is cancelled.</summary>
    Task RefundQuotaUsageAsync(Guid userId, decimal refundCost, double refundWeightGrams, CancellationToken ct = default);

    /// <summary>Resets all quotas whose ResetAt has passed.</summary>
    Task<int> ResetExpiredQuotasAsync(CancellationToken ct = default);

    // ── Balance ─────────────────────────────────────────────────────────
    Task<UserBalance?> GetBalanceAsync(Guid userId, CancellationToken ct = default);

    Task<UserBalance> GetOrCreateBalanceAsync(Guid userId, CancellationToken ct = default);

    Task<UserBalance> CreditBalanceAsync(Guid userId, decimal amount, string description, string performedBy, CancellationToken ct = default);

    Task<UserBalance> DebitBalanceAsync(Guid userId, decimal amount, string description, string performedBy, CancellationToken ct = default);

    Task<BalanceTransaction[]> GetTransactionHistoryAsync(Guid userId, int take = 50, CancellationToken ct = default);
}

public sealed record QuotaCheckResult(bool Allowed, string? DeniedReason, Guid? DeniedByQuotaId);

using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.PrintQuotas;

public sealed class PrintQuotaService(AppDbContext db, ILogger<PrintQuotaService> logger) : IPrintQuotaService
{
    // ── Quota CRUD ──────────────────────────────────────────────────────
    public async Task<PrintQuota[]> GetQuotasForUserAsync(Guid userId, CancellationToken ct = default)
        => await db.PrintQuotas.Where(q => q.UserId == userId).OrderBy(q => q.QuotaType).ToArrayAsync(ct);

    public async Task<PrintQuota[]> GetQuotasForGroupAsync(string groupName, CancellationToken ct = default)
        => await db.PrintQuotas.Where(q => q.GroupName == groupName).OrderBy(q => q.QuotaType).ToArrayAsync(ct);

    public async Task<PrintQuota[]> GetAllQuotasAsync(CancellationToken ct = default)
        => await db.PrintQuotas.Include(q => q.User).OrderByDescending(q => q.CreatedAt).ToArrayAsync(ct);

    public async Task<PrintQuota?> GetQuotaByIdAsync(Guid quotaId, CancellationToken ct = default)
        => await db.PrintQuotas.Include(q => q.User).FirstOrDefaultAsync(q => q.Id == quotaId, ct);

    public async Task<PrintQuota> CreateQuotaAsync(PrintQuota quota, CancellationToken ct = default)
    {
        quota.Id = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        quota.CreatedAt = now;
        quota.UpdatedAt = now;
        quota.PeriodStart = now;
        quota.UsedAmount = 0;
        quota.ResetAt = CalculateNextReset(now, quota.PeriodType);

        db.PrintQuotas.Add(quota);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created quota {QuotaId} type={Type} limit={Limit} for user={UserId} group={Group}",
            quota.Id, quota.QuotaType, quota.LimitAmount, quota.UserId, quota.GroupName);
        return quota;
    }

    public async Task<PrintQuota?> UpdateQuotaAsync(Guid quotaId, decimal? limitAmount, QuotaPeriodType? periodType, bool? isActive, string? notes, CancellationToken ct = default)
    {
        PrintQuota? quota = await db.PrintQuotas.FindAsync([quotaId], ct);
        if (quota is null)
        {
            return null;
        }

        if (limitAmount.HasValue)
        {
            quota.LimitAmount = limitAmount.Value;
        }

        if (periodType.HasValue)
        {
            quota.PeriodType = periodType.Value;
            quota.ResetAt = CalculateNextReset(quota.PeriodStart, periodType.Value);
        }

        if (isActive.HasValue)
        {
            quota.IsActive = isActive.Value;
        }

        if (notes is not null)
        {
            quota.Notes = notes;
        }

        quota.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return quota;
    }

    public async Task<bool> DeleteQuotaAsync(Guid quotaId, CancellationToken ct = default)
    {
        PrintQuota? quota = await db.PrintQuotas.FindAsync([quotaId], ct);
        if (quota is null)
        {
            return false;
        }

        db.PrintQuotas.Remove(quota);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Quota enforcement ───────────────────────────────────────────────
    public async Task<QuotaCheckResult> CheckQuotaAsync(Guid userId, decimal estimatedCost, int jobCount, double estimatedWeightGrams, CancellationToken ct = default)
    {
        PrintQuota[] quotas = await db.PrintQuotas
            .Where(q => q.IsActive && q.UserId == userId)
            .ToArrayAsync(ct);

        foreach (PrintQuota q in quotas)
        {
            if (IsExpired(q))
            {
                ResetQuota(q);
                continue;
            }

            decimal projected = q.QuotaType switch
            {
                QuotaType.Cost => q.UsedAmount + estimatedCost,
                QuotaType.Count => q.UsedAmount + jobCount,
                QuotaType.Weight => q.UsedAmount + (decimal)estimatedWeightGrams,
                _ => q.UsedAmount
            };

            if (projected > q.LimitAmount)
            {
                string reason = $"Quota exceeded: {q.QuotaType} limit is {q.LimitAmount}, current usage is {q.UsedAmount}, requested {projected - q.UsedAmount}";
                return new QuotaCheckResult(false, reason, q.Id);
            }
        }

        await db.SaveChangesAsync(ct);
        return new QuotaCheckResult(true, null, null);
    }

    public async Task DeductQuotaUsageAsync(Guid userId, decimal actualCost, double actualWeightGrams, CancellationToken ct = default)
    {
        PrintQuota[] quotas = await db.PrintQuotas
            .Where(q => q.IsActive && q.UserId == userId)
            .ToArrayAsync(ct);

        foreach (PrintQuota q in quotas)
        {
            if (IsExpired(q))
            {
                ResetQuota(q);
            }

            q.UsedAmount += q.QuotaType switch
            {
                QuotaType.Cost => actualCost,
                QuotaType.Count => 1,
                QuotaType.Weight => (decimal)actualWeightGrams,
                _ => 0
            };
            q.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RefundQuotaUsageAsync(Guid userId, decimal refundCost, double refundWeightGrams, CancellationToken ct = default)
    {
        PrintQuota[] quotas = await db.PrintQuotas
            .Where(q => q.IsActive && q.UserId == userId)
            .ToArrayAsync(ct);

        foreach (PrintQuota q in quotas)
        {
            decimal refund = q.QuotaType switch
            {
                QuotaType.Cost => refundCost,
                QuotaType.Count => 1,
                QuotaType.Weight => (decimal)refundWeightGrams,
                _ => 0
            };
            q.UsedAmount = Math.Max(0, q.UsedAmount - refund);
            q.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<int> ResetExpiredQuotasAsync(CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        PrintQuota[] expired = await db.PrintQuotas
            .Where(q => q.IsActive && q.ResetAt != null && q.ResetAt <= now)
            .ToArrayAsync(ct);

        foreach (PrintQuota q in expired)
        {
            ResetQuota(q);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Reset {Count} expired quotas", expired.Length);
        return expired.Length;
    }

    // ── Balance ─────────────────────────────────────────────────────────
    public async Task<UserBalance?> GetBalanceAsync(Guid userId, CancellationToken ct = default)
        => await db.UserBalances.FirstOrDefaultAsync(b => b.UserId == userId, ct);

    public async Task<UserBalance> GetOrCreateBalanceAsync(Guid userId, CancellationToken ct = default)
    {
        UserBalance? balance = await db.UserBalances.FirstOrDefaultAsync(b => b.UserId == userId, ct);
        if (balance is not null)
        {
            return balance;
        }

        DateTime now = DateTime.UtcNow;
        balance = new UserBalance
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BalanceAmount = 0,
            Currency = "USD",
            LastUpdated = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.UserBalances.Add(balance);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent insert — detach the failed entity and re-read
            db.Entry(balance).State = EntityState.Detached;
            balance = await db.UserBalances.FirstOrDefaultAsync(b => b.UserId == userId, ct);
            if (balance is null)
            {
                throw;
            }
        }

        return balance;
    }

    public async Task<UserBalance> CreditBalanceAsync(Guid userId, decimal amount, string description, string performedBy, CancellationToken ct = default)
    {
        UserBalance balance = await GetOrCreateBalanceAsync(userId, ct);
        balance.BalanceAmount += amount;
        DateTime now = DateTime.UtcNow;
        balance.LastUpdated = now;
        balance.UpdatedAt = now;

        db.BalanceTransactions.Add(new BalanceTransaction
        {
            Id = Guid.NewGuid(),
            UserBalanceId = balance.Id,
            Amount = amount,
            TransactionType = BalanceTransactionType.Credit,
            Description = description,
            PerformedBy = performedBy,
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Credited {Amount} to user {UserId} balance. New balance: {Balance}", amount, userId, balance.BalanceAmount);
        return balance;
    }

    public async Task<UserBalance> DebitBalanceAsync(Guid userId, decimal amount, string description, string performedBy, CancellationToken ct = default)
    {
        UserBalance balance = await GetOrCreateBalanceAsync(userId, ct);

        if (balance.BalanceAmount < amount)
        {
            throw new InvalidOperationException($"Insufficient balance: current {balance.BalanceAmount}, requested debit {amount}");
        }

        balance.BalanceAmount -= amount;
        DateTime now = DateTime.UtcNow;
        balance.LastUpdated = now;
        balance.UpdatedAt = now;

        db.BalanceTransactions.Add(new BalanceTransaction
        {
            Id = Guid.NewGuid(),
            UserBalanceId = balance.Id,
            Amount = -amount,
            TransactionType = BalanceTransactionType.Debit,
            Description = description,
            PerformedBy = performedBy,
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Debited {Amount} from user {UserId} balance. New balance: {Balance}", amount, userId, balance.BalanceAmount);
        return balance;
    }

    public async Task<BalanceTransaction[]> GetTransactionHistoryAsync(Guid userId, int take = 50, CancellationToken ct = default)
    {
        UserBalance? balance = await db.UserBalances.FirstOrDefaultAsync(b => b.UserId == userId, ct);
        if (balance is null)
        {
            return [];
        }

        return await db.BalanceTransactions
            .Where(t => t.UserBalanceId == balance.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Take(take)
            .ToArrayAsync(ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    private static bool IsExpired(PrintQuota q)
        => q.ResetAt.HasValue && q.ResetAt.Value <= DateTime.UtcNow;

    private static void ResetQuota(PrintQuota q)
    {
        DateTime now = DateTime.UtcNow;
        q.UsedAmount = 0;
        q.PeriodStart = now;
        q.ResetAt = CalculateNextReset(now, q.PeriodType);
        q.UpdatedAt = now;
    }

    private static DateTime? CalculateNextReset(DateTime from, QuotaPeriodType period) => period switch
    {
        QuotaPeriodType.Daily => from.Date.AddDays(1),
        QuotaPeriodType.Weekly => from.Date.AddDays(DaysUntilNextMonday(from.DayOfWeek)),
        QuotaPeriodType.Monthly => new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1),
        QuotaPeriodType.Semester => new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(6),
        QuotaPeriodType.Manual => null,
        _ => null
    };

    private static int DaysUntilNextMonday(DayOfWeek day)
    {
        int diff = ((int)DayOfWeek.Monday - (int)day + 7) % 7;
        return diff == 0 ? 7 : diff;
    }
}

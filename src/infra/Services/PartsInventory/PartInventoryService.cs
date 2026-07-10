using System.Data;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Repositories.PartsInventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.PartsInventory;

/// <summary>
/// EF Core implementation of <see cref="IPartInventoryService"/>.
/// Applies a single ledger entry per call and updates the denormalized
/// <see cref="PartInventory.OnHand"/> counter in the same transaction.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the service can be
/// safely invoked from background workers as well as HTTP request scopes.
/// </summary>
public class PartInventoryService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<PartInventoryService> logger) : IPartInventoryService
{
    public async Task<AdjustResult> AdjustAsync(string sku, AdjustCommand command, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentNullException.ThrowIfNull(command);

        if (command.Delta == 0)
        {
            return new AdjustResult(PartInventoryOutcome.InvalidRequest, null, 0, "Delta must be non-zero.");
        }

        string trimmedSku = sku.Trim();

        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Idempotency short-circuit: if an operation key was supplied and
        // we've already recorded an adjustment for it, return the prior
        // result without applying a duplicate delta.
        if (!string.IsNullOrWhiteSpace(command.OperationKey))
        {
            PartInventoryAdjustment? existing = await db.PartInventoryAdjustments
                .AsNoTracking()
                .Include(a => a.Bin)
                .FirstOrDefaultAsync(a => a.OperationKey == command.OperationKey, ct);
            if (existing is not null)
            {
                PartInventory? existingPart = await db.PartInventories
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == existing.PartInventoryId, ct);
                if (existingPart is not null && string.Equals(existingPart.Sku, trimmedSku, StringComparison.Ordinal))
                {
                    return new AdjustResult(
                        PartInventoryOutcome.IdempotentReplay,
                        ToDto(existing, existingPart.Sku),
                        existingPart.OnHand,
                        "Duplicate operation key; existing adjustment returned.");
                }
            }
        }

        return await ExecuteTransactionAsync(db, async () =>
        {
            PartInventory? part = await db.PartInventories
                .FirstOrDefaultAsync(p => p.Sku == trimmedSku, ct);
            if (part is null)
            {
                return new AdjustResult(PartInventoryOutcome.PartNotFound, null, 0, $"SKU '{trimmedSku}' not found.");
            }

            Guid? binId = null;
            if (!string.IsNullOrWhiteSpace(command.BinCode))
            {
                string trimmedCode = command.BinCode.Trim();
                Bin? bin = await db.Bins.FirstOrDefaultAsync(b => b.Code == trimmedCode, ct);
                if (bin is null || !bin.IsActive)
                {
                    return new AdjustResult(PartInventoryOutcome.BinNotFound, null, part.OnHand,
                        $"Bin '{trimmedCode}' not found or inactive.");
                }

                binId = bin.Id;
            }

            var adjustment = new PartInventoryAdjustment
            {
                Id = Guid.NewGuid(),
                PartInventoryId = part.Id,
                BinId = binId,
                Delta = command.Delta,
                Reason = command.Reason,
                PrintJobId = command.PrintJobId,
                OperationKey = string.IsNullOrWhiteSpace(command.OperationKey) ? null : command.OperationKey,
                Notes = command.Notes,
                UserId = command.UserId,
                CreatedAt = DateTime.UtcNow,
            };
            _ = db.PartInventoryAdjustments.Add(adjustment);

            part.OnHand += command.Delta;
            part.UpdatedAt = DateTime.UtcNow;

            try
            {
                _ = await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                logger.LogWarning(ex, "Unique constraint hit while adjusting SKU {Sku}; assuming idempotent replay.", trimmedSku);
                return new AdjustResult(PartInventoryOutcome.IdempotentReplay, null, part.OnHand, "Duplicate operation key.");
            }

            Bin? binForDto = binId.HasValue
                ? await db.Bins.AsNoTracking().FirstOrDefaultAsync(b => b.Id == binId, ct)
                : null;
            adjustment.Bin = binForDto;
            return new AdjustResult(PartInventoryOutcome.Ok, ToDto(adjustment, part.Sku), part.OnHand, null);
        }, ct);
    }

    internal static async Task<AdjustResult> ExecuteTransactionAsync(
        AppDbContext db,
        Func<Task<AdjustResult>> action,
        CancellationToken ct)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            AdjustResult inner = await action();
            if (inner.Outcome is PartInventoryOutcome.Ok or PartInventoryOutcome.IdempotentReplay)
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }

            return inner;
        }

        return await action();
    }

    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Maps an adjustment entity to its DTO. Exposed for use by controllers
    /// and cross-service helpers (e.g. harvest replay) in adjacent assemblies.
    /// </summary>
    public static PartAdjustmentResponse ToDto(PartInventoryAdjustment a, string sku)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new PartAdjustmentResponse(
            a.Id,
            a.PartInventoryId,
            sku,
            a.BinId,
            a.Bin?.Code,
            a.Delta,
            a.Reason,
            a.PrintJobId,
            a.OperationKey,
            a.Notes,
            a.UserId,
            a.CreatedAt);
    }
}

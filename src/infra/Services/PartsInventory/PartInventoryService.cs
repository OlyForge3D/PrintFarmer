using System.Data;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.PartsInventory;

/// <summary>
/// EF Core implementation of <see cref="IPartInventoryService"/>.
/// <para>
/// Every write path applies a single ledger entry inside a transaction and
/// updates the denormalized <see cref="PartInventory.OnHand"/> counter in the
/// same commit. Uses <see cref="IDbContextFactory{TContext}"/> so the service
/// is safe to invoke from background workers as well as HTTP request scopes.
/// </para>
/// <para>
/// Concurrency contract:
/// <list type="bullet">
///   <item>Idempotency is enforced by the composite unique index
///     <c>(PartInventoryId, OperationKey)</c>. Same-SKU duplicates surface as
///     <see cref="PartInventoryOutcome.IdempotentReplay"/> with the actual
///     committed adjustment and committed <see cref="PartInventory.OnHand"/>,
///     never an in-memory value from a rolled-back transaction.</item>
///   <item>On PostgreSQL a failed transaction is <em>poisoned</em>: any further
///     query raises <c>25P02</c>. We therefore always rollback on
///     <see cref="DbUpdateException"/> / <see cref="DbUpdateConcurrencyException"/>
///     and re-read committed state from a fresh <see cref="AppDbContext"/>.</item>
///   <item><see cref="DbUpdateConcurrencyException"/> on
///     <see cref="PartInventory"/> (RowVersion contention from a different
///     writer) triggers a bounded retry against fresh state.</item>
/// </list>
/// </para>
/// </summary>
public class PartInventoryService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<PartInventoryService> logger,
    IOperatorFeatureGate? featureGate = null) : IPartInventoryService
{
    /// <summary>Maximum retries for a benign RowVersion collision on <see cref="PartInventory"/>.</summary>
    private const int MaxConcurrencyRetries = 3;

    public async Task<AdjustResult> AdjustAsync(string sku, AdjustCommand command, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentNullException.ThrowIfNull(command);

        if (featureGate is not null && !featureGate.IsEnabled(OperatorFeature.PrintedPartsInventory))
        {
            return new AdjustResult(PartInventoryOutcome.FeatureDisabled, null, 0, "Printed-parts inventory is disabled.");
        }

        if (command.Delta == 0)
        {
            return new AdjustResult(PartInventoryOutcome.InvalidRequest, null, 0, "Delta must be non-zero.");
        }

        if (command.Reason == PartAdjustmentReason.Harvest && command.Delta < 0)
        {
            return new AdjustResult(PartInventoryOutcome.InvalidRequest, null, 0, "Harvest adjustments must be positive.");
        }

        if (command.Reason == PartAdjustmentReason.QcReject && command.Delta > 0)
        {
            return new AdjustResult(PartInventoryOutcome.InvalidRequest, null, 0, "QC-reject adjustments must be negative.");
        }

        string trimmedSku = PartInventoryIdentity.NormalizeSku(sku);
        string? operationKey = PartInventoryIdentity.NormalizeOperationKey(command.OperationKey);
        if (operationKey?.Length > 128)
        {
            return new AdjustResult(PartInventoryOutcome.InvalidRequest, null, 0, "Operation key must be 128 characters or fewer.");
        }

        // Pre-check idempotent replay against committed state, so happy-path
        // retries do not open a transaction at all.
        if (operationKey is not null)
        {
            AdjustResult? replay = await TryReplayAsync(trimmedSku, operationKey, ct);
            if (replay is not null)
            {
                return replay;
            }
        }

        for (int attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            AdjustResult result = await TryAdjustOnceAsync(trimmedSku, command, operationKey, ct);
            switch (result.Outcome)
            {
                case PartInventoryOutcome.Conflict when attempt < MaxConcurrencyRetries - 1:
                    // Benign RowVersion collision from a concurrent writer on the same SKU.
                    // Fresh state on the next iteration lets us serialize behind them.
                    logger.LogDebug(
                        "Retrying AdjustAsync for SKU {Sku} after concurrency collision (attempt {Attempt}).",
                        trimmedSku,
                        attempt + 1);
                    continue;
                default:
                    return result;
            }
        }

        return new AdjustResult(
            PartInventoryOutcome.Conflict,
            null,
            0,
            "Persistent concurrency conflict; please retry.");
    }

    public async Task<CreatePartResult> CreatePartAsync(CreatePartCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (featureGate is not null && !featureGate.IsEnabled(OperatorFeature.PrintedPartsInventory))
        {
            return new CreatePartResult(PartInventoryOutcome.FeatureDisabled, null, "Printed-parts inventory is disabled.");
        }

        if (string.IsNullOrWhiteSpace(command.Sku))
        {
            return new CreatePartResult(PartInventoryOutcome.InvalidRequest, null, "Sku is required.");
        }

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return new CreatePartResult(PartInventoryOutcome.InvalidRequest, null, "Name is required.");
        }

        if (command.InitialOnHand < 0 || command.ReorderPoint < 0)
        {
            return new CreatePartResult(PartInventoryOutcome.InvalidRequest, null, "InitialOnHand and ReorderPoint must be non-negative.");
        }

        string trimmedSku = PartInventoryIdentity.NormalizeSku(command.Sku);
        if (trimmedSku.Length > 64)
        {
            return new CreatePartResult(PartInventoryOutcome.InvalidRequest, null, "Sku must be 64 characters or fewer.");
        }

        string trimmedName = command.Name.Trim();
        if (trimmedName.Length > 200)
        {
            return new CreatePartResult(PartInventoryOutcome.InvalidRequest, null, "Name must be 200 characters or fewer.");
        }

        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Guid? defaultBinId = null;
        if (!string.IsNullOrWhiteSpace(command.DefaultBinCode))
        {
            string binCode = PartInventoryIdentity.NormalizeBinCode(command.DefaultBinCode);
            Bin? bin = await db.Bins.AsNoTracking().FirstOrDefaultAsync(b => b.Code == binCode, ct);
            if (bin is null || !bin.IsActive)
            {
                return new CreatePartResult(PartInventoryOutcome.BinNotFound, null,
                    $"Default bin '{binCode}' not found or inactive.");
            }

            defaultBinId = bin.Id;
        }

        bool relational = db.Database.IsRelational();
        IDbContextTransaction? transaction = relational
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        try
        {
            PartInventory? existing = await db.PartInventories
                .FirstOrDefaultAsync(p => p.Sku == trimmedSku, ct);
            if (existing is not null)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(ct);
                }

                return new CreatePartResult(PartInventoryOutcome.SkuAlreadyExists, null,
                    $"SKU '{trimmedSku}' already exists.");
            }

            var part = new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = trimmedSku,
                Name = trimmedName,
                Description = command.Description,
                ModelFileRef = command.ModelFileRef,
                DefaultBinId = defaultBinId,
                OnHand = 0,
                ReorderPoint = command.ReorderPoint,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.PartInventories.Add(part);

            if (command.InitialOnHand > 0)
            {
                var seed = new PartInventoryAdjustment
                {
                    Id = Guid.NewGuid(),
                    PartInventoryId = part.Id,
                    BinId = defaultBinId,
                    Delta = command.InitialOnHand,
                    ResultingBalance = command.InitialOnHand,
                    Reason = PartAdjustmentReason.Manual,
                    OperationKey = null,
                    Notes = "Initial stock seeded on create.",
                    UserId = command.UserId,
                    CreatedAt = DateTime.UtcNow,
                };
                _ = db.PartInventoryAdjustments.Add(seed);
                part.OnHand = command.InitialOnHand;
            }

            _ = await db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            // Reload from a fresh context so the caller sees the committed
            // row, not a tracked in-memory graph.
            await using AppDbContext readDb = await dbFactory.CreateDbContextAsync(ct);
            PartInventory? committed = await readDb.PartInventories
                .Include(p => p.DefaultBin)
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == part.Id, ct);
            return new CreatePartResult(PartInventoryOutcome.Ok, committed ?? part, null);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }

            logger.LogInformation(ex, "Unique-constraint conflict creating SKU {Sku}; treating as already-exists.", trimmedSku);
            await using AppDbContext readDb = await dbFactory.CreateDbContextAsync(ct);
            PartInventory? existing = await readDb.PartInventories
                .Include(p => p.DefaultBin)
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Sku == trimmedSku, ct);
            return new CreatePartResult(
                PartInventoryOutcome.SkuAlreadyExists,
                existing,
                $"SKU '{trimmedSku}' already exists.");
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(ct);
                }
                catch
                {
                    // swallow — the outer catch will observe the transaction as rolled back either way.
                }
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<AdjustResult> TryAdjustOnceAsync(
        string trimmedSku,
        AdjustCommand command,
        string? operationKey,
        CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);

        bool relational = db.Database.IsRelational();
        IDbContextTransaction? transaction = relational
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        try
        {
            PartInventory? part = await db.PartInventories
                .FirstOrDefaultAsync(p => p.Sku == trimmedSku, ct);
            if (part is null)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(ct);
                }

                return new AdjustResult(PartInventoryOutcome.PartNotFound, null, 0, $"SKU '{trimmedSku}' not found.");
            }

            if (!part.IsActive)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(ct);
                }

                return new AdjustResult(PartInventoryOutcome.InvalidRequest, null, part.OnHand, $"SKU '{trimmedSku}' is inactive.");
            }

            Guid? binId = null;
            if (!string.IsNullOrWhiteSpace(command.BinCode))
            {
                string trimmedCode = PartInventoryIdentity.NormalizeBinCode(command.BinCode);
                Bin? bin = await db.Bins.AsNoTracking().FirstOrDefaultAsync(b => b.Code == trimmedCode, ct);
                if (bin is null || !bin.IsActive)
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync(ct);
                    }

                    return new AdjustResult(PartInventoryOutcome.BinNotFound, null, part.OnHand,
                        $"Bin '{trimmedCode}' not found or inactive.");
                }

                binId = bin.Id;
            }

            if (command.PrintJobId is Guid printJobId
                && !await db.PrintJobs.AsNoTracking().AnyAsync(job => job.Id == printJobId, ct))
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(ct);
                }

                return new AdjustResult(
                    PartInventoryOutcome.JobNotFound,
                    null,
                    part.OnHand,
                    $"Print job '{printJobId}' not found.");
            }

            long proposedOnHand = (long)part.OnHand + command.Delta;
            if (proposedOnHand is < 0 or > int.MaxValue)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(ct);
                }

                string message = proposedOnHand < 0
                    ? "Adjustment would make on-hand stock negative."
                    : "Adjustment would overflow on-hand stock.";
                return new AdjustResult(
                    PartInventoryOutcome.InvalidRequest,
                    null,
                    part.OnHand,
                    message);
            }

            var adjustment = new PartInventoryAdjustment
            {
                Id = Guid.NewGuid(),
                PartInventoryId = part.Id,
                BinId = binId,
                Delta = command.Delta,
                ResultingBalance = (int)proposedOnHand,
                Reason = command.Reason,
                PrintJobId = command.PrintJobId,
                OperationKey = operationKey,
                Notes = command.Notes,
                UserId = command.UserId,
                CreatedAt = DateTime.UtcNow,
            };
            _ = db.PartInventoryAdjustments.Add(adjustment);

            part.OnHand = (int)proposedOnHand;
            part.UpdatedAt = DateTime.UtcNow;

            _ = await db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            // Reload the bin for the DTO (adjustment.Bin may be null when we
            // resolved binId via AsNoTracking above).
            Bin? binForDto = binId.HasValue
                ? await db.Bins.AsNoTracking().FirstOrDefaultAsync(b => b.Id == binId, ct)
                : null;
            adjustment.Bin = binForDto;
            return new AdjustResult(PartInventoryOutcome.Ok, ToDto(adjustment, part.Sku), part.OnHand, null);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex) && operationKey is not null)
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(ct);
                }
                catch
                {
                    // swallow — the outer catch will observe the transaction as rolled back either way.
                }
            }

            logger.LogInformation(
                ex,
                "Composite unique conflict on (SKU={Sku}, OperationKey={OperationKey}); returning idempotent replay.",
                trimmedSku,
                operationKey);
            AdjustResult? replay = await TryReplayAsync(trimmedSku, operationKey, ct);
            return replay ?? new AdjustResult(
                PartInventoryOutcome.Conflict,
                null,
                0,
                "Duplicate operation key but prior adjustment could not be reloaded.");
        }
        catch (DbUpdateConcurrencyException ex)
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(ct);
                }
                catch
                {
                    // swallow — the outer catch will observe the transaction as rolled back either way.
                }
            }

            logger.LogInformation(
                ex,
                "Concurrency (RowVersion) conflict on SKU {Sku}; caller will retry.",
                trimmedSku);
            return new AdjustResult(
                PartInventoryOutcome.Conflict,
                null,
                0,
                "Concurrent adjustment collision; please retry.");
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(ct);
                }
                catch
                {
                    // swallow — the outer catch will observe the transaction as rolled back either way.
                }
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Loads the committed adjustment for <paramref name="operationKey"/> under
    /// <paramref name="sku"/>. Returns <c>null</c> when no prior adjustment
    /// exists (caller must proceed with a new insert).
    /// </summary>
    private async Task<AdjustResult?> TryReplayAsync(string sku, string operationKey, CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        PartInventory? part = await db.PartInventories
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Sku == sku, ct);
        if (part is null)
        {
            return null;
        }

        PartInventoryAdjustment? prior = await db.PartInventoryAdjustments
            .AsNoTracking()
            .Include(a => a.Bin)
            .FirstOrDefaultAsync(a => a.PartInventoryId == part.Id && a.OperationKey == operationKey, ct);
        if (prior is null)
        {
            return null;
        }

        return new AdjustResult(
            PartInventoryOutcome.IdempotentReplay,
            ToDto(prior, part.Sku),
            prior.ResultingBalance,
            "Duplicate operation key; existing adjustment returned.");
    }

    /// <summary>
    /// True when the exception represents a unique-constraint violation across
    /// the supported providers (PostgreSQL <c>23505</c>, SQL Server <c>2601/2627</c>,
    /// SQLite <c>SQLITE_CONSTRAINT_UNIQUE</c>).
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Exception? inner = ex.InnerException;
        while (inner is not null)
        {
            string message = inner.Message ?? string.Empty;
            if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("23505", StringComparison.Ordinal)
                || message.Contains("2601", StringComparison.Ordinal)
                || message.Contains("2627", StringComparison.Ordinal))
            {
                return true;
            }

            inner = inner.InnerException;
        }

        return false;
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
            a.ResultingBalance,
            a.Reason,
            a.PrintJobId,
            a.OperationKey,
            a.Notes,
            a.UserId,
            a.CreatedAt);
    }
}

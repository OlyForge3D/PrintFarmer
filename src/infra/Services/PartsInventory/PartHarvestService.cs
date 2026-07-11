using System.Data;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.PartsInventory;

/// <summary>
/// EF Core implementation of <see cref="IPartHarvestService"/>.
/// <para>
/// The full harvest — job stamping, ledger writes, and on-hand updates — is
/// performed inside a single serializable transaction (on relational
/// providers). The composite unique index on
/// <c>(PartInventoryId, OperationKey)</c> plus the <c>HarvestedAt</c> stamp on
/// <see cref="PrintJob"/> together enforce duplicate-safe behaviour even under
/// racing callers.
/// </para>
/// <para>
/// Concurrency contract:
/// <list type="bullet">
///   <item>If the job was harvested before we started, return
///     <see cref="PartInventoryOutcome.IdempotentReplay"/> with the prior
///     adjustments — no writes, no double increment.</item>
///   <item>If a concurrent writer commits before us and we hit a unique
///     conflict, rollback (never commit a PostgreSQL-poisoned transaction),
///     reload the job in a fresh context, and if it is now harvested return
///     an idempotent replay with the committed prior ledger rather than a
///     spurious 409.</item>
///   <item>If a <see cref="DbUpdateConcurrencyException"/> fires on the
///     <see cref="PartInventory"/> row we tried to increment (a different job
///     harvested the same SKU at the same time) we retry a bounded number of
///     times against fresh state before returning a retryable conflict.</item>
/// </list>
/// </para>
/// </summary>
public class PartHarvestService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<PartHarvestService> logger) : IPartHarvestService
{
    /// <summary>Maximum retries for a benign RowVersion collision on a different job / same SKU.</summary>
    private const int MaxConcurrencyRetries = 3;

    public async Task<HarvestResult> HarvestJobAsync(
        Guid jobId,
        HarvestJobRequest request,
        string? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Pre-check idempotent replay against committed state.
        HarvestResult? preReplay = await TryLoadHarvestedReplayAsync(jobId, ct);
        if (preReplay is not null)
        {
            return preReplay;
        }

        for (int attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            HarvestResult result = await TryHarvestOnceAsync(jobId, request, userId, ct);
            if (result.Outcome == PartInventoryOutcome.Conflict && attempt < MaxConcurrencyRetries - 1)
            {
                logger.LogDebug(
                    "Retrying HarvestJobAsync for job {JobId} after benign concurrency collision (attempt {Attempt}).",
                    jobId,
                    attempt + 1);
                continue;
            }

            return result;
        }

        return new HarvestResult(
            PartInventoryOutcome.Conflict,
            null,
            "Persistent concurrency conflict; please retry.");
    }

    private async Task<HarvestResult> TryHarvestOnceAsync(
        Guid jobId,
        HarvestJobRequest request,
        string? userId,
        CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);

        PrintJob? job = await db.PrintJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            return new HarvestResult(PartInventoryOutcome.JobNotFound, null, $"Print job '{jobId}' not found.");
        }

        if (job.Status != PrintJobStatus.Completed)
        {
            return new HarvestResult(
                PartInventoryOutcome.JobNotCompleted,
                null,
                $"Print job '{jobId}' is in status {job.Status}; only Completed jobs can be harvested.");
        }

        if (job.HarvestedAt is not null)
        {
            return await LoadPriorHarvestAsync(db, job, ct);
        }

        Bin? bin = null;
        if (!string.IsNullOrWhiteSpace(request.BinCode))
        {
            string code = request.BinCode.Trim();
            bin = await db.Bins.FirstOrDefaultAsync(b => b.Code == code && b.IsActive, ct);
            if (bin is null)
            {
                return new HarvestResult(
                    PartInventoryOutcome.BinNotFound,
                    null,
                    $"Bin '{code}' not found or inactive.");
            }
        }

        (HarvestResult? failure, List<(PartInventory Part, int Quantity)> resolved) =
            await ResolveOutputsAsync(db, job, request, ct);
        if (failure is not null)
        {
            return failure;
        }

        string opKey = string.IsNullOrWhiteSpace(request.OperationKey)
            ? $"harvest:{jobId:N}"
            : request.OperationKey.Trim();

        bool relational = db.Database.IsRelational();
        IDbContextTransaction? transaction = relational
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        try
        {
            HarvestResult result = await CommitHarvestAsync(db, job, bin, resolved, opKey, userId, ct);
            if (result.Outcome == PartInventoryOutcome.Ok && transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            else if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }

            return result;
        }
        catch (DbUpdateException ex) when (PartInventoryService.IsUniqueViolation(ex))
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
                "Unique-constraint conflict harvesting job {JobId}; checking for concurrent commit.",
                jobId);

            // A concurrent writer may have committed just before us. Reload
            // from a fresh context to see the true committed state.
            HarvestResult? replay = await TryLoadHarvestedReplayAsync(jobId, ct);
            if (replay is not null)
            {
                return replay;
            }

            // Different-job / same-SKU collision on the composite unique index
            // will not fire (opKey embeds jobId), so this is genuinely retryable.
            return new HarvestResult(
                PartInventoryOutcome.Conflict,
                null,
                "Concurrent harvest collision; please retry.");
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
                "Concurrency (RowVersion) conflict on PartInventory during harvest of job {JobId}; caller will retry.",
                jobId);

            // Might be the same job racing (unlikely — HarvestedAt guard) or
            // a *different* job racing on the same SKU. Either way we bounce
            // to the outer retry loop with fresh state.
            HarvestResult? replay = await TryLoadHarvestedReplayAsync(jobId, ct);
            if (replay is not null)
            {
                return replay;
            }

            return new HarvestResult(
                PartInventoryOutcome.Conflict,
                null,
                "Concurrent stock update collision; please retry.");
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
    /// Deterministic mapping-resolution precedence for a harvest:
    ///   1. Request.Outputs (manual/override).
    ///   2. Project-file mapping (<see cref="PrintJob.ProjectFileId"/>).
    ///   3. G-code file mapping (<see cref="PrintJob.GcodeFileId"/>).
    /// </summary>
    private static async Task<(HarvestResult? Failure, List<(PartInventory Part, int Quantity)> Resolved)>
        ResolveOutputsAsync(AppDbContext db, PrintJob job, HarvestJobRequest request, CancellationToken ct)
    {
        var resolved = new List<(PartInventory Part, int Quantity)>();

        if (request.Outputs is not null && request.Outputs.Count > 0)
        {
            foreach (HarvestOutputRequestItem item in request.Outputs)
            {
                if (item.Quantity <= 0)
                {
                    return (new HarvestResult(PartInventoryOutcome.InvalidRequest, null,
                        $"Output quantity for SKU '{item.Sku}' must be positive."), resolved);
                }

                string sku = item.Sku.Trim();
                PartInventory? part = await db.PartInventories.FirstOrDefaultAsync(p => p.Sku == sku, ct);
                if (part is null)
                {
                    return (new HarvestResult(PartInventoryOutcome.PartNotFound, null,
                        $"SKU '{sku}' not found."), resolved);
                }

                if (!part.IsActive)
                {
                    return (new HarvestResult(PartInventoryOutcome.InvalidRequest, null,
                        $"SKU '{sku}' is inactive."), resolved);
                }

                resolved.Add((part, item.Quantity));
            }

            return (null, resolved);
        }

        List<PartOutputMapping> mappings = [];
        if (job.ProjectFileId is Guid pfid)
        {
            mappings = await db.PartOutputMappings
                .Include(m => m.PartInventory)
                .Where(m => m.PrintProjectFileId == pfid)
                .ToListAsync(ct);
        }

        if (mappings.Count == 0 && job.GcodeFileId is Guid gfid)
        {
            mappings = await db.PartOutputMappings
                .Include(m => m.PartInventory)
                .Where(m => m.GcodeFileId == gfid)
                .ToListAsync(ct);
        }

        if (mappings.Count == 0)
        {
            return (new HarvestResult(
                PartInventoryOutcome.NoMappings,
                null,
                "No output mappings configured for this job. Supply an 'outputs' array in the request."), resolved);
        }

        int copies = request.QuantityOverride ?? Math.Max(1, job.Copies);
        foreach (PartOutputMapping mapping in mappings)
        {
            if (mapping.PartInventory is null || !mapping.PartInventory.IsActive)
            {
                continue;
            }

            resolved.Add((mapping.PartInventory, mapping.Quantity * copies));
        }

        if (resolved.Count == 0)
        {
            return (new HarvestResult(
                PartInventoryOutcome.NoMappings,
                null,
                "Job has mappings, but every mapped SKU is inactive."), resolved);
        }

        return (null, resolved);
    }

    private static async Task<HarvestResult> CommitHarvestAsync(
        AppDbContext db,
        PrintJob job,
        Bin? bin,
        List<(PartInventory Part, int Quantity)> outputs,
        string operationKey,
        string? userId,
        CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        var responses = new List<PartAdjustmentResponse>(outputs.Count);

        for (int i = 0; i < outputs.Count; i++)
        {
            (PartInventory part, int quantity) = outputs[i];
            if (quantity <= 0)
            {
                return new HarvestResult(PartInventoryOutcome.InvalidRequest, null,
                    $"Non-positive output quantity for SKU '{part.Sku}'.");
            }

            if (db.Entry(part).State == EntityState.Detached)
            {
                _ = db.PartInventories.Attach(part);
            }

            part.OnHand += quantity;
            part.UpdatedAt = now;

            string entryKey = outputs.Count == 1
                ? operationKey
                : $"{operationKey}:{i}";

            var adjustment = new PartInventoryAdjustment
            {
                Id = Guid.NewGuid(),
                PartInventoryId = part.Id,
                BinId = bin?.Id,
                Delta = quantity,
                Reason = PartAdjustmentReason.Harvest,
                PrintJobId = job.Id,
                OperationKey = entryKey,
                Notes = null,
                UserId = userId,
                CreatedAt = now,
                Bin = bin,
            };
            _ = db.PartInventoryAdjustments.Add(adjustment);
            responses.Add(PartInventoryService.ToDto(adjustment, part.Sku));
        }

        job.HarvestedAt = now;
        job.HarvestOperationKey = operationKey;
        job.HarvestedByUserId = userId;
        job.HarvestedIntoBinId = bin?.Id;

        _ = await db.SaveChangesAsync(ct);

        return new HarvestResult(
            PartInventoryOutcome.Ok,
            new HarvestJobResponse(
                job.Id,
                now,
                bin?.Id,
                bin?.Code,
                AlreadyHarvested: false,
                responses),
            null);
    }

    /// <summary>
    /// Loads the prior harvest for <paramref name="jobId"/> from a fresh
    /// context; returns <c>null</c> when the job is not yet harvested.
    /// </summary>
    private async Task<HarvestResult?> TryLoadHarvestedReplayAsync(Guid jobId, CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        PrintJob? job = await db.PrintJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.HarvestedAt is null)
        {
            return null;
        }

        return await LoadPriorHarvestAsync(db, job, ct);
    }

    private static async Task<HarvestResult> LoadPriorHarvestAsync(AppDbContext db, PrintJob job, CancellationToken ct)
    {
        List<PartInventoryAdjustment> prior = await db.PartInventoryAdjustments
            .AsNoTracking()
            .Include(a => a.Bin)
            .Include(a => a.PartInventory)
            .Where(a => a.PrintJobId == job.Id)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        Bin? existingBin = job.HarvestedIntoBinId.HasValue
            ? await db.Bins.AsNoTracking().FirstOrDefaultAsync(b => b.Id == job.HarvestedIntoBinId, ct)
            : null;

        IReadOnlyList<PartAdjustmentResponse> priorDtos = prior
            .Select(a => PartInventoryService.ToDto(a, a.PartInventory?.Sku ?? string.Empty))
            .ToList();

        return new HarvestResult(
            PartInventoryOutcome.IdempotentReplay,
            new HarvestJobResponse(
                job.Id,
                job.HarvestedAt!.Value,
                job.HarvestedIntoBinId,
                existingBin?.Code,
                AlreadyHarvested: true,
                priorDtos),
            "Job already harvested; existing adjustments returned.");
    }
}

/// <summary>
/// Default reorder evaluation implementation used both by the parts-inventory API
/// and (via the same interface) by the F8 shift compiler in #713.
/// </summary>
public class ReorderEvaluationService(IDbContextFactory<AppDbContext> dbFactory) : IReorderEvaluationService
{
    public async Task<IReadOnlyList<ReorderCandidateResponse>> GetReorderCandidatesAsync(CancellationToken ct = default)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<PartInventory> below = await db.PartInventories
            .AsNoTracking()
            .Where(p => p.IsActive && p.OnHand < p.ReorderPoint)
            .OrderBy(p => p.Sku)
            .ToListAsync(ct);

        return below
            .Select(p => new ReorderCandidateResponse(
                p.Id,
                p.Sku,
                p.Name,
                p.OnHand,
                p.ReorderPoint,
                Math.Max(0, p.ReorderPoint - p.OnHand)))
            .ToList();
    }
}

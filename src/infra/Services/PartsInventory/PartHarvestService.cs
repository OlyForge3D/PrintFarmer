using System.Data;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.PartsInventory;

/// <summary>
/// EF Core implementation of <see cref="IPartHarvestService"/>.
/// The full harvest — job stamping, ledger writes, on-hand updates, and
/// tracking that harvest cannot re-run — is performed inside a single
/// serializable transaction. The <c>HarvestOperationKey</c> column on
/// <see cref="PrintJob"/> plus the unique <c>OperationKey</c> on the
/// ledger enforce duplicate-safe behavior even under racing callers.
/// </summary>
public class PartHarvestService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<PartHarvestService> logger) : IPartHarvestService
{
    public async Task<HarvestResult> HarvestJobAsync(
        Guid jobId,
        HarvestJobRequest request,
        string? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);

        PrintJob? job = await db.PrintJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
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

        // Duplicate-safe replay: if the job has already been harvested,
        // return the prior adjustments instead of applying the delta twice.
        if (job.HarvestedAt is not null)
        {
            List<PartInventoryAdjustment> prior = await db.PartInventoryAdjustments
                .AsNoTracking()
                .Include(a => a.Bin)
                .Include(a => a.PartInventory)
                .Where(a => a.PrintJobId == jobId)
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
                    job.HarvestedAt.Value,
                    job.HarvestedIntoBinId,
                    existingBin?.Code,
                    AlreadyHarvested: true,
                    priorDtos),
                "Job already harvested; existing adjustments returned.");
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

        // Resolve outputs. Priority order:
        //   1. Explicit outputs list on the request (manual override).
        //   2. Mapping keyed on the job's ProjectFile.
        //   3. Mapping keyed on the job's GcodeFile.
        var resolved = new List<(PartInventory Part, int Quantity)>();

        if (request.Outputs is not null && request.Outputs.Count > 0)
        {
            foreach (HarvestOutputRequestItem item in request.Outputs)
            {
                if (item.Quantity <= 0)
                {
                    return new HarvestResult(PartInventoryOutcome.InvalidRequest, null,
                        $"Output quantity for SKU '{item.Sku}' must be positive.");
                }

                string sku = item.Sku.Trim();
                PartInventory? part = await db.PartInventories
                    .FirstOrDefaultAsync(p => p.Sku == sku, ct);
                if (part is null)
                {
                    return new HarvestResult(PartInventoryOutcome.PartNotFound, null,
                        $"SKU '{sku}' not found.");
                }

                if (!part.IsActive)
                {
                    return new HarvestResult(PartInventoryOutcome.InvalidRequest, null,
                        $"SKU '{sku}' is inactive.");
                }

                resolved.Add((part, item.Quantity));
            }
        }
        else
        {
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
                return new HarvestResult(
                    PartInventoryOutcome.NoMappings,
                    null,
                    "No output mappings configured for this job. Supply an 'outputs' array in the request.");
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
                return new HarvestResult(
                    PartInventoryOutcome.NoMappings,
                    null,
                    "Job has mappings, but every mapped SKU is inactive.");
            }
        }

        string opKey = string.IsNullOrWhiteSpace(request.OperationKey)
            ? $"harvest:{jobId:N}"
            : request.OperationKey.Trim();

        // Enter a serializable transaction so a concurrent harvest either
        // sees this job's HarvestedAt update or fails its own commit.
        if (db.Database.IsRelational())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            HarvestResult result = await CommitHarvestAsync(db, job, bin, resolved, opKey, userId, ct);
            if (result.Outcome is PartInventoryOutcome.Ok)
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }

            return result;
        }

        return await CommitHarvestAsync(db, job, bin, resolved, opKey, userId, ct);
    }

    private async Task<HarvestResult> CommitHarvestAsync(
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

            // Attach if fetched via AsNoTracking or a prior include.
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

        try
        {
            _ = await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PartInventoryService.IsUniqueViolation(ex))
        {
            logger.LogWarning(ex, "Concurrent harvest detected for job {JobId}; returning conflict.", job.Id);
            return new HarvestResult(
                PartInventoryOutcome.Conflict,
                null,
                "Concurrent harvest already recorded for this job.");
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "Concurrent harvest concurrency for job {JobId}; returning conflict.", job.Id);
            return new HarvestResult(
                PartInventoryOutcome.Conflict,
                null,
                "Concurrent harvest already recorded for this job.");
        }

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

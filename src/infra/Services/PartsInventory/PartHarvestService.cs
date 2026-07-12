using System.Data;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.PartsInventory;

/// <summary>
/// Atomically claims a completed print job, increments every mapped SKU, and appends the
/// immutable harvest ledger. The job remains <see cref="PrintJobStatus.Completed"/>.
/// </summary>
public class PartHarvestService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<PartHarvestService> logger,
    IAttentionBroadcaster? attentionBroadcaster = null,
    IOperatorFeatureGate? featureGate = null) : IPartHarvestService
{
    private const int MaxHarvestQuantityPerSku = 10000;
    private const int MaxConcurrencyRetries = 3;

    /// <inheritdoc />
    public async Task<HarvestResult> HarvestJobAsync(
        Guid jobId,
        HarvestJobRequest request,
        string? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (featureGate is not null && !featureGate.IsEnabled(OperatorFeature.PrintedPartsInventory))
        {
            return new HarvestResult(
                PartInventoryOutcome.FeatureDisabled,
                null,
                "Printed-parts inventory is disabled.");
        }

        string? operationKey = PartInventoryIdentity.NormalizeOperationKey(request.OperationKey);
        if (operationKey?.Length > 128)
        {
            return new HarvestResult(
                PartInventoryOutcome.InvalidRequest,
                null,
                "Operation key must be 128 characters or fewer.");
        }

        HarvestResult? replay = await TryLoadHarvestedReplayAsync(jobId, ct);
        if (replay is not null)
        {
            return replay;
        }

        for (int attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            HarvestResult result = await TryHarvestOnceAsync(jobId, request, operationKey, userId, ct);
            if (result.Outcome != PartInventoryOutcome.Conflict || attempt == MaxConcurrencyRetries - 1)
            {
                return result;
            }

            replay = await TryLoadHarvestedReplayAsync(jobId, ct);
            if (replay is not null)
            {
                return replay;
            }
        }

        return new HarvestResult(
            PartInventoryOutcome.Conflict,
            null,
            "Persistent concurrency conflict; please retry.");
    }

    private async Task<HarvestResult> TryHarvestOnceAsync(
        Guid jobId,
        HarvestJobRequest request,
        string? requestedOperationKey,
        string? userId,
        CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        bool relational = db.Database.IsRelational();
        IDbContextTransaction? transaction = relational
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            : null;

        try
        {
            PrintJob? job = await db.PrintJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null)
            {
                return await RollbackAndReturnAsync(
                    transaction,
                    new HarvestResult(PartInventoryOutcome.JobNotFound, null, $"Print job '{jobId}' not found."),
                    ct);
            }

            if (job.Status != PrintJobStatus.Completed)
            {
                return await RollbackAndReturnAsync(
                    transaction,
                    new HarvestResult(
                        PartInventoryOutcome.JobNotCompleted,
                        null,
                        $"Print job '{jobId}' is in status {job.Status}; only Completed jobs can be harvested."),
                    ct);
            }

            if (job.HarvestedAt is not null)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(ct);
                }

                return await LoadPriorHarvestAsync(db, job, ct);
            }

            (HarvestResult? outputFailure, List<ResolvedOutput> outputs) =
                await ResolveOutputsAsync(db, job, request, ct);
            if (outputFailure is not null)
            {
                return await RollbackAndReturnAsync(transaction, outputFailure, ct);
            }

            (HarvestResult? binFailure, Bin? bin) = await ResolveBinAsync(db, request.BinCode, outputs, ct);
            if (binFailure is not null)
            {
                return await RollbackAndReturnAsync(transaction, binFailure, ct);
            }

            DateTime now = DateTime.UtcNow;
            string operationKey = requestedOperationKey ?? $"harvest:{jobId:N}";

            int claimed;
            if (relational)
            {
                claimed = await db.PrintJobs
                    .Where(j => j.Id == jobId
                        && j.Status == PrintJobStatus.Completed
                        && j.HarvestedAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(j => j.HarvestedAt, now)
                            .SetProperty(j => j.HarvestOperationKey, operationKey)
                            .SetProperty(j => j.HarvestedByUserId, userId)
                            .SetProperty(j => j.HarvestedIntoBinId, bin == null ? null : bin.Id),
                        ct);
            }
            else
            {
                PrintJob? trackedJob = await db.PrintJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
                claimed = trackedJob is not null && trackedJob.HarvestedAt is null ? 1 : 0;
                if (claimed == 1)
                {
                    trackedJob!.HarvestedAt = now;
                    trackedJob.HarvestOperationKey = operationKey;
                    trackedJob.HarvestedByUserId = userId;
                    trackedJob.HarvestedIntoBinId = bin?.Id;
                }
            }

            if (claimed != 1)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(ct);
                }

                return await TryLoadHarvestedReplayAsync(jobId, ct)
                    ?? new HarvestResult(PartInventoryOutcome.Conflict, null, "Concurrent harvest collision; please retry.");
            }

            var responses = new List<PartAdjustmentResponse>(outputs.Count);
            foreach (ResolvedOutput output in outputs)
            {
                int updated;
                if (relational)
                {
                    int maxCurrentBalance = int.MaxValue - output.Quantity;
                    updated = await db.PartInventories
                        .Where(p => p.Id == output.Part.Id
                            && p.IsActive
                            && p.OnHand <= maxCurrentBalance)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(p => p.OnHand, p => p.OnHand + output.Quantity)
                                .SetProperty(p => p.UpdatedAt, now),
                            ct);
                }
                else
                {
                    PartInventory? trackedPart = await db.PartInventories.FirstOrDefaultAsync(p => p.Id == output.Part.Id, ct);
                    long proposed = trackedPart is null ? -1 : (long)trackedPart.OnHand + output.Quantity;
                    updated = trackedPart is not null && trackedPart.IsActive && proposed <= int.MaxValue ? 1 : 0;
                    if (updated == 1)
                    {
                        trackedPart!.OnHand = (int)proposed;
                        trackedPart.UpdatedAt = now;
                    }
                }

                if (updated != 1)
                {
                    return await RollbackAndReturnAsync(
                        transaction,
                        new HarvestResult(
                            PartInventoryOutcome.InvalidRequest,
                            null,
                            $"SKU '{output.Part.Sku}' is inactive, missing, or would overflow on-hand stock."),
                        ct);
                }

                int resultingBalance = relational
                    ? await db.PartInventories
                        .AsNoTracking()
                        .Where(part => part.Id == output.Part.Id)
                        .Select(part => part.OnHand)
                        .SingleAsync(ct)
                    : await db.PartInventories
                        .Where(part => part.Id == output.Part.Id)
                        .Select(part => part.OnHand)
                        .SingleAsync(ct);

                string ledgerOperationKey = $"{operationKey}:{output.Part.Id:N}";
                var adjustment = new PartInventoryAdjustment
                {
                    Id = Guid.NewGuid(),
                    PartInventoryId = output.Part.Id,
                    BinId = bin?.Id,
                    Delta = output.Quantity,
                    ResultingBalance = resultingBalance,
                    Reason = PartAdjustmentReason.Harvest,
                    PrintJobId = jobId,
                    OperationKey = ledgerOperationKey,
                    UserId = userId,
                    CreatedAt = now,
                };
                _ = db.PartInventoryAdjustments.Add(adjustment);
                responses.Add(new PartAdjustmentResponse(
                    adjustment.Id,
                    adjustment.PartInventoryId,
                    output.Part.Sku,
                    adjustment.BinId,
                    bin?.Code,
                    adjustment.Delta,
                    adjustment.ResultingBalance,
                    adjustment.Reason,
                    adjustment.PrintJobId,
                    adjustment.OperationKey,
                    adjustment.Notes,
                    adjustment.UserId,
                    adjustment.CreatedAt));
            }

            if (bin is not null && !string.IsNullOrWhiteSpace(request.BinCode))
            {
                _ = db.BarcodeScanLogs.Add(new BarcodeScanLog
                {
                    Timestamp = now,
                    Barcode = bin.Code,
                    Action = BarcodeScanAction.Harvest,
                    Outcome = BarcodeScanOutcome.Resolved,
                    HttpStatus = 200,
                    BinId = bin.Id,
                    PartInventoryId = outputs.Count == 1 ? outputs[0].Part.Id : null,
                    UserId = userId,
                    Message = $"Harvested print job {jobId:D}.",
                });
            }

            _ = await db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            var response = new HarvestJobResponse(
                jobId,
                now,
                bin?.Id,
                bin?.Code,
                AlreadyHarvested: false,
                responses);

            await NotifyHarvestResolvedAsync(jobId, now, ct);
            return new HarvestResult(PartInventoryOutcome.Ok, response, null);
        }
        catch (DbUpdateException ex) when (PartInventoryService.IsUniqueViolation(ex))
        {
            await TryRollbackAsync(transaction, ct);
            logger.LogInformation(ex, "Unique conflict harvesting job {JobId}; checking committed state.", jobId);
            return await TryLoadHarvestedReplayAsync(jobId, ct)
                ?? new HarvestResult(PartInventoryOutcome.Conflict, null, "Concurrent harvest collision; please retry.");
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await TryRollbackAsync(transaction, ct);
            logger.LogInformation(ex, "Concurrency conflict harvesting job {JobId}.", jobId);
            return await TryLoadHarvestedReplayAsync(jobId, ct)
                ?? new HarvestResult(PartInventoryOutcome.Conflict, null, "Concurrent harvest collision; please retry.");
        }
        catch
        {
            await TryRollbackAsync(transaction, ct);
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

    private static async Task<(HarvestResult? Failure, List<ResolvedOutput> Outputs)> ResolveOutputsAsync(
        AppDbContext db,
        PrintJob job,
        HarvestJobRequest request,
        CancellationToken ct)
    {
        var resolved = new List<ResolvedOutput>();

        if (request.Outputs is { Count: > 0 })
        {
            if (request.QuantityOverride is not null)
            {
                return (new HarvestResult(
                    PartInventoryOutcome.InvalidRequest,
                    null,
                    "quantityOverride cannot be combined with explicit outputs; output quantities are final counts."), resolved);
            }

            var seenSkus = new HashSet<string>(StringComparer.Ordinal);
            foreach (HarvestOutputRequestItem item in request.Outputs)
            {
                if (item.Quantity is < 1 or > MaxHarvestQuantityPerSku)
                {
                    return (new HarvestResult(
                        PartInventoryOutcome.InvalidRequest,
                        null,
                        $"Output quantity for SKU '{item.Sku}' must be between 1 and {MaxHarvestQuantityPerSku}."), resolved);
                }

                string sku = PartInventoryIdentity.NormalizeSku(item.Sku);
                if (!seenSkus.Add(sku))
                {
                    return (new HarvestResult(
                        PartInventoryOutcome.InvalidRequest,
                        null,
                        $"Duplicate output SKU '{sku}' is not allowed."), resolved);
                }

                PartInventory? part = await db.PartInventories
                    .AsNoTracking()
                    .Include(p => p.DefaultBin)
                    .FirstOrDefaultAsync(p => p.Sku == sku, ct);
                if (part is null)
                {
                    return (new HarvestResult(PartInventoryOutcome.PartNotFound, null, $"SKU '{sku}' not found."), resolved);
                }

                if (!part.IsActive)
                {
                    return (new HarvestResult(PartInventoryOutcome.InvalidRequest, null, $"SKU '{sku}' is inactive."), resolved);
                }

                resolved.Add(new ResolvedOutput(part, item.Quantity));
            }

            return (null, resolved);
        }

        int copies = request.QuantityOverride ?? Math.Max(1, job.Copies);
        if (copies is < 1 or > MaxHarvestQuantityPerSku)
        {
            return (new HarvestResult(
                PartInventoryOutcome.InvalidRequest,
                null,
                $"quantityOverride must be between 1 and {MaxHarvestQuantityPerSku}."), resolved);
        }

        IQueryable<PartOutputMapping> mappingQuery = db.PartOutputMappings
            .AsNoTracking()
            .Include(m => m.PartInventory)
            .ThenInclude(p => p!.DefaultBin);

        List<PartOutputMapping> mappings = job.ProjectFileId is Guid projectFileId
            ? await mappingQuery
                .Where(m => m.PrintProjectFileId == projectFileId)
                .OrderBy(m => m.PartInventory!.Sku)
                .ToListAsync(ct)
            : [];

        if (mappings.Count == 0 && job.GcodeFileId is Guid gcodeFileId)
        {
            mappings = await mappingQuery
                .Where(m => m.GcodeFileId == gcodeFileId)
                .OrderBy(m => m.PartInventory!.Sku)
                .ToListAsync(ct);
        }

        if (mappings.Count == 0)
        {
            return (new HarvestResult(
                PartInventoryOutcome.NoMappings,
                null,
                "No output mappings configured for this job. Supply explicit outputs or configure a mapping."), resolved);
        }

        foreach (PartOutputMapping mapping in mappings)
        {
            if (mapping.PartInventory is null || !mapping.PartInventory.IsActive)
            {
                return (new HarvestResult(
                    PartInventoryOutcome.InvalidRequest,
                    null,
                    "A mapped SKU is missing or inactive."), resolved);
            }

            long quantity = (long)mapping.Quantity * copies;
            if (quantity is < 1 or > MaxHarvestQuantityPerSku)
            {
                return (new HarvestResult(
                    PartInventoryOutcome.InvalidRequest,
                    null,
                    $"Resolved quantity for SKU '{mapping.PartInventory.Sku}' must be between 1 and {MaxHarvestQuantityPerSku}."), resolved);
            }

            resolved.Add(new ResolvedOutput(mapping.PartInventory, (int)quantity));
        }

        return (null, resolved);
    }

    private static async Task<(HarvestResult? Failure, Bin? Bin)> ResolveBinAsync(
        AppDbContext db,
        string? requestedBinCode,
        IReadOnlyList<ResolvedOutput> outputs,
        CancellationToken ct)
    {
        PartInventory? invalidDefault = outputs
            .Select(output => output.Part)
            .FirstOrDefault(part => part.DefaultBinId is not null
                && (part.DefaultBin is null || !part.DefaultBin.IsActive));
        if (invalidDefault is not null)
        {
            return (new HarvestResult(
                PartInventoryOutcome.InvalidRequest,
                null,
                $"SKU '{invalidDefault.Sku}' references a missing or inactive default bin."), null);
        }

        List<Bin> expectedBins = outputs
            .Select(output => output.Part.DefaultBin)
            .Where(bin => bin is not null && bin.IsActive)
            .Cast<Bin>()
            .DistinctBy(bin => bin.Id)
            .OrderBy(bin => bin.Code)
            .ToList();

        Bin? actualBin = null;
        if (!string.IsNullOrWhiteSpace(requestedBinCode))
        {
            string normalizedCode = PartInventoryIdentity.NormalizeBinCode(requestedBinCode);
            actualBin = await db.Bins.AsNoTracking()
                .FirstOrDefaultAsync(bin => bin.Code == normalizedCode && bin.IsActive, ct);
            if (actualBin is null)
            {
                return (new HarvestResult(
                    PartInventoryOutcome.BinNotFound,
                    null,
                    $"Bin '{normalizedCode}' not found or inactive."), null);
            }
        }
        else if (expectedBins.Count == 1)
        {
            actualBin = expectedBins[0];
        }

        if (actualBin is null && expectedBins.Count == 0)
        {
            return (new HarvestResult(
                PartInventoryOutcome.BinNotFound,
                null,
                "No active bin was supplied and the mapped SKU(s) have no common default bin."), null);
        }

        bool wrongBin = expectedBins.Count > 1
            || (actualBin is not null && expectedBins.Any(expected => expected.Id != actualBin.Id));
        if (wrongBin)
        {
            List<string> expectedCodes = expectedBins.Select(bin => bin.Code).ToList();
            string message = actualBin is null
                ? $"Mapped outputs require different bins: {string.Join(", ", expectedCodes)}."
                : $"Bin '{actualBin.Code}' does not match expected bin(s): {string.Join(", ", expectedCodes)}.";
            var details = new WrongBinResponse(actualBin?.Code, expectedCodes, message);
            return (new HarvestResult(PartInventoryOutcome.WrongBin, null, message, details), null);
        }

        return (null, actualBin);
    }

    private async Task<HarvestResult?> TryLoadHarvestedReplayAsync(Guid jobId, CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        PrintJob? job = await db.PrintJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        return job?.HarvestedAt is null ? null : await LoadPriorHarvestAsync(db, job, ct);
    }

    private static async Task<HarvestResult> LoadPriorHarvestAsync(AppDbContext db, PrintJob job, CancellationToken ct)
    {
        List<PartInventoryAdjustment> prior = await db.PartInventoryAdjustments
            .AsNoTracking()
            .Include(a => a.Bin)
            .Include(a => a.PartInventory)
            .Where(a => a.PrintJobId == job.Id && a.Reason == PartAdjustmentReason.Harvest)
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.PartInventoryId)
            .ToListAsync(ct);

        Bin? existingBin = job.HarvestedIntoBinId is Guid binId
            ? await db.Bins.AsNoTracking().FirstOrDefaultAsync(b => b.Id == binId, ct)
            : null;

        return new HarvestResult(
            PartInventoryOutcome.IdempotentReplay,
            new HarvestJobResponse(
                job.Id,
                job.HarvestedAt!.Value,
                job.HarvestedIntoBinId,
                existingBin?.Code,
                AlreadyHarvested: true,
                prior.Select(a => PartInventoryService.ToDto(a, a.PartInventory?.Sku ?? string.Empty)).ToList()),
            "Job already harvested; existing adjustments returned.");
    }

    private async Task NotifyHarvestResolvedAsync(Guid jobId, DateTime occurredAt, CancellationToken ct)
    {
        if (attentionBroadcaster is null)
        {
            return;
        }

        try
        {
            await attentionBroadcaster.NotifyChangedAsync(
                new AttentionChangedPayload(
                    AttentionIdPrefixes.Build(AttentionIdPrefixes.Harvest, jobId),
                    AttentionChangeKind.Resolved,
                    occurredAt),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Harvest for job {JobId} committed, but attention invalidation failed.", jobId);
        }
    }

    private static async Task<HarvestResult> RollbackAndReturnAsync(
        IDbContextTransaction? transaction,
        HarvestResult result,
        CancellationToken ct)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(ct);
        }

        return result;
    }

    private static async Task TryRollbackAsync(IDbContextTransaction? transaction, CancellationToken ct)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(ct);
        }
        catch
        {
            // Preserve the original persistence exception.
        }
    }

    private sealed record ResolvedOutput(PartInventory Part, int Quantity);
}

/// <summary>Returns deterministic printed-part reorder candidates for the future shift compiler.</summary>
public class ReorderEvaluationService(IDbContextFactory<AppDbContext> dbFactory) : IReorderEvaluationService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ReorderCandidateResponse>> GetReorderCandidatesAsync(CancellationToken ct = default)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<PartInventory> below = await db.PartInventories
            .AsNoTracking()
            .Where(p => p.IsActive && p.OnHand <= p.ReorderPoint)
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

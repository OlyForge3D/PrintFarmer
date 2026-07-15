using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.PartsInventory;

/// <summary>Normalized output definition used by dispatch snapshots and harvest resolution.</summary>
public sealed record ResolvedPartOutputDefinition(
    Guid PartInventoryId,
    string Sku,
    int QuantityPerPrint,
    Guid? ExpectedBinId,
    string? ExpectedBinCode,
    PartHarvestOutputOrigin Origin,
    Guid SourceFileId,
    Guid SourceMappingId,
    Guid? JobOutputSnapshotId = null);

/// <summary>Captures immutable printed-output mappings at first successful dispatch.</summary>
public interface IPartOutputSnapshotService
{
    /// <summary>
    /// Adds immutable snapshot rows to the shared scoped context when the job has
    /// mappings and no prior snapshot. The caller owns the transaction and SaveChanges.
    /// </summary>
    Task<bool> CaptureJobSnapshotIfAbsentAsync(PrintJob job, CancellationToken ct = default);
}

/// <summary>Shared production implementation for every dispatch/assignment path.</summary>
public sealed class PartOutputSnapshotService(
    AppDbContext db,
    IOperatorFeatureGate featureGate) : IPartOutputSnapshotService
{
    /// <inheritdoc />
    public async Task<bool> CaptureJobSnapshotIfAbsentAsync(PrintJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!await featureGate.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, ct).ConfigureAwait(false))
        {
            return false;
        }

        bool alreadyCaptured = db.PrintJobPartOutputSnapshots.Local
            .Any(snapshot => snapshot.PrintJobId == job.Id)
            || await db.PrintJobPartOutputSnapshots
                .AsNoTracking()
                .AnyAsync(snapshot => snapshot.PrintJobId == job.Id, ct);
        if (alreadyCaptured)
        {
            return false;
        }

        IReadOnlyList<ResolvedPartOutputDefinition> outputs =
            await PartOutputMappingResolver.ResolveCurrentMappingsAsync(db, job, ct);
        if (outputs.Count == 0)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        foreach ((ResolvedPartOutputDefinition output, int sequence) in outputs.Select((value, index) => (value, index)))
        {
            _ = db.PrintJobPartOutputSnapshots.Add(new PrintJobPartOutputSnapshot
            {
                Id = Guid.NewGuid(),
                PrintJobId = job.Id,
                PartInventoryId = output.PartInventoryId,
                Sku = output.Sku,
                QuantityPerPrint = output.QuantityPerPrint,
                ExpectedBinId = output.ExpectedBinId,
                ExpectedBinCode = output.ExpectedBinCode,
                SourceKind = output.Origin == PartHarvestOutputOrigin.ProjectMapping
                    ? PartOutputMappingSourceKind.ProjectFile
                    : PartOutputMappingSourceKind.GcodeFile,
                SourceFileId = output.SourceFileId,
                SourceMappingId = output.SourceMappingId,
                Sequence = sequence,
                CreatedAt = now,
            });
        }

        return true;
    }
}

/// <summary>Provider-translatable output mapping resolver shared by dispatch and harvest.</summary>
public static class PartOutputMappingResolver
{
    /// <summary>Resolves current mappings using project-file then G-code precedence.</summary>
    public static async Task<IReadOnlyList<ResolvedPartOutputDefinition>> ResolveCurrentMappingsAsync(
        AppDbContext db,
        PrintJob job,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);

        IQueryable<PartOutputMapping> query = db.PartOutputMappings
            .AsNoTracking()
            .Include(mapping => mapping.PartInventory)
            .ThenInclude(part => part!.DefaultBin);

        if (job.ProjectFileId is Guid projectFileId)
        {
            List<PartOutputMapping> projectMappings = await query
                .Where(mapping => mapping.PrintProjectFileId == projectFileId)
                .OrderBy(mapping => mapping.PartInventory!.Sku)
                .ThenBy(mapping => mapping.Id)
                .ToListAsync(ct);
            if (projectMappings.Count > 0)
            {
                return ToDefinitions(
                    projectMappings,
                    PartHarvestOutputOrigin.ProjectMapping,
                    projectFileId);
            }
        }

        if (job.GcodeFileId is Guid gcodeFileId)
        {
            List<PartOutputMapping> gcodeMappings = await query
                .Where(mapping => mapping.GcodeFileId == gcodeFileId)
                .OrderBy(mapping => mapping.PartInventory!.Sku)
                .ThenBy(mapping => mapping.Id)
                .ToListAsync(ct);
            if (gcodeMappings.Count > 0)
            {
                return ToDefinitions(
                    gcodeMappings,
                    PartHarvestOutputOrigin.GcodeMapping,
                    gcodeFileId);
            }
        }

        return [];
    }

    /// <summary>Loads immutable first-dispatch output rows in deterministic order.</summary>
    public static async Task<IReadOnlyList<ResolvedPartOutputDefinition>> LoadJobSnapshotAsync(
        AppDbContext db,
        Guid printJobId,
        CancellationToken ct = default)
    {
        return await db.PrintJobPartOutputSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.PrintJobId == printJobId)
            .OrderBy(snapshot => snapshot.Sequence)
            .Select(snapshot => new ResolvedPartOutputDefinition(
                snapshot.PartInventoryId,
                snapshot.Sku,
                snapshot.QuantityPerPrint,
                snapshot.ExpectedBinId,
                snapshot.ExpectedBinCode,
                PartHarvestOutputOrigin.JobSnapshot,
                snapshot.SourceFileId,
                snapshot.SourceMappingId,
                snapshot.Id))
            .ToListAsync(ct);
    }

    private static List<ResolvedPartOutputDefinition> ToDefinitions(
        IEnumerable<PartOutputMapping> mappings,
        PartHarvestOutputOrigin origin,
        Guid sourceFileId)
    {
        return mappings.Select(mapping =>
        {
            PartInventory part = mapping.PartInventory
                ?? throw new InvalidOperationException($"Output mapping '{mapping.Id}' has no printed-part SKU.");
            return new ResolvedPartOutputDefinition(
                part.Id,
                PartInventoryIdentity.NormalizeSku(part.Sku),
                mapping.Quantity,
                part.DefaultBinId,
                part.DefaultBin?.Code,
                origin,
                sourceFileId,
                mapping.Id);
        }).ToList();
    }
}

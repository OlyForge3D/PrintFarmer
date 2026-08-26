using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Repositories;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Coordinates authoritative family persistence with the worker's derived Custom bundle.
/// </summary>
public sealed class ProfileFamilyService(
    SlicerDbContext dbContext,
    ICatalogServiceAdapter catalogService,
    IPrinterModelAliasService aliasService,
    IProfileFamilyRenderer renderer,
    IProfileFamilyWorkerClient workerClient,
    IPrinterProfileCheckRepository printerReferenceRepository,
    ILogger<ProfileFamilyService> logger) : IProfileFamilyService
{
    private readonly SlicerDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ICatalogServiceAdapter _catalogService =
        catalogService ?? throw new ArgumentNullException(nameof(catalogService));

    private readonly IPrinterModelAliasService _aliasService =
        aliasService ?? throw new ArgumentNullException(nameof(aliasService));

    private readonly IProfileFamilyRenderer _renderer =
        renderer ?? throw new ArgumentNullException(nameof(renderer));

    private readonly IProfileFamilyWorkerClient _workerClient =
        workerClient ?? throw new ArgumentNullException(nameof(workerClient));

    private readonly IPrinterProfileCheckRepository _printerReferenceRepository =
        printerReferenceRepository ?? throw new ArgumentNullException(nameof(printerReferenceRepository));

    private readonly ILogger<ProfileFamilyService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<CloneProfileFamilyResponseDto> CloneFamilyAsync(
        CloneProfileFamilyRequestDto request,
        Guid userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user identity is required.", nameof(userId));
        }

        string familyName = request.FamilyName.Trim();
        if (string.IsNullOrWhiteSpace(familyName))
        {
            throw new ArgumentException("familyName is required.", nameof(request));
        }

        if (request.TargetPrinterModelId == Guid.Empty)
        {
            throw new ArgumentException("targetPrinterModelId is required.", nameof(request));
        }

        CatalogModelInfo? targetModel = await _catalogService.GetModelByIdAsync(
            request.TargetPrinterModelId,
            ct);
        if (targetModel is null)
        {
            throw new ArgumentException(
                $"Target printer model '{request.TargetPrinterModelId}' was not found.",
                nameof(request));
        }

        CloneProfileFamilyRequestDto normalizedRequest = CopyRequest(request, familyName);
        MachineModelProfile? failedFamily = await FindRetryableFailedFamilyAsync(
            normalizedRequest,
            ct);

        (ProfileFamilyWorkerTarget worker, AllProfilesResponseDto catalog) =
            await _workerClient.GetCatalogAsync(
                normalizedRequest.SourceManufacturer,
                normalizedRequest.SlicerEngineVersion,
                ct);

        Guid familyId = failedFamily?.Id ?? Guid.NewGuid();
        ProfileFamilyRenderResult rendered = _renderer.Render(
            familyId,
            normalizedRequest,
            catalog);
        string familyHash = ComputeHash(
            familyName,
            $"{normalizedRequest.SourceManufacturer.Trim()}/{normalizedRequest.SourceMachineModelName.Trim()}",
            rendered.CanonicalFamilyOverridesJson);
        DateTime now = DateTime.UtcNow;

        MachineModelProfile family = failedFamily ?? new MachineModelProfile
        {
            Id = familyId,
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        family.Name = familyName;
        family.Manufacturer = "Custom";
        family.Description = $"Custom OrcaSlicer profile family for {targetModel.Name}";
        family.SlicerType = SlicerType.OrcaSlicer;
        family.PrinterModelId = normalizedRequest.TargetPrinterModelId;
        family.Hash = familyHash;
        family.IsSystem = false;
        family.IsPublic = true;
        family.SlicerVersion = worker.OrcaVersion;
        family.SlicerDistribution = normalizedRequest.SlicerDistribution.Trim();
        family.SourceMachineModelName = normalizedRequest.SourceMachineModelName.Trim();
        family.FamilyOverridesJson = rendered.CanonicalFamilyOverridesJson;
        family.CreatedByUserId ??= userId;
        family.RenderStatus = ProfileFamilyRenderStatus.Pending;
        family.LastRenderedAt = null;
        family.RenderedForOrcaVersion = null;
        family.UpdatedAt = now;

        List<MachineProfile> machineProfiles = rendered.MachineVariants
            .Select(variant => new MachineProfile
            {
                Id = Guid.NewGuid(),
                Name = variant.Name,
                Manufacturer = "Custom",
                Description = $"Generated {FormatNozzle(variant.NozzleDiameter)} mm variant for {familyName}",
                SlicerType = SlicerType.OrcaSlicer,
                PrinterModelId = normalizedRequest.TargetPrinterModelId,
                MachineModelProfileId = familyId,
                Hash = ComputeHash(familyHash, variant.SourceSystemPresetName, variant.OverridesJson),
                IsSystem = false,
                IsDefault = false,
                IsPublic = true,
                SlicerVersion = worker.OrcaVersion,
                SlicerDistribution = normalizedRequest.SlicerDistribution.Trim(),
                SourceSystemPresetName = variant.SourceSystemPresetName,
                OverridesJson = variant.OverridesJson,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            })
            .ToList();

        await PersistFamilyAsync(family, machineProfiles, failedFamily is not null, ct);

        try
        {
            await _workerClient.WriteBundleAsync(worker, rendered.Bundle, ct);
            try
            {
                await _aliasService.EnsureModelAliasAsync(
                    normalizedRequest.TargetPrinterModelId,
                    familyName,
                    "OrcaSlicer",
                    ct);
                await _catalogService.InvalidateModelAliasesAsync(
                    normalizedRequest.TargetPrinterModelId,
                    ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new ProfileFamilyConflictException(ex.Message, ex);
            }

            family.RenderStatus = ProfileFamilyRenderStatus.Healthy;
            family.LastRenderedAt = DateTime.UtcNow;
            family.RenderedForOrcaVersion = worker.OrcaVersion;
            family.UpdatedAt = family.LastRenderedAt.Value;
            _ = await _dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            family.RenderStatus = ProfileFamilyRenderStatus.Failed;
            family.UpdatedAt = DateTime.UtcNow;
            _ = await _dbContext.SaveChangesAsync(CancellationToken.None);
            _logger.LogError(
                ex,
                "Failed to install generated profile family {FamilyId} for OrcaSlicer {Version}",
                family.Id,
                worker.OrcaVersion);
            throw;
        }

        IReadOnlyList<ProfileFamilyMachineVariantDto> machineDtos = machineProfiles
            .Select((profile, index) => new ProfileFamilyMachineVariantDto(
                profile.Id,
                profile.Name,
                rendered.MachineVariants[index].NozzleDiameter,
                profile.SourceSystemPresetName!))
            .ToList();

        return new CloneProfileFamilyResponseDto(
            family.Id,
            family.Name,
            normalizedRequest.TargetPrinterModelId,
            family.RenderStatus,
            family.LastRenderedAt,
            machineDtos,
            rendered.ProcessProfileCount,
            rendered.FilamentProfileCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProfileFamilySummaryDto>> ListFamiliesAsync(
        ProfileFamilyRenderStatus? renderStatus,
        CancellationToken ct)
    {
        IQueryable<MachineModelProfile> query = _dbContext.MachineModelProfiles
            .AsNoTracking()
            .Where(family => !family.IsSystem && family.SlicerType == SlicerType.OrcaSlicer);

        if (renderStatus is not null)
        {
            query = query.Where(family => family.RenderStatus == renderStatus.Value);
        }

        List<MachineModelProfile> families = await query
            .Include(family => family.MachineProfiles)
            .OrderByDescending(family => family.CreatedAt)
            .ToListAsync(ct);

        return families.Select(MapToSummary).ToList();
    }

    /// <inheritdoc />
    public async Task<ProfileFamilySummaryDto> GetFamilyAsync(Guid familyId, CancellationToken ct)
    {
        MachineModelProfile? family = await _dbContext.MachineModelProfiles
            .AsNoTracking()
            .Include(candidate => candidate.MachineProfiles)
            .FirstOrDefaultAsync(candidate => candidate.Id == familyId, ct);

        if (!IsCustomFamily(family))
        {
            throw new ProfileFamilyNotFoundException(
                $"Custom profile family '{familyId}' was not found.");
        }

        return MapToSummary(family!);
    }

    /// <inheritdoc />
    public async Task DeleteFamilyAsync(Guid familyId, CancellationToken ct)
    {
        MachineModelProfile? family = await _dbContext.MachineModelProfiles
            .Include(candidate => candidate.MachineProfiles)
            .FirstOrDefaultAsync(candidate => candidate.Id == familyId, ct);

        if (!IsCustomFamily(family))
        {
            throw new ProfileFamilyNotFoundException(
                $"Custom profile family '{familyId}' was not found.");
        }

        List<Guid> variantIds = family!.MachineProfiles.Select(variant => variant.Id).ToList();

        await EnsureNoBlockingReferencesAsync(family, variantIds, ct);

        // Ordering (partial-failure safety): remove the worker bundle first. A worker failure throws
        // (HttpRequestException -> 503) before any DB or alias mutation, so the family remains fully
        // listed and usable. Deleting an already-absent bundle is idempotent (worker 404 -> success).
        await _workerClient.DeleteBundleAsync(family.RenderedForOrcaVersion, familyId, ct);

        // Mirror of create-time cache handling: drop the OrcaSlicer alias for the family name and
        // invalidate the catalog alias cache so the bound model stops resolving the family in-process,
        // with no worker restart. Families without a bound catalog model have no alias to remove.
        if (family.PrinterModelId is Guid printerModelId)
        {
            await _aliasService.RemoveModelAliasAsync(printerModelId, family.Name, "OrcaSlicer", ct);
            await _catalogService.InvalidateModelAliasesAsync(printerModelId, ct);
        }

        // Authoritative rows last, atomically. Process/filament profiles are never persisted (they
        // live only inside the worker bundle removed above), so removing the family row plus its
        // variant rows deletes every derived process and filament profile created by the clone.
        await using IDbContextTransaction transaction =
            await _dbContext.Database.BeginTransactionAsync(ct);
        _dbContext.MachineProfiles.RemoveRange(family.MachineProfiles);
        _ = _dbContext.MachineModelProfiles.Remove(family);
        _ = await _dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task EnsureNoBlockingReferencesAsync(
        MachineModelProfile family,
        List<Guid> variantIds,
        CancellationToken ct)
    {
        if (variantIds.Count == 0)
        {
            return;
        }

        // Slice-job reference: only NON-TERMINAL jobs (Queued, Processing) block deletion. A terminal
        // job (Completed, Failed, Cancelled) has already captured its effective machine profile as an
        // immutable snapshot (SliceJob.MachineProfileJson / MachineProfileSha256), so deletion does not
        // erase its provenance; blocking on historical jobs would make every family that ever sliced
        // permanently undeletable.
        SliceJob? blockingJob = await _dbContext.SliceJobs
            .AsNoTracking()
            .Where(job =>
                job.MachineProfileId != null
                && variantIds.Contains(job.MachineProfileId.Value)
                && (job.Status == SliceJobStatus.Queued || job.Status == SliceJobStatus.Processing))
            .OrderBy(job => job.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (blockingJob is not null)
        {
            throw new ProfileFamilyInUseException(
                $"Profile family '{family.Name}' cannot be deleted because slice job " +
                $"'{blockingJob.Id}' (status {blockingJob.Status}) references one of its machine profiles.");
        }

        // Printer reference: block when a registered printer's template machine profile points at a
        // family variant. Deliberately NOT Printer.ModelId, which references the stock catalog model
        // that survives family deletion; only the concrete TemplateMachineProfileId binding is orphaned
        // by removing the family. Runs against the shared AppDbContext (monolith and split modes alike).
        Printer? blockingPrinter =
            await _printerReferenceRepository.FindByTemplateMachineProfileIdsAsync(variantIds, ct);

        if (blockingPrinter is not null)
        {
            throw new ProfileFamilyInUseException(
                $"Profile family '{family.Name}' cannot be deleted because printer " +
                $"'{blockingPrinter.Name}' ({blockingPrinter.Id}) references one of its machine profiles.");
        }
    }

    private static bool IsCustomFamily([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] MachineModelProfile? family) =>
        family is { IsSystem: false, SlicerType: SlicerType.OrcaSlicer };

    private static ProfileFamilySummaryDto MapToSummary(MachineModelProfile family)
    {
        List<ProfileFamilyVariantSummaryDto> variants = family.MachineProfiles
            .OrderBy(variant => variant.Name, StringComparer.OrdinalIgnoreCase)
            .Select(variant => new ProfileFamilyVariantSummaryDto(
                variant.Id,
                variant.Name,
                ParseNozzleDiameter(variant.Name),
                variant.SourceSystemPresetName))
            .ToList();

        // sourceManufacturer is not persisted (the Manufacturer column is the literal "Custom") and is
        // not recoverable without a schema column, which is out of scope (no migration). The recoverable
        // source identity is surfaced via sourceMachineModelName instead.
        const string? sourceManufacturer = null;

        // Derived process/filament counts are produced only by the renderer at create time and live
        // solely inside the worker bundle; they are not persisted. A null communicates "not tracked
        // post-render" rather than a fabricated 0.
        int? processProfileCount = null;
        int? filamentProfileCount = null;

        return new ProfileFamilySummaryDto(
            family.Id,
            family.Name,
            family.PrinterModelId,
            family.RenderStatus,
            family.LastRenderedAt,
            family.RenderedForOrcaVersion,
            sourceManufacturer,
            family.SourceMachineModelName,
            family.SlicerDistribution,
            variants,
            processProfileCount,
            filamentProfileCount);
    }

    /// <summary>
    /// Recovers a variant's nozzle diameter from its persisted name. Variant names are rendered as
    /// <c>"{family} {nozzle} nozzle"</c> (see <c>ProfileFamilyRenderer.BuildMachineName</c>); the
    /// nozzle diameter is not stored as a column and is not reliably present in the persisted
    /// override JSON, so the name suffix is the honest recoverable source. Returns <see langword="null"/>
    /// when it cannot be parsed rather than a misleading 0.
    /// </summary>
    private static double? ParseNozzleDiameter(string variantName)
    {
        if (string.IsNullOrWhiteSpace(variantName))
        {
            return null;
        }

        const string suffix = " nozzle";
        string trimmed = variantName.TrimEnd();
        if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string withoutSuffix = trimmed[..^suffix.Length].TrimEnd();
        int lastSpace = withoutSuffix.LastIndexOf(' ');
        string token = lastSpace >= 0 ? withoutSuffix[(lastSpace + 1)..] : withoutSuffix;

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && parsed > 0
            ? parsed
            : null;
    }

    private async Task<MachineModelProfile?> FindRetryableFailedFamilyAsync(
        CloneProfileFamilyRequestDto request,
        CancellationToken ct)
    {
        string normalizedName = MachineModelProfile.NormalizeNameKey(request.FamilyName);
        List<MachineModelProfile> matchingFamilies = await _dbContext.MachineModelProfiles
            .Where(profile =>
                profile.SlicerType == SlicerType.OrcaSlicer
                && profile.NameNormalized == normalizedName)
            .Take(2)
            .ToListAsync(ct);

        if (matchingFamilies.Count > 1)
        {
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{request.FamilyName}' already exists.");
        }

        MachineModelProfile? retryableFamily = matchingFamilies.SingleOrDefault();
        if (retryableFamily is not null
            && (retryableFamily.IsSystem
                || retryableFamily.RenderStatus is not (
                    ProfileFamilyRenderStatus.Failed or ProfileFamilyRenderStatus.Pending)
                || retryableFamily.PrinterModelId != request.TargetPrinterModelId))
        {
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{request.FamilyName}' already exists.");
        }

        Guid? existingAliasTarget = await _aliasService.ResolveModelAliasAsync(
            request.FamilyName,
            "OrcaSlicer");
        if (existingAliasTarget.HasValue
            && existingAliasTarget.Value != request.TargetPrinterModelId)
        {
            throw new ProfileFamilyConflictException(
                $"OrcaSlicer model name '{request.FamilyName}' is already mapped to another printer model.");
        }

        return retryableFamily;
    }

    private async Task PersistFamilyAsync(
        MachineModelProfile family,
        IReadOnlyCollection<MachineProfile> machineProfiles,
        bool replaceExisting,
        CancellationToken ct)
    {
        await using IDbContextTransaction transaction =
            await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            if (replaceExisting)
            {
                List<MachineProfile> existingProfiles = await _dbContext.MachineProfiles
                    .Where(profile => profile.MachineModelProfileId == family.Id)
                    .ToListAsync(ct);
                _dbContext.MachineProfiles.RemoveRange(existingProfiles);
                _ = await _dbContext.SaveChangesAsync(ct);
            }
            else
            {
                _ = _dbContext.MachineModelProfiles.Add(family);
            }

            _dbContext.MachineProfiles.AddRange(machineProfiles);
            _ = await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsFamilyNameUniqueConstraintViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{family.Name}' already exists.",
                ex);
        }
    }

    private static CloneProfileFamilyRequestDto CopyRequest(
        CloneProfileFamilyRequestDto request,
        string familyName)
    {
        return new CloneProfileFamilyRequestDto
        {
            FamilyName = familyName,
            TargetPrinterModelId = request.TargetPrinterModelId,
            SourceManufacturer = request.SourceManufacturer,
            SourceMachineModelName = request.SourceMachineModelName,
            NozzleDiameters = [.. request.NozzleDiameters],
            FamilyOverrides = new Dictionary<string, System.Text.Json.JsonElement>(
                request.FamilyOverrides,
                StringComparer.Ordinal),
            SlicerEngineVersion = request.SlicerEngineVersion,
            SlicerDistribution = request.SlicerDistribution
        };
    }

    private static bool IsFamilyNameUniqueConstraintViolation(DbUpdateException exception)
    {
        const string familyNameIndex = "IX_MachineModelProfiles_Name_SlicerType";

        for (Exception? inner = exception.InnerException;
             inner is not null;
             inner = inner.InnerException)
        {
            if (inner is Microsoft.Data.Sqlite.SqliteException sqlite
                && sqlite.SqliteExtendedErrorCode is 1555 or 2067)
            {
                return sqlite.Message.Contains(
                    "MachineModelProfiles.NameNormalized, MachineModelProfiles.SlicerType",
                    StringComparison.OrdinalIgnoreCase);
            }

            if (inner is System.Data.Common.DbException dbException
                && string.Equals(dbException.SqlState, "23505", StringComparison.Ordinal))
            {
                string? constraintName =
                    inner.GetType().GetProperty("ConstraintName")?.GetValue(inner) as string;
                return string.Equals(
                    constraintName,
                    familyNameIndex,
                    StringComparison.OrdinalIgnoreCase);
            }

            if (inner.GetType().FullName is
                    "Microsoft.Data.SqlClient.SqlException" or
                    "System.Data.SqlClient.SqlException"
                && inner.GetType().GetProperty("Number")?.GetValue(inner) is int number
                && number is 2601 or 2627)
            {
                return inner.Message.Contains(
                    familyNameIndex,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static string ComputeHash(params string[] values)
    {
        string input = string.Join('\n', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string FormatNozzle(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

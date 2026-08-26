using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        // Staleness detection trigger (chosen: detection-on-read). Marking runs here, on the list read,
        // rather than via a scheduled sweep or an engine-version-change hook, because it needs no new
        // hosted-service infrastructure and guarantees the ?renderStatus=Stale filter and the bulk
        // render-stale endpoint observe up-to-date statuses. The cost is that a family is only marked
        // when someone looks; that is acceptable because staleness only matters at the moment an admin
        // inspects or re-renders families. Detection degrades safely when no worker is online.
        await DetectAndMarkStaleAsync(null, ct);

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
        // Detection-on-read, scoped to this family (see ListFamiliesAsync for the trigger rationale), so
        // a single GET reflects post-upgrade staleness without a worker round-trip on the read shape.
        await DetectAndMarkStaleAsync(familyId, ct);

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

        await EnsureNoBlockingReferencesAsync(family, variantIds, "deleted", ct);

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

    /// <inheritdoc />
    public async Task<ProfileFamilySummaryDto> EditFamilyAsync(
        Guid familyId,
        EditProfileFamilyRequestDto request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        MachineModelProfile family = await LoadTrackedFamilyAsync(familyId, ct);
        List<MachineProfile> existingVariants = family.MachineProfiles.ToList();

        // Resolve each facet: an absent (null) facet leaves the persisted value unchanged.
        string targetName = request.Name is null
            ? family.Name
            : NormalizeEditedName(request.Name);

        string targetSource = request.SourceMachineModelName is null
            ? family.SourceMachineModelName ?? string.Empty
            : NormalizeEditedSource(request.SourceMachineModelName);

        Dictionary<string, JsonElement> targetOverrides = request.FamilyOverrides is null
            ? ParseFamilyOverrides(family.FamilyOverridesJson)
            : new Dictionary<string, JsonElement>(request.FamilyOverrides, StringComparer.Ordinal);

        List<double> targetNozzles = request.NozzleDiameters is null
            ? DeriveNozzleDiameters(existingVariants)
            : NormalizeEditedNozzles(request.NozzleDiameters);

        bool isRename = !string.Equals(
            MachineModelProfile.NormalizeNameKey(targetName),
            MachineModelProfile.NormalizeNameKey(family.Name),
            StringComparison.Ordinal);

        // Fail fast on a rename collision (409) before any catalog fetch, worker call, or mutation.
        if (isRename)
        {
            await EnsureRenameAvailableAsync(family, targetName, ct);
        }

        // A nozzle-set edit that drops a variant is a scoped delete and must honour the same live
        // reference check as family deletion. Match by nozzle diameter so a surviving variant is never
        // treated as removed.
        if (request.NozzleDiameters is not null)
        {
            List<Guid> removedVariantIds = existingVariants
                .Where(variant => !targetNozzles.Any(nozzle =>
                    NozzleMatches(ParseNozzleDiameter(variant.Name), nozzle)))
                .Select(variant => variant.Id)
                .ToList();
            await EnsureNoBlockingReferencesAsync(family, removedVariantIds, "edited", ct);
        }

        await RenderAndInstallAsync(
            family,
            existingVariants,
            targetName,
            targetSource,
            targetNozzles,
            targetOverrides,
            isRename,
            ct);

        return MapToSummary(family);
    }

    /// <inheritdoc />
    public async Task<ProfileFamilySummaryDto> RenderFamilyAsync(Guid familyId, CancellationToken ct)
    {
        MachineModelProfile family = await LoadTrackedFamilyAsync(familyId, ct);
        List<MachineProfile> existingVariants = family.MachineProfiles.ToList();

        // A pure re-render reconstructs an equivalent request from the persisted family state, so no
        // facet changes and no rename occurs. Idempotent: the reconstructed nozzle set equals the
        // existing variant set, so the id-preserving merge updates every variant in place.
        await RenderAndInstallAsync(
            family,
            existingVariants,
            family.Name,
            family.SourceMachineModelName ?? string.Empty,
            DeriveNozzleDiameters(existingVariants),
            ParseFamilyOverrides(family.FamilyOverridesJson),
            isRename: false,
            ct);

        return MapToSummary(family);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProfileFamilyRenderResultDto>> RenderStaleFamiliesAsync(
        CancellationToken ct)
    {
        // Ensure post-upgrade staleness is detected before selecting the batch, so a bulk re-render run
        // picks up families that became stale since the last read.
        await DetectAndMarkStaleAsync(null, ct);

        // Re-render both Stale (post-upgrade drift) and Failed (recover a family whose last render or
        // install failed) families. Ordered oldest-first for a stable, bounded pass.
        List<Guid> targetIds = await _dbContext.MachineModelProfiles
            .AsNoTracking()
            .Where(family =>
                !family.IsSystem
                && family.SlicerType == SlicerType.OrcaSlicer
                && (family.RenderStatus == ProfileFamilyRenderStatus.Stale
                    || family.RenderStatus == ProfileFamilyRenderStatus.Failed))
            .OrderBy(family => family.CreatedAt)
            .Select(family => family.Id)
            .ToListAsync(ct);

        List<ProfileFamilyRenderResultDto> results = new(targetIds.Count);
        foreach (Guid targetId in targetIds)
        {
            // One bad family must never abort the batch: capture each outcome and continue.
            try
            {
                ProfileFamilySummaryDto rendered = await RenderFamilyAsync(targetId, ct);
                results.Add(new ProfileFamilyRenderResultDto(
                    rendered.FamilyId,
                    rendered.FamilyName,
                    rendered.RenderStatus,
                    null,
                    null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (string code, string detail) = ClassifyRenderFailure(ex);
                string familyName = await _dbContext.MachineModelProfiles
                    .AsNoTracking()
                    .Where(family => family.Id == targetId)
                    .Select(family => family.Name)
                    .FirstOrDefaultAsync(ct) ?? targetId.ToString();
                results.Add(new ProfileFamilyRenderResultDto(
                    targetId,
                    familyName,
                    ProfileFamilyRenderStatus.Failed,
                    code,
                    detail));
            }
        }

        return results;
    }

    /// <summary>
    /// Marks Healthy families whose bundle was rendered for an OrcaSlicer version other than the live
    /// engine version as <see cref="ProfileFamilyRenderStatus.Stale"/>. Never flips a family that has
    /// never rendered (<c>RenderedForOrcaVersion</c> is null — i.e. Pending/Failed): <c>Stale</c> means
    /// "rendered, but for an older version". Degrades safely when no worker is online (the live version
    /// is unknowable) by leaving all statuses untouched rather than guessing.
    /// </summary>
    private async Task DetectAndMarkStaleAsync(Guid? scopeFamilyId, CancellationToken ct)
    {
        string? liveVersion = await _workerClient.GetActiveOrcaVersionAsync(ct);
        if (string.IsNullOrWhiteSpace(liveVersion))
        {
            return;
        }

        IQueryable<MachineModelProfile> query = _dbContext.MachineModelProfiles
            .Where(family =>
                !family.IsSystem
                && family.SlicerType == SlicerType.OrcaSlicer
                && family.RenderStatus == ProfileFamilyRenderStatus.Healthy
                && family.RenderedForOrcaVersion != null
                && family.RenderedForOrcaVersion != liveVersion);

        if (scopeFamilyId is Guid id)
        {
            query = query.Where(family => family.Id == id);
        }

        List<MachineModelProfile> stale = await query.ToListAsync(ct);
        if (stale.Count == 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        foreach (MachineModelProfile family in stale)
        {
            family.RenderStatus = ProfileFamilyRenderStatus.Stale;
            family.UpdatedAt = now;
        }

        _ = await _dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Shared render-and-install core for edit and re-render. Renders the target state against the live
    /// worker, persists an id-preserving variant merge, installs the new bundle, and moves the alias on
    /// rename. Validation/source failures are raised before any mutation, leaving the family and its
    /// live bundle untouched. An install failure marks the family <c>Failed</c> and restores the
    /// previous good bundle so the farm is never left worse off.
    /// </summary>
    private async Task RenderAndInstallAsync(
        MachineModelProfile family,
        List<MachineProfile> existingVariants,
        string targetName,
        string targetSource,
        IReadOnlyList<double> targetNozzles,
        Dictionary<string, JsonElement> targetOverrides,
        bool isRename,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetSource))
        {
            throw new ProfileFamilySourceException(
                $"Profile family '{family.Name}' has no source machine model to render from; " +
                "re-bind it to a valid source machine model.");
        }

        string previousName = family.Name;

        // Select a fresh worker and download its full catalog (empty manufacturer = every manufacturer)
        // for the CURRENT live OrcaSlicer version. A missing worker throws HttpRequestException (503)
        // before any mutation.
        (ProfileFamilyWorkerTarget worker, AllProfilesResponseDto catalog) =
            await _workerClient.GetCatalogAsync(string.Empty, null, ct);

        // Capture the previous good bundle by rendering the CURRENT persisted state against the same
        // catalog, so a failed install can restore it. Best-effort: if the previous source no longer
        // resolves, there is nothing to restore (null) and the pre-mutation ordering below still keeps
        // the live bundle intact for the common case.
        ProfileFamilyBundleDto? previousBundle = TryRenderPreviousBundle(family, existingVariants, catalog);

        // Derive the source manufacturer from the catalog (it is not persisted). A source that no longer
        // resolves throws ProfileFamilySourceException (422) with an actionable detail — this also
        // covers the §5 "source preset gone after upgrade" case. Thrown BEFORE any DB or worker
        // mutation, so the family and its installed bundle are left exactly as they were.
        string sourceManufacturer = DeriveSourceManufacturer(catalog, targetSource);

        CloneProfileFamilyRequestDto renderRequest = BuildRenderRequest(
            family,
            targetName,
            sourceManufacturer,
            targetSource,
            targetNozzles,
            targetOverrides);

        // Render the new bundle in memory. Bad overrides/nozzles throw ArgumentException (400); a
        // missing source preset/nozzle throws ProfileFamilySourceException (422). Both fire before any
        // mutation, so a validation failure preserves the family and its live bundle.
        ProfileFamilyRenderResult rendered = _renderer.Render(family.Id, renderRequest, catalog);

        string familyHash = ComputeHash(
            targetName,
            $"{sourceManufacturer}/{targetSource}",
            rendered.CanonicalFamilyOverridesJson);
        DateTime now = DateTime.UtcNow;

        // Persist the authoritative state as Pending with an id-preserving variant merge before touching
        // the worker, mirroring CloneFamilyAsync's persist-then-install ordering.
        family.Name = targetName;
        family.Hash = familyHash;
        family.SlicerVersion = worker.OrcaVersion;
        family.SourceMachineModelName = targetSource;
        family.FamilyOverridesJson = rendered.CanonicalFamilyOverridesJson;
        family.RenderStatus = ProfileFamilyRenderStatus.Pending;
        family.UpdatedAt = now;

        MergeVariants(family, existingVariants, rendered, worker.OrcaVersion, familyHash, now);

        try
        {
            _ = await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsFamilyNameUniqueConstraintViolation(ex))
        {
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{targetName}' already exists.",
                ex);
        }

        try
        {
            await _workerClient.WriteBundleAsync(worker, rendered.Bundle, ct);

            if (family.PrinterModelId is Guid printerModelId)
            {
                try
                {
                    // Add the alias for the (possibly new) name first so lookups keep resolving, then
                    // drop the stale old-name alias on a rename. Ordering guarantees no lookup gap.
                    await _aliasService.EnsureModelAliasAsync(printerModelId, targetName, "OrcaSlicer", ct);
                    if (isRename)
                    {
                        await _aliasService.RemoveModelAliasAsync(printerModelId, previousName, "OrcaSlicer", ct);
                    }

                    await _catalogService.InvalidateModelAliasesAsync(printerModelId, ct);
                }
                catch (InvalidOperationException ex)
                {
                    throw new ProfileFamilyConflictException(ex.Message, ex);
                }
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

            // Previous-good-bundle preservation. The worker's InstallAsync removes a bundle on a
            // blocking install failure rather than restoring the prior one, so a failed re-render can
            // leave the family with no bundle. Re-install the captured previous good bundle (best-effort)
            // so the family still slices via GET /api/slicer/profiles/machine/for-model/{modelId}. This
            // runs only for the narrow window where the render succeeded but the install did not; a
            // source/validation failure never reaches here, having thrown before the worker was touched.
            if (previousBundle is not null)
            {
                try
                {
                    await _workerClient.WriteBundleAsync(worker, previousBundle, CancellationToken.None);
                    if (family.PrinterModelId is Guid restoreModelId)
                    {
                        await _catalogService.InvalidateModelAliasesAsync(restoreModelId, CancellationToken.None);
                    }
                }
                catch (Exception restoreEx)
                {
                    _logger.LogError(
                        restoreEx,
                        "Failed to restore the previous good bundle for profile family {FamilyId} after a failed re-render",
                        family.Id);
                }
            }

            _logger.LogError(
                ex,
                "Failed to re-render profile family {FamilyId} for OrcaSlicer {Version}",
                family.Id,
                worker.OrcaVersion);
            throw;
        }
    }

    /// <summary>
    /// Renders the family's CURRENT persisted state into a bundle for restore-on-failure, or returns
    /// <see langword="null"/> when it cannot be reproduced (e.g. its source preset is already gone).
    /// </summary>
    private ProfileFamilyBundleDto? TryRenderPreviousBundle(
        MachineModelProfile family,
        List<MachineProfile> existingVariants,
        AllProfilesResponseDto catalog)
    {
        try
        {
            List<double> previousNozzles = DeriveNozzleDiameters(existingVariants);
            if (previousNozzles.Count == 0 || string.IsNullOrWhiteSpace(family.SourceMachineModelName))
            {
                return null;
            }

            string previousManufacturer = DeriveSourceManufacturer(catalog, family.SourceMachineModelName);
            CloneProfileFamilyRequestDto previousRequest = BuildRenderRequest(
                family,
                family.Name,
                previousManufacturer,
                family.SourceMachineModelName,
                previousNozzles,
                ParseFamilyOverrides(family.FamilyOverridesJson));

            return _renderer.Render(family.Id, previousRequest, catalog).Bundle;
        }
        catch (Exception ex) when (ex is ProfileFamilySourceException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Merges rendered variants into the tracked family in place: a surviving variant (matched by nozzle
    /// diameter) keeps its <c>MachineProfile.Id</c> so printer/job references are never orphaned by an
    /// unrelated edit; a new nozzle materialises a new row; a dropped nozzle's row is removed (its
    /// reference check having already passed).
    /// </summary>
    private void MergeVariants(
        MachineModelProfile family,
        List<MachineProfile> existingVariants,
        ProfileFamilyRenderResult rendered,
        string orcaVersion,
        string familyHash,
        DateTime now)
    {
        List<MachineProfile> unmatched = existingVariants.ToList();

        foreach (RenderedMachineVariant variant in rendered.MachineVariants)
        {
            MachineProfile? match = unmatched.FirstOrDefault(existing =>
                NozzleMatches(ParseNozzleDiameter(existing.Name), variant.NozzleDiameter));

            if (match is not null)
            {
                _ = unmatched.Remove(match);
                match.Name = variant.Name;
                match.Description = $"Generated {FormatNozzle(variant.NozzleDiameter)} mm variant for {family.Name}";
                match.PrinterModelId = family.PrinterModelId;
                match.Hash = ComputeHash(familyHash, variant.SourceSystemPresetName, variant.OverridesJson);
                match.SlicerVersion = orcaVersion;
                match.SlicerDistribution = family.SlicerDistribution;
                match.SourceSystemPresetName = variant.SourceSystemPresetName;
                match.OverridesJson = variant.OverridesJson;
                match.UpdatedAt = now;
                continue;
            }

            MachineProfile created = new()
            {
                Id = Guid.NewGuid(),
                Name = variant.Name,
                Manufacturer = "Custom",
                Description = $"Generated {FormatNozzle(variant.NozzleDiameter)} mm variant for {family.Name}",
                SlicerType = SlicerType.OrcaSlicer,
                PrinterModelId = family.PrinterModelId,
                MachineModelProfileId = family.Id,
                Hash = ComputeHash(familyHash, variant.SourceSystemPresetName, variant.OverridesJson),
                IsSystem = false,
                IsDefault = false,
                IsPublic = true,
                SlicerVersion = orcaVersion,
                SlicerDistribution = family.SlicerDistribution,
                SourceSystemPresetName = variant.SourceSystemPresetName,
                OverridesJson = variant.OverridesJson,
                CreatedByUserId = family.CreatedByUserId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _ = _dbContext.MachineProfiles.Add(created);
        }

        if (unmatched.Count > 0)
        {
            _dbContext.MachineProfiles.RemoveRange(unmatched);
        }
    }

    /// <summary>
    /// Re-checks the global name and alias collision rules for a rename, mirroring the create-time
    /// checks. Throws <see cref="ProfileFamilyConflictException"/> (409) on a normalized-name clash with
    /// another family or an OrcaSlicer alias already mapped to a different printer model.
    /// </summary>
    private async Task EnsureRenameAvailableAsync(
        MachineModelProfile family,
        string targetName,
        CancellationToken ct)
    {
        string normalized = MachineModelProfile.NormalizeNameKey(targetName);
        bool nameTaken = await _dbContext.MachineModelProfiles
            .AnyAsync(
                candidate =>
                    candidate.Id != family.Id
                    && candidate.SlicerType == SlicerType.OrcaSlicer
                    && candidate.NameNormalized == normalized,
                ct);
        if (nameTaken)
        {
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{targetName}' already exists.");
        }

        Guid? aliasTarget = await _aliasService.ResolveModelAliasAsync(targetName, "OrcaSlicer");
        if (aliasTarget.HasValue && aliasTarget.Value != family.PrinterModelId)
        {
            throw new ProfileFamilyConflictException(
                $"OrcaSlicer model name '{targetName}' is already mapped to another printer model.");
        }
    }

    private async Task<MachineModelProfile> LoadTrackedFamilyAsync(Guid familyId, CancellationToken ct)
    {
        MachineModelProfile? family = await _dbContext.MachineModelProfiles
            .Include(candidate => candidate.MachineProfiles)
            .FirstOrDefaultAsync(candidate => candidate.Id == familyId, ct);

        if (!IsCustomFamily(family))
        {
            throw new ProfileFamilyNotFoundException(
                $"Custom profile family '{familyId}' was not found.");
        }

        return family!;
    }

    private static CloneProfileFamilyRequestDto BuildRenderRequest(
        MachineModelProfile family,
        string familyName,
        string sourceManufacturer,
        string sourceMachineModelName,
        IReadOnlyList<double> nozzleDiameters,
        Dictionary<string, JsonElement> familyOverrides)
    {
        return new CloneProfileFamilyRequestDto
        {
            FamilyName = familyName,
            TargetPrinterModelId = family.PrinterModelId ?? Guid.Empty,
            SourceManufacturer = sourceManufacturer,
            SourceMachineModelName = sourceMachineModelName,
            NozzleDiameters = [.. nozzleDiameters],
            FamilyOverrides = familyOverrides,
            SlicerEngineVersion = null,
            SlicerDistribution = string.IsNullOrWhiteSpace(family.SlicerDistribution)
                ? "OrcaSlicer"
                : family.SlicerDistribution
        };
    }

    /// <summary>
    /// Derives the source manufacturer for a family from the live catalog by locating the manufacturer
    /// whose models include the family's source machine-model name. The manufacturer is not persisted
    /// (§ slice-1 decision), so it is recovered here. Throws <see cref="ProfileFamilySourceException"/>
    /// (422) with an actionable detail when the source model can no longer be found — the §5
    /// source-preset-gone case.
    /// </summary>
    private static string DeriveSourceManufacturer(
        AllProfilesResponseDto catalog,
        string sourceMachineModelName)
    {
        foreach (KeyValuePair<string, ManufacturerProfilesDto> pair in catalog.ByHierarchy)
        {
            if (pair.Value.Models.Values.Any(model => string.Equals(
                    model.Name,
                    sourceMachineModelName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return pair.Key;
            }
        }

        throw new ProfileFamilySourceException(
            $"Source machine model '{sourceMachineModelName}' is unavailable on the selected OrcaSlicer " +
            "worker; its source preset may have been removed by a bundle upgrade. Re-bind the family to " +
            "an available source machine model.");
    }

    private static List<double> DeriveNozzleDiameters(IEnumerable<MachineProfile> variants)
    {
        return variants
            .Select(variant => ParseNozzleDiameter(variant.Name))
            .Where(nozzle => nozzle is > 0)
            .Select(nozzle => nozzle!.Value)
            .Distinct()
            .Order()
            .ToList();
    }

    private static Dictionary<string, JsonElement> ParseFamilyOverrides(string? canonicalJson)
    {
        Dictionary<string, JsonElement> overrides = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(canonicalJson))
        {
            return overrides;
        }

        using JsonDocument document = JsonDocument.Parse(canonicalJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return overrides;
        }

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            overrides[property.Name] = property.Value.Clone();
        }

        return overrides;
    }

    private static string NormalizeEditedName(string name)
    {
        string trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("name must not be empty.", nameof(name));
        }

        return trimmed;
    }

    private static string NormalizeEditedSource(string sourceMachineModelName)
    {
        string trimmed = sourceMachineModelName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException(
                "sourceMachineModelName must not be empty.",
                nameof(sourceMachineModelName));
        }

        return trimmed;
    }

    private static List<double> NormalizeEditedNozzles(IReadOnlyList<double> nozzleDiameters)
    {
        if (nozzleDiameters.Count == 0)
        {
            throw new ArgumentException(
                "nozzleDiameters must contain at least one nozzle; a family cannot have every variant removed.",
                nameof(nozzleDiameters));
        }

        return nozzleDiameters
            .Where(double.IsFinite)
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToList();
    }

    private static (string Code, string Detail) ClassifyRenderFailure(Exception exception) => exception switch
    {
        ProfileFamilySourceException => ("source_preset_unavailable", exception.Message),
        ProfileFamilyConflictException => ("profile_family_name_conflict", exception.Message),
        ProfileFamilyInUseException => ("profile_family_in_use", exception.Message),
        ArgumentException => ("invalid_profile_family", exception.Message),
        HttpRequestException => ("profile_family_worker_unavailable", exception.Message),
        _ => ("profile_family_render_failed", exception.Message)
    };

    private static bool NozzleMatches(double? candidate, double target) =>
        candidate is double value && Math.Abs(value - target) < 1e-6;

    private async Task EnsureNoBlockingReferencesAsync(
        MachineModelProfile family,
        List<Guid> variantIds,
        string blockedAction,
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
                $"Profile family '{family.Name}' cannot be {blockedAction} because slice job " +
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
                $"Profile family '{family.Name}' cannot be {blockedAction} because printer " +
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

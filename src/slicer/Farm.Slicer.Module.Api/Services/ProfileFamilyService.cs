using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Repositories;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

    /// <summary>
    /// Maximum number of Stale/Failed families re-rendered per <c>render-stale</c> call. Each family is
    /// a synchronous worker HTTP round-trip, so the batch is bounded to stay well within
    /// Kestrel/nginx request timeouts; the response reports how many remain so a client drains the
    /// queue across successive calls (S4).
    /// </summary>
    private const int MaxStaleRenderBatch = 25;

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
    public async Task DeleteFamilyAsync(Guid familyId, bool force, CancellationToken ct)
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

        // enforceCoverageLoss: this is the ONLY path that removes the family's OrcaSlicer alias, so it is
        // the only path that can strip a model's last profile coverage. force bypasses ONLY that indirect
        // coverage check — never the direct-reference refusal, which always runs first below regardless of
        // force, so a variant bound as a printer's template profile can never be force-deleted.
        await EnsureNoBlockingReferencesAsync(
            family, variantIds, "deleted", enforceCoverageLoss: true, forceCoverageLoss: force, ct);

        // Ordering (partial-failure safety): remove the worker bundle first. A worker failure throws
        // (HttpRequestException -> 503) before any DB or alias mutation, so the family remains fully
        // listed and usable. Deleting an already-absent bundle is idempotent (worker 404 -> success).
        //
        // Pass null (any fresh online worker), NEVER family.RenderedForOrcaVersion: the bundle name is
        // version-independent (PrintFarmer-{familyId:N}) and lives on the worker host across an in-place
        // engine upgrade, but the worker selector filters candidates on EXACT version equality with no
        // fallback. After an upgrade makes a family Stale, pinning to the render-time version selects no
        // worker and throws (503) forever, so a Stale family could never be deleted (C1, issue #2079).
        await _workerClient.DeleteBundleAsync(null, familyId, ct);

        try
        {
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
        catch (DbUpdateConcurrencyException ex)
        {
            // Delete-vs-delete (or delete-vs-mutation) race: EF matched zero rows because a concurrent
            // request already removed or modified this family. The worker bundle and alias were removed
            // first (worker-first ordering), so on a concurrent DELETE the row is gone and nothing is
            // stranded. But a concurrent MUTATION (e.g. a child MachineProfile UPDATE) can raise this while
            // the family row SURVIVES — and its bundle/alias are already gone, so leaving it Healthy would
            // list a family whose slicing is broken. That is consensus finding C3 applied to this new
            // concurrency branch (H2): detach the abandoned graph, and if the row still exists mark it
            // Failed via the guarded helper so it is visibly broken, re-deletable, and surfaces under
            // ?renderStatus=Failed, before surfacing the clean 409.
            DetachTrackedGraph();

            // CancellationToken.None, NOT ct: this is a compensation read inside a catch block whose whole
            // job is to decide whether the surviving row must be marked Failed. ct is the caller's request
            // token and may already be cancelled here; threading it through would let AnyAsync throw
            // OperationCanceledException, skip TryMarkRenderFailedAsync, and leave a family reporting Healthy
            // with no bundle behind it (the exact H2 defect, reopened via cancellation). Do not "helpfully"
            // thread ct back in.
            bool familyStillExists = await _dbContext.MachineModelProfiles
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == family.Id, CancellationToken.None);
            if (familyStillExists)
            {
                await TryMarkRenderFailedAsync(family.Id);
            }

            throw new ProfileFamilyConcurrencyException(
                $"Profile family '{familyId}' was modified by a concurrent request; retry the operation.",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The worker bundle is already gone but the alias/DB cleanup failed (C3). Leaving the row
            // Healthy would report a family whose bundle no longer exists and whose slicing is broken.
            // Compensate by marking it Failed so it is visibly broken and re-deletable rather than a
            // silent half-delete. The transaction (if any) rolled back on the way out, so the reload
            // observes the still-present row. Guarded (H3) so a status-write failure cannot mask the
            // original cleanup exception the caller still needs to classify.
            await TryMarkRenderFailedAsync(family.Id);
            _logger.LogError(
                ex,
                "Profile family {FamilyId} bundle was removed from the worker but alias/DB cleanup failed; marked Failed for re-deletion.",
                family.Id);
            throw;
        }
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

        // The dropped-variant reference check is centralized in RenderAndInstallAsync (below) so both an
        // edit and a plain re-render honour it — a re-render that could drop a variant (e.g. an
        // unparseable variant name) must not orphan a printer/job reference either (S6).
        await RenderAndInstallAsync(
            family,
            existingVariants,
            targetName,
            targetSource,
            targetNozzles,
            targetOverrides,
            isRename,
            "edited",
            markFailedOnPreInstallFailure: false,
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
            "re-rendered",
            markFailedOnPreInstallFailure: true,
            ct);

        return MapToSummary(family);
    }

    /// <inheritdoc />
    public async Task<RenderStaleFamiliesResponseDto> RenderStaleFamiliesAsync(
        CancellationToken ct)
    {
        // Ensure post-upgrade staleness is detected before selecting the batch, so a bulk re-render run
        // picks up families that became stale since the last read.
        await DetectAndMarkStaleAsync(null, ct);

        // Re-render both Stale (post-upgrade drift) and Failed (recover a family whose last render or
        // install failed) families. Ordered oldest-first for a stable, bounded pass.
        List<Guid> allTargetIds = await _dbContext.MachineModelProfiles
            .AsNoTracking()
            .Where(family =>
                !family.IsSystem
                && family.SlicerType == SlicerType.OrcaSlicer
                && (family.RenderStatus == ProfileFamilyRenderStatus.Stale
                    || family.RenderStatus == ProfileFamilyRenderStatus.Failed))
            .OrderBy(family => family.CreatedAt)
            .Select(family => family.Id)
            .ToListAsync(ct);

        // Bound the batch: each family is a synchronous worker HTTP round-trip, so an unbounded loop
        // over dozens of families would blow past Kestrel/nginx request timeouts and abort mid-batch
        // (S4). Process at most MaxStaleRenderBatch per call and report how many remain so a client can
        // drain the queue across successive calls. No DB transaction is held across the worker calls.
        List<Guid> targetIds = allTargetIds.Take(MaxStaleRenderBatch).ToList();
        int remainingCount = allTargetIds.Count - targetIds.Count;

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

        return new RenderStaleFamiliesResponseDto(results, remainingCount);
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

        try
        {
            _ = await _dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Detection runs on the read path (ListFamiliesAsync/GetFamilyAsync), which is gated on the
            // non-admin slicing:submit permission (C4). A concurrent engine upgrade lets ordinary readers
            // race to update the same rows; a save failure (e.g. DbUpdateConcurrencyException) must never
            // turn that transient write conflict into a denial of service for readers. Swallow it — the
            // status is recomputed on the next read — and detach the unsaved edits so they cannot leak
            // into a later save on this scoped context.
            _logger.LogWarning(
                ex,
                "Staleness detection could not persist updated render statuses; returning the read unaffected.");
            foreach (MachineModelProfile family in stale)
            {
                _dbContext.Entry(family).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Shared render-and-install core for edit and re-render. Renders the target state against the live
    /// worker, persists an id-preserving variant merge, installs the new bundle, and moves the alias on
    /// rename. Validation/source failures are raised before any mutation, leaving the family and its
    /// live bundle untouched. An install failure marks the family <c>Failed</c> and restores the
    /// previous good bundle so the farm is never left worse off.
    /// </summary>
    /// <remarks>
    /// <c>markFailedOnPreInstallFailure</c> selects the pre-install failure policy. When
    /// <see langword="true"/> (the re-render path), a non-cancellation failure that occurs BEFORE the
    /// install begins — catalog fetch, source derivation, or in-memory render — also stamps the persisted
    /// row <c>Failed</c>, honouring <see cref="RenderFamilyAsync"/>'s "on any failure the family is marked
    /// Failed" contract so a source/worker error can never leave the row Healthy and invisible to
    /// render-stale (H1). When <see langword="false"/> (the edit path), those same failures are pure
    /// validation-time rejections of caller input and leave the family exactly as it was, per
    /// <see cref="EditFamilyAsync"/>'s contract. Install-time failures mark <c>Failed</c> on both paths.
    /// </remarks>
    private async Task RenderAndInstallAsync(
        MachineModelProfile family,
        List<MachineProfile> existingVariants,
        string targetName,
        string targetSource,
        IReadOnlyList<double> targetNozzles,
        Dictionary<string, JsonElement> targetOverrides,
        bool isRename,
        string blockedAction,
        bool markFailedOnPreInstallFailure,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetSource))
        {
            throw new ProfileFamilySourceException(
                $"Profile family '{family.Name}' has no source machine model to render from; " +
                "re-bind it to a valid source machine model.");
        }

        // Any existing variant whose nozzle diameter is not in the target set is dropped by MergeVariants.
        // Dropping a variant is a scoped delete, so it must honour the same live reference check as family
        // deletion — for BOTH edit and re-render (S6). Matching by nozzle diameter means a surviving
        // variant is never treated as removed; an unparseable variant name has no diameter, so a re-render
        // that would silently drop it is blocked here when a printer or non-terminal job references it.
        // Runs before any catalog fetch, worker call, or mutation, so a blocked reference is a clean 409.
        List<Guid> droppedVariantIds = existingVariants
            .Where(variant => !targetNozzles.Any(nozzle =>
                NozzleMatches(ParseNozzleDiameter(variant.Name), nozzle)))
            .Select(variant => variant.Id)
            .ToList();
        await EnsureNoBlockingReferencesAsync(
            family, droppedVariantIds, blockedAction, enforceCoverageLoss: false, forceCoverageLoss: false, ct);

        string previousName = family.Name;

        ProfileFamilyWorkerTarget worker;
        ProfileFamilyBundleDto? previousBundle;
        string sourceManufacturer;
        ProfileFamilyRenderResult rendered;
        try
        {
            // Select a fresh worker and download its full catalog (empty manufacturer = every
            // manufacturer) for the CURRENT live OrcaSlicer version. A missing worker throws
            // HttpRequestException (503) before any mutation.
            AllProfilesResponseDto catalog;
            (worker, catalog) = await _workerClient.GetCatalogAsync(string.Empty, null, ct);

            // Capture the previous good bundle by rendering the CURRENT persisted state against the same
            // catalog, so a failed install can restore it. Best-effort: if the previous source no longer
            // resolves, there is nothing to restore (null) and the pre-mutation ordering below still keeps
            // the live bundle intact for the common case.
            previousBundle = TryRenderPreviousBundle(family, existingVariants, catalog);

            // Derive the source manufacturer from the catalog (it is not persisted). A source that no
            // longer resolves throws ProfileFamilySourceException (422) with an actionable detail — this
            // also covers the §5 "source preset gone after upgrade" case. Thrown BEFORE any DB or worker
            // mutation, so the family and its installed bundle are left exactly as they were.
            sourceManufacturer = DeriveSourceManufacturer(catalog, targetSource);

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
            rendered = _renderer.Render(family.Id, renderRequest, catalog);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // H1: catalog fetch, source derivation, and in-memory render all run BEFORE the install
            // try below. On the re-render path a failure here would otherwise report Failed to the caller
            // while leaving the persisted row Healthy/Stale — invisible to ?renderStatus=Failed and never
            // retried by render-stale — so honour RenderFamilyAsync's "on any failure the family is marked
            // Failed" contract by flipping the row to Failed. Nothing has been installed on the worker or
            // mutated in the DB yet, so no bundle/alias restore is needed: only the status flips. Guarded
            // (H3) so a status-write failure cannot mask the real exception the caller needs for its
            // 422/503/500 classification, and skipped on cancellation because a cancelled request is not a
            // render failure. The edit path passes false: an identical pre-install failure there is a
            // validation-time rejection of caller input and must leave the family exactly as it was, per
            // EditFamilyAsync's contract.
            if (markFailedOnPreInstallFailure)
            {
                await TryMarkRenderFailedAsync(family.Id);
            }

            throw;
        }

        string familyHash = ComputeHash(
            targetName,
            $"{sourceManufacturer}/{targetSource}",
            rendered.CanonicalFamilyOverridesJson);
        DateTime now = DateTime.UtcNow;

        try
        {
            // Install-then-persist (C2): write the new bundle and move the alias FIRST, and only mutate
            // and save the authoritative DB row once both succeed. The previous good row and variant set
            // are left untouched throughout the install, so a failed install/alias can never destroy the
            // last good configuration and there is nothing to roll back. This deliberately does NOT wrap
            // the work in a DB transaction: the alias service writes through a SEPARATE AppDbContext
            // connection to the SAME database, so an open SlicerDbContext write transaction here would
            // hold a write lock the alias write then blocks on ("database is locked"), which the rename
            // end-to-end test reproduces. Persist-last achieves the identical invariant without the lock,
            // and is idempotent across repeated attempts because the persisted state stays previous-good
            // until success, so TryRenderPreviousBundle always reproduces the good bundle on a retry.
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

            // Both the worker bundle and the alias are now in place; commit the authoritative target
            // state as a single Healthy save with an id-preserving variant merge. family.Name must be set
            // before MergeVariants so the generated variant descriptions carry the new name.
            family.Name = targetName;
            family.Hash = familyHash;
            family.SlicerVersion = worker.OrcaVersion;
            family.SourceMachineModelName = targetSource;
            family.FamilyOverridesJson = rendered.CanonicalFamilyOverridesJson;
            family.RenderStatus = ProfileFamilyRenderStatus.Healthy;
            family.LastRenderedAt = now;
            family.RenderedForOrcaVersion = worker.OrcaVersion;
            family.UpdatedAt = now;

            MergeVariants(family, existingVariants, rendered, worker.OrcaVersion, familyHash, now);

            try
            {
                _ = await _dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Optimistic-concurrency race. MachineModelProfile carries no concurrency token, so EF only
                // throws here when the UPDATE matched ZERO rows — i.e. the family row was DELETED by a
                // concurrent request between our load and this persist (a concurrent UPDATE is
                // last-writer-wins and never throws). That is the render-vs-delete race Bishop flagged: the
                // bundle we installed on the worker above now has no DB row left to ever drive its removal.
                // Detach our abandoned mutations, confirm the row is really gone, roll back the bundle we
                // just installed so nothing is stranded, and surface a clean 404 rather than a raw 500. If
                // the row somehow still exists, restore the previous good state and report a 409.
                DetachTrackedGraph();

                // CancellationToken.None, NOT ct: this is a compensation read inside a catch block whose
                // result routes the entire recovery — the deleted-concurrently branch (roll back the just-
                // installed bundle) versus the generic restore branch. ct is the caller's request token and
                // may already be cancelled here; threading it through would let AnyAsync throw
                // OperationCanceledException, skip the deleted-concurrently branch, and fall through to the
                // restore path, which restores against a row that no longer exists and re-orphans the bundle
                // just installed — the unrecoverable state this whole fix exists to prevent. Do not thread ct.
                bool familyStillExists = await _dbContext.MachineModelProfiles
                    .AsNoTracking()
                    .AnyAsync(candidate => candidate.Id == family.Id, CancellationToken.None);
                if (!familyStillExists)
                {
                    await TryDeleteInstalledBundleAsync(family.Id);

                    // H3: on a rename we already created the TARGET-name alias above; the concurrent delete
                    // only knew the PREVIOUS name, so the target alias would otherwise survive pointing at a
                    // catalog model with no family or bundle behind it. Best-effort remove it (guarded) so
                    // no orphaned alias is left. No-op for a non-rename (target == previous name, which the
                    // concurrent delete already removed).
                    await TryRemoveOrphanedTargetAliasAsync(family, targetName, isRename);
                    throw new ProfileFamilyConcurrentlyDeletedException(
                        $"Profile family '{family.Id}' was deleted by a concurrent request while its bundle " +
                        "was being rendered; the partially installed bundle has been removed.",
                        ex);
                }

                // H1: the row survives (a concurrent MODIFICATION, not a delete) but our persist lost the
                // race, so the DB still holds the OLD state while the new bundle is already installed and the
                // alias already moved — a silent DB/worker divergence. Restore the previous good bundle and
                // alias and mark the row Failed (both guarded) BEFORE surfacing the 409, so the caller's
                // write genuinely lost but the farm is left coherent rather than split-brained. Symmetric
                // with the generic failure handler below, which does the same restore + mark-Failed.
                await RestorePreviousGoodStateAsync(
                    family, worker, previousBundle, previousName, targetName, isRename);
                await TryMarkRenderFailedAsync(family.Id);
                throw new ProfileFamilyConcurrencyException(
                    $"Profile family '{family.Id}' was modified by a concurrent request; retry the operation.",
                    ex);
            }
            catch (DbUpdateException ex) when (IsFamilyNameUniqueConstraintViolation(ex))
            {
                // A rename that raced past EnsureRenameAvailableAsync collides only here. The new bundle
                // and alias are already installed, so route through the restore path below (which reverts
                // both) and surface a 409 rather than a bare 500.
                throw new ProfileFamilyConflictException(
                    $"A slicer profile family named '{targetName}' already exists.",
                    ex);
            }
        }
        catch (ProfileFamilyConcurrentlyDeletedException)
        {
            // The concurrency handler above already rolled back the installed bundle, removed any orphaned
            // target alias (H3), and left the row alone (there is none). Rethrow WITHOUT the
            // restore/mark-Failed compensation below: restoring would re-install a bundle for a family that
            // no longer exists (re-stranding it), and marking Failed would write to a row that is gone.
            throw;
        }
        catch (ProfileFamilyConcurrencyException)
        {
            // A concurrent MODIFICATION (not a delete) won the race and the row survives. The concurrency
            // handler above already restored the previous good bundle and alias and marked the row Failed
            // (H1), so rethrow as a clean 409 WITHOUT repeating the restore/mark-Failed compensation below.
            throw;
        }
        catch (Exception ex)
        {
            // The DB row and variant set were never mutated before the single Healthy save above, so
            // there is nothing to roll back: restore the previous good worker bundle and alias, then
            // stamp only RenderStatus=Failed on the untouched row (MarkRenderFailedAsync detaches any
            // pending in-memory mutations and reloads the previous good row before flipping the status).
            // A failed re-render therefore never leaves the farm worse off, and the restore is idempotent
            // across repeated attempts because the persisted state is still the previous good one.
            await RestorePreviousGoodStateAsync(
                family, worker, previousBundle, previousName, targetName, isRename);
            await TryMarkRenderFailedAsync(family.Id);

            _logger.LogError(
                ex,
                "Failed to re-render profile family {FamilyId} for OrcaSlicer {Version}",
                family.Id,
                worker.OrcaVersion);
            throw;
        }
    }

    /// <summary>
    /// Restores the previous good worker bundle and OrcaSlicer alias after a failed re-render, so the
    /// family still slices via <c>GET /api/slicer/profiles/machine/for-model/{modelId}</c> and resolves
    /// under its previous name. Best-effort: every step is idempotent and a restore failure is logged
    /// rather than thrown, so it can never mask the original render failure. Symmetric with the DB
    /// rollback — the bundle, alias, and DB row are all returned to the pre-edit state together.
    /// </summary>
    private async Task RestorePreviousGoodStateAsync(
        MachineModelProfile family,
        ProfileFamilyWorkerTarget worker,
        ProfileFamilyBundleDto? previousBundle,
        string previousName,
        string targetName,
        bool isRename)
    {
        try
        {
            if (previousBundle is not null)
            {
                // The worker's InstallAsync removes a bundle on a blocking install failure rather than
                // restoring the prior one, so a failed re-render can leave the family with no bundle;
                // re-install the captured previous good bundle to recover it.
                await _workerClient.WriteBundleAsync(worker, previousBundle, CancellationToken.None);
            }

            if (family.PrinterModelId is Guid printerModelId)
            {
                // Restore the previous name's alias and drop the target name's alias (both idempotent),
                // so a failed rename does not leave the model resolving to a name whose bundle was rolled
                // back — which would return 404 and leave the model LESS resolvable than doing nothing.
                await _aliasService.EnsureModelAliasAsync(
                    printerModelId, previousName, "OrcaSlicer", CancellationToken.None);
                if (isRename)
                {
                    await _aliasService.RemoveModelAliasAsync(
                        printerModelId, targetName, "OrcaSlicer", CancellationToken.None);
                }

                await _catalogService.InvalidateModelAliasesAsync(printerModelId, CancellationToken.None);
            }
        }
        catch (Exception restoreEx)
        {
            _logger.LogError(
                restoreEx,
                "Failed to restore the previous good state for profile family {FamilyId} after a failed re-render",
                family.Id);
        }
    }

    /// <summary>
    /// Best-effort wrapper around <see cref="MarkRenderFailedAsync"/> for use inside a catch block: a
    /// failure to persist <see cref="ProfileFamilyRenderStatus.Failed"/> is logged rather than thrown, so
    /// it can never replace the original render/delete exception the caller still needs for its
    /// 422/503/500 classification (H3). Symmetric with the swallow-and-log guard in
    /// <see cref="RestorePreviousGoodStateAsync"/>.
    /// </summary>
    private async Task TryMarkRenderFailedAsync(Guid familyId)
    {
        try
        {
            await MarkRenderFailedAsync(familyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to persist RenderStatus=Failed for profile family {FamilyId} while handling an earlier failure",
                familyId);
        }
    }

    /// <summary>
    /// Persists ONLY <see cref="ProfileFamilyRenderStatus.Failed"/> (plus <c>UpdatedAt</c>) against the
    /// current on-disk row, discarding any pending in-memory mutations. Detaches the tracked graph so the
    /// reload reflects the rolled-back (previous good) state, guaranteeing the abandoned target values are
    /// never written — only the status flips. Uses <see cref="CancellationToken.None"/> so the family is
    /// still marked broken even if the request was cancelled.
    /// </summary>
    private async Task MarkRenderFailedAsync(Guid familyId)
    {
        DetachTrackedGraph();

        MachineModelProfile? reverted = await _dbContext.MachineModelProfiles
            .FirstOrDefaultAsync(candidate => candidate.Id == familyId, CancellationToken.None);
        if (reverted is null)
        {
            return;
        }

        reverted.RenderStatus = ProfileFamilyRenderStatus.Failed;
        reverted.UpdatedAt = DateTime.UtcNow;
        _ = await _dbContext.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Detaches every tracked entity so a follow-up query reflects the on-disk state rather than the
    /// abandoned in-memory mutations. Used after a failed save (render rollback) and after a concurrency
    /// conflict, where the pending graph must be discarded before re-reading the row.
    /// </summary>
    private void DetachTrackedGraph()
    {
        foreach (EntityEntry entry in _dbContext.ChangeTracker.Entries().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Best-effort removal of a bundle installed on the worker that was orphaned by a concurrent delete:
    /// the family row is already gone, so nothing else will ever drive this bundle's removal. Guarded the
    /// same way as <see cref="TryMarkRenderFailedAsync"/> (H3) — a cleanup failure is logged and swallowed
    /// so it can never mask the <see cref="ProfileFamilyConcurrentlyDeletedException"/> the caller is being
    /// given, and never throws out of the catch block that calls it. Uses <see cref="CancellationToken.None"/>
    /// so the orphaned bundle is still removed even if the original request was cancelled.
    /// </summary>
    private async Task TryDeleteInstalledBundleAsync(Guid familyId)
    {
        try
        {
            // Pass null (any fresh online worker), NEVER a pinned version: the bundle name is
            // version-independent (PrintFarmer-{familyId:N}) and the worker selector filters on EXACT
            // version equality, so pinning could select no worker and leave the bundle stranded — the same
            // reasoning as the delete path (C1). Idempotent: a worker 404 is treated as success.
            await _workerClient.DeleteBundleAsync(null, familyId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to roll back the worker bundle for profile family {FamilyId} after it was deleted concurrently during a render; the bundle may be orphaned on the worker.",
                familyId);
        }
    }

    /// <summary>
    /// Best-effort removal of the TARGET-name OrcaSlicer alias created during a rename whose family row was
    /// then deleted by a concurrent request (H3). The install added the new-name alias before the persist,
    /// but the concurrent delete only knew the PREVIOUS name, so the target alias would otherwise survive
    /// pointing at a catalog model with no family or bundle behind it. Guarded exactly like
    /// <see cref="TryDeleteInstalledBundleAsync"/>: a failure is logged and swallowed so it can never mask
    /// the <see cref="ProfileFamilyConcurrentlyDeletedException"/> the caller is being given, and never
    /// throws out of the catch block. No-op when the operation was not a rename (the target name equals the
    /// previous name, which the concurrent delete already removed) or the family has no bound catalog model.
    /// Uses <see cref="CancellationToken.None"/> so cleanup still runs if the original request was cancelled.
    /// </summary>
    private async Task TryRemoveOrphanedTargetAliasAsync(
        MachineModelProfile family,
        string targetName,
        bool isRename)
    {
        if (!isRename || family.PrinterModelId is not Guid printerModelId)
        {
            return;
        }

        try
        {
            await _aliasService.RemoveModelAliasAsync(printerModelId, targetName, "OrcaSlicer", CancellationToken.None);
            await _catalogService.InvalidateModelAliasesAsync(printerModelId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to remove the orphaned target alias '{TargetName}' for profile family {FamilyId} after it was deleted concurrently during a rename; the alias may be orphaned.",
                targetName,
                family.Id);
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

        // Every other exception is an unexpected internal failure (500-class). Return a fixed detail —
        // never exception.Message — so an unfiltered internal message can never leak onto the bulk
        // response, matching the fixed string the single-family endpoints return for a 500 (S5).
        _ => ("profile_family_render_failed", "Profile family re-render failed unexpectedly.")
    };

    // Nozzle-diameter equality tolerance. Kept at 1e-4 to match ProfileFamilyRenderer.NearlyEqual so the
    // service and renderer agree on which variants a rendered nozzle set matches; a tighter tolerance
    // here (previously 1e-6) could classify a surviving variant as removed and drop a referenced row (S6).
    private const double NozzleTolerance = 1e-4;

    private static bool NozzleMatches(double? candidate, double target) =>
        candidate is double value && Math.Abs(value - target) < NozzleTolerance;

    private async Task EnsureNoBlockingReferencesAsync(
        MachineModelProfile family,
        List<Guid> variantIds,
        string blockedAction,
        bool enforceCoverageLoss,
        bool forceCoverageLoss,
        CancellationToken ct)
    {
        // Direct binding: a live reference points at a concrete family VARIANT (a printer's template
        // machine profile, or a non-terminal slice job). Only meaningful when variants are being removed.
        if (variantIds.Count > 0)
        {
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
            // family variant. This is the DIRECT binding — an FK-ish pointer at a concrete variant row that
            // removing the family would orphan. It is never bypassed by forceCoverageLoss below: force only
            // waives the indirect coverage check, not this. Runs against the shared AppDbContext (monolith
            // and split modes alike).
            Printer? blockingPrinter =
                await _printerReferenceRepository.FindByTemplateMachineProfileIdsAsync(variantIds, ct);

            if (blockingPrinter is not null)
            {
                throw new ProfileFamilyInUseException(
                    $"Profile family '{family.Name}' cannot be {blockedAction} because printer " +
                    $"'{blockingPrinter.Name}' ({blockingPrinter.Id}) references one of its machine profiles.");
            }
        }

        // Indirect binding (coverage loss): a custom family exists precisely because OrcaSlicer ships no
        // profiles for that model (#2056), so the family's alias is frequently the model's ONLY OrcaSlicer
        // coverage. Deleting the family removes that alias and every printer of the model silently loses
        // coverage — GET .../profiles/machine/for-model/{modelId} starts returning 404 no_profiles_for_model.
        // Only DELETE removes the alias (a PATCH rename adds the new-name alias before dropping the old, so
        // coverage is never lost mid-edit), so only the delete path passes enforceCoverageLoss. The model
        // ROW surviving the delete is irrelevant here: a surviving row with no OrcaSlicer alias still has no
        // coverage. force bypasses ONLY this check (#2086 escape hatch), never the direct binding above.
        if (enforceCoverageLoss && !forceCoverageLoss)
        {
            await EnsureNoLastCoverageLossAsync(family, blockedAction, ct);
        }
    }

    /// <summary>
    /// Refuses when removing this family's OrcaSlicer alias would leave its bound catalog model with zero
    /// OrcaSlicer coverage AND a registered printer uses that model — the indirect binding #2086 describes.
    /// "Last coverage" mirrors the read path (<see cref="Farm.Slicer.Module.Api.Controllers.Slicing.ProfilesController"/>
    /// for-model resolution): OrcaSlicer coverage is the set of aliases whose slicer type is
    /// <c>OrcaSlicer</c>, compared by name case-insensitively. Comparing the model's remaining OrcaSlicer
    /// aliases against the family's own name (trimmed, case-insensitive — exactly how the alias is created
    /// and removed) means a genuinely distinct OrcaSlicer alias always short-circuits to allow, so this
    /// cannot raise a false refusal while other coverage exists. A family whose own alias is not present at
    /// all (its original render failed before the alias was created) strands nothing, so it is allowed
    /// without an override (H4).
    /// </summary>
    private async Task EnsureNoLastCoverageLossAsync(
        MachineModelProfile family,
        string blockedAction,
        CancellationToken ct)
    {
        // A family with no bound catalog model contributes no alias, so removing it cannot strand coverage.
        if (family.PrinterModelId is not Guid printerModelId)
        {
            return;
        }

        IReadOnlyList<SlicerModelAliasDto> aliases =
            await _catalogService.GetModelAliasesAsync(printerModelId, ct);

        string familyName = family.Name.Trim();

        // The delete removes THIS family's own OrcaSlicer alias, so only that alias's presence can strand
        // coverage. A family whose original render failed can be persisted before its alias was ever
        // created, so its own alias is absent from the model's set — deleting it removes nothing and loses
        // no coverage, so allow it without forcing an override (H4). An empty alias set is the same case.
        bool familyAliasPresent = aliases.Any(alias =>
            string.Equals(alias.SlicerType, "OrcaSlicer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(alias.SlicerModelName)
            && string.Equals(alias.SlicerModelName.Trim(), familyName, StringComparison.OrdinalIgnoreCase));
        if (!familyAliasPresent)
        {
            return;
        }

        bool otherOrcaCoverageRemains = aliases.Any(alias =>
            string.Equals(alias.SlicerType, "OrcaSlicer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(alias.SlicerModelName)
            && !string.Equals(alias.SlicerModelName.Trim(), familyName, StringComparison.OrdinalIgnoreCase));

        // Another OrcaSlicer alias survives the delete, so the model keeps coverage — nothing to strand.
        if (otherOrcaCoverageRemains)
        {
            return;
        }

        // This family's alias is the model's last OrcaSlicer coverage. Refuse only when a registered
        // printer actually uses the model; with no dependent printer there is nothing to orphan.
        Printer? affectedPrinter = await _printerReferenceRepository.FindByModelIdAsync(printerModelId, ct);
        if (affectedPrinter is null)
        {
            return;
        }

        throw new ProfileFamilyLastCoverageException(
            $"Profile family '{family.Name}' cannot be {blockedAction} because its OrcaSlicer alias is the " +
            $"last machine profile coverage for a model that printer '{affectedPrinter.Name}' " +
            $"({affectedPrinter.Id}) uses; removing it would leave that printer with no machine profiles. " +
            "Re-point the printer to another profile family, or pass force=true to delete anyway.");
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

        // sourceManufacturer is not persisted (the Manufacturer column is the literal "Custom"). It is
        // derivable ONLY by fetching the full worker catalog (see DeriveSourceManufacturer), which C4
        // forbids on this non-admin read path — doing so would add a per-family worker round-trip to
        // every GET. The recoverable source identity is surfaced via sourceMachineModelName instead; see
        // the ProfileFamilySummaryDto.SourceManufacturer XML doc for the full rationale.
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

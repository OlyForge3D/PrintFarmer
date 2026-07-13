using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Default <see cref="IFilamentCoverageService"/> implementation for issue #709.
/// Composes existing PrintFarmer building blocks: toolhead-spool bindings on
/// <see cref="Toolhead"/>, per-extruder gcode metadata on <see cref="GcodeFile"/>,
/// active + assigned-queued <see cref="PrintJob"/> rows, <see cref="ISpoolmanService"/>
/// for remaining weight, and <see cref="IPrinterStatusCacheReader"/> for batched
/// fleet progress. Never mutates spool remaining — completion reconciliation
/// remains owned by <see cref="PrintJobCompletionService"/>.
/// </summary>
public class FilamentCoverageService(
    AppDbContext db,
    IFilamentCoverageSpoolResolver spoolResolver,
    IPrintersService printersService,
    IPrinterStatusCacheReader printerStatusCache,
    ISettingsService settingsService,
    IOperatorFeatureGate featureGate,
    ILogger<FilamentCoverageService> logger)
    : IFilamentCoverageService, IFilamentCoverageAttentionSource
{
    // Machine-readable reason codes. Clients should NEVER localize these; they
    // are stable identifiers callers can key off of.
    private const string ReasonNoSpoolAssigned = "no-spool-assigned";
    private const string ReasonSpoolRemainingUnknown = "spool-remaining-unknown";
    private const string ReasonNoGcodeMetadata = "no-gcode-metadata";
    private const string ReasonNoPerExtruderMetadata = "no-per-extruder-metadata";
    private const string ReasonQueuedJobMetadataUnknown = "queued-job-metadata-unknown";
    private const string ReasonInsufficientRemaining = "insufficient-remaining";
    private const string ReasonNoActiveJob = "no-active-job";
    private const string ReasonMaterialMismatch = "material-mismatch";
    private const string ReasonSpoolMaterialUnknown = "spool-material-unknown";
    private const string ReasonToolheadUnavailable = "toolhead-unavailable";

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IFilamentCoverageSpoolResolver _spoolResolver = spoolResolver ?? throw new ArgumentNullException(nameof(spoolResolver));
    private readonly IPrintersService _printersService = printersService ?? throw new ArgumentNullException(nameof(printersService));
    private readonly IPrinterStatusCacheReader _printerStatusCache = printerStatusCache ?? throw new ArgumentNullException(nameof(printerStatusCache));
    private readonly ISettingsService _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    private readonly IOperatorFeatureGate _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
    private readonly ILogger<FilamentCoverageService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private SpoolCoverageSettings GetSettings()
    {
        try
        {
            return _settingsService.Get<SpoolCoverageSettings>() ?? new SpoolCoverageSettings();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FilamentCoverage] Falling back to default SpoolCoverageSettings");
            return new SpoolCoverageSettings();
        }
    }

    /// <inheritdoc />
    public async Task<PrinterFilamentCoverageDto?> GetForPrinterAsync(Guid printerId, CancellationToken ct)
    {
        Printer? printer = await _db.Printers
            .AsNoTracking()
            .Include(p => p.Toolheads)
            .FirstOrDefaultAsync(p => p.Id == printerId, ct)
            .ConfigureAwait(false);

        if (printer is null)
        {
            return null;
        }

        List<PrintJob> jobs = await _db.PrintJobs
            .AsNoTracking()
            .Include(j => j.GcodeFile)
            .AsSplitQuery()
            .Where(j => j.AssignedPrinterId == printerId
                && (j.Status == PrintJobStatus.Queued
                    || j.Status == PrintJobStatus.Assigned
                    || j.Status == PrintJobStatus.Starting
                    || j.Status == PrintJobStatus.Printing
                    || j.Status == PrintJobStatus.Paused))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> resolved =
            await _spoolResolver.ResolveAsync([printer], ct).ConfigureAwait(false);
        IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot> spoolLookup = resolved[printer.Id];

        SpoolCoverageSettings settings = GetSettings();

        // Fetch one bounded live status for the single-printer path; the fleet
        // path uses the batched status-cache snapshot instead.
        bool hasActive = jobs.Any(j =>
            j.Status is PrintJobStatus.Starting or PrintJobStatus.Printing or PrintJobStatus.Paused);
        double? liveProgress = hasActive
            ? await TryReadLiveProgressAsync(printer.Id, settings.LiveProgressTimeoutMs, ct).ConfigureAwait(false)
            : null;

        return ComputeForPrinter(printer, jobs, spoolLookup, liveProgress);
    }

    /// <inheritdoc />
    public async Task<FleetFilamentCoverageDto> GetForFleetAsync(CancellationToken ct)
    {
        List<Printer> printers = await _db.Printers
            .AsNoTracking()
            .Include(p => p.Toolheads)
            .OrderBy(p => p.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (printers.Count == 0)
        {
            return new FleetFilamentCoverageDto([], DateTime.UtcNow);
        }

        List<Guid> ids = printers.ConvertAll(p => p.Id);

        // Batch-load all jobs for the entire fleet in a single query to avoid
        // per-printer round-trips.
        List<PrintJob> allJobs = await _db.PrintJobs
            .AsNoTracking()
            .Include(j => j.GcodeFile)
            .AsSplitQuery()
            .Where(j => j.AssignedPrinterId != null
                && ids.Contains(j.AssignedPrinterId.Value)
                && (j.Status == PrintJobStatus.Queued
                    || j.Status == PrintJobStatus.Assigned
                    || j.Status == PrintJobStatus.Starting
                    || j.Status == PrintJobStatus.Printing
                    || j.Status == PrintJobStatus.Paused))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        Dictionary<Guid, List<PrintJob>> jobsByPrinter = allJobs
            .GroupBy(j => j.AssignedPrinterId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> fleetSpoolLookup =
            await _spoolResolver.ResolveAsync(printers, ct).ConfigureAwait(false);

        // Fleet progress comes from one thread-safe cache snapshot. All
        // supported polling/subscription backends populate this cache, avoiding
        // N live backend calls and any shared scoped-service concurrency risk.
        IReadOnlyDictionary<Guid, PrinterStatusDto> cachedStatuses = _printerStatusCache.GetAllStatuses();

        // Pure compute — no concurrent shared-context access, no exception
        // swallowing that would mask EF issues as "Unknown". Real errors
        // propagate so upstream tests/monitoring see regressions instead of
        // silent Unknown rows.
        List<PrinterFilamentCoverageDto> ordered = new(printers.Count);
        foreach (Printer printer in printers)
        {
            List<PrintJob> jobs = jobsByPrinter.TryGetValue(printer.Id, out List<PrintJob>? list) ? list : [];
            IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot> scoped = fleetSpoolLookup[printer.Id];
            double? liveProgress = cachedStatuses.TryGetValue(printer.Id, out PrinterStatusDto? cachedStatus)
                ? cachedStatus.Progress
                : null;
            ordered.Add(ComputeForPrinter(printer, jobs, scoped, liveProgress));
        }

        return new FleetFilamentCoverageDto(ordered, DateTime.UtcNow);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FilamentRunoutWarningDto>> GetRunoutWarningsAsync(CancellationToken ct)
    {
        SpoolCoverageSettings settings = GetSettings();

        if (!_featureGate.IsEnabled(OperatorFeature.FilamentCoverage))
        {
            return [];
        }

        FleetFilamentCoverageDto fleet = await GetForFleetAsync(ct).ConfigureAwait(false);
        TimeSpan lead = TimeSpan.FromMinutes(Math.Max(0, settings.RunoutWarningLeadMinutes));
        DateTime now = DateTime.UtcNow;

        List<FilamentRunoutWarningDto> warnings = [];
        foreach (PrinterFilamentCoverageDto printer in fleet.Printers)
        {
            foreach (ToolheadCoverageDto th in printer.Toolheads)
            {
                if (th.Status == FilamentCoverageStatus.Unknown)
                {
                    continue;
                }

                // When we have a concrete ETA, only warn if it falls within
                // the configured lead window. When we don't have an ETA but
                // the slot is Insufficient (queue-projection exceeds spool),
                // warn unconditionally UNLESS the operator opted out of
                // queued-shortage warnings via settings.
                bool hasEta = th.PredictedRunoutAt.HasValue;
                bool runoutSoon = hasEta && th.PredictedRunoutAt!.Value - now <= lead;
                bool etaLessInsufficient = !hasEta
                    && th.Status == FilamentCoverageStatus.Runout
                    && settings.QueuedShortageWarningsEnabled;

                if (!runoutSoon && !etaLessInsufficient)
                {
                    continue;
                }

                string reason = hasEta
                    ? "runout-during-active-job"
                    : "insufficient-for-assigned-queue";

                warnings.Add(new FilamentRunoutWarningDto(
                    printer.PrinterId,
                    printer.PrinterName,
                    th.ToolheadIndex,
                    th.SpoolId,
                    th.Material,
                    th.RemainingGrams,
                    th.PredictedRunoutAt,
                    reason));
            }
        }

        return warnings;
    }

    // ---------------------------------------------------------------------
    // Core computation (kept internal so tests can drive it deterministically).
    // PURE / synchronous / no I/O: no _db, no _printersService, no
    // _spoolmanService calls happen in here. Callers pre-fetch everything.
    // This is the seam that #709 convergence item 2 requires so fleet-wide
    // computation can be executed sequentially against a shared scoped
    // DbContext without racing on second-operation errors.
    // ---------------------------------------------------------------------
    internal PrinterFilamentCoverageDto ComputeForPrinter(
        Printer printer,
        List<PrintJob> jobs,
        IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot> spoolLookup,
        double? liveProgress)
    {
        SpoolCoverageSettings settings = GetSettings();
        DateTime evaluatedAt = DateTime.UtcNow;
        List<Toolhead> allToolheads = (printer.Toolheads ?? [])
            .OrderBy(t => t.Index)
            .ToList();
        List<Toolhead> toolheads = allToolheads
            .Where(t => ToolheadIndexMapper.IsFilamentSource(t, allToolheads))
            .ToList();

        PrintJob? activeJob = jobs.FirstOrDefault(j =>
            j.Status is PrintJobStatus.Starting or PrintJobStatus.Printing or PrintJobStatus.Paused);
        List<PrintJob> assignedQueuedJobs = jobs
            .Where(j => j.Id != activeJob?.Id
                && j.Status is PrintJobStatus.Assigned or PrintJobStatus.Queued)
            .ToList();

        // Per-toolhead PER-COPY grams for the active job.
        (Dictionary<int, double> activePerCopy, bool activeHasKnownMetadata, bool activeIsMultiToolMissing) =
            ComputePerCopyToolheadDemand(activeJob, toolheads);
        Dictionary<int, string> activeMaterials = ComputeToolheadMaterialRequirements(activeJob, activePerCopy.Keys);

        int activeRemainingCopies = activeJob is null ? 0 : Math.Max(0, activeJob.RemainingCopies);

        // Per-toolhead demand for each assigned queued job (aggregated).
        // Multi-copy queued jobs multiply per-copy grams by RemainingCopies.
        Dictionary<int, double> queuedDemand = new();
        Dictionary<int, HashSet<string>> queuedMaterials = new();
        HashSet<int> queuedUnknownIndices = [];
        HashSet<int> queuedTouchedIndices = [];
        bool queuedHasUnknownMetadata = false;
        foreach (PrintJob qj in assignedQueuedJobs)
        {
            (Dictionary<int, double> jobPerCopy, bool known, bool _) =
                ComputePerCopyToolheadDemand(qj, toolheads);

            if (!known)
            {
                queuedHasUnknownMetadata = true;

                // Unknown-metadata queued job. Without per-extruder or
                // fallback grams we cannot allocate demand to any slot; taint
                // every slot on this printer so the client sees Unknown for
                // the queue portion of the response.
                foreach (Toolhead t in toolheads)
                {
                    _ = queuedUnknownIndices.Add(t.Index);
                }

                continue;
            }

            int qRemaining = Math.Max(0, qj.RemainingCopies);
            if (qRemaining == 0)
            {
                continue;
            }

            foreach ((int idx, double grams) in jobPerCopy)
            {
                _ = queuedTouchedIndices.Add(idx);
                queuedDemand[idx] = (queuedDemand.TryGetValue(idx, out double existing) ? existing : 0) + (grams * qRemaining);
            }

            foreach ((int idx, string material) in ComputeToolheadMaterialRequirements(qj, jobPerCopy.Keys))
            {
                if (!queuedMaterials.TryGetValue(idx, out HashSet<string>? materials))
                {
                    materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    queuedMaterials[idx] = materials;
                }

                _ = materials.Add(material);
            }
        }

        List<ToolheadCoverageDto> slots = new(toolheads.Count);
        DateTime? earliestRunout = null;

        foreach (Toolhead th in toolheads)
        {
            FilamentCoverageSpoolSnapshot? spoolSnapshot = null;
            int? effectiveSpoolId = th.CurrentSpoolId ?? (th.IsPrimary ? printer.CurrentSpoolId : null);
            if (effectiveSpoolId is int sid)
            {
                _ = spoolLookup.TryGetValue(sid, out spoolSnapshot);
            }

            SpoolmanSpoolDto? spool = spoolSnapshot?.Spool;
            double? rawRemainingGrams = spool?.RemainingWeightG;
            string? loadedMaterial = spool?.Material
                ?? th.CurrentMaterial
                ?? (th.IsPrimary ? printer.CurrentMaterial : null);

            // Per-slot active demand. Slicer demand keys are 0-based G-code T-indices, so the
            // stored toolhead index (1-based for MMU gates) must be translated through the mapper
            // before matching (issue #711 round-10 Finding 2). A null G-code index is the shared
            // physical hotend of an MMU printer and never carries filament demand of its own.
            int? gcodeIndex =
                ToolheadIndexMapper.ToFilamentSourceGcodeToolIndex(th, toolheads);
            bool activeHasThisSlot = gcodeIndex.HasValue && activePerCopy.ContainsKey(gcodeIndex.Value);
            double perCopyGrams = activeHasThisSlot ? activePerCopy[gcodeIndex!.Value] : 0.0;

            double? currentJobRequired = activeHasThisSlot
                ? perCopyGrams * activeRemainingCopies
                : (double?)null;

            double consumedCurrentCopy = 0;
            double currentCopyRemaining = 0;
            double? currentJobRemaining = null;
            if (activeHasThisSlot && activeRemainingCopies > 0)
            {
                double currentCopyFrac = liveProgress.HasValue
                    ? Math.Clamp(1.0 - (liveProgress.Value / 100.0), 0.0, 1.0)
                    : 1.0;
                consumedCurrentCopy = perCopyGrams * (1.0 - currentCopyFrac);
                currentCopyRemaining = perCopyGrams * currentCopyFrac;
                double futureCopyRemaining = perCopyGrams * Math.Max(0, activeRemainingCopies - 1);
                currentJobRemaining = currentCopyRemaining + futureCopyRemaining;
            }

            // Managed sources are only decremented by PrintJobCompletionService
            // after a copy completes. During an active print, reconcile that
            // static snapshot by subtracting estimated consumption exactly
            // once. Native Moonraker Spoolman already tracks live consumption,
            // so its remaining weight is used directly.
            double? remainingGrams = rawRemainingGrams;
            if (remainingGrams.HasValue
                && activeHasThisSlot
                && spoolSnapshot?.TracksLiveConsumption == false)
            {
                remainingGrams = Math.Max(0, remainingGrams.Value - consumedCurrentCopy);
            }

            // Per-slot queued demand. queuedUnknownIndices is a per-toolhead taint set keyed by the
            // stored toolhead index; queuedTouchedIndices/queuedDemand are keyed by 0-based G-code
            // demand index, so those are matched through the mapped index (Finding 2).
            bool queuedUnknownForThisSlot = queuedUnknownIndices.Contains(th.Index);
            double? queuedRequired = queuedUnknownForThisSlot
                ? null
                : (gcodeIndex.HasValue && queuedTouchedIndices.Contains(gcodeIndex.Value)
                    ? (queuedDemand.TryGetValue(gcodeIndex.Value, out double q) ? q : 0.0)
                    : 0.0);

            double? totalDemand = null;
            if (currentJobRemaining.HasValue && queuedRequired.HasValue)
            {
                totalDemand = currentJobRemaining.Value + queuedRequired.Value;
            }
            else if (!activeHasThisSlot && queuedRequired.HasValue)
            {
                // Slot is not used by the active job at all (or no active
                // job) — total demand is just the queued portion.
                totalDemand = queuedRequired.Value;
            }

            string? requiredActiveMaterial = currentJobRemaining is > 0
                && gcodeIndex.HasValue
                && activeMaterials.TryGetValue(gcodeIndex.Value, out string? activeMaterial)
                    ? activeMaterial
                    : null;
            HashSet<string>? requiredQueuedMaterials = queuedRequired is > 0
                && gcodeIndex.HasValue
                && queuedMaterials.TryGetValue(gcodeIndex.Value, out HashSet<string>? queuedMaterialSet)
                    ? queuedMaterialSet
                    : null;
            (bool materialCompatible, string? materialReason) = CheckMaterialCompatibility(
                loadedMaterial,
                requiredActiveMaterial,
                requiredQueuedMaterials);
            double? availableForNewDemand = spool is not null
                && remainingGrams.HasValue
                && totalDemand.HasValue
                && !queuedUnknownForThisSlot
                && materialCompatible
                && (activeJob is null || activeHasKnownMetadata)
                    ? Math.Max(0.0, remainingGrams.Value - settings.ReserveGrams - totalDemand.Value)
                    : null;

            // Determine status + reason.
            FilamentCoverageStatus status;
            string? reason;
            (status, reason) = ClassifySlot(
                spool,
                spoolSnapshot?.ErrorReason,
                effectiveSpoolId,
                activeJob,
                activeHasKnownMetadata,
                activeIsMultiToolMissing,
                currentJobRemaining,
                queuedUnknownForThisSlot,
                queuedRequired,
                totalDemand,
                remainingGrams,
                settings.ReserveGrams,
                materialCompatible,
                materialReason);

            DateTime? runoutAt = null;
            int? runoutLayer = null;
            if (status == FilamentCoverageStatus.Runout
                && remainingGrams is double rem
                && currentJobRemaining is double activeRemaining
                && activeRemaining > 0
                && perCopyGrams > 0
                && activeJob is not null
                && activeJob.EstimatedPrintTime is TimeSpan dur
                && dur.TotalSeconds > 0
                && rem < currentCopyRemaining - settings.ReserveGrams)
            {
                // Managed snapshots are completion-updated, so their raw value
                // is already the current-copy start weight. Native snapshots
                // are live and need estimated consumption added back.
                double startRemainingGrams = spoolSnapshot?.TracksLiveConsumption == true
                    ? rem + consumedCurrentCopy
                    : rawRemainingGrams!.Value;
                double availableAtCopyStart = Math.Max(0.0, startRemainingGrams - settings.ReserveGrams);
                double runoutFraction = Math.Clamp(availableAtCopyStart / perCopyGrams, 0.0, 1.0);

                if (activeJob.ActualStartTime is DateTime startedAt)
                {
                    runoutAt = startedAt.AddSeconds(runoutFraction * dur.TotalSeconds);
                }
                else
                {
                    double currentProgressFraction = liveProgress.HasValue
                        ? Math.Clamp(liveProgress.Value / 100.0, 0.0, 1.0)
                        : 0.0;
                    double remainingFraction = Math.Max(0.0, runoutFraction - currentProgressFraction);
                    runoutAt = evaluatedAt.AddSeconds(remainingFraction * dur.TotalSeconds);
                }

                if (activeJob.GcodeFile?.TotalLayers is int totalLayers && totalLayers > 0)
                {
                    double runoutLayerRaw = totalLayers * runoutFraction;
                    runoutLayer = (int)Math.Round(Math.Clamp(runoutLayerRaw, 1, totalLayers));
                }

                if (earliestRunout is null || runoutAt < earliestRunout)
                {
                    earliestRunout = runoutAt;
                }
            }

            slots.Add(new ToolheadCoverageDto(
                th.Index,
                string.IsNullOrWhiteSpace(th.Name) ? $"Extruder {th.Index + 1}" : th.Name,
                effectiveSpoolId,
                loadedMaterial,
                spool?.ColorHex ?? th.CurrentFilamentColor,
                remainingGrams,
                currentJobRequired,
                currentJobRemaining,
                queuedRequired,
                totalDemand,
                status,
                reason,
                runoutAt,
                runoutLayer)
            {
                ToolheadId = th.Id,
                AvailableForNewDemandGrams = availableForNewDemand,
            });
        }

        // Demand keys are 0-based G-code indices; project the physical toolheads into the same
        // index space before deciding which demand rows have no matching slot (Finding 2).
        HashSet<int> physicalIndices = toolheads
            .Select(ToolheadIndexMapper.ToGcodeToolIndex)
            .Where(index => index.HasValue)
            .Select(index => index!.Value)
            .ToHashSet();
        IEnumerable<int> missingRequiredIndices = activePerCopy.Keys
            .Concat(queuedDemand.Keys)
            .Where(index => !physicalIndices.Contains(index))
            .Where(index =>
            {
                double perCopy = activePerCopy.TryGetValue(index, out double activeGrams) ? activeGrams : 0;
                double currentFraction = liveProgress.HasValue
                    ? Math.Clamp(1.0 - (liveProgress.Value / 100.0), 0.0, 1.0)
                    : 1.0;
                double activeRemaining = (perCopy * currentFraction)
                    + (perCopy * Math.Max(0, activeRemainingCopies - 1));
                double queuedRemaining = queuedDemand.TryGetValue(index, out double queuedGrams) ? queuedGrams : 0;
                return activeRemaining > 0 || queuedRemaining > 0;
            })
            .Distinct()
            .OrderBy(index => index);
        foreach (int index in missingRequiredIndices)
        {
            double perCopyGrams = activePerCopy.TryGetValue(index, out double activeGrams) ? activeGrams : 0;
            double? currentJobRequired = perCopyGrams > 0 ? perCopyGrams * activeRemainingCopies : null;
            double? currentJobRemaining = null;
            if (currentJobRequired.HasValue)
            {
                double currentCopyFraction = liveProgress.HasValue
                    ? Math.Clamp(1.0 - (liveProgress.Value / 100.0), 0.0, 1.0)
                    : 1.0;
                currentJobRemaining = (perCopyGrams * currentCopyFraction)
                    + (perCopyGrams * Math.Max(0, activeRemainingCopies - 1));
            }

            double queuedRequired = queuedDemand.TryGetValue(index, out double queuedGrams) ? queuedGrams : 0;
            double totalDemand = (currentJobRemaining ?? 0) + queuedRequired;
            string? requiredMaterial = activeMaterials.TryGetValue(index, out string? activeMaterial)
                ? activeMaterial
                : queuedMaterials.TryGetValue(index, out HashSet<string>? materials)
                    ? materials.FirstOrDefault()
                    : null;
            slots.Add(new ToolheadCoverageDto(
                index,
                $"Extruder {index + 1}",
                null,
                requiredMaterial,
                null,
                null,
                currentJobRequired,
                currentJobRemaining,
                queuedRequired,
                totalDemand,
                FilamentCoverageStatus.Unknown,
                ReasonToolheadUnavailable,
                null,
                null));
        }

        if (slots.Count == 0 && (activeJob is not null || assignedQueuedJobs.Count > 0))
        {
            string reason = activeJob is not null && !activeHasKnownMetadata
                ? activeIsMultiToolMissing ? ReasonNoPerExtruderMetadata : ReasonNoGcodeMetadata
                : queuedHasUnknownMetadata ? ReasonQueuedJobMetadataUnknown : ReasonToolheadUnavailable;
            slots.Add(new ToolheadCoverageDto(
                0,
                "Extruder 1",
                null,
                null,
                null,
                null,
                null,
                null,
                queuedHasUnknownMetadata ? null : 0,
                null,
                FilamentCoverageStatus.Unknown,
                reason,
                null,
                null));
        }

        slots.Sort((left, right) => left.ToolheadIndex.CompareTo(right.ToolheadIndex));
        FilamentCoverageStatus aggregate = AggregateStatus(slots);

        return new PrinterFilamentCoverageDto(
            printer.Id,
            printer.Name,
            aggregate,
            slots,
            activeJob?.Id,
            activeJob?.Name,
            liveProgress,
            earliestRunout,
            assignedQueuedJobs.Count,
            evaluatedAt);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns per-toolhead PER-COPY grams demanded by the job. Callers
    /// multiply by <see cref="PrintJob.RemainingCopies"/> to get the full
    /// remaining demand (#709 convergence item 3). When the gcode carries
    /// per-extruder metadata each extruder's grams map directly to the
    /// matching toolhead index. Falls back to a single-tool primary-toolhead
    /// assignment only when the gcode declares zero/one extruder. Multi-
    /// extruder jobs whose gcode omits <c>FilamentPerExtruderWeightG</c> are
    /// treated as "unknown metadata" (never a false positive).
    /// </summary>
    private (Dictionary<int, double> perCopy, bool hasKnownMetadata, bool multiToolMissingBreakdown) ComputePerCopyToolheadDemand(
        PrintJob? job,
        List<Toolhead> toolheads)
    {
        Dictionary<int, double> demand = new();
        if (job is null || job.GcodeFile is null)
        {
            return (demand, false, false);
        }

        double[]? perExtruder = ParsePerExtruder(job.GcodeFile.FilamentPerExtruderWeightG);

        // Multi-toolhead breakdown wins when present.
        if (perExtruder is { Length: > 0 })
        {
            for (int i = 0; i < perExtruder.Length; i++)
            {
                double grams = perExtruder[i];
                if (grams > 0)
                {
                    demand[i] = grams;
                }
            }

            return (demand, demand.Count > 0, false);
        }

        int? extruderCount = job.GcodeFile.ExtruderCount;
        bool declaredMultiTool = extruderCount is > 1;

        // Single-tool fallback: attribute EstimatedFilamentWeightG (or the
        // job's own estimate copy) to the primary toolhead. Only safe when
        // the gcode does NOT declare multiple extruders.
        if (!declaredMultiTool)
        {
            double? single = job.GcodeFile.EstimatedFilamentWeightG ?? job.EstimatedFilamentUsage;
            if (single is > 0)
            {
                // Attribute the single-extruder estimate to the primary toolhead in 0-based G-code
                // space so it matches the per-extruder path and the slot loop's mapper-based
                // lookups (issue #711 round-10 Finding 2).
                Toolhead? primaryToolhead = toolheads.FirstOrDefault(t => t.IsPrimary)
                    ?? toolheads.FirstOrDefault();
                int primaryIdx = (primaryToolhead is not null
                    ? ToolheadIndexMapper.ToFilamentSourceGcodeToolIndex(primaryToolhead, toolheads)
                    : null) ?? 0;
                demand[primaryIdx] = single.Value;
                return (demand, true, false);
            }

            return (demand, false, false);
        }

        // Multi-tool declared but no per-extruder breakdown. This is the
        // "unknown metadata" case #709 must never turn into a false runout.
        return (demand, false, true);
    }

    private static double[]? ParsePerExtruder(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<double[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<int, string> ComputeToolheadMaterialRequirements(
        PrintJob? job,
        IEnumerable<int> demandedIndices)
    {
        Dictionary<int, string> materials = [];
        if (job?.GcodeFile is null)
        {
            return materials;
        }

        string[]? perExtruder = ParseStringArray(job.GcodeFile.FilamentPerExtruderType);
        string? fallback = !string.IsNullOrWhiteSpace(job.RequiredMaterialType)
            ? job.RequiredMaterialType
            : job.GcodeFile.RequiredMaterial;
        foreach (int index in demandedIndices)
        {
            string? material = perExtruder is not null && index < perExtruder.Length
                ? perExtruder[index]
                : fallback;
            if (string.IsNullOrWhiteSpace(material))
            {
                material = fallback;
            }

            if (!string.IsNullOrWhiteSpace(material))
            {
                materials[index] = material.Trim();
            }
        }

        return materials;
    }

    private static string[]? ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (bool compatible, string? reason) CheckMaterialCompatibility(
        string? loadedMaterial,
        string? activeMaterial,
        IReadOnlySet<string>? queuedMaterials)
    {
        List<string> required = [];
        if (!string.IsNullOrWhiteSpace(activeMaterial))
        {
            required.Add(activeMaterial);
        }

        if (queuedMaterials is not null)
        {
            required.AddRange(queuedMaterials);
        }

        if (required.Count == 0)
        {
            return (true, null);
        }

        if (string.IsNullOrWhiteSpace(loadedMaterial))
        {
            return (false, ReasonSpoolMaterialUnknown);
        }

        return required.All(material => string.Equals(material, loadedMaterial, StringComparison.OrdinalIgnoreCase))
            ? (true, null)
            : (false, ReasonMaterialMismatch);
    }

    private static (FilamentCoverageStatus status, string? reason) ClassifySlot(
        SpoolmanSpoolDto? spool,
        string? spoolErrorReason,
        int? currentSpoolId,
        PrintJob? activeJob,
        bool activeHasKnownMetadata,
        bool activeIsMultiToolMissing,
        double? currentJobRemaining,
        bool queuedUnknownForThisSlot,
        double? queuedRequired,
        double? totalDemand,
        double? remainingGrams,
        double reserveGrams,
        bool materialCompatible,
        string? materialReason)
    {
        // Determine whether any real demand exists on this slot (needed for
        // the no-spool branch below and for the "no active job, empty queue"
        // Covers shortcut).
        bool activeDemandExists = currentJobRemaining.HasValue && currentJobRemaining.Value > 0;
        bool queuedDemandExists = queuedRequired.HasValue && queuedRequired.Value > 0;
        bool anyDemand = activeDemandExists || queuedDemandExists || queuedUnknownForThisSlot;

        // Active demand with unknown usage metadata is always Unknown, even
        // when the slot has no bound spool and calculated demand is absent.
        if (activeJob is not null && !activeHasKnownMetadata)
        {
            return (FilamentCoverageStatus.Unknown,
                activeIsMultiToolMissing ? ReasonNoPerExtruderMetadata : ReasonNoGcodeMetadata);
        }

        // Unknown data cases first — never turn into false positives.
        if (currentSpoolId is null)
        {
            // NO-SPOOL (#709 convergence item 4): a slot with no spool bound
            // is Covers ONLY when nothing demands filament from it. Any
            // active-job demand, any queued-job demand, or any unknown queued
            // metadata on this slot → Unknown / no-spool-assigned. This
            // includes the "toolhead unused by active but needed by queue"
            // case that the prior activeJob-null shortcut let slip through.
            if (!anyDemand)
            {
                return (FilamentCoverageStatus.Covers, null);
            }

            return (FilamentCoverageStatus.Unknown, ReasonNoSpoolAssigned);
        }

        if (spool is null)
        {
            return (FilamentCoverageStatus.Unknown, spoolErrorReason ?? FilamentCoverageSpoolResolver.ReasonSourceUnavailable);
        }

        if (remainingGrams is null)
        {
            return (FilamentCoverageStatus.Unknown, ReasonSpoolRemainingUnknown);
        }

        if (queuedUnknownForThisSlot)
        {
            return (FilamentCoverageStatus.Unknown, ReasonQueuedJobMetadataUnknown);
        }

        if (!materialCompatible)
        {
            return (FilamentCoverageStatus.Unknown, materialReason);
        }

        if (activeJob is null && !queuedDemandExists)
        {
            return (FilamentCoverageStatus.Covers, ReasonNoActiveJob);
        }

        double usable = Math.Max(0.0, remainingGrams.Value - reserveGrams);
        double demand = totalDemand ?? 0.0;

        if (usable + 1e-6 >= demand)
        {
            return (FilamentCoverageStatus.Covers, null);
        }

        return (FilamentCoverageStatus.Runout, ReasonInsufficientRemaining);
    }

    private static FilamentCoverageStatus AggregateStatus(List<ToolheadCoverageDto> slots)
    {
        if (slots.Count == 0)
        {
            return FilamentCoverageStatus.Covers;
        }

        bool anyRunout = false;
        bool anyUnknown = false;
        foreach (ToolheadCoverageDto s in slots)
        {
            if (s.Status == FilamentCoverageStatus.Runout)
            {
                anyRunout = true;
            }
            else if (s.Status == FilamentCoverageStatus.Unknown)
            {
                anyUnknown = true;
            }
        }

        if (anyRunout)
        {
            return FilamentCoverageStatus.Runout;
        }

        return anyUnknown ? FilamentCoverageStatus.Unknown : FilamentCoverageStatus.Covers;
    }

    private async Task<double?> TryReadLiveProgressAsync(Guid printerId, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, timeoutMs)));
            PrintJobStatusDto? status = await _printersService.GetPrintJobStatusAsync(printerId, linked.Token).ConfigureAwait(false);
            return status?.Progress;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("[FilamentCoverage] Live progress timed out for printer {PrinterId}", printerId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FilamentCoverage] Live progress unavailable for printer {PrinterId}", printerId);
            return null;
        }
    }
}

using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
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
/// for remaining weight, and <see cref="IPrintersService.GetPrintJobStatusAsync"/>
/// for live progress. Never mutates spool remaining — completion reconciliation
/// remains owned by <see cref="PrintJobCompletionService"/>.
/// </summary>
public class FilamentCoverageService(
    AppDbContext db,
    ISpoolmanService spoolmanService,
    IPrintersService printersService,
    ISettingsService settingsService,
    ILogger<FilamentCoverageService> logger)
    : IFilamentCoverageService, IFilamentCoverageAttentionSource
{
    // Machine-readable reason codes. Clients should NEVER localize these; they
    // are stable identifiers callers can key off of.
    private const string ReasonSpoolmanUnconfigured = "spoolman-unconfigured";
    private const string ReasonNoSpoolAssigned = "no-spool-assigned";
    private const string ReasonSpoolRemainingUnknown = "spool-remaining-unknown";
    private const string ReasonNoGcodeMetadata = "no-gcode-metadata";
    private const string ReasonNoPerExtruderMetadata = "no-per-extruder-metadata";
    private const string ReasonQueuedJobMetadataUnknown = "queued-job-metadata-unknown";
    private const string ReasonInsufficientRemaining = "insufficient-remaining";
    private const string ReasonNoActiveJob = "no-active-job";

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ISpoolmanService _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));
    private readonly IPrintersService _printersService = printersService ?? throw new ArgumentNullException(nameof(printersService));
    private readonly ISettingsService _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
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

        Dictionary<int, SpoolmanSpoolDto?> spoolLookup = await ResolveSpoolsAsync(printer, ct).ConfigureAwait(false);

        SpoolCoverageSettings settings = GetSettings();

        // Fetch live progress synchronously for the single-printer path; the
        // fleet path pre-fetches sequentially so it can call the pure compute.
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
        SpoolCoverageSettings settings = GetSettings();

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

        // Batch-resolve every referenced spool once.
        HashSet<int> spoolIds = [];
        foreach (Printer p in printers)
        {
            foreach (Toolhead t in p.Toolheads ?? [])
            {
                if (t.CurrentSpoolId is int id)
                {
                    spoolIds.Add(id);
                }
            }
        }

        Dictionary<int, SpoolmanSpoolDto?> fleetSpoolLookup = await FetchSpoolsAsync(spoolIds, ct).ConfigureAwait(false);

        // FLEET THREAD-SAFETY (#709 convergence item 2): pre-fetch live
        // progress for every printer with an active job SEQUENTIALLY so we
        // never make overlapping calls into shared scoped services
        // (IPrintersService/AppDbContext are not safe for concurrent use).
        // The compute phase below is pure and does not touch _db or
        // _printersService, so it can safely run in a simple sequential loop.
        Dictionary<Guid, double?> progressByPrinter = new(printers.Count);
        foreach (Printer printer in printers)
        {
            bool hasActive = jobsByPrinter.TryGetValue(printer.Id, out List<PrintJob>? pjobs)
                && pjobs.Any(j => j.Status is PrintJobStatus.Starting or PrintJobStatus.Printing or PrintJobStatus.Paused);

            progressByPrinter[printer.Id] = hasActive
                ? await TryReadLiveProgressAsync(printer.Id, settings.LiveProgressTimeoutMs, ct).ConfigureAwait(false)
                : null;
        }

        // Pure compute — no concurrent shared-context access, no exception
        // swallowing that would mask EF issues as "Unknown". Real errors
        // propagate so upstream tests/monitoring see regressions instead of
        // silent Unknown rows.
        List<PrinterFilamentCoverageDto> ordered = new(printers.Count);
        foreach (Printer printer in printers)
        {
            List<PrintJob> jobs = jobsByPrinter.TryGetValue(printer.Id, out List<PrintJob>? list) ? list : [];
            Dictionary<int, SpoolmanSpoolDto?> scoped = ScopeSpoolLookup(printer, fleetSpoolLookup);
            _ = progressByPrinter.TryGetValue(printer.Id, out double? liveProgress);
            ordered.Add(ComputeForPrinter(printer, jobs, scoped, liveProgress));
        }

        return new FleetFilamentCoverageDto(ordered, DateTime.UtcNow);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FilamentRunoutWarningDto>> GetRunoutWarningsAsync(CancellationToken ct)
    {
        SpoolCoverageSettings settings = GetSettings();

        // Rebase-note (#725): once IOperatorFeatureGate.FilamentCoverageEnabled
        // exists, replace this local Enabled check with the operator gate. The
        // gate is authoritative: a disabled feature must emit no warnings at
        // all so the attention feed's suppression contract stays clean.
        if (!settings.Enabled)
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
                    && th.Status == FilamentCoverageStatus.Insufficient
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
        Dictionary<int, SpoolmanSpoolDto?> spoolLookup,
        double? liveProgress)
    {
        SpoolCoverageSettings settings = GetSettings();
        DateTime evaluatedAt = DateTime.UtcNow;
        List<Toolhead> toolheads = (printer.Toolheads ?? [])
            .OrderBy(t => t.Index)
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

        // COPIES (#709 convergence item 3): the classification basis is
        // remaining copies of the active job (current copy in full, plus every
        // subsequent copy) NOT prorated by progress. Progress only affects the
        // display "how much of the CURRENT copy remains" value, never the
        // classification. This keeps status stable as progress ticks.
        int activeRemainingCopies = activeJob is null ? 0 : Math.Max(0, activeJob.RemainingCopies);

        // Per-toolhead demand for each assigned queued job (aggregated).
        // Multi-copy queued jobs multiply per-copy grams by RemainingCopies.
        Dictionary<int, double> queuedDemand = new();
        HashSet<int> queuedUnknownIndices = [];
        HashSet<int> queuedTouchedIndices = [];
        foreach (PrintJob qj in assignedQueuedJobs)
        {
            (Dictionary<int, double> jobPerCopy, bool known, bool _) =
                ComputePerCopyToolheadDemand(qj, toolheads);

            if (!known)
            {
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
        }

        List<ToolheadCoverageDto> slots = new(toolheads.Count);
        DateTime? earliestRunout = null;

        foreach (Toolhead th in toolheads)
        {
            SpoolmanSpoolDto? spool = null;
            if (th.CurrentSpoolId is int sid)
            {
                _ = spoolLookup.TryGetValue(sid, out spool);
            }

            double? remainingGrams = spool?.RemainingWeightG;

            // Per-slot active demand.
            bool activeHasThisSlot = activePerCopy.ContainsKey(th.Index);
            double perCopyGrams = activeHasThisSlot ? activePerCopy[th.Index] : 0.0;

            // STABLE-BASIS classification (#709 convergence item 1):
            // CurrentJobRequiredGrams reports the FULL demand across every
            // remaining copy (per-copy × RemainingCopies). This value is
            // stable while a print is running — it does NOT shrink with
            // progress, so an initially-insufficient job cannot silently flip
            // to Covers. This is also what feeds the classifier below.
            double? currentJobRequired = activeHasThisSlot
                ? perCopyGrams * activeRemainingCopies
                : (double?)null;

            // Display-only prorated remaining grams. Kept for the UI so it
            // can show "you'll consume ~X g more before the print ends".
            // Progress applies to the CURRENT copy; whole future copies are
            // added at full weight.
            double? currentJobRemainingDisplay = null;
            if (activeHasThisSlot && activeRemainingCopies > 0)
            {
                double currentCopyFrac = liveProgress.HasValue
                    ? Math.Clamp(1.0 - (liveProgress.Value / 100.0), 0.0, 1.0)
                    : 1.0;
                double currentCopyRemaining = perCopyGrams * currentCopyFrac;
                double futureCopyRemaining = perCopyGrams * Math.Max(0, activeRemainingCopies - 1);
                currentJobRemainingDisplay = currentCopyRemaining + futureCopyRemaining;
            }

            // Per-slot queued demand.
            bool queuedUnknownForThisSlot = queuedUnknownIndices.Contains(th.Index);
            double? queuedRequired = queuedUnknownForThisSlot
                ? null
                : (queuedTouchedIndices.Contains(th.Index)
                    ? (queuedDemand.TryGetValue(th.Index, out double q) ? q : 0.0)
                    : 0.0);

            // Total demand for CLASSIFICATION (#709 item 1): stable basis.
            double? totalDemand = null;
            if (currentJobRequired.HasValue && queuedRequired.HasValue)
            {
                totalDemand = currentJobRequired.Value + queuedRequired.Value;
            }
            else if (!activeHasThisSlot && queuedRequired.HasValue)
            {
                // Slot is not used by the active job at all (or no active
                // job) — total demand is just the queued portion.
                totalDemand = queuedRequired.Value;
            }

            // Determine status + reason.
            FilamentCoverageStatus status;
            string? reason;
            (status, reason) = ClassifySlot(
                spool,
                th.CurrentSpoolId,
                activeJob,
                activeHasKnownMetadata,
                activeIsMultiToolMissing,
                currentJobRequired,
                queuedUnknownForThisSlot,
                queuedRequired,
                totalDemand,
                remainingGrams,
                settings.ReserveGrams);

            // Predicted runout for the ACTIVE job only. Anchored to the job's
            // ActualStartTime + estimated duration so ETA does NOT drift as
            // progress ticks (#709 convergence item 1). Falls back to
            // "now + usable/reqFull × dur" when start time is unavailable.
            DateTime? runoutAt = null;
            int? runoutLayer = null;
            if (status == FilamentCoverageStatus.Insufficient
                && spool?.RemainingWeightG is double rem
                && rem > 0
                && currentJobRequired is double reqFull
                && reqFull > 0
                && activeJob is not null
                && activeJob.EstimatedPrintTime is TimeSpan dur
                && dur.TotalSeconds > 0
                && rem < reqFull - settings.ReserveGrams)
            {
                double usableRemaining = Math.Max(0.0, rem - settings.ReserveGrams);
                double runoutFraction = usableRemaining / reqFull;

                if (activeJob.ActualStartTime is DateTime startedAt)
                {
                    // Stable anchor: run-out is at start + fraction * duration.
                    // Independent of DateTime.UtcNow so successive polls give
                    // the same answer.
                    runoutAt = startedAt.AddSeconds(runoutFraction * dur.TotalSeconds);
                }
                else
                {
                    // Fallback: no known start time; approximate from wall-clock.
                    double secondsToRunout = usableRemaining * dur.TotalSeconds / reqFull;
                    runoutAt = evaluatedAt.AddSeconds(secondsToRunout);
                }

                if (activeJob.GcodeFile?.TotalLayers is int totalLayers && totalLayers > 0)
                {
                    // Stable layer projection: does NOT include a
                    // progress-derived "consumed" term. Layer at runout is
                    // purely the usable fraction of the print.
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
                th.CurrentSpoolId,
                th.CurrentMaterial ?? spool?.Material,
                th.CurrentFilamentColor ?? spool?.ColorHex,
                remainingGrams,
                currentJobRequired,
                currentJobRemainingDisplay,
                queuedRequired,
                totalDemand,
                status,
                reason,
                runoutAt,
                runoutLayer));
        }

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
                int primaryIdx = toolheads.FirstOrDefault(t => t.IsPrimary)?.Index
                    ?? toolheads.FirstOrDefault()?.Index
                    ?? 0;
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

    private static (FilamentCoverageStatus status, string? reason) ClassifySlot(
        SpoolmanSpoolDto? spool,
        int? currentSpoolId,
        PrintJob? activeJob,
        bool activeHasKnownMetadata,
        bool activeIsMultiToolMissing,
        double? currentJobRequired,
        bool queuedUnknownForThisSlot,
        double? queuedRequired,
        double? totalDemand,
        double? remainingGrams,
        double reserveGrams)
    {
        // Determine whether any real demand exists on this slot (needed for
        // the no-spool branch below and for the "no active job, empty queue"
        // Covers shortcut).
        bool activeDemandExists = currentJobRequired.HasValue && currentJobRequired.Value > 0;
        bool queuedDemandExists = queuedRequired.HasValue && queuedRequired.Value > 0;
        bool anyDemand = activeDemandExists || queuedDemandExists || queuedUnknownForThisSlot;

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
            return (FilamentCoverageStatus.Unknown, ReasonSpoolmanUnconfigured);
        }

        if (remainingGrams is null)
        {
            return (FilamentCoverageStatus.Unknown, ReasonSpoolRemainingUnknown);
        }

        if (queuedUnknownForThisSlot)
        {
            return (FilamentCoverageStatus.Unknown, ReasonQueuedJobMetadataUnknown);
        }

        if (activeJob is not null && !activeHasKnownMetadata)
        {
            return (FilamentCoverageStatus.Unknown,
                activeIsMultiToolMissing ? ReasonNoPerExtruderMetadata : ReasonNoGcodeMetadata);
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

        return (FilamentCoverageStatus.Insufficient, ReasonInsufficientRemaining);
    }

    private static FilamentCoverageStatus AggregateStatus(List<ToolheadCoverageDto> slots)
    {
        if (slots.Count == 0)
        {
            return FilamentCoverageStatus.Covers;
        }

        bool anyInsufficient = false;
        bool anyUnknown = false;
        foreach (ToolheadCoverageDto s in slots)
        {
            if (s.Status == FilamentCoverageStatus.Insufficient)
            {
                anyInsufficient = true;
            }
            else if (s.Status == FilamentCoverageStatus.Unknown)
            {
                anyUnknown = true;
            }
        }

        if (anyInsufficient)
        {
            return FilamentCoverageStatus.Insufficient;
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
        catch (OperationCanceledException)
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

    private async Task<Dictionary<int, SpoolmanSpoolDto?>> ResolveSpoolsAsync(Printer printer, CancellationToken ct)
    {
        HashSet<int> ids = [];
        foreach (Toolhead th in printer.Toolheads ?? [])
        {
            if (th.CurrentSpoolId is int sid)
            {
                _ = ids.Add(sid);
            }
        }

        return await FetchSpoolsAsync(ids, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, SpoolmanSpoolDto?>> FetchSpoolsAsync(HashSet<int> ids, CancellationToken ct)
    {
        Dictionary<int, SpoolmanSpoolDto?> result = new();
        foreach (int id in ids)
        {
            try
            {
                result[id] = await _spoolmanService.GetSpoolByIdAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[FilamentCoverage] Spool {SpoolId} could not be resolved from Spoolman — treating as unknown",
                    id);
                result[id] = null;
            }
        }

        return result;
    }

    private static Dictionary<int, SpoolmanSpoolDto?> ScopeSpoolLookup(Printer printer, Dictionary<int, SpoolmanSpoolDto?> shared)
    {
        Dictionary<int, SpoolmanSpoolDto?> scoped = new();
        foreach (Toolhead th in printer.Toolheads ?? [])
        {
            if (th.CurrentSpoolId is int sid && shared.TryGetValue(sid, out SpoolmanSpoolDto? spool))
            {
                scoped[sid] = spool;
            }
        }

        return scoped;
    }
}

using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Modules.Maintenance.Services.Maintenance;

/// <summary>
/// Background service that synchronizes printer statistics from printer APIs for maintenance tracking.
/// Polls printer backends (Moonraker/PrusaLink/OctoPrint/SDCP) to collect cumulative print hours,
/// job counts, and filament usage to track maintenance needs.
/// </summary>
public class PrintStatsSyncHostedService(
    IServiceProvider serviceProvider,
    ILogger<PrintStatsSyncHostedService> logger,
    IOptionsMonitor<PrintStatsSyncSettings> settingsMonitor,
    IBackgroundServiceMonitor serviceMonitor) : BackgroundService
{
    private const string ServiceId = "PrintStatsSyncService";

    // M19-1 (issue #711, round-19): external print-hours counter discontinuity detection.
    // HoursEpsilon absorbs floating-point noise so a flat/no-op reading is never misread as a
    // decrease. ReboundSlackFactor/ReboundSlackHours bound how much the external total may
    // plausibly grow between two ticks: cumulative print-hours can never advance faster than
    // real wall-clock time elapses, so a "rebound" delta that exceeds elapsed time (plus a small
    // multiplicative + additive slack for clock jitter) indicates the reading itself is spurious
    // (e.g. Moonraker history.reset_totals, or a transient dip-then-recovery), not real wear.
    private const double HoursEpsilon = 0.001;
    private const double ReboundSlackFactor = 1.05;
    private const double ReboundSlackHours = 0.05;

    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<PrintStatsSyncHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptionsMonitor<PrintStatsSyncSettings> _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
    private readonly IBackgroundServiceMonitor _serviceMonitor = serviceMonitor ?? throw new ArgumentNullException(nameof(serviceMonitor));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PrintStatsSyncSettings settings = _settingsMonitor.CurrentValue;

        // Register with the service monitor
        _serviceMonitor.Register(
            ServiceId,
            "Print Statistics Sync",
            "Synchronizes printer statistics from backends for maintenance tracking",
            "Maintenance",
            "pf-icon-stats",
            settings.IntervalSeconds);
        _serviceMonitor.ReportStarted(ServiceId);

        if (!settings.Enabled)
        {
            _logger.LogInformation("Print statistics sync service is disabled");
            _serviceMonitor.ReportEnabled(ServiceId, false);
            return;
        }

        _serviceMonitor.ReportEnabled(ServiceId, true);
        _logger.LogInformation(
            "Print statistics sync service started. Interval: {Interval}s, Max printers per iteration: {MaxPrinters}",
            settings.IntervalSeconds,
            settings.MaxPrintersPerIteration);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                settings = _settingsMonitor.CurrentValue; // Reload settings each iteration
                if (!settings.Enabled)
                {
                    _logger.LogInformation("Print statistics sync disabled, pausing service");
                    _serviceMonitor.ReportEnabled(ServiceId, false);
                    continue;
                }

                _serviceMonitor.ReportEnabled(ServiceId, true);
                await SyncPrinterStatisticsAsync(settings, stoppingToken);
                _serviceMonitor.ReportSuccess(ServiceId, settings.IntervalSeconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Print statistics sync service stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during print statistics sync");
                _serviceMonitor.ReportError(ServiceId, ex.Message);
            }
        }

        _serviceMonitor.ReportStopped(ServiceId);
    }

    internal async Task SyncPrinterStatisticsAsync(PrintStatsSyncSettings settings, CancellationToken ct)
    {
        try
        {
            // Keyset rotation query (issue #2061): materializes at most MaxPrintersPerIteration
            // rows, ordered by staleness (PrinterServiceState.LastStatsSyncAttemptedAt ascending,
            // never-attempted first) with Id as a tiebreaker, so every printer is synced within a
            // bounded number of intervals instead of only the first N ever advancing. Deliberately
            // does NOT use GetAllAsync — other callers depend on its full-table, unordered semantics.
            List<Printer> printers;
            using (IServiceScope printerListScope = _serviceProvider.CreateScope())
            {
                IPrintersRepository printersRepo =
                    printerListScope.ServiceProvider.GetRequiredService<IPrintersRepository>();
                printers = await printersRepo.GetForStatsSyncRotationAsync(settings.MaxPrintersPerIteration, ct);
            }

            if (printers.Count == 0)
            {
                _logger.LogDebug("No printers found to sync statistics");
                return;
            }

            _logger.LogInformation(
                "Syncing statistics for {SyncCount} printers this iteration",
                printers.Count);

            // Issue #2329: printers sharing a ModelId within this rotation batch reuse one
            // grouped job-statistics aggregate query instead of each re-materializing the
            // model's entire all-time job history. Scoped to this single cycle only (fresh
            // dictionary per call) and populated lazily as printers are processed below, so a
            // failing model's aggregate query is isolated to just the printers that share it -
            // matching the per-printer scope isolation already used elsewhere in this loop.
            Dictionary<Guid, PrintJobStatisticsAggregate> jobStatsAggregateCache = [];

            // Process each printer
            foreach (Printer printer in printers)
            {
                try
                {
                    // A printer owns one scoped AppDbContext/unit of work. If any operation fails
                    // after mutating its tracked baseline, disposing this scope discards those
                    // mutations while later printers continue in fresh scopes.
                    using IServiceScope printerScope = _serviceProvider.CreateScope();
                    IServiceProvider scopedServices = printerScope.ServiceProvider;
                    IPrinterStatisticsRepository statsRepo =
                        scopedServices.GetRequiredService<IPrinterStatisticsRepository>();
                    IToolheadStatisticsRepository toolheadStatsRepo =
                        scopedServices.GetRequiredService<IToolheadStatisticsRepository>();
                    IPrintJobStatisticsRepository jobStatsRepo =
                        scopedServices.GetRequiredService<IPrintJobStatisticsRepository>();
                    IOperatorFeatureGate featureGate =
                        scopedServices.GetRequiredService<IOperatorFeatureGate>();

                    ToolheadActivitySnapshot? activitySnapshot = await SyncPrinterStatisticsAsync(
                        printer,
                        settings,
                        statsRepo,
                        toolheadStatsRepo,
                        jobStatsRepo,
                        featureGate,
                        scopedServices,
                        ct,
                        jobStatsAggregateCache);

                    // Persist only after the complete per-printer flow succeeds. This atomically
                    // commits the external baseline, aggregate totals, and toolhead wear. Pending
                    // telemetry is acknowledged only after that commit, so a failed save retries
                    // against the same baseline-scoped evidence on the next cycle.
                    await CommitAndAcknowledgeAsync(
                        statsRepo,
                        scopedServices.GetService<IToolheadActivityAccumulator>(),
                        activitySnapshot,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to sync statistics for printer '{Name}' (ID: {Id}, Backend: {Backend})",
                        printer.Name,
                        printer.Id,
                        (PrinterBackend)printer.Backend);
                }
                finally
                {
                    // Advance the rotation cursor even on failure: a printer whose sync keeps
                    // throwing (or whose backend is unreachable every tick) must not permanently
                    // monopolize the front of the queue and starve every other printer behind it
                    // (issue #2061 review finding). This intentionally uses its own isolated
                    // scope/DbContext, separate from the per-printer processing scope above, so it
                    // can never be affected by (or accidentally persist) whatever that scope left
                    // tracked-but-unsaved after a failure. It is also independent of
                    // PrinterStatistics.LastSyncTime, which keeps its existing "last ACTUAL
                    // successful sync" meaning used elsewhere for backend-history math.
                    try
                    {
                        using IServiceScope cursorScope = _serviceProvider.CreateScope();
                        IPrintersRepository cursorPrintersRepo =
                            cursorScope.ServiceProvider.GetRequiredService<IPrintersRepository>();
                        await cursorPrintersRepo.MarkStatsSyncAttemptedAsync(printer.Id, DateTime.UtcNow, ct);
                    }
                    catch (Exception markEx)
                    {
                        _logger.LogWarning(
                            markEx,
                            "Failed to advance print-stats-sync rotation cursor for printer '{Name}' (ID: {Id})",
                            printer.Name,
                            printer.Id);
                    }
                }
            }

            _logger.LogDebug("Print statistics sync completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during printer statistics sync scan");
        }
    }

    internal async Task<ToolheadActivitySnapshot?> SyncPrinterStatisticsAsync(
        Printer printer,
        PrintStatsSyncSettings settings,
        IPrinterStatisticsRepository statsRepo,
        IToolheadStatisticsRepository toolheadStatsRepo,
        IPrintJobStatisticsRepository jobStatsRepo,
        IOperatorFeatureGate featureGate,
        IServiceProvider serviceProvider,
        CancellationToken ct,
        IDictionary<Guid, PrintJobStatisticsAggregate>? jobStatsAggregateCache = null)
    {
        _logger.LogDebug(
            "Syncing statistics for printer '{Name}' (ID: {Id}, Backend: {Backend})",
            printer.Name,
            printer.Id,
            (PrinterBackend)printer.Backend);

        // Get or create printer statistics
        PrinterStatistics? stats = await statsRepo.GetByPrinterIdAsync(printer.Id, ct);

        // Capture prior state BEFORE any mutation (issue #711, round-7 Finding 1).
        //
        // Per-toolhead attribution (FIX B) only credits INCREMENTAL external hours once a baseline
        // exists. A brand-new statistics row dumps the full backend history into the printer-wide
        // counter but NOT onto any toolhead, so per-tool tracking "starts fresh" from the next sync.
        //
        // The baseline is read from the dedicated ExternalPrintHours counter, NOT TotalPrintHours
        // (round-5 BLOCKER): TotalPrintHours is inflated at the end of every cycle by
        // SyncPrintFarmerJobStatisticsAsync, so reading the baseline from it made the external delta
        // collapse to 0 forever after the first cycle.
        //
        // ExternalBaselineInitializedUtc is a null-sentinel gate (round-7 Finding 1). While it is
        // null the external baseline has NEVER been trustworthy-captured, so we must not snapshot a
        // possibly PF-inflated TotalPrintHours as the baseline, must not attribute a historical
        // delta, and (for a supported-but-failed sync) must not run the reset-then-add that would
        // otherwise permanently double a previously-inflated total.
        bool statsExisted = stats != null;
        double previousExternalHours = stats?.ExternalPrintHours ?? 0;
        DateTime? previousExternalAttributionUtc = stats?.LastExternalHoursAttributionUtc;
        bool baselineInitialized = stats?.ExternalBaselineInitializedUtc != null;
        DateTime syncUtc = DateTime.UtcNow;

        if (stats == null)
        {
            stats = new PrinterStatistics
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id
            };
        }

        // Sync from external printer API. The tri-state outcome distinguishes a trustworthy refresh
        // (Succeeded) from a backend that cannot report external history (Unsupported, e.g.
        // PrusaLink) and a supported backend whose call failed this cycle (Failed).
        ExternalSyncOutcome outcome = await SyncExternalPrinterStatisticsAsync(
            printer,
            stats,
            settings,
            serviceProvider,
            ct);

        double externalDelta = 0;
        bool attributionEligible = false;

        // M19-1 (issue #711, round-19): set when this cycle's fresh external total is a
        // discontinuity (a decrease, e.g. Moonraker history.reset_totals, or an implausible
        // rebound) rather than genuine incremental wear. A discontinuity still needs its epoch
        // closed out below — the accumulator must be drained and the attribution boundary
        // advanced — but with zero credited hours, so the NEXT cycle starts a clean window
        // instead of spanning across the reset.
        bool externalCounterDiscontinuity = false;

        switch (outcome)
        {
            case ExternalSyncOutcome.Succeeded:
                // Backend history just refreshed TotalPrintHours/TotalJobsCompleted. Snapshot the
                // external-only totals from the freshly synced values BEFORE PrintFarmer job
                // aggregation inflates TotalPrintHours.
                double freshExternalHours = stats.TotalPrintHours;
                long freshExternalJobs = stats.TotalJobsCompleted;

                if (baselineInitialized)
                {
                    double rawDelta = freshExternalHours - previousExternalHours;
                    bool isDecrease = rawDelta < -HoursEpsilon;

                    // Cumulative print-hours can advance by at most one wall-clock hour per
                    // wall-clock hour elapsed. A same-or-larger jump (beyond a small slack for
                    // clock/sync jitter) since the last attributed tick is physically implausible
                    // as real wear, so treat it as a discontinuity too — this also covers a
                    // transient dip immediately followed by a "recovery" that would otherwise
                    // re-credit the historical gap once compared against the dip's low baseline.
                    bool isImplausibleRebound = !isDecrease
                        && previousExternalAttributionUtc is DateTime prevAttributionForRebound
                        && rawDelta > (Math.Max(0, (syncUtc - prevAttributionForRebound).TotalHours) * ReboundSlackFactor) + ReboundSlackHours;

                    if (isDecrease || isImplausibleRebound)
                    {
                        externalCounterDiscontinuity = true;
                        externalDelta = 0;
                        attributionEligible = false;
                        _logger.LogWarning(
                            "Printer '{Name}' external print-hours counter discontinuity detected " +
                            "(previous={Previous:F2}h, fresh={Fresh:F2}h, reason={Reason}); baseline " +
                            "and attribution boundary advance this cycle with zero hours credited " +
                            "(issue #711, round-19 M19-1).",
                            printer.Name,
                            previousExternalHours,
                            freshExternalHours,
                            isDecrease ? "decrease" : "implausible-rebound");
                    }
                    else
                    {
                        // Established baseline, no discontinuity: attribute the incremental growth.
                        externalDelta = Math.Max(0, rawDelta);
                        attributionEligible = true;
                    }
                }
                else
                {
                    // First trustworthy external sync: capture the baseline but DO NOT attribute the
                    // full historical total as one cycle's wear (round-7 Finding 1).
                    externalDelta = 0;
                    attributionEligible = false;
                }

                stats.ExternalPrintHours = freshExternalHours;
                stats.ExternalJobsCompleted = freshExternalJobs;
                stats.ExternalBaselineInitializedUtc ??= syncUtc;
                baselineInitialized = true;
                break;

            case ExternalSyncOutcome.Unsupported:
                // Backend cannot report external history (e.g. PrusaLink). The authoritative external
                // baseline is zero and PrintFarmer job history is the only source. Snapshot the zero
                // baseline once so the reset-then-add below stays idempotent instead of compounding
                // PF totals every cycle. Importantly this snapshots an authoritative zero, NOT a
                // possibly PF-inflated TotalPrintHours.
                stats.ExternalPrintHours = 0;
                stats.ExternalJobsCompleted = 0;
                stats.ExternalBaselineInitializedUtc ??= syncUtc;
                baselineInitialized = true;
                externalDelta = 0;
                attributionEligible = false;
                break;

            default:
                // Supported backend that failed this cycle (ExternalSyncOutcome.Failed). Keep the
                // last-known external baseline untouched and attribute nothing. If the baseline was
                // never initialized we must NOT snapshot a possibly PF-inflated TotalPrintHours, so
                // we leave TotalPrintHours authoritative and skip the reset-then-add below (guarded
                // by baselineInitialized).
                externalDelta = 0;
                attributionEligible = false;
                break;
        }

        // PrintFarmer history is an absolute all-time aggregate, not a per-cycle delta. Reset the
        // combined totals to their last-known external baselines before adding it so failed or
        // unsupported external syncs cannot compound PF totals each cycle. Only aggregate once a
        // trustworthy baseline exists; otherwise the reset target (ExternalPrintHours) is not yet
        // meaningful and TotalPrintHours stays authoritative (round-7 Finding 1).
        bool aggregatePrintFarmerJobs = settings.IncludePrintFarmerJobs && baselineInitialized;
        if (aggregatePrintFarmerJobs)
        {
            stats.TotalPrintHours = stats.ExternalPrintHours;
            stats.TotalJobsCompleted = checked((int)stats.ExternalJobsCompleted);
            await SyncPrintFarmerJobStatisticsAsync(
                printer,
                stats,
                jobStatsRepo,
                jobStatsAggregateCache ?? new Dictionary<Guid, PrintJobStatisticsAggregate>(),
                ct);
        }

        ToolheadActivitySnapshot? snapshotToAcknowledge = null;
        double? attributionWindowSeconds = null;

        // Update statistics in database
        if (outcome == ExternalSyncOutcome.Succeeded || aggregatePrintFarmerJobs)
        {
            // Attribute only an established external backend's per-cycle delta. PrintFarmer job
            // aggregation above is mutable and model-wide, so it is not a reliable wear source.
            // The increment remains uncommitted until the outer scoped SaveChangesAsync.
            IToolheadActivityAccumulator? activityAccumulator =
                serviceProvider.GetService<IToolheadActivityAccumulator>();

            // M19-1 (issue #711, round-19): a discontinuity closes out its epoch the same way a
            // genuine credit does — peek-and-later-acknowledge the accumulator through "now" and
            // advance the boundary — but WITHOUT computing an attribution window, so the (already
            // zeroed) externalDelta below is guaranteed to attribute nothing for this cycle. The
            // next cycle's real delta then measures from this fresh boundary instead of spanning
            // across the reset.
            if (externalDelta > 0 || externalCounterDiscontinuity)
            {
                snapshotToAcknowledge = activityAccumulator?.PeekActiveSeconds(printer.Id);
                if (externalDelta > 0)
                {
                    if (previousExternalAttributionUtc is DateTime previousAttribution)
                    {
                        attributionWindowSeconds = Math.Max(
                            0,
                            (syncUtc - previousAttribution).TotalSeconds);
                    }
                    else
                    {
                        attributionEligible = false;
                        _logger.LogInformation(
                            "Printer '{Name}' advanced external history by {Delta:F2}h with no persisted " +
                            "attribution boundary; per-toolhead wear is unattributed for this cycle.",
                            printer.Name,
                            externalDelta);
                    }
                }

                stats.LastExternalHoursAttributionUtc = syncUtc;
            }

            stats.LastSyncTime = syncUtc;
            await statsRepo.UpsertAsync(stats, ct);

            IReadOnlyList<Guid> credited = await AttributeExternalToolheadHoursAsync(
                printer.Id,
                statsExisted,
                attributionEligible,
                await featureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false),
                printer.SupportsPerToolAttribution,
                externalDelta,
                toolheadStatsRepo,
                ct,
                _logger,
                snapshotToAcknowledge,
                attributionWindowSeconds: attributionWindowSeconds);
            if (credited.Count > 0)
            {
                _logger.LogDebug(
                    "Attributed {Delta:F2}h across {ToolheadCount} physical toolheads on printer '{Name}'",
                    externalDelta,
                    credited.Count,
                    printer.Name);
            }
        }

        return snapshotToAcknowledge;
    }

    internal static async Task CommitAndAcknowledgeAsync(
        IPrinterStatisticsRepository statsRepo,
        IToolheadActivityAccumulator? activityAccumulator,
        ToolheadActivitySnapshot? activitySnapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(statsRepo);
        await statsRepo.SaveChangesAsync(ct);
        if (activitySnapshot is not null)
        {
            activityAccumulator?.AckActiveSecondsThrough(activitySnapshot);
        }
    }

    /// <summary>
    /// Tri-state result of an external-backend statistics sync (issue #711, round-7 Finding 1).
    /// </summary>
    internal enum ExternalSyncOutcome
    {
        /// <summary>Backend history was refreshed successfully this cycle.</summary>
        Succeeded,

        /// <summary>Backend does not support external history totals (e.g. PrusaLink).</summary>
        Unsupported,

        /// <summary>A supported backend's history call failed or returned no data this cycle.</summary>
        Failed
    }

    internal static async Task<IReadOnlyList<Guid>> AttributeExternalToolheadHoursAsync(
        Guid printerId,
        bool statsExisted,
        bool externalSyncSuccess,
        bool perToolMaintenanceEnabled,
        bool supportsPerToolAttribution,
        double externalDelta,
        IToolheadStatisticsRepository toolheadStatsRepo,
        CancellationToken ct,
        ILogger? logger = null,
        ToolheadActivitySnapshot? activitySnapshot = null,
        IToolheadActivityAccumulator? activityAccumulator = null,
        double? attributionWindowSeconds = null)
    {
        if (!statsExisted
            || !externalSyncSuccess
            || !perToolMaintenanceEnabled
            || externalDelta <= 0)
        {
            return [];
        }

        // Per-toolhead wear must be backed by real per-tool telemetry. When the backend cannot
        // attribute the external-history delta to specific toolheads (issue #711, round-10
        // Finding 1) we must NOT fabricate wear by equal-splitting the delta across idle heads.
        // Leave the delta unattributed for per-toolhead wear; the caller has already advanced the
        // printer-wide totals and the ExternalPrintHours baseline for this cycle.
        if (!supportsPerToolAttribution)
        {
            logger?.LogInformation(
                "Printer {PrinterId} has no per-tool attribution capability; per-toolhead wear is " +
                "unattributed for this cycle ({Delta:F2}h external history delta) (issue #711).",
                printerId,
                externalDelta);
            return [];
        }

        ToolheadActivitySnapshot snapshot =
            activitySnapshot
            ?? activityAccumulator?.PeekActiveSeconds(printerId)
            ?? ToolheadActivitySnapshot.Empty(printerId);
        IReadOnlyDictionary<int, Guid> physicalToolheads =
            await toolheadStatsRepo.GetPhysicalToolheadIdsByIndexAsync(printerId, ct);
        if (physicalToolheads.Count == 0)
        {
            return [];
        }

        // Primary path (issue #711, round-14): distribute the external-history delta across physical
        // toolheads in proportion to the per-tool active time the backend actually observed over this
        // sync interval. Unlike the single-snapshot fallback below, this correctly represents tool
        // switches that happened within the interval — the head that printed most gets the most wear.
        Dictionary<Guid, double> perToolheadSeconds = new();
        foreach ((int toolIndex, double seconds) in snapshot.ActiveSeconds)
        {
            if (seconds > 0 && physicalToolheads.TryGetValue(toolIndex, out Guid toolheadId))
            {
                perToolheadSeconds[toolheadId] = perToolheadSeconds.TryGetValue(toolheadId, out double existing)
                    ? existing + seconds
                    : seconds;
            }
        }

        double recognizedPhysicalSeconds = perToolheadSeconds.Values.Sum();
        double windowSeconds = attributionWindowSeconds ?? snapshot.WindowSeconds;

        // Known-idle seconds (issue #711, round-19 V19-1/H19-1) are a CONFIRMED absence of print —
        // not missing telemetry — so they must be excluded from the coverage denominator entirely.
        // Otherwise a printer that is idle most of the day has its print-time coverage diluted by
        // the idle hours, destroying the vast majority of the external-history delta attribution
        // (e.g. 1h of fully-observed printing after 23h of confirmed idle would previously compute
        // coverage = 1h / 24h ≈ 0.04 instead of the correct 1.0).
        double effectiveWindowSeconds = Math.Max(0, windowSeconds - snapshot.KnownIdleSeconds);
        if (recognizedPhysicalSeconds > 0 && effectiveWindowSeconds > 0)
        {
            double coverage = Math.Min(recognizedPhysicalSeconds / effectiveWindowSeconds, 1);
            Dictionary<Guid, double> weights = perToolheadSeconds.ToDictionary(
                static kvp => kvp.Key,
                kvp => (kvp.Value / recognizedPhysicalSeconds) * coverage);
            ToolheadHourAttribution intervalAttribution = ToolheadHourAttribution.FromWeights(weights, externalDelta);
            IReadOnlyList<Guid> intervalCredited = await toolheadStatsRepo.ApplyToolheadHoursAsync(printerId, intervalAttribution, ct);
            logger?.LogDebug(
                "Printer {PrinterId} attributed {Credited:F2}h of a {Delta:F2}h external delta across " +
                "{ToolheadCount} physical toolhead(s) with {Coverage:P1} telemetry coverage over a " +
                "{EffectiveWindow:F0}s effective window ({Window:F0}s minus {KnownIdle:F0}s known-idle) " +
                "(issue #711).",
                printerId,
                intervalAttribution.TotalHours,
                externalDelta,
                intervalCredited.Count,
                coverage,
                effectiveWindowSeconds,
                windowSeconds,
                snapshot.KnownIdleSeconds);
            return intervalCredited;
        }

        // A point-in-time status cannot quantify coverage, so no full-delta fallback is permitted.
        logger?.LogInformation(
            "Printer {PrinterId} supports per-tool attribution but produced no active-tool " +
            "duration telemetry for its {Window:F0}s baseline window ({EffectiveWindow:F0}s " +
            "effective after excluding {KnownIdle:F0}s known-idle); per-toolhead wear is " +
            "unattributed for the {Delta:F2}h external history delta (issue #711).",
            printerId,
            windowSeconds,
            effectiveWindowSeconds,
            snapshot.KnownIdleSeconds,
            externalDelta);
        return [];
    }

    private async Task<ExternalSyncOutcome> SyncExternalPrinterStatisticsAsync(
        Printer printer,
        PrinterStatistics stats,
        PrintStatsSyncSettings settings,
        IServiceProvider serviceProvider,
        CancellationToken ct)
    {
        try
        {
            PrinterBackend backend = (PrinterBackend)printer.Backend;

            // Only Moonraker and OctoPrint currently support external history statistics
            if (backend != PrinterBackend.Moonraker && backend != PrinterBackend.OctoPrint)
            {
                _logger.LogDebug("{Backend} printer '{Name}' - using PrintFarmer job history only", backend, printer.Name);
                return ExternalSyncOutcome.Unsupported;
            }

            bool synced = await SyncBackendHistoryStatisticsAsync(printer, stats, serviceProvider, settings, ct);
            return synced ? ExternalSyncOutcome.Succeeded : ExternalSyncOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to sync external statistics for printer '{Name}'",
                printer.Name);
            return ExternalSyncOutcome.Failed;
        }
    }

    private async Task<bool> SyncBackendHistoryStatisticsAsync(
        Printer printer,
        PrinterStatistics stats,
        IServiceProvider serviceProvider,
        PrintStatsSyncSettings settings,
        CancellationToken ct)
    {
        try
        {
            PrinterBackend backend = (PrinterBackend)printer.Backend;
            IBackendClientFactory clientFactory = serviceProvider.GetRequiredService<IBackendClientFactory>();
            IBackendClient client = clientFactory.GetClient(backend);

            if (client is not ISupportsHistory historyClient)
            {
                _logger.LogWarning(
                    "Backend {Backend} does not support history for statistics sync on printer '{Name}'",
                    backend, printer.Name);
                return false;
            }

            // Use timeout from settings
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(settings.ApiTimeoutSeconds));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            _logger.LogInformation(
                "Fetching history totals from {Backend} for printer '{Name}' at {Url}",
                backend,
                printer.Name,
                printer.BackendUrl);

            HistoryTotals? historyTotals = await historyClient.GetHistoryTotalsAsync(
                printer.BackendUrl,
                printer.Credential,
                linkedCts.Token);

            if (historyTotals == null || historyTotals.JobTotals == null)
            {
                _logger.LogWarning(
                    "No history totals available from {Backend} for printer '{Name}' (historyTotals={HasTotals}, jobTotals={HasJobTotals})",
                    backend,
                    printer.Name,
                    historyTotals != null,
                    historyTotals?.JobTotals != null);
                return false;
            }

            // Extract statistics
            JobTotals jobTotals = historyTotals.JobTotals;

            // Update statistics
            // Backend TotalTime is in seconds, convert to hours
            stats.TotalPrintHours = jobTotals.TotalPrintTime / 3600.0;
            stats.TotalJobsCompleted = (int)jobTotals.TotalJobs;

            // Backend TotalFilamentUsed is in mm, convert to meters and approximate grams (PLA 1.75mm)
            double filamentMm = jobTotals.TotalFilamentUsed;
            stats.TotalFilamentUsedMeters = filamentMm / 1000.0;
            stats.TotalFilamentUsedGrams = filamentMm * 0.00237;

            _logger.LogInformation(
                "Synced {Backend} statistics for '{Name}': {Hours:F1}h, {Jobs} jobs, {Filament:F1}m filament",
                backend,
                printer.Name,
                stats.TotalPrintHours,
                stats.TotalJobsCompleted,
                stats.TotalFilamentUsedMeters);

            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "{Backend} statistics sync timed out for printer '{Name}'",
                (PrinterBackend)printer.Backend, printer.Name);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to sync {Backend} statistics for printer '{Name}'",
                (PrinterBackend)printer.Backend,
                printer.Name);
            return false;
        }
    }

    private async Task SyncPrintFarmerJobStatisticsAsync(
        Printer printer,
        PrinterStatistics stats,
        IPrintJobStatisticsRepository jobStatsRepo,
        IDictionary<Guid, PrintJobStatisticsAggregate> jobStatsAggregateCache,
        CancellationToken ct)
    {
        try
        {
            // TODO(#711): Replace this model-wide, all-time aggregation with a per-printer
            // completion watermark. Until then it must not feed per-toolhead wear attribution.
            // Note: PrintJobStatistics doesn't have PrinterId directly, so we query by printer
            // model.
            //
            // Issue #2329: printers that share a ModelId within the same sync cycle reuse one
            // grouped SQL aggregate (COUNT + SUM) instead of each independently re-fetching and
            // re-summing the model's entire all-time job history. The cache is populated lazily,
            // per distinct ModelId, and only on success - a failed aggregate query is never
            // cached, so it doesn't silently suppress aggregation for a later printer sharing the
            // same (possibly transiently failing) model.
            if (!jobStatsAggregateCache.TryGetValue(printer.ModelId, out PrintJobStatisticsAggregate? aggregate))
            {
                aggregate = await jobStatsRepo.GetAggregateByPrinterModelAsync(
                    printer.ModelId,
                    successfulOnly: true,
                    fromDate: null,
                    cancellationToken: ct);
                jobStatsAggregateCache[printer.ModelId] = aggregate;
            }

            if (aggregate.JobCount == 0)
            {
                _logger.LogDebug(
                    "No PrintFarmer job statistics found for printer model of '{Name}'",
                    printer.Name);
                return;
            }

            // Add to existing statistics (combining external and PrintFarmer data)
            stats.TotalJobsCompleted += aggregate.JobCount;
            stats.TotalPrintHours += aggregate.TotalDurationHours;

            _logger.LogDebug(
                "Added PrintFarmer statistics for '{Name}': +{Jobs} jobs, +{Hours}h",
                printer.Name,
                aggregate.JobCount,
                aggregate.TotalDurationHours);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to sync PrintFarmer job statistics for printer '{Name}'",
                printer.Name);
            throw;
        }
    }
}

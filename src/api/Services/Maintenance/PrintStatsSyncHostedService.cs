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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Maintenance;

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
            List<Printer> printers;
            using (IServiceScope printerListScope = _serviceProvider.CreateScope())
            {
                IPrintersRepository printersRepo =
                    printerListScope.ServiceProvider.GetRequiredService<IPrintersRepository>();
                printers = await printersRepo.GetAllAsync(ct);
            }

            if (printers.Count == 0)
            {
                _logger.LogDebug("No printers found to sync statistics");
                return;
            }

            // Limit printers per iteration to avoid overload
            int printersToSync = Math.Min(printers.Count, settings.MaxPrintersPerIteration);

            _logger.LogInformation(
                "Syncing statistics for {SyncCount} of {TotalCount} printers",
                printersToSync,
                printers.Count);

            // Process each printer
            for (int i = 0; i < printersToSync; i++)
            {
                Printer printer = printers[i];

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

                    await SyncPrinterStatisticsAsync(
                        printer,
                        settings,
                        statsRepo,
                        toolheadStatsRepo,
                        jobStatsRepo,
                        featureGate,
                        scopedServices,
                        ct);

                    // Persist only after the complete per-printer flow succeeds. This atomically
                    // commits the external baseline, aggregate totals, and toolhead wear.
                    await statsRepo.SaveChangesAsync(ct);
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
            }

            _logger.LogDebug("Print statistics sync completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during printer statistics sync scan");
        }
    }

    internal async Task SyncPrinterStatisticsAsync(
        Printer printer,
        PrintStatsSyncSettings settings,
        IPrinterStatisticsRepository statsRepo,
        IToolheadStatisticsRepository toolheadStatsRepo,
        IPrintJobStatisticsRepository jobStatsRepo,
        IOperatorFeatureGate featureGate,
        IServiceProvider serviceProvider,
        CancellationToken ct)
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
        bool baselineInitialized = stats?.ExternalBaselineInitializedUtc != null;

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
                    // Established baseline: attribute only the incremental external growth.
                    externalDelta = Math.Max(0, freshExternalHours - previousExternalHours);
                    attributionEligible = true;
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
                stats.ExternalBaselineInitializedUtc ??= DateTime.UtcNow;
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
                stats.ExternalBaselineInitializedUtc ??= DateTime.UtcNow;
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
            await SyncPrintFarmerJobStatisticsAsync(printer, stats, jobStatsRepo, ct);
        }

        // Update statistics in database
        if (outcome == ExternalSyncOutcome.Succeeded || aggregatePrintFarmerJobs)
        {
            stats.LastSyncTime = DateTime.UtcNow;
            await statsRepo.UpsertAsync(stats, ct);

            // Attribute only an established external backend's per-cycle delta. PrintFarmer job
            // aggregation above is mutable and model-wide, so it is not a reliable wear source.
            // The increment remains uncommitted until the outer scoped SaveChangesAsync.
            IReadOnlyList<Guid> credited = await AttributeExternalToolheadHoursAsync(
                printer.Id,
                statsExisted,
                attributionEligible,
                featureGate.IsEnabled(OperatorFeature.MultiSlotFallback),
                printer.SupportsPerToolAttribution,
                externalDelta,
                toolheadStatsRepo,
                ct,
                _logger,
                serviceProvider.GetService<IPrinterStatusCacheReader>());
            if (credited.Count > 0)
            {
                _logger.LogDebug(
                    "Attributed {Delta:F2}h across {ToolheadCount} physical toolheads on printer '{Name}'",
                    externalDelta,
                    credited.Count,
                    printer.Name);
            }
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
        IPrinterStatusCacheReader? statusCache = null)
    {
        if (!statsExisted
            || !externalSyncSuccess
            || !perToolMaintenanceEnabled
            || externalDelta <= 0.0001)
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

        IReadOnlyDictionary<int, Guid> physicalToolheads =
            await toolheadStatsRepo.GetPhysicalToolheadIdsByIndexAsync(printerId, ct);
        if (physicalToolheads.Count == 0)
        {
            return [];
        }

        PrinterStatusCacheSnapshot? snapshot = statusCache?.GetSnapshot(printerId);
        MmuStatusDto? mmuStatus = PrinterStatusFreshness.IsFreshOnline(snapshot, DateTime.UtcNow)
            ? snapshot!.Status.MmuStatus
            : null;
        int? activeToolIndex = mmuStatus is { Enabled: true, ActiveTool: >= 0 }
            ? mmuStatus.ActiveTool
            : mmuStatus is { Enabled: true, ActiveGate: >= 0 }
                ? mmuStatus.ActiveGate
                : null;

        if (activeToolIndex.HasValue
            && physicalToolheads.TryGetValue(activeToolIndex.Value, out Guid activeToolheadId))
        {
            // NOTE (issue #711, round-10 Finding 1): this credits the whole interval delta to the
            // latest-known active tool. It is an approximation — it cannot represent tool switches
            // that happened within the sync interval — and is only reachable for backends that opt
            // in via Printer.SupportsPerToolAttribution. Accumulating per-tool weights over the
            // interval is tracked as future work behind the same capability flag.
            ToolheadHourAttribution attribution = ToolheadHourAttribution.FromWeights(
                new Dictionary<Guid, double> { [activeToolheadId] = 1.0 },
                externalDelta);
            return await toolheadStatsRepo.ApplyToolheadHoursAsync(printerId, attribution, ct);
        }

        // Capable backend but no fresh active-tool telemetry this cycle: leave the delta
        // unattributed for per-toolhead wear rather than fabricate an equal split (Finding 1).
        logger?.LogInformation(
            "Printer {PrinterId} supports per-tool attribution but produced no fresh active-tool " +
            "telemetry this cycle; per-toolhead wear is unattributed for the {Delta:F2}h external " +
            "history delta (issue #711).",
            printerId,
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
        CancellationToken ct)
    {
        try
        {
            // TODO(#711): Replace this model-wide, all-time aggregation with a per-printer
            // completion watermark. Until then it must not feed per-toolhead wear attribution.
            // Get all successful jobs for this printer from PrintFarmer history
            // Note: PrintJobStatistics doesn't have PrinterId directly, so we query by printer model
            List<PrintJobStatistics> printerJobs = await jobStatsRepo.GetByPrinterModelAsync(
                printer.ModelId,
                successfulOnly: true,
                fromDate: null,
                cancellationToken: ct);

            if (printerJobs.Count == 0)
            {
                _logger.LogDebug(
                    "No PrintFarmer job statistics found for printer model of '{Name}'",
                    printer.Name);
                return;
            }

            // Aggregate PrintFarmer job data
            int totalJobs = printerJobs.Count;
            double totalHours = printerJobs
                .Where(j => j.ActualDurationMs.HasValue)
                .Sum(j => j.ActualDurationMs!.Value / 1000.0 / 3600.0); // ms to hours

            // Add to existing statistics (combining external and PrintFarmer data)
            stats.TotalJobsCompleted += totalJobs;
            stats.TotalPrintHours += totalHours;

            _logger.LogDebug(
                "Added PrintFarmer statistics for '{Name}': +{Jobs} jobs, +{Hours}h",
                printer.Name,
                totalJobs,
                totalHours);
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

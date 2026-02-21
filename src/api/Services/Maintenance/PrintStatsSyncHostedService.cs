using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Maintenance;
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

    private async Task SyncPrinterStatisticsAsync(PrintStatsSyncSettings settings, CancellationToken ct)
    {
        try
        {
            // Create a scope to get the scoped repositories
            using IServiceScope scope = _serviceProvider.CreateScope();
            IPrintersRepository printersRepo = scope.ServiceProvider.GetRequiredService<IPrintersRepository>();
            IPrinterStatisticsRepository statsRepo = scope.ServiceProvider.GetRequiredService<IPrinterStatisticsRepository>();
            IPrintJobStatisticsRepository jobStatsRepo = scope.ServiceProvider.GetRequiredService<IPrintJobStatisticsRepository>();

            // Get all printers
            List<Printer> printers = await printersRepo.GetAllAsync(ct);

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
                    await SyncPrinterStatisticsAsync(printer, settings, statsRepo, jobStatsRepo, scope.ServiceProvider, ct);
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

            // Save all changes
            await statsRepo.SaveChangesAsync(ct);

            _logger.LogDebug("Print statistics sync completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during printer statistics sync scan");
        }
    }

    private async Task SyncPrinterStatisticsAsync(
        Printer printer,
        PrintStatsSyncSettings settings,
        IPrinterStatisticsRepository statsRepo,
        IPrintJobStatisticsRepository jobStatsRepo,
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
        if (stats == null)
        {
            stats = new PrinterStatistics
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id
            };
        }

        // Sync from external printer API
        bool externalSyncSuccess = await SyncExternalPrinterStatisticsAsync(
            printer,
            stats,
            settings,
            serviceProvider,
            ct);

        // Sync from PrintFarmer job history
        if (settings.IncludePrintFarmerJobs)
        {
            await SyncPrintFarmerJobStatisticsAsync(printer, stats, jobStatsRepo, ct);
        }

        // Update statistics in database
        if (externalSyncSuccess || settings.IncludePrintFarmerJobs)
        {
            stats.LastSyncTime = DateTime.UtcNow;
            await statsRepo.UpsertAsync(stats, ct);
        }
    }

    private async Task<bool> SyncExternalPrinterStatisticsAsync(
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
                return false;
            }

            return await SyncBackendHistoryStatisticsAsync(printer, stats, serviceProvider, settings, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to sync external statistics for printer '{Name}'",
                printer.Name);
            return false;
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
        }
    }
}

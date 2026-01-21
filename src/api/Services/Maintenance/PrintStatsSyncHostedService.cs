using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
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
    IOptionsMonitor<PrintStatsSyncSettings> settingsMonitor) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<PrintStatsSyncHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptionsMonitor<PrintStatsSyncSettings> _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PrintStatsSyncSettings settings = _settingsMonitor.CurrentValue;

        if (!settings.Enabled)
        {
            _logger.LogInformation("Print statistics sync service is disabled");
            return;
        }

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
                    continue;
                }

                await SyncPrinterStatisticsAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Print statistics sync service stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during print statistics sync");
            }
        }
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

            switch (backend)
            {
                case PrinterBackend.Moonraker:
                    return await SyncMoonrakerStatisticsAsync(printer, stats, serviceProvider, settings, ct);

                case PrinterBackend.PrusaLink:
                    // PrusaLink doesn't have built-in history statistics API
                    // Statistics would come from PrintFarmer job history only
                    _logger.LogDebug("PrusaLink printer '{Name}' - using PrintFarmer job history only", printer.Name);
                    return false;

                case PrinterBackend.OctoPrint:
                    // OctoPrint statistics would come from plugin data or PrintFarmer history
                    _logger.LogDebug("OctoPrint printer '{Name}' - using PrintFarmer job history only", printer.Name);
                    return false;

                case PrinterBackend.SDCP:
                    // SDCP statistics would come from PrintFarmer job history
                    _logger.LogDebug("SDCP printer '{Name}' - using PrintFarmer job history only", printer.Name);
                    return false;

                default:
                    _logger.LogWarning("Unsupported backend type {Backend} for printer '{Name}'", backend, printer.Name);
                    return false;
            }
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

    private async Task<bool> SyncMoonrakerStatisticsAsync(
        Printer printer,
        PrinterStatistics stats,
        IServiceProvider serviceProvider,
        PrintStatsSyncSettings settings,
        CancellationToken ct)
    {
        try
        {
            IMoonrakerClient moonrakerClient = serviceProvider.GetRequiredService<IMoonrakerClient>();

            // Use timeout from settings
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(settings.ApiTimeoutSeconds));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // Get history totals from Moonraker
            HistoryTotals? historyTotals = await moonrakerClient.GetHistoryTotalsAsync(
                printer.ServerUrl,
                linkedCts.Token);

            if (historyTotals == null || historyTotals.JobTotals == null)
            {
                _logger.LogDebug("No history totals available from Moonraker for printer '{Name}'", printer.Name);
                return false;
            }

            // Extract statistics
            JobTotals jobTotals = historyTotals.JobTotals;

            // Update statistics
            // Moonraker TotalTime is in seconds, convert to hours
            stats.TotalPrintHours = jobTotals.TotalPrintTime / 3600.0;
            stats.TotalJobsCompleted = jobTotals.TotalJobs;

            // Moonraker TotalFilamentUsed is in mm, convert to grams (approximate: 1mm³ = 0.00125g for PLA at 1.75mm)
            // More accurate: for 1.75mm filament, 1mm length ≈ 0.00237g for PLA
            double filamentMm = jobTotals.TotalFilamentUsed;
            stats.TotalFilamentUsedMeters = filamentMm / 1000.0; // mm to meters
            stats.TotalFilamentUsedGrams = filamentMm * 0.00237; // Approximate grams for PLA 1.75mm

            _logger.LogDebug(
                "Synced Moonraker statistics for '{Name}': {Hours}h, {Jobs} jobs, {Filament}m",
                printer.Name,
                stats.TotalPrintHours,
                stats.TotalJobsCompleted,
                stats.TotalFilamentUsedMeters);

            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Moonraker statistics sync timed out for printer '{Name}'", printer.Name);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to sync Moonraker statistics for printer '{Name}'",
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

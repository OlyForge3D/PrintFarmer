using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Printers;
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

            // TODO: Phase 2 - Implement actual statistics sync from printer APIs
            // This is a stub that will be implemented in the next iteration
            // For now, we just log that we would sync statistics
            for (int i = 0; i < printersToSync; i++)
            {
                Printer printer = printers[i];
                _logger.LogDebug(
                    "Would sync statistics for printer '{Name}' (ID: {Id}, Backend: {Backend})",
                    printer.Name,
                    printer.Id,
                    (PrinterBackend)printer.Backend);

                // Placeholder: In Phase 2, we'll:
                // 1. Check printer backend type (Moonraker/PrusaLink/OctoPrint/SDCP)
                // 2. Call appropriate backend API to get statistics
                // 3. Parse response and extract cumulative hours, job counts, filament usage
                // 4. Update or create PrinterStatistics record
                // 5. Handle errors gracefully (printer offline, API errors, etc.)
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during printer statistics sync scan");
        }
    }
}

using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Startup;

/// <summary>
/// Background service that syncs orphaned print jobs on API startup.
/// Jobs can get stuck in "Printing" status if the API restarts while a print completes.
/// This service waits for the printer status cache to populate, then syncs any orphaned jobs.
/// </summary>
public class OrphanedJobSyncStartupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OrphanedJobSyncStartupService> _logger;

    public OrphanedJobSyncStartupService(
        IServiceProvider serviceProvider,
        ILogger<OrphanedJobSyncStartupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for printer status cache to be populated by polling services
        // Give the polling services ~15 seconds to do their first poll
        _logger.LogInformation("[OrphanedJobSync] Waiting for printer status cache to populate...");
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var completionService = scope.ServiceProvider.GetRequiredService<IPrintJobCompletionService>();
            var statusCache = scope.ServiceProvider.GetRequiredService<IPrinterStatusCacheReader>();

            _logger.LogInformation("[OrphanedJobSync] Running startup sync of orphaned jobs...");

            string? LookupPrinterState(Guid printerId)
            {
                var status = statusCache.GetStatus(printerId);
                return status?.State;
            }

            int syncedCount = await completionService.SyncOrphanedPrintingJobsAsync(
                LookupPrinterState,
                stoppingToken);

            if (syncedCount > 0)
            {
                _logger.LogInformation($"[OrphanedJobSync] Startup sync completed: {syncedCount} orphaned job(s) synchronized");
            }
            else
            {
                _logger.LogInformation("[OrphanedJobSync] Startup sync completed: no orphaned jobs found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OrphanedJobSync] Error during startup sync of orphaned jobs");
        }
    }
}

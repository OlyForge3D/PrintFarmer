using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Startup;

/// <summary>
/// Background service that periodically syncs orphaned print jobs.
/// Jobs can get stuck in "Printing" status if:
/// - The API restarts while a print completes
/// - A print is cancelled directly on the printer and the WebSocket/polling misses the transition
/// - A transient database error prevents the real-time state handler from persisting the change
///
/// Runs once at startup (after a 15-second warm-up) and then every 60 seconds.
/// </summary>
public class OrphanedJobSyncStartupService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(60);

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
        _logger.LogInformation("[OrphanedJobSync] Waiting for printer status cache to populate...");
        await Task.Delay(InitialDelay, stoppingToken);

        // Run initial startup sync, then loop periodically
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSyncAsync(stoppingToken);

            try
            {
                await Task.Delay(ReconciliationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunSyncAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var completionService = scope.ServiceProvider.GetRequiredService<IPrintJobCompletionService>();
            var statusCache = scope.ServiceProvider.GetRequiredService<IPrinterStatusCacheReader>();

            string? LookupPrinterState(Guid printerId)
            {
                var status = statusCache.GetStatus(printerId);
                return status?.State;
            }

            int syncedCount = await completionService.SyncOrphanedPrintingJobsAsync(
                LookupPrinterState,
                ct);

            if (syncedCount > 0)
            {
                _logger.LogInformation("[OrphanedJobSync] Reconciliation synced {SyncedCount} orphaned job(s)", syncedCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OrphanedJobSync] Error during orphaned job sync");
        }
    }
}

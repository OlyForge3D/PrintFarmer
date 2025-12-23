using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Workers;

/// <summary>
/// Background service for cleaning up stale workers (those without recent heartbeats)
/// </summary>
public class StaleWorkerCleanupHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StaleWorkerCleanupHostedService> _logger;
    private readonly IOptionsMonitor<StaleWorkerCleanupSettings> _settingsMonitor;

    public StaleWorkerCleanupHostedService(
        IServiceProvider serviceProvider,
        ILogger<StaleWorkerCleanupHostedService> logger,
        IOptionsMonitor<StaleWorkerCleanupSettings> settingsMonitor)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _settingsMonitor.CurrentValue;

        if (!settings.Enabled)
        {
            _logger.LogInformation("Stale worker cleanup is disabled");
            return;
        }

        _logger.LogInformation(
            "Stale worker cleanup service started. Interval: {Interval}s, Stale threshold: {Threshold}min, AutoDelete: {AutoDelete}",
            settings.IntervalSeconds,
            settings.StaleAfterMinutes,
            settings.AutoDelete);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;

                settings = _settingsMonitor.CurrentValue; // Reload settings each iteration
                if (!settings.Enabled)
                {
                    _logger.LogInformation("Stale worker cleanup disabled, pausing cleanup service");
                    continue;
                }

                await CleanupStaleWorkersAsync(settings);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Stale worker cleanup service stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stale worker cleanup");
            }
        }
    }

    private async Task CleanupStaleWorkersAsync(StaleWorkerCleanupSettings settings)
    {
        try
        {
            // Create a scope to get the scoped repository
            using var scope = _serviceProvider.CreateScope();
            var workerRepository = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();

            var cutoffTime = DateTime.UtcNow.AddMinutes(-settings.StaleAfterMinutes);

            // Get all workers
            var allWorkers = await workerRepository.GetAllAsync(int.MaxValue, 0);

            var staleWorkers = allWorkers
                .Where(w => IsStale(w, cutoffTime))
                .ToList();

            if (staleWorkers.Count == 0)
            {
                _logger.LogDebug("No stale workers found during cleanup scan");
                return;
            }

            _logger.LogInformation(
                "Found {Count} stale worker(s) without heartbeat since {CutoffTime}",
                staleWorkers.Count,
                cutoffTime);

            if (settings.AutoDelete)
            {
                await DeleteStaleWorkersAsync(workerRepository, staleWorkers);
            }
            else
            {
                await MarkStaleWorkersOfflineAsync(workerRepository, staleWorkers);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during stale worker cleanup scan");
        }
    }

    private bool IsStale(Worker worker, DateTime cutoffTime)
    {
        // Consider a worker stale if:
        // 1. It hasn't sent a heartbeat (LastHeartbeat is null)
        // 2. Last heartbeat was before cutoff time
        // 3. Worker is marked as offline

        if (worker.LastHeartbeat == null)
            return true;

        if (worker.LastHeartbeat < cutoffTime)
            return true;

        return false;
    }

    private async Task MarkStaleWorkersOfflineAsync(IWorkerRepository workerRepository, List<Worker> staleWorkers)
    {
        try
        {
            foreach (var worker in staleWorkers)
            {
                if (worker.Status == WorkerStatus.Offline)
                    continue;

                await workerRepository.UpdateStatusAsync(worker.Id, WorkerStatus.Offline);

                _logger.LogInformation(
                    "Marked worker '{WorkerName}' (ID: {WorkerId}) as offline due to inactivity",
                    worker.Name,
                    worker.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking stale workers offline");
        }
    }

    private async Task DeleteStaleWorkersAsync(IWorkerRepository workerRepository, List<Worker> staleWorkers)
    {
        try
        {
            foreach (var worker in staleWorkers)
            {
                await workerRepository.DeleteAsync(worker.Id);

                _logger.LogInformation(
                    "Deleted stale worker '{WorkerName}' (ID: {WorkerId}) due to inactivity",
                    worker.Name,
                    worker.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting stale workers");
        }
    }
}

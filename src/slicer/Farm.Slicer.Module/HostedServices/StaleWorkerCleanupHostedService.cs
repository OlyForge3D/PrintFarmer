using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.HostedServices;

/// <summary>
/// Background service for cleaning up stale workers (those without recent heartbeats).
/// </summary>
public class StaleWorkerCleanupHostedService : BackgroundService
{
    private const string ServiceId = "StaleWorkerCleanupService";
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StaleWorkerCleanupHostedService> _logger;
    private readonly IOptionsMonitor<StaleWorkerCleanupSettings> _settingsMonitor;
    private readonly IHostedServiceMonitor? _serviceMonitor;

    public StaleWorkerCleanupHostedService(
        IServiceProvider serviceProvider,
        ILogger<StaleWorkerCleanupHostedService> logger,
        IOptionsMonitor<StaleWorkerCleanupSettings> settingsMonitor,
        IHostedServiceMonitor? serviceMonitor = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
        _serviceMonitor = serviceMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StaleWorkerCleanupSettings settings = _settingsMonitor.CurrentValue;

        // Register with the service monitor (if available)
        _serviceMonitor?.Register(
            ServiceId,
            "Stale Worker Cleanup",
            "Removes inactive slicer workers that haven't sent heartbeats",
            "Slicing",
            "pf-icon-worker",
            settings.IntervalSeconds);
        _serviceMonitor?.ReportStarted(ServiceId);

        if (!settings.Enabled)
        {
            _logger.LogInformation("Stale worker cleanup is disabled");
            _serviceMonitor?.ReportEnabled(ServiceId, false);
            return;
        }

        _serviceMonitor?.ReportEnabled(ServiceId, true);
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
                {
                    break;
                }

                settings = _settingsMonitor.CurrentValue; // Reload settings each iteration
                if (!settings.Enabled)
                {
                    _logger.LogInformation("Stale worker cleanup disabled, pausing cleanup service");
                    _serviceMonitor?.ReportEnabled(ServiceId, false);
                    continue;
                }

                _serviceMonitor?.ReportEnabled(ServiceId, true);
                await CleanupStaleWorkersAsync(settings);
                _serviceMonitor?.ReportSuccess(ServiceId, settings.IntervalSeconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Stale worker cleanup service stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stale worker cleanup");
                _serviceMonitor?.ReportError(ServiceId, ex.Message);
            }
        }

        _serviceMonitor?.ReportStopped(ServiceId);
    }

    private async Task CleanupStaleWorkersAsync(StaleWorkerCleanupSettings settings)
    {
        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            IWorkerRepository workerRepository = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();

            DateTime cutoffTime = DateTime.UtcNow.AddMinutes(-settings.StaleAfterMinutes);

            IReadOnlyList<Worker> allWorkers = await workerRepository.GetAllAsync(int.MaxValue, 0);

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

    private static bool IsStale(Worker worker, DateTime cutoffTime)
    {
        if (worker.LastHeartbeat == null)
        {
            return true;
        }

        return worker.LastHeartbeat < cutoffTime;
    }

    private async Task MarkStaleWorkersOfflineAsync(IWorkerRepository workerRepository, List<Worker> staleWorkers)
    {
        try
        {
            foreach (Worker worker in staleWorkers)
            {
                if (worker.Status == WorkerStatus.Offline)
                {
                    continue;
                }

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
            foreach (Worker worker in staleWorkers)
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

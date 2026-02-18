using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.HostedServices;

/// <summary>
/// Background service for monitoring worker health and updating status.
/// </summary>
public class WorkerHealthMonitorService(
    IServiceProvider serviceProvider,
    ILogger<WorkerHealthMonitorService> logger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<WorkerHealthMonitorService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker Health Monitor Service starting");

        // Wait a bit before starting monitoring
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckWorkerHealthAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in worker health monitoring");
            }

            // Wait before next check
            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("Worker Health Monitor Service stopping");
    }

    private async Task CheckWorkerHealthAsync()
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        IWorkerRepository workerRepository = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();

        // Get workers that haven't sent heartbeat within timeout
        IReadOnlyList<Worker> staleWorkers = await workerRepository.GetStaleWorkersAsync(_heartbeatTimeout);

        if (staleWorkers.Count > 0)
        {
            _logger.LogWarning("Found {Count} stale worker(s) with no recent heartbeat", staleWorkers.Count);

            foreach (Worker worker in staleWorkers)
            {
                TimeSpan? timeSinceHeartbeat = worker.LastHeartbeat.HasValue
                    ? DateTime.UtcNow - worker.LastHeartbeat.Value
                    : null;

                _logger.LogWarning(
                    "Marking worker {WorkerId} ({WorkerName}) as Offline - last heartbeat: {TimeSinceHeartbeat}s ago",
                    worker.Id,
                    worker.Name,
                    timeSinceHeartbeat?.TotalSeconds);

                await workerRepository.UpdateStatusAsync(worker.Id, WorkerStatus.Offline);
            }

            await workerRepository.SaveChangesAsync();
        }
    }
}

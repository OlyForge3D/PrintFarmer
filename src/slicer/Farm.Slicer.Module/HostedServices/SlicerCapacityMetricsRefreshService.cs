using System.Linq;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.HostedServices;

/// <summary>
/// Periodically refreshes the worker-capacity snapshot backing
/// <see cref="SlicerServiceMetrics"/>'s observable capacity gauges.
/// </summary>
/// <remarks>
/// <see cref="SlicerServiceMetrics"/> is a singleton, but computing capacity requires
/// querying <see cref="IWorkerRepository"/>, which depends on a scoped
/// <c>SlicerDbContext</c>. Rather than have a scoped service (<c>SlicersService</c>)
/// hand the singleton a <c>this</c>-bound delegate — which would leave the singleton
/// holding a reference to a disposed scope once the request ends — this hosted service
/// creates its own short-lived scope on every refresh, computes the snapshot, and hands
/// it to the metrics singleton via <see cref="SlicerServiceMetrics.UpdateCapacitySnapshot"/>.
/// The gauge callbacks then read a volatile field synchronously, with no I/O and no
/// dependency on a scope that may have already been disposed (see #1676).
/// </remarks>
public sealed class SlicerCapacityMetricsRefreshService(
    IServiceProvider serviceProvider,
    SlicerServiceMetrics metrics,
    ILogger<SlicerCapacityMetricsRefreshService> logger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
#pragma warning disable CA2213 // SlicerServiceMetrics is a singleton owned by the DI container; this service must not dispose it
    private readonly SlicerServiceMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
#pragma warning restore CA2213
    private readonly ILogger<SlicerCapacityMetricsRefreshService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Slicer Capacity Metrics Refresh Service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                // A failed refresh must not crash the service or the singleton keeps
                // exporting its last-known-good snapshot instead of a fabricated zero.
                _logger.LogWarning(ex, "Failed to refresh slicer capacity metrics snapshot");
            }

            try
            {
                await Task.Delay(_refreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _logger.LogInformation("Slicer Capacity Metrics Refresh Service stopping");
    }

    /// <summary>
    /// Executes a single refresh cycle: opens a scope, queries live worker capacity,
    /// and publishes the result to <see cref="SlicerServiceMetrics"/>. Exposed as
    /// <c>internal</c> (rather than folded into the private timer loop) so tests can
    /// drive one deterministic refresh without waiting on <see cref="_refreshInterval"/>.
    /// </summary>
    /// <param name="ct">Cancellation token observed while querying workers.</param>
    internal async Task RefreshOnceAsync(CancellationToken ct = default)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        IWorkerRepository workerRepo = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();

        IReadOnlyList<Worker> workers = await workerRepo.GetAllAsync(limit: 1000);
        ct.ThrowIfCancellationRequested();

        int totalCapacity = workers.Where(IsLiveWorker).Sum(w => w.TotalSlots);
        int availableCapacity = workers
            .Where(w => IsLiveWorker(w) && w.Status != WorkerStatus.Draining)
            .Sum(w => w.FreeSlots);
        int activeJobs = workers.Where(IsLiveWorker).Sum(w => w.ActiveJobs);

        _metrics.UpdateCapacitySnapshot(totalCapacity, availableCapacity, activeJobs);
    }

    private static bool IsLiveWorker(Worker worker)
    {
        return !worker.IsDisabled &&
               worker.Status != WorkerStatus.Offline &&
               worker.Status != WorkerStatus.Error;
    }
}

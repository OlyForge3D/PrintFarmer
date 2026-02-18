using Farm.Slicer.Module.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.HostedServices;

/// <summary>
/// Background service for continuously dispatching queued jobs to available workers.
/// </summary>
public class JobDispatchingService(
    IServiceProvider serviceProvider,
    ILogger<JobDispatchingService> logger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<JobDispatchingService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Job Dispatching Service starting");

        // Wait a bit before starting dispatching
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchJobsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in job dispatching");
            }

            // Wait before next poll
            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("Job Dispatching Service stopping");
    }

    private async Task DispatchJobsAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        ISlicerJobDispatcherService dispatcherService = scope.ServiceProvider.GetRequiredService<ISlicerJobDispatcherService>();

        // Try to dispatch jobs in a loop until no more can be dispatched
        int dispatchedCount = 0;
        int maxDispatchPerCycle = 10; // Limit to prevent long-running cycles

        while (dispatchedCount < maxDispatchPerCycle && !cancellationToken.IsCancellationRequested)
        {
            bool dispatched = await dispatcherService.DispatchNextJobAsync(cancellationToken);
            if (!dispatched)
            {
                // No job was dispatched (either no jobs in queue or no available workers)
                break;
            }

            dispatchedCount++;
        }

        if (dispatchedCount > 0)
        {
            _logger.LogDebug("Dispatched {DispatchedCount} job(s) in this cycle", dispatchedCount);
        }
    }
}

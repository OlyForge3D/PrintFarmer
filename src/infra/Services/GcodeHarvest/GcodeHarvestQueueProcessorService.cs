using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.GcodeHarvest;

/// <summary>
/// Background service that processes queued gcode harvest operations.
/// Runs continuously and dequeues items for processing.
/// </summary>
public class GcodeHarvestQueueProcessorService(
    IServiceProvider serviceProvider,
    ILogger<GcodeHarvestQueueProcessorService> logger)
    : BackgroundService
{
    /// <summary>
    /// Interval to check the queue (milliseconds). Default 5 seconds.
    /// </summary>
    private const int QueueCheckIntervalMs = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Gcode harvest queue processor service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IGcodeHarvestQueue>();

                // Get the next item to process
                var queueItem = await queue.DequeueAsync();
                if (queueItem == null)
                {
                    // No items to process, wait before checking again
                    await Task.Delay(QueueCheckIntervalMs, stoppingToken);
                    continue;
                }

                logger.LogInformation(
                    "Processing queue item {QueueItemId} for printer {PrinterId}",
                    queueItem.Id,
                    queueItem.PrinterId);

                try
                {
                    // Mark as processing
                    await queue.MarkProcessingAsync(queueItem.Id);

                    // Deserialize parameters
                    var parameters = JsonSerializer.Deserialize<StartGcodeHarvestDto>(queueItem.Parameters)
                        ?? throw new InvalidOperationException("Failed to deserialize harvest parameters");

                    // Get the harvest service from DI using GetType lookup
                    // We use reflection to avoid circular dependency between infra and API layers
                    var harvestServiceType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => {
                            try { return a.GetTypes(); }
                            catch { return Type.EmptyTypes; }
                        })
                        .FirstOrDefault(t => t.Name == "IGcodeHarvestService" && t.IsInterface);

                    if (harvestServiceType == null)
                    {
                        logger.LogWarning("IGcodeHarvestService not found in loaded assemblies");
                        await queue.MarkFailedAsync(queueItem.Id, "IGcodeHarvestService not found");
                        continue;
                    }

                    var harvestService = scope.ServiceProvider.GetService(harvestServiceType);
                    if (harvestService == null)
                    {
                        logger.LogWarning("IGcodeHarvestService is not registered in DI");
                        await queue.MarkFailedAsync(queueItem.Id, "IGcodeHarvestService not registered");
                        continue;
                    }

                    // Call StartHarvestAsync via reflection
                    var startMethod = harvestServiceType.GetMethod("StartHarvestAsync");
                    if (startMethod == null)
                    {
                        logger.LogWarning("StartHarvestAsync method not found");
                        await queue.MarkFailedAsync(queueItem.Id, "StartHarvestAsync method not found");
                        continue;
                    }

                    var result = await (dynamic)startMethod.Invoke(harvestService, new object[] { parameters, stoppingToken })!;

                    // Mark as completed
                    await queue.MarkCompletedAsync(queueItem.Id, 0, 0, 0, 0);

                    logger.LogInformation(
                        "Completed queue item {QueueItemId}",
                        queueItem.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Error processing queue item {QueueItemId}",
                        queueItem.Id);

                    await queue.MarkFailedAsync(
                        queueItem.Id,
                        ex.Message,
                        ex.StackTrace);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in harvest queue processor");
                // Wait before retrying to avoid tight loop on persistent errors
                await Task.Delay(QueueCheckIntervalMs, stoppingToken);
            }
        }

        logger.LogInformation("Gcode harvest queue processor service stopping");
    }
}

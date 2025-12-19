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

                    Console.Error.WriteLine($"[HARVEST_PROCESSOR] CHECKPOINT_1 for queue item {queueItem.Id}");
                    
                    logger.LogInformation("xxx_CHECKPOINT_1: About to get harvest service type");

                    // Get the harvest service from DI using GetType lookup
                    // We use reflection to avoid circular dependency between infra and API layers
                    var harvestServiceType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => {
                            try { return a.GetTypes(); }
                            catch { return Type.EmptyTypes; }
                        })
                        .FirstOrDefault(t => t.Name == "IGcodeHarvestService" && t.IsInterface);

                    logger.LogInformation("xxx_CHECKPOINT_2: Got harvest service type: {ServiceType}", harvestServiceType?.FullName ?? "NULL");

                    if (harvestServiceType == null)
                    {
                        logger.LogWarning("IGcodeHarvestService not found in loaded assemblies");
                        await queue.MarkFailedAsync(queueItem.Id, "IGcodeHarvestService not found");
                        continue;
                    }

                    var harvestService = scope.ServiceProvider.GetService(harvestServiceType);
                    logger.LogInformation("xxx_CHECKPOINT_3: Got harvest service instance: {IsNull}", harvestService == null);
                    if (harvestService == null)
                    {
                        logger.LogWarning("IGcodeHarvestService is not registered in DI");
                        await queue.MarkFailedAsync(queueItem.Id, "IGcodeHarvestService not registered");
                        continue;
                    }

                    // Call StartHarvestAsync via reflection
                    var startMethod = harvestServiceType.GetMethod("StartHarvestAsync");
                    logger.LogInformation("xxx_CHECKPOINT_4: Got start method: {IsNull}", startMethod == null);
                    if (startMethod == null)
                    {
                        logger.LogWarning("StartHarvestAsync method not found");
                        await queue.MarkFailedAsync(queueItem.Id, "StartHarvestAsync method not found");
                        continue;
                    }

                    logger.LogInformation("xxx_CHECKPOINT_5: About to invoke StartHarvestAsync");
                    logger.LogError($"[DIAGNOSTIC] About to call StartHarvestAsync for queue item {queueItem.Id}");
                    logger.LogInformation("xxx_CHECKPOINT_5b: After error log");
                    var result = (object?)await (dynamic)startMethod.Invoke(harvestService, new object[] { parameters, stoppingToken })!;
                    logger.LogError($"[DIAGNOSTIC] StartHarvestAsync returned result = {(result == null ? "NULL" : result.GetType().FullName)}");

                    // Extract result properties if available
                    string? operationId = null;
                    bool? success = null;
                    string? message = null;
                    if (result != null)
                    {
                        try
                        {
                            dynamic dynResult = result;
                            operationId = dynResult.OperationId?.ToString();
                            success = dynResult.Success;
                            message = dynResult.Message;
                        }
                        catch { }
                    }

                    logger.LogInformation(
                        "StartHarvestAsync returned for queue item {QueueItemId}: operationId={OperationId}, success={Success}, message={Message}",
                        queueItem.Id,
                        operationId ?? "unknown",
                        success ?? false,
                        message ?? "no message");

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

using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.GcodeHarvest;

/// <summary>
/// Abstraction for the gcode harvest operation queue.
/// Decouples API request handling from background processing.
/// </summary>
public interface IGcodeHarvestQueue
{
    /// <summary>
    /// Add a harvest request to the queue.
    /// </summary>
    Task<GcodeHarvestQueueItem> EnqueueAsync(Guid printerId, StartGcodeHarvestDto parameters, int priority = 0);

    /// <summary>
    /// Get the next item to process (highest priority, oldest first).
    /// </summary>
    Task<GcodeHarvestQueueItem?> DequeueAsync();

    /// <summary>
    /// Get all pending items for a printer.
    /// </summary>
    Task<IReadOnlyList<GcodeHarvestQueueItem>> GetPendingForPrinterAsync(Guid printerId);

    /// <summary>
    /// Get all queued items with optional filtering.
    /// </summary>
    Task<IReadOnlyList<GcodeHarvestQueueItem>> GetQueuedItemsAsync(GcodeHarvestQueueItemStatus? status = null);

    /// <summary>
    /// Cancel a queued item if it hasn't started processing.
    /// </summary>
    Task<bool> CancelAsync(Guid queueItemId);

    /// <summary>
    /// Mark a queue item as processing.
    /// </summary>
    Task MarkProcessingAsync(Guid queueItemId);

    /// <summary>
    /// Mark a queue item as completed with results.
    /// </summary>
    Task MarkCompletedAsync(
        Guid queueItemId,
        int filesFound,
        int filesAdded,
        int filesSkipped,
        int filesErrored);

    /// <summary>
    /// Mark a queue item as failed with error details.
    /// </summary>
    Task MarkFailedAsync(Guid queueItemId, string errorMessage, string? errorDetails = null);
}

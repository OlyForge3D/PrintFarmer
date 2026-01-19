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
    /// <param name="printerId">The printer ID to harvest gcode from</param>
    /// <param name="parameters">The harvest parameters</param>
    /// <param name="priority">The priority level (higher values processed first)</param>
    Task<GcodeHarvestQueueItem> EnqueueAsync(Guid printerId, StartGcodeHarvestDto parameters, int priority = 0);

    /// <summary>
    /// Get the next item to process (highest priority, oldest first).
    /// </summary>
    Task<GcodeHarvestQueueItem?> DequeueAsync();

    /// <summary>
    /// Get all pending items for a printer.
    /// </summary>
    /// <param name="printerId">The printer ID to get pending items for</param>
    Task<IReadOnlyList<GcodeHarvestQueueItem>> GetPendingForPrinterAsync(Guid printerId);

    /// <summary>
    /// Get all queued items with optional filtering.
    /// </summary>
    /// <param name="status">Optional status filter</param>
    Task<IReadOnlyList<GcodeHarvestQueueItem>> GetQueuedItemsAsync(GcodeHarvestQueueItemStatus? status = null);

    /// <summary>
    /// Cancel a queued item if it hasn't started processing.
    /// </summary>
    /// <param name="queueItemId">The queue item ID to cancel</param>
    Task<bool> CancelAsync(Guid queueItemId);

    /// <summary>
    /// Mark a queue item as processing.
    /// </summary>
    /// <param name="queueItemId">The queue item ID to mark as processing</param>
    Task MarkProcessingAsync(Guid queueItemId);

    /// <summary>
    /// Mark a queue item as completed with results.
    /// </summary>
    /// <param name="queueItemId">The queue item ID to mark as completed</param>
    /// <param name="filesFound">The number of files found</param>
    /// <param name="filesAdded">The number of files added</param>
    /// <param name="filesSkipped">The number of files skipped</param>
    /// <param name="filesErrored">The number of files with errors</param>
    Task MarkCompletedAsync(
        Guid queueItemId,
        int filesFound,
        int filesAdded,
        int filesSkipped,
        int filesErrored);

    /// <summary>
    /// Mark a queue item as failed with error details.
    /// </summary>
    /// <param name="queueItemId">The queue item ID to mark as failed</param>
    /// <param name="errorMessage">The error message</param>
    /// <param name="errorDetails">Optional detailed error information</param>
    Task MarkFailedAsync(Guid queueItemId, string errorMessage, string? errorDetails = null);
}

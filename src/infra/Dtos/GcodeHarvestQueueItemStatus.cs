namespace Farm.Infrastructure;

/// <summary>
/// Status of a harvest operation in the queue.
/// </summary>
public enum GcodeHarvestQueueItemStatus
{
    Pending = 0,      // Waiting to be processed
    Processing = 1,   // Currently being processed
    Completed = 2,    // Successfully completed
    Failed = 3,       // Failed during processing
    Cancelled = 4     // Cancelled by user
}

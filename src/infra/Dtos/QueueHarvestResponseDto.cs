namespace Farm.Infrastructure;

/// <summary>
/// Response when a harvest operation is queued.
/// </summary>
public record QueueHarvestResponseDto(
    Guid QueueItemId,
    string Message,
    GcodeHarvestQueueItemStatus Status);

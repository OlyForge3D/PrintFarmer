namespace Farm.Infrastructure;

/// <summary>
/// DTO representing a queued harvest operation (for API responses).
/// </summary>
public record GcodeHarvestQueueItemDto(
    Guid Id,
    Guid PrinterId,
    string PrinterName,
    DateTime QueuedAt,
    DateTime? ProcessingStartedAt = null,
    DateTime? CompletedAt = null,
    GcodeHarvestQueueItemStatus Status = GcodeHarvestQueueItemStatus.Pending,
    int Priority = 0,
    string? ErrorMessage = null,
    int? FilesFound = null,
    int? FilesAdded = null);

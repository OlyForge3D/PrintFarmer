using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Represents a G-code harvesting operation and aggregate progress / results.
/// </summary>
public record GcodeHarvestOperationDto(
    Guid Id,
    Guid PrinterId,
    string PrinterName,
    DateTime StartedAt,
    DateTime? CompletedAt = null,
    GcodeHarvestStatusDto Status = GcodeHarvestStatusDto.Running,
    string? ErrorMessage = null,
    string? ErrorType = null,
    string? ErrorPhase = null,
    string? ErrorDetails = null,
    string? FailedResource = null,
    bool IsRetryable = false,
    DateTime? ErrorOccurredAt = null,
    int FilesFound = 0,
    int FilesProcessed = 0, // Calculated as FilesAdded + FilesSkipped + FilesErrored
    int FilesAdded = 0,
    int FilesSkipped = 0,
    int FilesErrored = 0,
    long TotalBytesProcessed = 0,
    bool IncludeSubdirectories = true,
    long? MaxFileSizeBytes = null,
    DateTime? ModifiedAfter = null);

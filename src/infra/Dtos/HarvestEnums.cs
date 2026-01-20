namespace Farm.Infrastructure;

// ===========================================================================
// G-code Harvest Operation Enumerations
// ===========================================================================
// Enums related to the G-code harvesting workflow from connected printers.
// ===========================================================================

/// <summary>
/// Overall status of a G-code harvest operation.
/// </summary>
public enum GcodeHarvestStatusDto
{
    /// <summary>Harvest operation is currently in progress.</summary>
    Running = 0,

    /// <summary>Harvest operation completed successfully.</summary>
    Completed = 1,

    /// <summary>Harvest operation failed due to an error.</summary>
    Failed = 2,

    /// <summary>Harvest operation was cancelled by the user.</summary>
    Cancelled = 3
}

/// <summary>
/// Status of a harvest operation in the processing queue.
/// </summary>
public enum GcodeHarvestQueueItemStatus
{
    /// <summary>Waiting in queue to be processed.</summary>
    Pending = 0,

    /// <summary>Currently being processed by a worker.</summary>
    Processing = 1,

    /// <summary>Successfully completed all processing steps.</summary>
    Completed = 2,

    /// <summary>Failed during processing with an error.</summary>
    Failed = 3,

    /// <summary>Cancelled by user before completion.</summary>
    Cancelled = 4
}

/// <summary>
/// Phase of the harvest operation where an error occurred.
/// </summary>
public enum HarvestErrorPhaseDto
{
    /// <summary>Error during printer/file discovery phase.</summary>
    Discovery = 0,

    /// <summary>Error during file download from printer.</summary>
    Download = 1,

    /// <summary>Error during file processing (parsing, validation).</summary>
    Processing = 2,

    /// <summary>Error during completion/finalization phase.</summary>
    Completion = 3
}

/// <summary>
/// Type of error encountered during harvest operation.
/// </summary>
public enum HarvestErrorTypeDto
{
    /// <summary>Network connection error (timeout, unreachable).</summary>
    ConnectionError = 0,

    /// <summary>Authentication failed (invalid API key, credentials).</summary>
    AuthenticationError = 1,

    /// <summary>File system error (disk full, permission denied).</summary>
    FileSystemError = 2,

    /// <summary>Validation error (invalid file format, corrupted data).</summary>
    ValidationError = 3,

    /// <summary>Unknown or unclassified error.</summary>
    UnknownError = 4
}

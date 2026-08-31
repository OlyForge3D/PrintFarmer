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

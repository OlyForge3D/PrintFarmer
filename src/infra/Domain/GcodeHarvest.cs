using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

// ============================================================================
// G-CODE HARVESTING SYSTEM
// Domain entities and enums for harvesting G-code files from printers.
// Supports queued operations, file discovery, and library import.
// ============================================================================
#region Enumerations

/// <summary>
/// Overall status of a G-code harvest operation.
/// Tracks the lifecycle from running through completion or failure.
/// </summary>
public enum GcodeHarvestStatus
{
    /// <summary>Harvest operation is currently in progress.</summary>
    Running = 0,

    /// <summary>Harvest operation completed successfully.</summary>
    Completed = 1,

    /// <summary>Harvest operation failed due to an error.</summary>
    Failed = 2,

    /// <summary>Harvest operation was cancelled by user request.</summary>
    Cancelled = 3
}

/// <summary>
/// Status of a harvest queue item for background processing.
/// Tracks items from initial queue through processing completion.
/// </summary>
public enum GcodeHarvestQueueItemStatus
{
    /// <summary>Item is waiting in queue to be processed.</summary>
    Pending = 0,

    /// <summary>Item is currently being processed by the background service.</summary>
    Processing = 1,

    /// <summary>Item processing completed successfully.</summary>
    Completed = 2,

    /// <summary>Item processing failed with an error.</summary>
    Failed = 3,

    /// <summary>Item was cancelled by user before or during processing.</summary>
    Cancelled = 4
}

/// <summary>
/// Status of an individual file discovered during harvest.
/// Tracks each file from discovery through import or skip.
/// </summary>
public enum HarvestFileStatus
{
    /// <summary>File has been discovered but not yet processed.</summary>
    Pending = 0,

    /// <summary>File is currently being downloaded or processed.</summary>
    InProgress = 1,

    /// <summary>File was successfully imported into the library.</summary>
    Complete = 2,

    /// <summary>File processing failed with an error.</summary>
    Failed = 3,

    /// <summary>File processing was cancelled.</summary>
    Cancelled = 4,

    /// <summary>File was skipped (e.g., already exists in library, filtered out).</summary>
    Skipped = 5
}

/// <summary>
/// Phase of harvest operation where an error occurred.
/// Helps identify which stage of the pipeline failed for troubleshooting.
/// </summary>
public enum HarvestErrorPhase
{
    /// <summary>Error occurred during file listing/discovery on the printer.</summary>
    Discovery = 0,

    /// <summary>Error occurred while downloading the file from the printer.</summary>
    Download = 1,

    /// <summary>Error occurred during file processing, parsing, or import.</summary>
    Processing = 2,

    /// <summary>Error occurred during finalization or cleanup.</summary>
    Completion = 3
}

/// <summary>
/// Category of error that occurred during harvest.
/// Provides classification for error handling and retry logic.
/// </summary>
public enum HarvestErrorType
{
    /// <summary>Network connectivity or timeout issues.</summary>
    ConnectionError = 0,

    /// <summary>API key invalid, expired, or insufficient permissions.</summary>
    AuthenticationError = 1,

    /// <summary>Cannot access files, directories, or storage.</summary>
    FileSystemError = 2,

    /// <summary>File content validation or format issues.</summary>
    ValidationError = 3,

    /// <summary>Unexpected or unclassified error.</summary>
    UnknownError = 4
}

#endregion

#region Entities

/// <summary>
/// Represents a G-code harvest operation that scans a printer for files
/// and imports them into the local library. Tracks progress, errors, and statistics.
/// </summary>
public class GcodeHarvestOperation
{
    /// <summary>Unique identifier for this harvest operation.</summary>
    public Guid Id { get; set; }

    /// <summary>ID of the printer being harvested from.</summary>
    public Guid PrinterId { get; set; }

    /// <summary>Navigation property to the source printer.</summary>
    public Printer Printer { get; set; } = null!;

    /// <summary>When the harvest operation started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When the harvest operation completed (null if still running).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Current status of the harvest operation.</summary>
    public GcodeHarvestStatus Status { get; set; }

    /// <summary>User-friendly error message if the operation failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Category of error (ConnectionError, AuthenticationError, etc.).</summary>
    public string? ErrorType { get; set; }

    /// <summary>Phase where error occurred (Discovery, Download, Processing, Completion).</summary>
    public string? ErrorPhase { get; set; }

    /// <summary>JSON-serialized error details including exception type, stack trace, and context.</summary>
    public string? ErrorDetails { get; set; }

    /// <summary>File path or URL that caused the failure.</summary>
    public string? FailedResource { get; set; }

    /// <summary>Whether this error condition can potentially be retried.</summary>
    public bool IsRetryable { get; set; } = false;

    /// <summary>Exact timestamp when the error occurred.</summary>
    public DateTime? ErrorOccurredAt { get; set; }

    /// <summary>Total number of G-code files found on the printer.</summary>
    public int FilesFound { get; set; }

    /// <summary>Number of files successfully added to the library.</summary>
    public int FilesAdded { get; set; }

    /// <summary>Number of files skipped (already in library or filtered).</summary>
    public int FilesSkipped { get; set; }

    /// <summary>Number of files that failed to process.</summary>
    public int FilesErrored { get; set; }

    /// <summary>Total bytes downloaded and processed.</summary>
    public long TotalBytesProcessed { get; set; }

    /// <summary>Whether to scan subdirectories on the printer.</summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>Maximum file size to harvest in bytes (default 100MB).</summary>
    public long? MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>Only harvest files modified after this date.</summary>
    public DateTime? ModifiedAfter { get; set; }

    /// <summary>Allowed file extensions (without dot), stored as JSON array.</summary>
    public string[]? FileExtensions { get; set; }

    /// <summary>Minimum file size to harvest in bytes.</summary>
    public long? MinFileSizeBytes { get; set; }

    /// <summary>How to handle duplicate files (skip, replace, rename).</summary>
    public string? DuplicateHandling { get; set; }

    /// <summary>
    /// Collection of files discovered during this harvest operation.
    /// Cascade delete: deleting operation removes discovered files (but preserves imported GcodeFiles).
    /// </summary>
    public ICollection<HarvestDiscoveredFile> DiscoveredFiles { get; set; } = new List<HarvestDiscoveredFile>();
}

/// <summary>
/// Queue item for G-code harvest operations. Decouples API requests from background processing,
/// allowing multiple harvest requests to be queued and processed sequentially with priority.
/// </summary>
public class GcodeHarvestQueueItem
{
    /// <summary>Unique identifier for this queue item.</summary>
    public Guid Id { get; set; }

    /// <summary>ID of the printer to harvest from.</summary>
    public Guid PrinterId { get; set; }

    /// <summary>When this item was added to the queue.</summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>When background processing started (null if still pending).</summary>
    public DateTime? ProcessingStartedAt { get; set; }

    /// <summary>When processing completed (null if not finished).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Processing priority (higher values processed first).</summary>
    public int Priority { get; set; } = 0;

    /// <summary>Current status of this queue item.</summary>
    public GcodeHarvestQueueItemStatus Status { get; set; } = GcodeHarvestQueueItemStatus.Pending;

    /// <summary>JSON-serialized StartGcodeHarvestDto parameters for deferred processing.</summary>
    public string Parameters { get; set; } = string.Empty;

    /// <summary>Error message if processing failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Detailed error information for debugging.</summary>
    public string? ErrorDetails { get; set; }

    /// <summary>Number of files found (cached after completion).</summary>
    public int FilesFound { get; set; }

    /// <summary>Number of files added (cached after completion).</summary>
    public int FilesAdded { get; set; }

    /// <summary>Number of files skipped (cached after completion).</summary>
    public int FilesSkipped { get; set; }

    /// <summary>Number of files with errors (cached after completion).</summary>
    public int FilesErrored { get; set; }

    /// <summary>Navigation property to the target printer.</summary>
    public Printer? Printer { get; set; }
}

/// <summary>
/// Represents a file discovered on a printer during harvest.
/// Tracks the file through download, processing, and import into the library.
/// </summary>
public class HarvestDiscoveredFile
{
    /// <summary>Unique identifier for this discovered file record.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>ID of the parent harvest operation.</summary>
    public Guid HarvestOperationId { get; set; }

    /// <summary>Navigation property to the parent harvest operation.</summary>
    public GcodeHarvestOperation? HarvestOperation { get; set; }

    /// <summary>Full path of the file on the printer.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>File name without path.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>URL to the file's thumbnail image on the printer.</summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>Current processing status of this file.</summary>
    public HarvestFileStatus Status { get; set; } = HarvestFileStatus.Pending;

    /// <summary>Error message if processing failed.</summary>
    public string? Error { get; set; }

    /// <summary>When this file was discovered.</summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>When download/processing started.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When processing completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Whether this file already exists in the local library.</summary>
    public bool AlreadyInLibrary { get; set; } = false;

    /// <summary>Hash of file content for duplicate detection.</summary>
    public string? FileHash { get; set; }

    /// <summary>Nozzle diameter extracted from G-code metadata.</summary>
    public double? ExtractedNozzleDiameter { get; set; }

    /// <summary>Material/filament type extracted from G-code metadata.</summary>
    public string? ExtractedMaterial { get; set; }

    /// <summary>Estimated print time in seconds extracted from G-code.</summary>
    public double? ExtractedPrintTime { get; set; }

    /// <summary>Filament length in mm extracted from G-code.</summary>
    public double? ExtractedFilamentLength { get; set; }

    /// <summary>Slicer name extracted from G-code comments.</summary>
    public string? ExtractedSlicerName { get; set; }

    /// <summary>Slicer version extracted from G-code comments.</summary>
    public string? ExtractedSlicerVersion { get; set; }

    /// <summary>File modification timestamp from the printer.</summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// Mappings to GcodeFile entities created from this discovered file.
    /// Protected by Restrict delete behavior to preserve library files when cleaning harvest data.
    /// </summary>
    public ICollection<HarvestFileGcodeFileMapping> GcodeFileMappings { get; set; } = new List<HarvestFileGcodeFileMapping>();
}

/// <summary>
/// Mapping table linking discovered harvest files to imported GcodeFile entities.
/// Preserves the relationship between harvest metadata and library files.
/// </summary>
public class HarvestFileGcodeFileMapping
{
    /// <summary>Unique identifier for this mapping.</summary>
    public Guid Id { get; set; }

    /// <summary>ID of the discovered file from harvest.</summary>
    public Guid HarvestDiscoveredFileId { get; set; }

    /// <summary>Navigation property to the source discovered file.</summary>
    public HarvestDiscoveredFile HarvestDiscoveredFile { get; set; } = null!;

    /// <summary>ID of the GcodeFile created in the library.</summary>
    public Guid GcodeFileId { get; set; }

    /// <summary>Navigation property to the library GcodeFile.</summary>
    public GcodeFile GcodeFile { get; set; } = null!;

    /// <summary>When this mapping was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

#endregion

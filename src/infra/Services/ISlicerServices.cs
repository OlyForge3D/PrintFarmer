using System.Diagnostics.CodeAnalysis;
using DistributedSlicingJob = Farm.Slicer.Module.Models.DistributedSlicingJob;
using SlicerEngineType = Farm.Slicer.Module.Models.SlicerEngineType;
using SlicerQueueStats = Farm.Slicer.Module.Services.SlicerQueueStats;
using SlicingResult = Farm.Slicer.Module.Models.SlicingResult;

namespace Farm.Infrastructure;

/// <summary>
/// Interface for job queue operations in distributed slicing system
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "ISlicerJobQueue models a worker job queue abstraction; renaming would be a breaking API change and 'Queue' accurately describes the semantics.")]
public interface ISlicerJobQueue
{
    /// <summary>
    /// Enqueue a new slicing job
    /// </summary>
    /// <param name="job">The slice job to enqueue.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task EnqueueAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dequeue the next available job for a specific worker
    /// </summary>
    /// <param name="workerId">The unique identifier of the worker.</param>
    /// <param name="preferredEngine">The preferred slicer engine type, or null for any.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<DistributedSlicingJob?> DequeueAsync(string workerId, SlicerEngineType? preferredEngine = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a job as completed with results
    /// </summary>
    /// <param name="job">The slice job to mark as completed.</param>
    /// <param name="result">The slicing result containing output data.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task CompleteJobAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a job as failed
    /// </summary>
    /// <param name="jobId">The unique identifier of the slice job.</param>
    /// <param name="errorMessage">The error message describing the failure.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task FailJobAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update job progress
    /// </summary>
    /// <param name="jobId">The unique identifier of the slice job.</param>
    /// <param name="progress">The progress percentage (0-100).</param>
    /// <param name="currentStep">The current step description, or null.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task UpdateProgressAsync(Guid jobId, int progress, string? currentStep = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get job status
    /// </summary>
    /// <param name="jobId">The unique identifier of the slice job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<DistributedSlicingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a job
    /// </summary>
    /// <param name="jobId">The unique identifier of the slice job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get queue statistics
    /// </summary>
    /// <param name="engine">The slicer engine type to filter by, or null for all.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<SlicerQueueStats> GetQueueStatsAsync(SlicerEngineType? engine = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get jobs by user
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="limit">The maximum number of jobs to return, or null for all.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<List<DistributedSlicingJob>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup completed and failed jobs older than specified age
    /// </summary>
    /// <param name="maxAge">The maximum age of jobs to retain.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task CleanupOldJobsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeue failed jobs for retry
    /// </summary>
    /// <param name="maxRetryCount">The maximum number of retry attempts.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task RequeueFailedJobsAsync(int maxRetryCount = 3, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeue a specific job for retry, optionally with a delay/backoff and optional jitter percent.
    /// </summary>
    /// <param name="job">The slice job to requeue.</param>
    /// <param name="delay">The delay before the job becomes available, or null for immediate.</param>
    /// <param name="jitterPercent">The percentage of jitter to add to the delay (0.0-1.0).</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task RequeueJobAsync(DistributedSlicingJob job, TimeSpan? delay = null, double jitterPercent = 0.0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find existing job by correlation ID and checksum for idempotency
    /// </summary>
    /// <param name="correlationId">The correlation identifier for the job.</param>
    /// <param name="checksum">The checksum of the job input for deduplication.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<DistributedSlicingJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a job with the given correlation ID and checksum already exists
    /// </summary>
    /// <param name="correlationId">The correlation identifier for the job.</param>
    /// <param name="checksum">The checksum of the job input for deduplication.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default);
}

// In-process ISlicerEngine concept removed (migrated to external worker services). Placeholder deleted.

/// <summary>
/// Interface for file storage operations
/// </summary>
public interface ISlicerFileStorage
{
    /// <summary>
    /// Upload a file and return its URL
    /// </summary>
    /// <param name="key">The storage key for the file.</param>
    /// <param name="fileStream">The stream containing the file data.</param>
    /// <param name="contentType">The MIME content type of the file.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<string> UploadFileAsync(string key, Stream fileStream, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload a file and return its URL
    /// </summary>
    /// <param name="key">The storage key for the file.</param>
    /// <param name="fileData">The byte array containing the file data.</param>
    /// <param name="contentType">The MIME content type of the file.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<string> UploadFileAsync(string key, byte[] fileData, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a file by its URL or key
    /// </summary>
    /// <param name="keyOrUrl">The storage key or URL of the file.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<Stream> DownloadFileAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a file as byte array
    /// </summary>
    /// <param name="keyOrUrl">The storage key or URL of the file.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<byte[]> DownloadFileBytesAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file exists
    /// </summary>
    /// <param name="keyOrUrl">The storage key or URL of the file.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<bool> FileExistsAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a file
    /// </summary>
    /// <param name="keyOrUrl">The storage key or URL of the file.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task DeleteFileAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get file metadata
    /// </summary>
    /// <param name="keyOrUrl">The storage key or URL of the file.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<SlicerFileMetadata?> GetFileMetadataAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate a signed URL for temporary access
    /// </summary>
    /// <param name="keyOrUrl">The storage key or URL of the file.</param>
    /// <param name="expiration">The duration until the signed URL expires.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<string> GenerateSignedUrlAsync(string keyOrUrl, TimeSpan expiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup temporary files older than specified age
    /// </summary>
    /// <param name="maxAge">The maximum age of temporary files to retain.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    void CleanupTempFiles(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

// ISlicerOrchestrator and ISlicerProgressNotifier have been migrated to Farm.Slicer.Module.Services.
// SlicerQueueStats has been migrated to Farm.Slicer.Module.Services (imported via type alias above).

public class SlicerValidationResult
{
    public bool IsValid { get; set; }

    private readonly List<string> _issues = new();
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Issues => _issues;

    public IReadOnlyList<string> Warnings => _warnings;

    public long FileSizeBytes { get; set; }

    public string? FileType { get; set; }

    public Dictionary<string, object> Metadata { get; } = new Dictionary<string, object>();

    public void AddIssue(string issue)
    {
        if (!string.IsNullOrWhiteSpace(issue))
        {
            _issues.Add(issue);
        }
    }

    public void AddWarning(string warning)
    {
        if (!string.IsNullOrWhiteSpace(warning))
        {
            _warnings.Add(warning);
        }
    }

    public SlicerValidationResult()
    {
        IsValid = true;
    }
}

public class SlicerFileMetadata
{
    public string Key { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime LastModified { get; set; }

    public string? ETag { get; set; }

    public Dictionary<string, string> CustomMetadata { get; } = new Dictionary<string, string>();
}

public class SlicerEngineInfo
{
    public SlicerEngineType Engine { get; set; }

    public string Version { get; set; } = string.Empty;

    public bool IsHealthy { get; set; }

    public int ActiveWorkers { get; set; }

    public long QueueDepth { get; set; }

    public IReadOnlyList<string> SupportedExtensions { get; set; } = Array.Empty<string>();

    public TimeSpan? EstimatedWaitTime { get; set; }
}

public class SlicerOrchestratorHealth
{
    public bool IsHealthy { get; set; }

    public Dictionary<SlicerEngineType, SlicerEngineInfo> Engines { get; } = new Dictionary<SlicerEngineType, SlicerEngineInfo>();

    public bool JobQueueHealthy { get; set; }

    public bool FileStorageHealthy { get; set; }

    public int TotalActiveJobs { get; set; }

    public long TotalQueuedJobs { get; set; }

    public DateTime LastHealthCheck { get; set; } = DateTime.UtcNow;
}

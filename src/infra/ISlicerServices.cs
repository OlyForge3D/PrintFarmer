using System.Diagnostics.CodeAnalysis;

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
    Task EnqueueAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dequeue the next available job for a specific worker
    /// </summary>
    Task<DistributedSlicingJob?> DequeueAsync(string workerId, SlicerEngineType? preferredEngine = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a job as completed with results
    /// </summary>
    Task CompleteJobAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a job as failed
    /// </summary>
    Task FailJobAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update job progress
    /// </summary>
    Task UpdateProgressAsync(Guid jobId, int progress, string? currentStep = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get job status
    /// </summary>
    Task<DistributedSlicingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a job
    /// </summary>
    Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get queue statistics
    /// </summary>
    Task<SlicerQueueStats> GetQueueStatsAsync(SlicerEngineType? engine = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get jobs by user
    /// </summary>
    Task<List<DistributedSlicingJob>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup completed and failed jobs older than specified age
    /// </summary>
    Task CleanupOldJobsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeue failed jobs for retry
    /// </summary>
    Task RequeueFailedJobsAsync(int maxRetryCount = 3, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeue a specific job for retry, optionally with a delay/backoff and optional jitter percent.
    /// </summary>
    Task RequeueJobAsync(DistributedSlicingJob job, TimeSpan? delay = null, double jitterPercent = 0.0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find existing job by correlation ID and checksum for idempotency
    /// </summary>
    Task<DistributedSlicingJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a job with the given correlation ID and checksum already exists
    /// </summary>
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
    Task<string> UploadFileAsync(string key, Stream fileStream, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload a file and return its URL
    /// </summary>
    Task<string> UploadFileAsync(string key, byte[] fileData, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a file by its URL or key
    /// </summary>
    Task<Stream> DownloadFileAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a file as byte array
    /// </summary>
    Task<byte[]> DownloadFileBytesAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file exists
    /// </summary>
    Task<bool> FileExistsAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a file
    /// </summary>
    Task DeleteFileAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get file metadata
    /// </summary>
    Task<SlicerFileMetadata?> GetFileMetadataAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate a signed URL for temporary access
    /// </summary>
    Task<string> GenerateSignedUrlAsync(string keyOrUrl, TimeSpan expiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup temporary files older than specified age
    /// </summary>
    void CleanupTempFiles(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for slicer service orchestration
/// </summary>
public interface ISlicerOrchestrator
{
    /// <summary>
    /// Submit a slicing job
    /// </summary>
    Task<SlicingJobResponse> SubmitJobAsync(SlicingJobRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get job status
    /// </summary>
    Task<SlicingJobStatusResponse?> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a job
    /// </summary>
    Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get available slicer engines
    /// </summary>
    Task<List<SlicerEngineInfo>> GetAvailableEnginesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get queue statistics
    /// </summary>
    Task<Dictionary<SlicerEngineType, SlicerQueueStats>> GetAllQueueStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get user's jobs
    /// </summary>
    Task<List<SlicingJobStatusResponse>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Health check for the orchestrator
    /// </summary>
    Task<SlicerOrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for progress notifications
/// </summary>
public interface ISlicerProgressNotifier
{
    /// <summary>
    /// Send progress update to subscribers
    /// </summary>
    Task NotifyProgressAsync(SlicingProgressUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job completion notification
    /// </summary>
    Task NotifyCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job failure notification
    /// </summary>
    Task NotifyFailureAsync(DistributedSlicingJob job, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to progress updates for a specific job
    /// </summary>
    Task SubscribeToJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsubscribe from job updates
    /// </summary>
    Task UnsubscribeFromJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Supporting data structures
/// </summary>

public class SlicerQueueStats
{
    public SlicerEngineType Engine { get; set; }
    public long QueuedJobs { get; set; }
    public long ProcessingJobs { get; set; }
    public long CompletedJobs { get; set; }
    public long FailedJobs { get; set; }
    public int ActiveWorkers { get; set; }
    public double AverageProcessingTimeSeconds { get; set; }
    public TimeSpan? EstimatedWaitTime { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

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

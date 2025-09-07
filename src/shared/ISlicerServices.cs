namespace Farm.Web.Shared;

/// <summary>
/// Interface for job queue operations in distributed slicing system
/// </summary>
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
}

/// <summary>
/// Interface for slicer engine implementations
/// </summary>
public interface ISlicerEngine
{
    /// <summary>
    /// Type of slicer engine
    /// </summary>
    SlicerEngineType EngineType { get; }

    /// <summary>
    /// Version of the slicer
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Check if the slicer is available and healthy
    /// </summary>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Slice a 3D model file
    /// </summary>
    Task<SlicingResult> SliceAsync(DistributedSlicingJob job, IProgress<SlicingProgressUpdate>? progressCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate a 3D model file
    /// </summary>
    Task<SlicerValidationResult> ValidateModelAsync(Stream modelFile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get estimated processing time for a job
    /// </summary>
    Task<TimeSpan> EstimateProcessingTimeAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get supported file extensions
    /// </summary>
    IReadOnlyList<string> SupportedFileExtensions { get; }
}

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
    Task CleanupTempFilesAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
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
    public TimeSpan EstimatedWaitTime { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class SlicerValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public long FileSizeBytes { get; set; }
    public string? FileType { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class SlicerFileMetadata
{
    public string Key { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastModified { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string> CustomMetadata { get; set; } = new();
}

public class SlicerEngineInfo
{
    public SlicerEngineType Engine { get; set; }
    public string Version { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public int ActiveWorkers { get; set; }
    public long QueueDepth { get; set; }
    public IReadOnlyList<string> SupportedExtensions { get; set; } = Array.Empty<string>();
    public TimeSpan EstimatedWaitTime { get; set; }
}

public class SlicerOrchestratorHealth
{
    public bool IsHealthy { get; set; }
    public Dictionary<SlicerEngineType, SlicerEngineInfo> Engines { get; set; } = new();
    public bool JobQueueHealthy { get; set; }
    public bool FileStorageHealthy { get; set; }
    public int TotalActiveJobs { get; set; }
    public long TotalQueuedJobs { get; set; }
    public DateTime LastHealthCheck { get; set; } = DateTime.UtcNow;
}
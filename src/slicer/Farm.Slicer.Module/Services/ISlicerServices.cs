using System.Diagnostics.CodeAnalysis;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Interface for job queue operations in distributed slicing system.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "ISlicerJobQueue models a worker job queue abstraction; renaming would be a breaking API change and 'Queue' accurately describes the semantics.")]
public interface ISlicerJobQueue
{
    /// <summary>
    /// Enqueue a new slicing job.
    /// </summary>
    Task EnqueueAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dequeue the next available job for a specific worker.
    /// </summary>
    Task<DistributedSlicingJob?> DequeueAsync(string workerId, SlicerEngineType? preferredEngine = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a job as completed with results.
    /// </summary>
    Task CompleteJobAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a job as failed.
    /// </summary>
    Task FailJobAsync(DistributedSlicingJob job, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update job progress.
    /// </summary>
    Task UpdateProgressAsync(DistributedSlicingJob job, int progress, string? currentStep = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get job status.
    /// </summary>
    Task<DistributedSlicingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a job.
    /// </summary>
    Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get queue statistics.
    /// </summary>
    Task<SlicerQueueStats> GetQueueStatsAsync(SlicerEngineType? engine = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get queue statistics for every slicer engine in one aggregate query.
    /// </summary>
    Task<IReadOnlyDictionary<SlicerEngineType, SlicerQueueStats>> GetAllQueueStatsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get jobs by user.
    /// </summary>
    Task<List<DistributedSlicingJob>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup completed and failed jobs older than specified age.
    /// </summary>
    Task CleanupOldJobsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeue failed jobs for retry.
    /// </summary>
    Task RequeueFailedJobsAsync(int maxRetryCount = 3, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeue a specific job for retry, optionally with a delay/backoff and optional jitter percent.
    /// </summary>
    Task RequeueJobAsync(DistributedSlicingJob job, TimeSpan? delay = null, double jitterPercent = 0.0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find existing job by correlation ID and checksum for idempotency.
    /// </summary>
    Task<DistributedSlicingJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a job with the given correlation ID and checksum already exists.
    /// </summary>
    Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for file storage operations.
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public interface ISlicerFileStorage
#pragma warning restore SA1402
{
    /// <summary>
    /// Upload a file and return its URL.
    /// </summary>
    Task<string> UploadFileAsync(string key, Stream fileStream, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload a file and return its URL.
    /// </summary>
    Task<string> UploadFileAsync(string key, byte[] fileData, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a file by its URL or key.
    /// </summary>
    Task<Stream> DownloadFileAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a file as byte array.
    /// </summary>
    Task<byte[]> DownloadFileBytesAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file exists.
    /// </summary>
    Task<bool> FileExistsAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a file.
    /// </summary>
    Task DeleteFileAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get file metadata.
    /// </summary>
    Task<SlicerFileMetadata?> GetFileMetadataAsync(string keyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate a signed URL for temporary access.
    /// </summary>
    Task<string> GenerateSignedUrlAsync(string keyOrUrl, TimeSpan expiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup temporary files older than specified age.
    /// </summary>
    void CleanupTempFiles(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for slicer service orchestration.
/// </summary>
#pragma warning disable SA1402
public interface ISlicerOrchestrator
#pragma warning restore SA1402
{
    /// <summary>
    /// Submit a slicing job.
    /// </summary>
    Task<SlicingJobResponse> SubmitJobAsync(SlicingJobRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get job status.
    /// </summary>
    Task<SlicingJobStatusResponse?> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a job.
    /// </summary>
    Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get available slicer engines.
    /// </summary>
    Task<List<SlicerEngineInfo>> GetAvailableEnginesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get queue statistics.
    /// </summary>
    Task<Dictionary<SlicerEngineType, SlicerQueueStats>> GetAllQueueStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get user's jobs.
    /// </summary>
    Task<List<SlicingJobStatusResponse>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Health check for the orchestrator.
    /// </summary>
    Task<SlicerOrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for progress notifications.
/// </summary>
#pragma warning disable SA1402
public interface ISlicerProgressNotifier
#pragma warning restore SA1402
{
    /// <summary>
    /// Send progress update to subscribers.
    /// </summary>
    Task NotifyProgressAsync(SlicingProgressUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job completion notification.
    /// </summary>
    Task NotifyCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job failure notification.
    /// </summary>
    Task NotifyFailureAsync(DistributedSlicingJob job, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to progress updates for a specific job.
    /// </summary>
    Task SubscribeToJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsubscribe from job updates.
    /// </summary>
    Task UnsubscribeFromJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Queue statistics for a slicer engine.
/// </summary>
#pragma warning disable SA1402
public class SlicerQueueStats
#pragma warning restore SA1402
{
    /// <summary>Gets or sets the slicer engine represented by these metrics.</summary>
    public SlicerEngineType Engine { get; set; }

    /// <summary>Gets or sets the number of jobs waiting for the engine.</summary>
    public long QueuedJobs { get; set; }

    /// <summary>Gets or sets all jobs persisted in the processing status.</summary>
    public long ProcessingJobs { get; set; }

    /// <summary>Gets or sets all successfully completed jobs.</summary>
    public long CompletedJobs { get; set; }

    /// <summary>Gets or sets all failed jobs.</summary>
    public long FailedJobs { get; set; }

    /// <summary>
    /// Gets or sets live, enabled, non-draining workers that advertise this engine.
    /// </summary>
    public int ActiveWorkers { get; set; }

    /// <summary>
    /// Gets or sets the arithmetic mean duration of completed jobs with valid start and completion
    /// timestamps, rounded to seconds. The value is zero when no valid timing history exists.
    /// </summary>
    public double AverageProcessingTimeSeconds { get; set; }

    /// <summary>
    /// Gets or sets the estimated completion time for queued and actively leased work.
    /// A null value means no live capacity or no valid timing history is available.
    /// </summary>
    public TimeSpan? EstimatedWaitTime { get; set; }

    /// <summary>Gets or sets the UTC instant at which all fields were evaluated.</summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// File metadata for slicer storage.
/// </summary>
#pragma warning disable SA1402
public class SlicerFileMetadata
#pragma warning restore SA1402
{
    public string Key { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime LastModified { get; set; }

    public string? ETag { get; set; }

    public Dictionary<string, string> CustomMetadata { get; } = new Dictionary<string, string>();
}

/// <summary>
/// Information about a slicer engine.
/// </summary>
#pragma warning disable SA1402
public class SlicerEngineInfo
#pragma warning restore SA1402
{
    public SlicerEngineType Engine { get; set; }

    public string Version { get; set; } = string.Empty;

    public bool IsHealthy { get; set; }

    public int ActiveWorkers { get; set; }

    public long QueueDepth { get; set; }

    public IReadOnlyList<string> SupportedExtensions { get; set; } = Array.Empty<string>();

    public TimeSpan? EstimatedWaitTime { get; set; }

    /// <summary>
    /// All registered library versions for this engine, sorted newest-first
    /// (issue #578). The React version selector renders these; the first
    /// entry is the "current" engine and any additional entries are the
    /// retained "previous" engines. Empty when the engine is compiled in
    /// but no plugin is present at runtime.
    /// </summary>
    public IReadOnlyList<string> AvailableVersions { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Orchestrator health information.
/// </summary>
#pragma warning disable SA1402
public class SlicerOrchestratorHealth
#pragma warning restore SA1402
{
    public bool IsHealthy { get; set; }

    public Dictionary<SlicerEngineType, SlicerEngineInfo> Engines { get; } = new Dictionary<SlicerEngineType, SlicerEngineInfo>();

    public bool JobQueueHealthy { get; set; }

    public bool FileStorageHealthy { get; set; }

    public int TotalActiveJobs { get; set; }

    public long TotalQueuedJobs { get; set; }

    public DateTime LastHealthCheck { get; set; } = DateTime.UtcNow;
}

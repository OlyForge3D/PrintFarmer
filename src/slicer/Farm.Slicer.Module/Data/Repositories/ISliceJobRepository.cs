using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for managing <see cref="SliceJob"/> entity persistence and queries.
/// </summary>
public interface ISliceJobRepository
{
    /// <summary>Adds a new slice job.</summary>
    Task AddAsync(SliceJob job, CancellationToken ct = default);

    /// <summary>Gets a slice job by its unique identifier.</summary>
    Task<SliceJob?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets all jobs for a specific user with optional pagination.</summary>
    Task<IReadOnlyList<SliceJob>> GetByUserIdAsync(Guid userId, int? limit = null, int? offset = null, CancellationToken ct = default);

    /// <summary>Gets jobs filtered by status.</summary>
    Task<IReadOnlyList<SliceJob>> GetByStatusAsync(string status, int? limit = null, CancellationToken ct = default);

    /// <summary>Gets active jobs assigned to a specific worker.</summary>
    Task<IReadOnlyList<SliceJob>> GetJobsByWorkerIdAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>Gets queued jobs ordered by priority and queue time.</summary>
    Task<IReadOnlyList<SliceJob>> GetQueuedJobsAsync(int? limit = null, CancellationToken ct = default);

    /// <summary>Updates job status and optional progress fields.</summary>
    Task UpdateStatusAsync(Guid jobId, string status, string? progressMessage = null, int? progressPercent = null, CancellationToken ct = default);

    /// <summary>Marks a job as started by the specified worker.</summary>
    Task MarkStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default);

    /// <summary>Marks a job as completed with a result file URL.</summary>
    Task MarkCompletedAsync(Guid jobId, string resultFileUrl, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default);

    /// <summary>Marks a job completed and associates artifacts with byte-total aggregation.</summary>
    Task MarkCompletedWithArtifactsAsync(Guid jobId, string resultFileUrl, IEnumerable<Guid> artifactIds, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default);

    /// <summary>Marks a job as failed with an error message.</summary>
    Task MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default);

    /// <summary>Updates job progress percentage and message.</summary>
    Task UpdateProgressAsync(Guid jobId, int progressPercent, string progressMessage, CancellationToken ct = default);

    /// <summary>Atomically claims the next queued job matching capabilities (worker pull model).</summary>
    Task<SliceJob?> ClaimNextJobAsync(Guid workerId, string[]? capabilities, int leaseDurationSeconds, CancellationToken ct = default);

    /// <summary>Finds an existing job by correlation ID and checksum (idempotency lookup).</summary>
    Task<SliceJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken ct = default);

    /// <summary>Checks whether a job exists with the given correlation ID and checksum.</summary>
    Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken ct = default);

    /// <summary>Finds jobs that are stuck (processing but lease expired or long-running).</summary>
    Task<IReadOnlyList<SliceJob>> GetStuckJobsAsync(int maxAgeSeconds, int? limit = null, CancellationToken ct = default);

    /// <summary>Renews the lease for a job (extends <c>LeaseExpiresAt</c>).</summary>
    Task RenewLeaseAsync(Guid jobId, int leaseDurationSeconds, CancellationToken ct = default);

    /// <summary>Increments retry count and requeues or fails the job.</summary>
    Task IncrementRetryAndRequeueAsync(Guid jobId, int maxRetries, CancellationToken ct = default);

    /// <summary>Returns the total count of jobs, optionally filtered by status.</summary>
    Task<int> CountAsync(string? status = null, CancellationToken ct = default);

    /// <summary>Returns a paged list of jobs with sorting and optional status filter.</summary>
    Task<IReadOnlyList<SliceJob>> GetPagedAsync(int page, int pageSize, string? status = null, string? sortBy = null, string? sortDir = null, CancellationToken ct = default);

    /// <summary>Requeues a single failed job for retry (user-initiated).</summary>
    Task RetryJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Saves pending changes to the database.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}

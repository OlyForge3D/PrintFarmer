using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Slicing;

/// <summary>
/// Repository for managing SliceJob entities
/// </summary>
public interface ISliceJobRepository
{
    /// <summary>
    /// Add a new slice job to the database
    /// </summary>
    /// <param name="job">The slice job to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(SliceJob job, CancellationToken ct = default);

    /// <summary>
    /// Get a slice job by ID
    /// </summary>
    /// <param name="id">The unique identifier of the slice job.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SliceJob?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get all jobs for a specific user
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="offset">Number of jobs to skip for pagination.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SliceJob>> GetByUserIdAsync(Guid userId, int? limit = null, int? offset = null, CancellationToken ct = default);

    /// <summary>
    /// Get jobs by status
    /// </summary>
    /// <param name="status">The status to filter jobs by.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SliceJob>> GetByStatusAsync(string status, int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Get active jobs assigned to a specific worker
    /// </summary>
    /// <param name="workerId">The unique identifier of the worker.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SliceJob>> GetJobsByWorkerIdAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Get queued jobs ordered by priority and queue time
    /// </summary>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SliceJob>> GetQueuedJobsAsync(int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Update job status and related fields
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="status">The new status value.</param>
    /// <param name="progressMessage">Optional progress message.</param>
    /// <param name="progressPercent">Optional progress percentage (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateStatusAsync(Guid jobId, string status, string? progressMessage = null, int? progressPercent = null, CancellationToken ct = default);

    /// <summary>
    /// Mark job as started by a worker
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="workerId">The unique identifier of the worker starting the job.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Mark job as completed with result
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="resultFileUrl">URL to the resulting sliced file.</param>
    /// <param name="estimatedPrintTimeSeconds">Optional estimated print time in seconds.</param>
    /// <param name="filamentUsedGrams">Optional filament usage in grams.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkCompletedAsync(Guid jobId, string resultFileUrl, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default);

    /// <summary>
    /// Mark job completed with artifact associations and summary aggregation.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="resultFileUrl">URL to the resulting sliced file.</param>
    /// <param name="artifactIds">Collection of artifact identifiers to associate with the job.</param>
    /// <param name="estimatedPrintTimeSeconds">Optional estimated print time in seconds.</param>
    /// <param name="filamentUsedGrams">Optional filament usage in grams.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkCompletedWithArtifactsAsync(Guid jobId, string resultFileUrl, IEnumerable<Guid> artifactIds, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default);

    /// <summary>
    /// Mark job as failed with error message
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="errorMessage">The error message describing the failure.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Update job progress
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="progressPercent">The progress percentage (0-100).</param>
    /// <param name="progressMessage">The progress status message.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateProgressAsync(Guid jobId, int progressPercent, string progressMessage, CancellationToken ct = default);

    /// <summary>
    /// Atomically claim the next available queued job matching capabilities (worker pull model)
    /// Sets status to Processing, assigns workerId, and sets ClaimedAt timestamp with lease expiration
    /// </summary>
    /// <param name="workerId">Worker claiming the job</param>
    /// <param name="capabilities">Optional capabilities filter (null means accept any job)</param>
    /// <param name="leaseDurationSeconds">Lease duration in seconds</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Claimed job or null if no matching job available</returns>
    Task<SliceJob?> ClaimNextJobAsync(Guid workerId, string[]? capabilities, int leaseDurationSeconds, CancellationToken ct = default);

    /// <summary>
    /// Find an existing job by correlation id and checksum (idempotency lookup)
    /// </summary>
    /// <param name="correlationId">The correlation identifier for idempotency.</param>
    /// <param name="checksum">The checksum of the job input.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SliceJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken ct = default);

    /// <summary>
    /// Check if a job exists with the given correlation id and checksum
    /// </summary>
    /// <param name="correlationId">The correlation identifier for idempotency.</param>
    /// <param name="checksum">The checksum of the job input.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken ct = default);

    /// <summary>
    /// Find jobs that are considered stuck (Processing but lease expired or long-running) and return a paged set.
    /// </summary>
    /// <param name="maxAgeSeconds">Maximum age in seconds before a job is considered stuck.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SliceJob>> GetStuckJobsAsync(int maxAgeSeconds, int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Renew the lease for a job (extend LeaseExpiresAt) when a worker heartbeats.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="leaseDurationSeconds">The new lease duration in seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RenewLeaseAsync(Guid jobId, int leaseDurationSeconds, CancellationToken ct = default);

    /// <summary>
    /// Increment the retry count, set status back to Queued (or Failed if exceeded) and clear worker assignment.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="maxRetries">Maximum number of retries before marking as failed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task IncrementRetryAndRequeueAsync(Guid jobId, int maxRetries, CancellationToken ct = default);

    /// <summary>
    /// Save changes to the database
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct = default);
}

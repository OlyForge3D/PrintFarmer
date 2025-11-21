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
    Task AddAsync(SliceJob job, CancellationToken ct = default);

    /// <summary>
    /// Get a slice job by ID
    /// </summary>
    Task<SliceJob?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get all jobs for a specific user
    /// </summary>
    Task<IReadOnlyList<SliceJob>> GetByUserIdAsync(Guid userId, int? limit = null, int? offset = null, CancellationToken ct = default);

    /// <summary>
    /// Get jobs by status
    /// </summary>
    Task<IReadOnlyList<SliceJob>> GetByStatusAsync(string status, int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Get active jobs assigned to a specific worker
    /// </summary>
    Task<IReadOnlyList<SliceJob>> GetJobsByWorkerIdAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Get queued jobs ordered by priority and queue time
    /// </summary>
    Task<IReadOnlyList<SliceJob>> GetQueuedJobsAsync(int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Update job status and related fields
    /// </summary>
    Task UpdateStatusAsync(Guid jobId, string status, string? progressMessage = null, int? progressPercent = null, CancellationToken ct = default);

    /// <summary>
    /// Mark job as started by a worker
    /// </summary>
    Task MarkStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Mark job as completed with result
    /// </summary>
    Task MarkCompletedAsync(Guid jobId, string resultFileUrl, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default);
    /// <summary>
    /// Mark job completed with artifact associations and summary aggregation.
    /// </summary>
    Task MarkCompletedWithArtifactsAsync(Guid jobId, string resultFileUrl, IEnumerable<Guid> artifactIds, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default);

    /// <summary>
    /// Mark job as failed with error message
    /// </summary>
    Task MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Update job progress
    /// </summary>
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
    Task<SliceJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken ct = default);

    /// <summary>
    /// Check if a job exists with the given correlation id and checksum
    /// </summary>
    Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken ct = default);

    /// <summary>
    /// Find jobs that are considered stuck (Processing but lease expired or long-running) and return a paged set.
    /// </summary>
    Task<IReadOnlyList<SliceJob>> GetStuckJobsAsync(int maxAgeSeconds, int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Renew the lease for a job (extend LeaseExpiresAt) when a worker heartbeats.
    /// </summary>
    Task RenewLeaseAsync(Guid jobId, int leaseDurationSeconds, CancellationToken ct = default);

    /// <summary>
    /// Increment the retry count, set status back to Queued (or Failed if exceeded) and clear worker assignment.
    /// </summary>
    Task IncrementRetryAndRequeueAsync(Guid jobId, int maxRetries, CancellationToken ct = default);

    /// <summary>
    /// Save changes to the database
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}

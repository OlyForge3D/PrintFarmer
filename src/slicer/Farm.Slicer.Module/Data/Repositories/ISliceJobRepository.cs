using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;

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

    /// <summary>
    /// Gets job counts grouped by normalized engine and status.
    /// </summary>
    /// <remarks>
    /// Jobs without a valid canonical engine name are grouped under OrcaSlicer, matching
    /// <see cref="SlicerEngineNames.Resolve(SliceJob)"/> legacy-row semantics.
    /// </remarks>
    Task<IReadOnlyDictionary<(SlicerEngineType Engine, string Status), int>> GetQueueCountsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Gets worker capacity, active lease, and completed-job timing aggregates for every engine.
    /// </summary>
    /// <param name="nowUtc">The shared UTC instant used to evaluate lease expiry.</param>
    /// <param name="workerHeartbeatCutoffUtc">
    /// The oldest heartbeat that qualifies a worker as live.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyDictionary<SlicerEngineType, SlicerQueueMetricAggregate>> GetQueueMetricAggregatesAsync(
        DateTime nowUtc,
        DateTime workerHeartbeatCutoffUtc,
        CancellationToken ct = default);

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

    /// <summary>Gets a processing job while its unexpired lease is owned by the worker.</summary>
    Task<SliceJob?> GetByActiveWorkerLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        CancellationToken ct = default);

    /// <summary>Updates progress only while the worker still owns an unexpired processing lease.</summary>
    Task<bool> TryUpdateProgressForActiveLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        int progressPercent,
        string progressMessage,
        CancellationToken ct = default);

    /// <summary>Completes a job only while the worker still owns an unexpired processing lease.</summary>
    Task<bool> TryCompleteForActiveLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        string resultFileUrl,
        IEnumerable<Guid> artifactIds,
        int? estimatedPrintTimeSeconds = null,
        decimal? filamentUsedGrams = null,
        string? machineProfileSha256 = null,
        string? processProfileSha256 = null,
        string? filamentProfileSha256 = null,
        CancellationToken ct = default);

    /// <summary>Fails a job only while the worker still owns an unexpired processing lease.</summary>
    Task<bool> TryFailForActiveLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        string errorMessage,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically claims the next eligible job using a registered worker identity and bounded retry policy.
    /// </summary>
    Task<SliceJob?> ClaimNextJobAsync(
        WorkerClaimIdentity worker,
        int leaseDurationSeconds,
        int maxRetries,
        CancellationToken ct = default);

    /// <summary>Finds an existing job by correlation ID and checksum (idempotency lookup).</summary>
    Task<SliceJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken ct = default);

    /// <summary>Checks whether a job exists with the given correlation ID and checksum.</summary>
    Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken ct = default);

    /// <summary>Finds processing jobs whose current lease has expired.</summary>
    Task<IReadOnlyList<SliceJob>> GetExpiredLeaseJobsAsync(int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Renews an active, unexpired lease when it is still owned by the specified worker.
    /// </summary>
    Task<bool> RenewLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        int leaseDurationSeconds,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically requeues or fails a job only if the selected claim remains expired.
    /// </summary>
    Task<bool> TryRecoverExpiredLeaseAsync(
        Guid jobId,
        Guid? expectedWorkerId,
        Guid? expectedClaimToken,
        DateTime expectedLeaseExpiresAt,
        int maxRetries,
        CancellationToken ct = default);

    /// <summary>
    /// Renews an unexpired lease only when the presented worker, lease token and fencing counter
    /// all still match the persisted row.
    /// </summary>
    /// <param name="jobId">The claimed job.</param>
    /// <param name="workerId">The worker that holds the lease.</param>
    /// <param name="leaseToken">The lease token issued at claim time.</param>
    /// <param name="leaseFence">The fencing counter issued at claim time.</param>
    /// <param name="leaseDurationSeconds">The requested lease extension.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when exactly one row was extended.</returns>
    Task<bool> TryRenewLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid leaseToken,
        long leaseFence,
        int leaseDurationSeconds,
        CancellationToken ct = default);

    /// <summary>Increments retry count and requeues or fails the job.</summary>
    Task IncrementRetryAndRequeueAsync(Guid jobId, int maxRetries, CancellationToken ct = default);

    /// <summary>Requeues or fails a job only while the worker still owns an active lease.</summary>
    Task<bool> TryRequeueForActiveLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        int maxRetries,
        CancellationToken ct = default);

    /// <summary>Returns the total count of jobs, optionally filtered by status.</summary>
    Task<int> CountAsync(string? status = null, CancellationToken ct = default);

    /// <summary>Returns a paged list of jobs with sorting and optional status filter.</summary>
    Task<IReadOnlyList<SliceJob>> GetPagedAsync(int page, int pageSize, string? status = null, string? sortBy = null, string? sortDir = null, CancellationToken ct = default);

    /// <summary>Requeues an owner-visible terminal job only if its observed version is unchanged.</summary>
    Task<SliceJob?> TryRetryJobAsync(
        Guid jobId,
        Guid expectedUserId,
        string expectedStatus,
        DateTime expectedUpdatedAt,
        CancellationToken ct = default);

    /// <summary>Saves pending changes to the database.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// Bounded per-engine inputs used to build public slicer queue statistics.
/// </summary>
public sealed class SlicerQueueMetricAggregate
{
    /// <summary>
    /// Gets the number of live, enabled workers that advertise the engine and can accept work.
    /// </summary>
    public int ActiveWorkers { get; init; }

    /// <summary>
    /// Gets the total configured slots across active workers.
    /// </summary>
    public int DispatchCapacity { get; init; }

    /// <summary>
    /// Gets the number of processing jobs with an authoritative active worker lease.
    /// </summary>
    public int ActiveLeasedJobs { get; init; }

    /// <summary>
    /// Gets the number of valid completed-job timing samples.
    /// </summary>
    public int TimingSampleCount { get; init; }

    /// <summary>
    /// Gets the arithmetic mean duration of valid completed-job timing samples in seconds.
    /// </summary>
    public double AverageProcessingTimeSeconds { get; init; }
}

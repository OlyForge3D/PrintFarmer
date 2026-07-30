using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for managing <see cref="Artifact"/> persistence and queries.
/// Uses <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/> for thread-safe scoped contexts.
/// </summary>
public interface IArtifactsRepository
{
    /// <summary>Adds an artifact and saves changes.</summary>
    /// <param name="artifact">The artifact to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Artifact> AddAsync(Artifact artifact, CancellationToken ct = default);

    /// <summary>
    /// Adds a worker artifact only after acquiring the job through its active lease fence.
    /// </summary>
    Task<bool> TryAddForActiveLeaseAsync(
        Artifact artifact,
        Guid workerId,
        Guid claimToken,
        CancellationToken ct = default);

    /// <summary>Gets an artifact by its unique identifier.</summary>
    /// <param name="id">The artifact identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Artifact?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets all artifacts for a specific slice job.</summary>
    /// <param name="jobId">The slice job identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Artifact>> GetByJobIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Gets all artifacts in the repository.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Artifact>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Gets all artifacts older than the specified date.</summary>
    /// <param name="cutoffDate">The cutoff date for filtering.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Artifact>> GetOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default);

    /// <summary>Gets cleanup operations whose byte-deletion phase must be resumed.</summary>
    Task<IReadOnlyList<Artifact>> GetCleanupInProgressAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically reserves an eligible artifact for cleanup, excluding any concurrent promotion pin.
    /// </summary>
    Task<bool> TryReserveForCleanupAsync(
        Guid artifactId,
        Guid? expectedReservationToken,
        DateTime? expectedReservedAtUtc,
        Guid reservationToken,
        DateTime reservedAtUtc,
        DateTime staleBeforeUtc,
        CancellationToken ct = default);

    /// <summary>Atomically enters the immutable, idempotent byte-deletion phase.</summary>
    Task<bool> TryBeginCleanupDeletionAsync(
        Guid artifactId,
        Guid reservationToken,
        DateTime startedAtUtc,
        CancellationToken ct = default);

    /// <summary>Finalizes metadata only after byte deletion began under the exact operation token.</summary>
    Task<bool> FinalizeCleanupAsync(
        Guid artifactId,
        Guid reservationToken,
        CancellationToken ct = default);

    /// <summary>Releases a reservation only before its durable deletion phase begins.</summary>
    Task ReleaseCleanupReservationAsync(
        Guid artifactId,
        Guid reservationToken,
        CancellationToken ct = default);

    /// <summary>
    /// Pins an artifact against cleanup while a promotion runs, or confirms an existing pin held by the
    /// same operation.
    /// </summary>
    /// <param name="artifactId">The artifact being promoted.</param>
    /// <param name="checkpointId">
    /// The durable core-context checkpoint coordinating the promotion, or <see langword="null"/> when
    /// the caller is reserving the artifact before its checkpoint exists.
    /// </param>
    /// <param name="operation">The owner-scoped identity of the promotion that owns the artifact.</param>
    /// <param name="startedAtUtc">The UTC timestamp of the pin.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the artifact is pinned by <paramref name="operation"/>;
    /// <see langword="false"/> when another operation already owns it or the artifact no longer exists.
    /// </returns>
    Task<bool> TryPinForPromotionAsync(
        Guid artifactId,
        Guid? checkpointId,
        PromotionOperationIdentity operation,
        DateTime startedAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Records the durable promotion result on the artifact so its lineage survives cleanup.
    /// </summary>
    /// <param name="artifactId">The promoted artifact.</param>
    /// <param name="gcodeFileId">The promoted G-code file identity.</param>
    /// <param name="promotedAtUtc">The UTC completion timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the artifact was found and acknowledged.</returns>
    Task<bool> MarkPromotedAsync(
        Guid artifactId,
        Guid gcodeFileId,
        DateTime promotedAtUtc,
        CancellationToken ct = default);

    /// <summary>Releases a promotion pin after a permanent failure so cleanup may resume.</summary>
    /// <param name="artifactId">The artifact to release.</param>
    /// <param name="operation">The owner-scoped identity that holds the pin.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when a pin held by the operation was released.</returns>
    Task<bool> ReleasePromotionPinAsync(
        Guid artifactId,
        PromotionOperationIdentity operation,
        CancellationToken ct = default);

    /// <summary>Gets the total size of all artifacts in bytes.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> GetTotalSizeAsync(CancellationToken ct = default);

    /// <summary>Updates an existing artifact.</summary>
    /// <param name="artifact">The artifact to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(Artifact artifact, CancellationToken ct = default);

    /// <summary>Deletes an artifact by its unique identifier.</summary>
    /// <param name="id">The artifact identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the artifact was found and deleted.</returns>
    Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Deletes multiple artifacts at once.</summary>
    /// <param name="artifactIds">The artifact identifiers to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteMultipleAsync(IEnumerable<Guid> artifactIds, CancellationToken ct = default);

    /// <summary>Gets a worker by its unique identifier (used for updating artifact counters).</summary>
    /// <param name="workerId">The worker identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Worker?> GetWorkerByIdAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>Updates a worker entity (e.g., artifact counters).</summary>
    /// <param name="worker">The worker to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default);

    /// <summary>Saves pending changes to the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct = default);
}

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

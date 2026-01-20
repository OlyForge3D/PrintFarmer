using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Artifacts;

/// <summary>
/// Repository for managing artifact persistence and queries.
/// </summary>
public interface IArtifactsRepository
{
    /// <summary>
    /// Adds an artifact to the repository and saves changes.
    /// </summary>
    /// <param name="artifact">The artifact to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Artifact> AddAsync(Artifact artifact, CancellationToken ct = default);

    /// <summary>
    /// Gets an artifact by ID.
    /// </summary>
    /// <param name="id">The artifact ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Artifact?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all artifacts for a specific job.
    /// </summary>
    /// <param name="jobId">The job ID to get artifacts for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Artifact>> GetByJobIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Gets all artifacts in the repository (for cleanup operations).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Artifact>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all artifacts older than the specified date.
    /// </summary>
    /// <param name="cutoffDate">The cutoff date for filtering.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Artifact>> GetOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default);

    /// <summary>
    /// Gets the total size of all artifacts.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> GetTotalSizeAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates an artifact (e.g., to set deletion timestamp or other properties).
    /// </summary>
    /// <param name="artifact">The artifact to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(Artifact artifact, CancellationToken ct = default);

    /// <summary>
    /// Deletes an artifact by ID.
    /// </summary>
    /// <param name="id">The artifact ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Deletes multiple artifacts at once.
    /// </summary>
    /// <param name="artifactIds">The artifact IDs to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteMultipleAsync(IEnumerable<Guid> artifactIds, CancellationToken ct = default);

    /// <summary>
    /// Gets a worker by ID (used for updating worker artifact counters).
    /// </summary>
    /// <param name="workerId">The worker ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Worker?> GetWorkerByIdAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Updates a worker (e.g., artifact counters).
    /// </summary>
    /// <param name="worker">The worker to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default);

    /// <summary>
    /// Saves pending changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct = default);
}

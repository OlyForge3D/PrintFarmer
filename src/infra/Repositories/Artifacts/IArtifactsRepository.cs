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
    Task<Artifact> AddAsync(Artifact artifact, CancellationToken ct = default);

    /// <summary>
    /// Gets an artifact by ID.
    /// </summary>
    Task<Artifact?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all artifacts for a specific job.
    /// </summary>
    Task<IReadOnlyList<Artifact>> GetByJobIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Gets all artifacts in the repository (for cleanup operations).
    /// </summary>
    Task<IReadOnlyList<Artifact>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all artifacts older than the specified date.
    /// </summary>
    Task<IReadOnlyList<Artifact>> GetOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default);

    /// <summary>
    /// Gets the total size of all artifacts.
    /// </summary>
    Task<long> GetTotalSizeAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates an artifact (e.g., to set deletion timestamp or other properties).
    /// </summary>
    Task UpdateAsync(Artifact artifact, CancellationToken ct = default);

    /// <summary>
    /// Deletes an artifact by ID.
    /// </summary>
    Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Deletes multiple artifacts at once.
    /// </summary>
    Task DeleteMultipleAsync(IEnumerable<Guid> artifactIds, CancellationToken ct = default);

    /// <summary>
    /// Gets a worker by ID (used for updating worker artifact counters).
    /// </summary>
    Task<Worker?> GetWorkerByIdAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Updates a worker (e.g., artifact counters).
    /// </summary>
    Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default);

    /// <summary>
    /// Saves pending changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}

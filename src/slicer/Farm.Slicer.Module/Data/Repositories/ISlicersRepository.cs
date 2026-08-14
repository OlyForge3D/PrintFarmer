using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for managing <see cref="SlicerService"/> registrations.
/// Tracks available slicer services and their configurations.
/// </summary>
public interface ISlicersRepository
{
    /// <summary>Lists all registered slicer services.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct);

    /// <summary>Adds a new slicer service registration.</summary>
    /// <param name="svc">The slicer service to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(SlicerService svc, CancellationToken ct);

    /// <summary>Gets a slicer service by its unique identifier.</summary>
    /// <param name="id">The slicer service identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SlicerService?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Gets the most recently seen slicer service registered under a stable worker
    /// instance identifier, so a redeploy can update the existing record in place
    /// instead of creating a duplicate. The instance ID only locates the row to
    /// update — every registration still receives freshly minted credentials.
    /// </summary>
    /// <param name="instanceId">The stable worker instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SlicerService?> GetByInstanceIdAsync(string instanceId, CancellationToken ct);

    /// <summary>Removes a slicer service registration.</summary>
    /// <param name="svc">The slicer service to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(SlicerService svc, CancellationToken ct);

    /// <summary>Saves pending changes to the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Discards all locally tracked entity changes without touching the database.
    /// Used to recover from a failed <see cref="SaveChangesAsync"/> (e.g. a unique
    /// InstanceId conflict from a concurrent registration) so the caller can retry
    /// the operation against a freshly queried row instead of resubmitting stale,
    /// already-tracked entities.
    /// </summary>
    void ClearTracking();
}

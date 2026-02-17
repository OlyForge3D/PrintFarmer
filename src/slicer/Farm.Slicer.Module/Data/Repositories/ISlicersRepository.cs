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

    /// <summary>Removes a slicer service registration.</summary>
    /// <param name="svc">The slicer service to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(SlicerService svc, CancellationToken ct);

    /// <summary>Saves pending changes to the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct);
}

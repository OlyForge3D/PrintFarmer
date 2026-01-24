using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Slicing;

/// <summary>
/// Repository for managing slicer service registrations.
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

    /// <summary>Gets a slicer service by ID.</summary>
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

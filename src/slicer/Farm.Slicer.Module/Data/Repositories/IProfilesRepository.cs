using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Simple repository for <see cref="ProcessProfile"/> CRUD operations.
/// Used for basic profile management without slicer-specific filtering.
/// </summary>
public interface IProfilesRepository
{
    /// <summary>Gets all process profiles.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<ProcessProfile>> GetAllAsync(CancellationToken ct);

    /// <summary>Finds a process profile by its unique identifier.</summary>
    /// <param name="id">The profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProcessProfile?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Adds a new process profile.</summary>
    /// <param name="profile">The profile to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(ProcessProfile profile, CancellationToken ct);

    /// <summary>Removes a process profile.</summary>
    /// <param name="profile">The profile to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(ProcessProfile profile, CancellationToken ct);

    /// <summary>Saves pending changes to the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct);
}

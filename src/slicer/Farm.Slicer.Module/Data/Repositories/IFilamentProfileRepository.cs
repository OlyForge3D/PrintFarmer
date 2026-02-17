using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for querying and mutating <see cref="FilamentProfile"/> entities from OrcaSlicer.
/// </summary>
public interface IFilamentProfileRepository
{
    /// <summary>Gets a filament profile by its unique identifier.</summary>
    /// <param name="id">The profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets filament profiles for a slicer engine with optional user filtering.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="includeSystem">Whether to include system profiles.</param>
    /// <param name="userId">Optional user ID to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FilamentProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default);

    /// <summary>Gets a filament profile by its content hash.</summary>
    /// <param name="hash">The profile content hash.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentProfile?> GetByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>Adds a new filament profile.</summary>
    /// <param name="profile">The profile to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(FilamentProfile profile, CancellationToken ct = default);

    /// <summary>Updates an existing filament profile.</summary>
    /// <param name="profile">The profile to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(FilamentProfile profile, CancellationToken ct = default);

    /// <summary>Deletes a filament profile.</summary>
    /// <param name="profile">The profile to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(FilamentProfile profile, CancellationToken ct = default);

    /// <summary>Deletes all system profiles for a given slicer engine.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of profiles deleted.</returns>
    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);
}

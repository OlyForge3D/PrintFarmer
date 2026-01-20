using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Locations;

/// <summary>
/// Repository interface for location data access operations.
/// Provides CRUD and query operations for printer locations.
/// </summary>
public interface ILocationRepository
{
    /// <summary>
    /// Gets all active locations.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<Location>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Gets all locations including inactive ones.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<Location>> GetAllWithInactiveAsync(CancellationToken ct);

    /// <summary>
    /// Finds a location by ID.
    /// </summary>
    /// <param name="id">The location ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Location?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Finds a location by name.
    /// </summary>
    /// <param name="name">The location name to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Location?> FindByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Checks if a location with the given name exists.
    /// </summary>
    /// <param name="name">The location name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ExistsByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Adds a new location to the repository.
    /// </summary>
    /// <param name="location">The location to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(Location location, CancellationToken ct);

    /// <summary>
    /// Updates an existing location.
    /// </summary>
    /// <param name="location">The location to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(Location location, CancellationToken ct);

    /// <summary>
    /// Removes a location from the repository (hard delete).
    /// Note: Use UpdateAsync with IsActive=false for soft deletes.
    /// </summary>
    /// <param name="location">The location to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(Location location, CancellationToken ct);

    /// <summary>
    /// Gets all printers assigned to a location.
    /// </summary>
    /// <param name="locationId">The location ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<Printer>> GetPrintersInLocationAsync(Guid locationId, CancellationToken ct);

    /// <summary>
    /// Gets the count of printers assigned to a location.
    /// </summary>
    /// <param name="locationId">The location ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> GetPrinterCountAsync(Guid locationId, CancellationToken ct);

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct);
}

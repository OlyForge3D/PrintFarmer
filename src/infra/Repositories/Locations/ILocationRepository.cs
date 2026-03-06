using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Locations;

/// <summary>
/// Repository interface for location data access operations.
/// Provides CRUD, query, and tree operations for printer locations.
/// </summary>
public interface ILocationRepository
{
    /// <summary>
    /// Gets all active locations.
    /// </summary>
    Task<List<Location>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Gets all locations including inactive ones.
    /// </summary>
    Task<List<Location>> GetAllWithInactiveAsync(CancellationToken ct);

    /// <summary>
    /// Finds a location by ID.
    /// </summary>
    Task<Location?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Finds a location by ID with children eagerly loaded.
    /// </summary>
    Task<Location?> FindByIdWithChildrenAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Finds a location by name within a specific parent (or root if parentId is null).
    /// </summary>
    Task<Location?> FindByNameAndParentAsync(string name, Guid? parentId, CancellationToken ct);

    /// <summary>
    /// Finds a location by name.
    /// </summary>
    Task<Location?> FindByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Checks if a location with the given name exists under the given parent.
    /// </summary>
    Task<bool> ExistsByNameAndParentAsync(string name, Guid? parentId, CancellationToken ct);

    /// <summary>
    /// Checks if a location with the given name exists.
    /// </summary>
    Task<bool> ExistsByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Gets all active locations as a flat list with children loaded for tree building.
    /// </summary>
    Task<List<Location>> GetAllWithChildrenAsync(CancellationToken ct);

    /// <summary>
    /// Gets the children of a location.
    /// </summary>
    Task<List<Location>> GetChildrenAsync(Guid parentId, CancellationToken ct);

    /// <summary>
    /// Gets the ancestor chain for a location (from root to the location's parent).
    /// </summary>
    Task<List<Location>> GetAncestorsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Gets all descendants of a location (recursive).
    /// </summary>
    Task<List<Location>> GetDescendantsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Checks if a location is a descendant of another location.
    /// Used to prevent circular references when moving.
    /// </summary>
    Task<bool> IsDescendantOfAsync(Guid locationId, Guid potentialAncestorId, CancellationToken ct);

    /// <summary>
    /// Adds a new location to the repository.
    /// </summary>
    Task AddAsync(Location location, CancellationToken ct);

    /// <summary>
    /// Updates an existing location.
    /// </summary>
    Task UpdateAsync(Location location, CancellationToken ct);

    /// <summary>
    /// Removes a location from the repository (hard delete).
    /// Note: Use UpdateAsync with IsActive=false for soft deletes.
    /// </summary>
    Task RemoveAsync(Location location, CancellationToken ct);

    /// <summary>
    /// Gets all printers assigned to a location.
    /// </summary>
    Task<List<Printer>> GetPrintersInLocationAsync(Guid locationId, CancellationToken ct);

    /// <summary>
    /// Gets the count of printers assigned to a location.
    /// </summary>
    Task<int> GetPrinterCountAsync(Guid locationId, CancellationToken ct);

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct);
}

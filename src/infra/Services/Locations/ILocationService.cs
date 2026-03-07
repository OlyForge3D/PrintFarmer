using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Locations;

/// <summary>
/// Service for managing printer locations and their associated printers.
/// Provides CRUD, tree hierarchy, and printer management capabilities.
/// </summary>
public interface ILocationService
{
    Task<List<Location>> GetAllAsync(CancellationToken ct);

    Task<List<Location>> GetAllWithInactiveAsync(CancellationToken ct);

    Task<Location?> FindByIdAsync(Guid id, CancellationToken ct);

    Task<Location?> FindByNameAsync(string name, CancellationToken ct);

    Task<bool> ExistsByNameAsync(string name, CancellationToken ct);

    Task<LocationDto> CreateLocationAsync(CreateLocationDto dto, CancellationToken ct);

    Task<LocationDto?> UpdateLocationAsync(Guid id, UpdateLocationDto dto, CancellationToken ct);

    Task<bool> DeleteLocationAsync(Guid id, CancellationToken ct);

    Task<LocationDto[]> GetAllLocationDtosAsync(CancellationToken ct);

    Task<LocationDetailsDto?> GetLocationDetailsAsync(Guid id, CancellationToken ct);

    Task<List<Printer>> GetPrintersInLocationAsync(Guid locationId, CancellationToken ct);

    Task<bool> AssignPrinterToLocationAsync(Guid printerId, Guid locationId, CancellationToken ct);

    Task<bool> RemovePrinterFromLocationAsync(Guid printerId, CancellationToken ct);

    Task UpdatePrinterCountAsync(Guid locationId, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    // Tree hierarchy operations

    /// <summary>
    /// Gets the full location tree as nested DTOs.
    /// </summary>
    /// <param name="rootId">Optional root location ID to get a subtree. Null for full tree.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<LocationTreeDto>> GetTreeAsync(Guid? rootId, CancellationToken ct);

    /// <summary>
    /// Gets the ancestor chain for a location (for breadcrumbs).
    /// </summary>
    Task<List<LocationBreadcrumbDto>> GetAncestorsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Gets all descendants of a location (flat list).
    /// </summary>
    Task<List<LocationDto>> GetDescendantsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Moves a location to a new parent.
    /// Updates Path for all descendants.
    /// </summary>
    Task<LocationDto?> MoveAsync(Guid id, Guid? newParentId, CancellationToken ct);

    /// <summary>
    /// Gets all printers in a location's subtree (the location itself and all descendants).
    /// Includes real-time status from the printer status cache.
    /// </summary>
    Task<List<LocationSubtreePrinterDto>> GetSubtreePrintersAsync(Guid locationId, CancellationToken ct);
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Locations;

/// <summary>
/// Service for managing printer locations and their associated printers.
/// Provides CRUD operations, filtering, and printer management capabilities.
/// </summary>
public interface ILocationService
{
    /// <summary>
    /// Gets all active locations.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<Location>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Gets all locations including inactive ones.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<Location>> GetAllWithInactiveAsync(CancellationToken ct);

    /// <summary>
    /// Finds a location by its ID.
    /// </summary>
    /// <param name="id">The unique identifier of the location.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<Location?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Finds a location by name.
    /// </summary>
    /// <param name="name">The name of the location to find.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<Location?> FindByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Checks if a location with the given name already exists.
    /// </summary>
    /// <param name="name">The name to check for existence.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<bool> ExistsByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Creates a new location from a DTO.
    /// </summary>
    /// <param name="dto">The data transfer object containing location creation data.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<LocationDto> CreateLocationAsync(CreateLocationDto dto, CancellationToken ct);

    /// <summary>
    /// Updates an existing location.
    /// </summary>
    /// <param name="id">The unique identifier of the location to update.</param>
    /// <param name="dto">The data transfer object containing updated location data.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<LocationDto?> UpdateLocationAsync(Guid id, UpdateLocationDto dto, CancellationToken ct);

    /// <summary>
    /// Deletes a location (soft delete via IsActive flag).
    /// </summary>
    /// <param name="id">The unique identifier of the location to delete.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<bool> DeleteLocationAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Gets all locations as DTOs for API responses.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<LocationDto[]> GetAllLocationDtosAsync(CancellationToken ct);

    /// <summary>
    /// Gets a location with all its associated printers.
    /// </summary>
    /// <param name="id">The unique identifier of the location.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<LocationDetailsDto?> GetLocationDetailsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Gets all printers assigned to a location.
    /// </summary>
    /// <param name="locationId">The unique identifier of the location.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<Printer>> GetPrintersInLocationAsync(Guid locationId, CancellationToken ct);

    /// <summary>
    /// Assigns a printer to a location.
    /// Updates the printer's LocationId and maintains PrinterCount denormalization.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer to assign.</param>
    /// <param name="locationId">The unique identifier of the target location.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<bool> AssignPrinterToLocationAsync(Guid printerId, Guid locationId, CancellationToken ct);

    /// <summary>
    /// Removes a printer from its location.
    /// Sets LocationId to null and updates PrinterCount denormalization.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer to remove.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<bool> RemovePrinterFromLocationAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Updates the PrinterCount denormalization for a location.
    /// Should be called when printers are added/removed from a location.
    /// </summary>
    /// <param name="locationId">The unique identifier of the location to update.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task UpdatePrinterCountAsync(Guid locationId, CancellationToken ct);

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task SaveChangesAsync(CancellationToken ct);
}

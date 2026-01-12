using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Locations
{
    /// <summary>
    /// Service for managing printer locations and their associated printers.
    /// Provides CRUD operations, filtering, and printer management capabilities.
    /// </summary>
    public interface ILocationService
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
        /// Finds a location by its ID.
        /// </summary>
        Task<Location?> FindByIdAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Finds a location by name.
        /// </summary>
        Task<Location?> FindByNameAsync(string name, CancellationToken ct);

        /// <summary>
        /// Checks if a location with the given name already exists.
        /// </summary>
        Task<bool> ExistsByNameAsync(string name, CancellationToken ct);

        /// <summary>
        /// Creates a new location from a DTO.
        /// </summary>
        Task<LocationDto> CreateLocationAsync(CreateLocationDto dto, CancellationToken ct);

        /// <summary>
        /// Updates an existing location.
        /// </summary>
        Task<LocationDto?> UpdateLocationAsync(Guid id, UpdateLocationDto dto, CancellationToken ct);

        /// <summary>
        /// Deletes a location (soft delete via IsActive flag).
        /// </summary>
        Task<bool> DeleteLocationAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Gets all locations as DTOs for API responses.
        /// </summary>
        Task<LocationDto[]> GetAllLocationDtosAsync(CancellationToken ct);

        /// <summary>
        /// Gets a location with all its associated printers.
        /// </summary>
        Task<LocationDetailsDto?> GetLocationDetailsAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Gets all printers assigned to a location.
        /// </summary>
        Task<List<Printer>> GetPrintersInLocationAsync(Guid locationId, CancellationToken ct);

        /// <summary>
        /// Assigns a printer to a location.
        /// Updates the printer's LocationId and maintains PrinterCount denormalization.
        /// </summary>
        Task<bool> AssignPrinterToLocationAsync(Guid printerId, Guid locationId, CancellationToken ct);

        /// <summary>
        /// Removes a printer from its location.
        /// Sets LocationId to null and updates PrinterCount denormalization.
        /// </summary>
        Task<bool> RemovePrinterFromLocationAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Updates the PrinterCount denormalization for a location.
        /// Should be called when printers are added/removed from a location.
        /// </summary>
        Task UpdatePrinterCountAsync(Guid locationId, CancellationToken ct);

        /// <summary>
        /// Persists changes to the database.
        /// </summary>
        Task SaveChangesAsync(CancellationToken ct);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Locations;

/// <summary>
/// Service for managing printer locations.
/// Provides CRUD operations, filtering, and printer assignment capabilities.
/// </summary>
public class LocationService : ILocationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUnifiedLoggingService _logger;
    private readonly IMapper _mapper;

    public LocationService(
        IUnitOfWork unitOfWork,
        IUnifiedLoggingService logger,
        IMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mapper);

        _unitOfWork = unitOfWork;
        _logger = logger;
        _mapper = mapper;
    }

    /// <summary>
    /// Gets all active locations.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<List<Location>> GetAllAsync(CancellationToken ct)
    {
        try
        {
            return await _unitOfWork.Locations.GetAllAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving locations");
            throw;
        }
    }

    /// <summary>
    /// Gets all locations including inactive ones.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<List<Location>> GetAllWithInactiveAsync(CancellationToken ct)
    {
        try
        {
            return await _unitOfWork.Locations.GetAllWithInactiveAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all locations with inactive");
            throw;
        }
    }

    /// <summary>
    /// Finds a location by its ID.
    /// </summary>
    /// <param name="id">The unique identifier of the location.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<Location?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(id));
        }

        try
        {
            return await _unitOfWork.Locations.FindByIdAsync(id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error finding location with ID {id}");
            throw;
        }
    }

    /// <summary>
    /// Finds a location by name.
    /// </summary>
    /// <param name="name">The name of the location to find.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<Location?> FindByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Location name cannot be empty", nameof(name));
        }

        try
        {
            return await _unitOfWork.Locations.FindByNameAsync(name, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error finding location with name '{name}'");
            throw;
        }
    }

    /// <summary>
    /// Checks if a location with the given name already exists.
    /// </summary>
    /// <param name="name">The name to check for existence.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<bool> ExistsByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Location name cannot be empty", nameof(name));
        }

        try
        {
            return await _unitOfWork.Locations.ExistsByNameAsync(name, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error checking location name existence for '{name}'");
            throw;
        }
    }

    /// <summary>
    /// Creates a new location from a DTO.
    /// </summary>
    /// <param name="dto">The data transfer object containing location creation data.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<LocationDto> CreateLocationAsync(CreateLocationDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Location name is required", nameof(dto));
        }

        try
        {
            // Check for duplicate name
            if (await ExistsByNameAsync(dto.Name.Trim(), ct))
            {
                throw new InvalidOperationException($"A location with the name '{dto.Name}' already exists.");
            }

            var location = new Location
            {
                Id = Guid.NewGuid(),
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim(),
                PrinterCount = 0,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow
            };

            await _unitOfWork.Locations.AddAsync(location, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation($"Location '{location.Name}' created successfully");

            return _mapper.Map<LocationDto>(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating location");
            throw;
        }
    }

    /// <summary>
    /// Updates an existing location.
    /// </summary>
    /// <param name="id">The unique identifier of the location to update.</param>
    /// <param name="dto">The data transfer object containing updated location data.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<LocationDto?> UpdateLocationAsync(Guid id, UpdateLocationDto dto, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(dto);

        try
        {
            Location? location = await FindByIdAsync(id, ct);
            if (location == null)
            {
                return null;
            }

            // Check for name duplicate if name is being changed
            if (!string.IsNullOrWhiteSpace(dto.Name) && dto.Name.Trim() != location.Name)
            {
                if (await ExistsByNameAsync(dto.Name.Trim(), ct))
                {
                    throw new InvalidOperationException($"A location with the name '{dto.Name}' already exists.");
                }
            }

            // Update only provided fields
            if (!string.IsNullOrWhiteSpace(dto.Name))
            {
                location.Name = dto.Name.Trim();
            }

            if (dto.Description != null)
            {
                location.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
            }

            await _unitOfWork.Locations.UpdateAsync(location, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation($"Location '{location.Name}' updated successfully");

            return _mapper.Map<LocationDto>(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error updating location with ID {id}");
            throw;
        }
    }

    /// <summary>
    /// Deletes a location (soft delete via IsActive flag).
    /// </summary>
    /// <param name="id">The unique identifier of the location to delete.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<bool> DeleteLocationAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(id));
        }

        try
        {
            Location? location = await FindByIdAsync(id, ct);
            if (location == null)
            {
                return false;
            }

            location.IsActive = false;
            location.ModifiedAt = DateTime.UtcNow;

            await _unitOfWork.Locations.UpdateAsync(location, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation($"Location '{location.Name}' deleted (soft delete)");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error deleting location with ID {id}");
            throw;
        }
    }

    /// <summary>
    /// Gets all locations as DTOs for API responses.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<LocationDto[]> GetAllLocationDtosAsync(CancellationToken ct)
    {
        try
        {
            List<Location> locations = await GetAllAsync(ct);
            return _mapper.Map<LocationDto[]>(locations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving location DTOs");
            throw;
        }
    }

    /// <summary>
    /// Gets a location with all its associated printers.
    /// </summary>
    /// <param name="id">The unique identifier of the location.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<LocationDetailsDto?> GetLocationDetailsAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(id));
        }

        try
        {
            Location? location = await FindByIdAsync(id, ct);
            if (location == null)
            {
                return null;
            }

            List<Printer> printers = await GetPrintersInLocationAsync(id, ct);

            LocationDetailsDto detailsDto = _mapper.Map<LocationDetailsDto>(location);
            detailsDto.Printers = _mapper.Map<PrinterInfoDto[]>(printers);

            return detailsDto;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error retrieving location details for ID {id}");
            throw;
        }
    }

    /// <summary>
    /// Gets all printers assigned to a location.
    /// </summary>
    /// <param name="locationId">The unique identifier of the location.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<List<Printer>> GetPrintersInLocationAsync(Guid locationId, CancellationToken ct)
    {
        if (locationId == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(locationId));
        }

        try
        {
            return await _unitOfWork.Locations.GetPrintersInLocationAsync(locationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error retrieving printers for location {locationId}");
            throw;
        }
    }

    /// <summary>
    /// Assigns a printer to a location.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer to assign.</param>
    /// <param name="locationId">The unique identifier of the target location.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<bool> AssignPrinterToLocationAsync(Guid printerId, Guid locationId, CancellationToken ct)
    {
        if (printerId == Guid.Empty)
        {
            throw new ArgumentException("Printer ID cannot be empty", nameof(printerId));
        }

        if (locationId == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(locationId));
        }

        try
        {
            // Verify location exists
            Location location = await FindByIdAsync(locationId, ct) ?? throw new KeyNotFoundException($"Location with ID {locationId} not found");

            // In a real implementation, we would update the printer entity
            // This requires access to the printer repository or context
            // For now, just update the location's printer count
            await UpdatePrinterCountAsync(locationId, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error assigning printer {printerId} to location {locationId}");
            throw;
        }
    }

    /// <summary>
    /// Removes a printer from its location.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer to remove.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<bool> RemovePrinterFromLocationAsync(Guid printerId, CancellationToken ct)
    {
        if (printerId == Guid.Empty)
        {
            throw new ArgumentException("Printer ID cannot be empty", nameof(printerId));
        }

        try
        {
            // In a real implementation, we would update the printer entity
            // This requires access to the printer repository or context
            // For now, this is a placeholder for the logic
            await Task.CompletedTask;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error removing printer {printerId} from location");
            throw;
        }
    }

    /// <summary>
    /// Updates the PrinterCount denormalization for a location.
    /// </summary>
    /// <param name="locationId">The unique identifier of the location to update.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task UpdatePrinterCountAsync(Guid locationId, CancellationToken ct)
    {
        if (locationId == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(locationId));
        }

        try
        {
            Location? location = await FindByIdAsync(locationId, ct);
            if (location == null)
            {
                return;
            }

            int count = await _unitOfWork.Locations.GetPrinterCountAsync(locationId, ct);
            location.PrinterCount = count;

            await _unitOfWork.Locations.UpdateAsync(location, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation($"Updated printer count for location '{location.Name}': {count}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error updating printer count for location {locationId}");
            throw;
        }
    }

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving location changes");
            throw;
        }
    }
}

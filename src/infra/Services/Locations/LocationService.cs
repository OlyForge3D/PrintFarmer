using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Locations;

/// <summary>
/// Service for managing printer locations with hierarchy support.
/// Provides CRUD, tree operations, filtering, and printer assignment.
/// </summary>
public class LocationService : ILocationService
{
    private const int MaxDepth = 10;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<LocationService> _logger;
    private readonly IMapper _mapper;

    public LocationService(
        IUnitOfWork unitOfWork,
        ILogger<LocationService> logger,
        IMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mapper);

        _unitOfWork = unitOfWork;
        _logger = logger;
        _mapper = mapper;
    }

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
            _logger.LogError(ex, "Error finding location with ID {Id}", id);
            throw;
        }
    }

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
            _logger.LogError(ex, "Error finding location with name '{Name}'", name);
            throw;
        }
    }

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
            _logger.LogError(ex, "Error checking location name existence for '{Name}'", name);
            throw;
        }
    }

    public async Task<LocationDto> CreateLocationAsync(CreateLocationDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Location name is required", nameof(dto));
        }

        try
        {
            string trimmedName = dto.Name.Trim();

            // Validate parent exists if specified
            int parentDepth = 0;
            string parentPath = string.Empty;
            if (dto.ParentId is not null)
            {
                Location parent = await FindByIdAsync(dto.ParentId.Value, ct)
                    ?? throw new KeyNotFoundException($"Parent location with ID {dto.ParentId} not found");

                parentDepth = parent.Depth;
                parentPath = parent.Path;

                if (parentDepth + 1 >= MaxDepth)
                {
                    throw new InvalidOperationException($"Cannot create location: maximum depth of {MaxDepth} would be exceeded.");
                }
            }

            // Check for duplicate name under same parent
            if (await _unitOfWork.Locations.ExistsByNameAndParentAsync(trimmedName, dto.ParentId, ct))
            {
                throw new InvalidOperationException($"A location with the name '{trimmedName}' already exists under this parent.");
            }

            int depth = dto.ParentId is not null ? parentDepth + 1 : 0;
            string path = dto.ParentId is not null
                ? $"{parentPath}/{trimmedName}"
                : $"/{trimmedName}";

            var location = new Location
            {
                Id = Guid.NewGuid(),
                Name = trimmedName,
                Description = dto.Description?.Trim(),
                ParentId = dto.ParentId,
                Path = path,
                Depth = depth,
                SortOrder = dto.SortOrder ?? 0,
                PrinterCount = 0,
                TotalPrinterCount = 0,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow
            };

            await _unitOfWork.Locations.AddAsync(location, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Location '{LocationName}' created at path '{Path}'", location.Name, location.Path);

            return _mapper.Map<LocationDto>(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating location");
            throw;
        }
    }

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
            if (location is null)
            {
                return null;
            }

            bool nameChanged = false;

            // Check for name duplicate under same parent if name is being changed
            if (!string.IsNullOrWhiteSpace(dto.Name) && dto.Name.Trim() != location.Name)
            {
                Guid? effectiveParentId = dto.ParentId ?? location.ParentId;
                if (await _unitOfWork.Locations.ExistsByNameAndParentAsync(dto.Name.Trim(), effectiveParentId, ct))
                {
                    throw new InvalidOperationException($"A location with the name '{dto.Name}' already exists under this parent.");
                }

                location.Name = dto.Name.Trim();
                nameChanged = true;
            }

            if (dto.Description is not null)
            {
                location.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
            }

            if (dto.SortOrder is not null)
            {
                location.SortOrder = dto.SortOrder.Value;
            }

            // If name changed, rebuild path for this location and all descendants
            if (nameChanged)
            {
                await RebuildPathAsync(location, ct);
            }

            await _unitOfWork.Locations.UpdateAsync(location, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Location '{LocationName}' updated successfully", location.Name);

            return _mapper.Map<LocationDto>(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating location with ID {Id}", id);
            throw;
        }
    }

    public async Task<bool> DeleteLocationAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(id));
        }

        try
        {
            Location? location = await FindByIdAsync(id, ct);
            if (location is null)
            {
                return false;
            }

            // Cannot delete a location that has active children
            List<Location> children = await _unitOfWork.Locations.GetChildrenAsync(id, ct);
            if (children.Count > 0)
            {
                throw new InvalidOperationException("Cannot delete a location that has child locations. Remove or move children first.");
            }

            location.IsActive = false;
            location.ModifiedAt = DateTime.UtcNow;

            await _unitOfWork.Locations.UpdateAsync(location, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Location '{LocationName}' deleted (soft delete)", location.Name);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting location with ID {Id}", id);
            throw;
        }
    }

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

    public async Task<LocationDetailsDto?> GetLocationDetailsAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(id));
        }

        try
        {
            Location? location = await FindByIdAsync(id, ct);
            if (location is null)
            {
                return null;
            }

            List<Printer> printers = await GetPrintersInLocationAsync(id, ct);

            LocationDetailsDto detailsDto = _mapper.Map<LocationDetailsDto>(location);
            detailsDto.Printers = _mapper.Map<DiscoveryPrinterInfoDto[]>(printers);

            return detailsDto;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving location details for ID {Id}", id);
            throw;
        }
    }

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
            _logger.LogError(ex, "Error retrieving printers for location {LocationId}", locationId);
            throw;
        }
    }

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
            Location location = await FindByIdAsync(locationId, ct) ?? throw new KeyNotFoundException($"Location with ID {locationId} not found");

            await UpdatePrinterCountAsync(locationId, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error assigning printer {PrinterId} to location {LocationId}", printerId, locationId);
            throw;
        }
    }

    public async Task<bool> RemovePrinterFromLocationAsync(Guid printerId, CancellationToken ct)
    {
        if (printerId == Guid.Empty)
        {
            throw new ArgumentException("Printer ID cannot be empty", nameof(printerId));
        }

        try
        {
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing printer {PrinterId} from location", printerId);
            throw;
        }
    }

    public async Task UpdatePrinterCountAsync(Guid locationId, CancellationToken ct)
    {
        if (locationId == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(locationId));
        }

        try
        {
            Location? location = await FindByIdAsync(locationId, ct);
            if (location is null)
            {
                return;
            }

            int count = await _unitOfWork.Locations.GetPrinterCountAsync(locationId, ct);
            location.PrinterCount = count;

            // Calculate TotalPrinterCount (this location + all descendants)
            List<Location> descendants = await _unitOfWork.Locations.GetDescendantsAsync(locationId, ct);
            int totalCount = count;
            foreach (Location desc in descendants)
            {
                totalCount += await _unitOfWork.Locations.GetPrinterCountAsync(desc.Id, ct);
            }

            location.TotalPrinterCount = totalCount;

            await _unitOfWork.Locations.UpdateAsync(location, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Updated printer count for location '{LocationName}': {Count} (total: {TotalCount})", location.Name, count, totalCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating printer count for location {LocationId}", locationId);
            throw;
        }
    }

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

    // ============ Tree hierarchy operations ============
    public async Task<List<LocationTreeDto>> GetTreeAsync(Guid? rootId, CancellationToken ct)
    {
        try
        {
            List<Location> allLocations = await _unitOfWork.Locations.GetAllAsync(ct);

            if (rootId is not null)
            {
                // Build subtree from rootId
                List<Location> descendants = await _unitOfWork.Locations.GetDescendantsAsync(rootId.Value, ct);
                Location? root = allLocations.FirstOrDefault(l => l.Id == rootId.Value);
                if (root is null)
                {
                    return [];
                }

                return [BuildTreeNode(root, descendants)];
            }

            // Build full tree from root locations
            List<Location> roots = allLocations.Where(l => l.ParentId is null).ToList();
            List<Location> nonRoots = allLocations.Where(l => l.ParentId is not null).ToList();

            return roots
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => r.Name)
                .Select(r => BuildTreeNode(r, nonRoots))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building location tree");
            throw;
        }
    }

    public async Task<List<LocationBreadcrumbDto>> GetAncestorsAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(id));
        }

        try
        {
            List<Location> ancestors = await _unitOfWork.Locations.GetAncestorsAsync(id, ct);
            return ancestors.Select(a => new LocationBreadcrumbDto(a.Id, a.Name)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ancestors for location {Id}", id);
            throw;
        }
    }

    public async Task<List<LocationDto>> GetDescendantsAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(id));
        }

        try
        {
            List<Location> descendants = await _unitOfWork.Locations.GetDescendantsAsync(id, ct);
            return _mapper.Map<List<LocationDto>>(descendants);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting descendants for location {Id}", id);
            throw;
        }
    }

    public async Task<LocationDto?> MoveAsync(Guid id, Guid? newParentId, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Location ID cannot be empty", nameof(id));
        }

        try
        {
            Location? location = await FindByIdAsync(id, ct);
            if (location is null)
            {
                return null;
            }

            // Can't move to itself
            if (newParentId == id)
            {
                throw new InvalidOperationException("A location cannot be its own parent.");
            }

            // Validate new parent exists
            if (newParentId is not null)
            {
                Location? newParent = await FindByIdAsync(newParentId.Value, ct)
                    ?? throw new KeyNotFoundException($"New parent location with ID {newParentId} not found");

                // Prevent circular references
                if (await _unitOfWork.Locations.IsDescendantOfAsync(newParentId.Value, id, ct))
                {
                    throw new InvalidOperationException("Cannot move a location to one of its own descendants.");
                }

                // Check depth limit
                int newDepth = newParent.Depth + 1;
                List<Location> descendants = await _unitOfWork.Locations.GetDescendantsAsync(id, ct);
                int maxDescendantDepth = descendants.Count > 0 ? descendants.Max(d => d.Depth) - location.Depth : 0;
                if (newDepth + maxDescendantDepth >= MaxDepth)
                {
                    throw new InvalidOperationException($"Cannot move location: maximum depth of {MaxDepth} would be exceeded.");
                }

                // Check name uniqueness under new parent
                if (await _unitOfWork.Locations.ExistsByNameAndParentAsync(location.Name, newParentId, ct))
                {
                    throw new InvalidOperationException($"A location with the name '{location.Name}' already exists under the target parent.");
                }
            }

            location.ParentId = newParentId;

            // Rebuild path for this location and all descendants
            await RebuildPathAsync(location, ct);

            await _unitOfWork.Locations.UpdateAsync(location, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Location '{LocationName}' moved to parent {NewParentId}", location.Name, newParentId?.ToString() ?? "root");

            return _mapper.Map<LocationDto>(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving location {Id} to parent {NewParentId}", id, newParentId);
            throw;
        }
    }

    // ============ Private helpers ============
    private LocationTreeDto BuildTreeNode(Location location, List<Location> allLocations)
    {
        List<Location> children = allLocations
            .Where(l => l.ParentId == location.Id)
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .ToList();

        return new LocationTreeDto
        {
            Id = location.Id,
            Name = location.Name,
            Description = location.Description,
            ParentId = location.ParentId,
            Path = location.Path,
            Depth = location.Depth,
            SortOrder = location.SortOrder,
            PrinterCount = location.PrinterCount,
            TotalPrinterCount = location.TotalPrinterCount,
            Children = children.Select(c => BuildTreeNode(c, allLocations)).ToList()
        };
    }

    private async Task RebuildPathAsync(Location location, CancellationToken ct)
    {
        // Compute this location's new path
        if (location.ParentId is null)
        {
            location.Path = $"/{location.Name}";
            location.Depth = 0;
        }
        else
        {
            Location? parent = await FindByIdAsync(location.ParentId.Value, ct);
            if (parent is not null)
            {
                location.Path = $"{parent.Path}/{location.Name}";
                location.Depth = parent.Depth + 1;
            }
        }

        // Recursively update all descendants
        List<Location> children = await _unitOfWork.Locations.GetChildrenAsync(location.Id, ct);
        foreach (Location child in children)
        {
            child.Path = $"{location.Path}/{child.Name}";
            child.Depth = location.Depth + 1;
            await _unitOfWork.Locations.UpdateAsync(child, ct);
            await RebuildPathAsync(child, ct);
        }
    }
}

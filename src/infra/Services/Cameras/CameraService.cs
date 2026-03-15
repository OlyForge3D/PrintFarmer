using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Service for managing standalone cameras and aggregating all camera feeds.
/// </summary>
public class CameraService : ICameraService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CameraService> _logger;
    private readonly IMapper _mapper;
    private readonly IPrintersService _printersService;

    public CameraService(
        IUnitOfWork unitOfWork,
        ILogger<CameraService> logger,
        IMapper mapper,
        IPrintersService printersService)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(printersService);

        _unitOfWork = unitOfWork;
        _logger = logger;
        _mapper = mapper;
        _printersService = printersService;
    }

    /// <inheritdoc />
    public async Task<List<Camera>> GetAllAsync(CancellationToken ct)
    {
        try
        {
            return await _unitOfWork.Cameras.GetAllAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cameras");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<Camera>> GetEnabledAsync(CancellationToken ct)
    {
        try
        {
            return await _unitOfWork.Cameras.GetEnabledAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving enabled cameras");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Camera?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Camera ID cannot be empty", nameof(id));
        }

        try
        {
            return await _unitOfWork.Cameras.FindByIdAsync(id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding camera with ID {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Camera?> FindByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Camera name cannot be empty", nameof(name));
        }

        try
        {
            return await _unitOfWork.Cameras.FindByNameAsync(name, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding camera with name {Name}", name);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            return await _unitOfWork.Cameras.ExistsByNameAsync(name, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking camera existence by name {Name}", name);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto> CreateAsync(CreateCameraDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Camera name is required", nameof(dto));
        }

        try
        {
            // Check for duplicate name
            if (await ExistsByNameAsync(dto.Name, ct))
            {
                throw new InvalidOperationException($"A camera with name '{dto.Name}' already exists");
            }

            // Validate PrinterId if provided
            if (dto.PrinterId.HasValue)
            {
                Printer? printer = await _printersService.FindByIdAsync(dto.PrinterId.Value, ct);
                if (printer == null)
                {
                    throw new InvalidOperationException($"Printer with ID '{dto.PrinterId.Value}' not found");
                }
            }

            Camera camera = new()
            {
                Id = Guid.NewGuid(),
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim(),
                StreamUrl = dto.StreamUrl?.Trim(),
                SnapshotUrl = dto.SnapshotUrl?.Trim(),
                IsEnabled = dto.IsEnabled,
                SortOrder = dto.SortOrder,
                Location = dto.Location?.Trim(),
                PrinterId = dto.PrinterId,
                Source = dto.Source ?? CameraSource.Standalone,
                CameraType = dto.CameraType ?? CameraType.General,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _unitOfWork.Cameras.Add(camera);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Created camera {CameraName} with ID {CameraId}", camera.Name, camera.Id);

            return MapToDto(camera);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error creating camera {DtoName}", dto.Name);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto?> UpdateAsync(Guid id, UpdateCameraDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Camera ID cannot be empty", nameof(id));
        }

        try
        {
            Camera? camera = await FindByIdAsync(id, ct);
            if (camera == null)
            {
                return null;
            }

            // Check for duplicate name if name is being changed
            if (!string.IsNullOrWhiteSpace(dto.Name) &&
                !dto.Name.Equals(camera.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (await ExistsByNameAsync(dto.Name, ct))
                {
                    throw new InvalidOperationException($"A camera with name '{dto.Name}' already exists");
                }

                camera.Name = dto.Name.Trim();
            }

            if (dto.Description != null)
            {
                camera.Description = dto.Description.Trim();
            }

            if (dto.StreamUrl != null)
            {
                camera.StreamUrl = string.IsNullOrWhiteSpace(dto.StreamUrl) ? null : dto.StreamUrl.Trim();
            }

            if (dto.SnapshotUrl != null)
            {
                camera.SnapshotUrl = string.IsNullOrWhiteSpace(dto.SnapshotUrl) ? null : dto.SnapshotUrl.Trim();
            }

            if (dto.IsEnabled.HasValue)
            {
                camera.IsEnabled = dto.IsEnabled.Value;
            }

            if (dto.SortOrder.HasValue)
            {
                camera.SortOrder = dto.SortOrder.Value;
            }

            if (dto.Location != null)
            {
                camera.Location = string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim();
            }

            if (dto.Source.HasValue)
            {
                camera.Source = dto.Source.Value;
            }

            if (dto.CameraType.HasValue)
            {
                camera.CameraType = dto.CameraType.Value;
            }

            // Validate and update PrinterId if provided
            if (dto.PrinterId != camera.PrinterId)
            {
                if (dto.PrinterId.HasValue)
                {
                    Printer? printer = await _printersService.FindByIdAsync(dto.PrinterId.Value, ct);
                    if (printer == null)
                    {
                        throw new InvalidOperationException($"Printer with ID '{dto.PrinterId.Value}' not found");
                    }
                }

                camera.PrinterId = dto.PrinterId;
            }

            camera.UpdatedAt = DateTime.UtcNow;

            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Updated camera {CameraName} with ID {CameraId}", camera.Name, camera.Id);

            return MapToDto(camera);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error updating camera {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Camera ID cannot be empty", nameof(id));
        }

        try
        {
            Camera? camera = await FindByIdAsync(id, ct);
            if (camera == null)
            {
                return false;
            }

            _unitOfWork.Cameras.Remove(camera);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Deleted camera {CameraName} with ID {CameraId}", camera.Name, camera.Id);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting camera {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto?> ToggleEnabledAsync(Guid id, bool isEnabled, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Camera ID cannot be empty", nameof(id));
        }

        try
        {
            Camera? camera = await FindByIdAsync(id, ct);
            if (camera == null)
            {
                return null;
            }

            camera.IsEnabled = isEnabled;
            camera.UpdatedAt = DateTime.UtcNow;

            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Toggled camera {CameraName} enabled status to {IsEnabled}", camera.Name, isEnabled);

            return MapToDto(camera);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling camera {Id} enabled status", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto[]> GetAllDtosAsync(CancellationToken ct)
    {
        try
        {
            List<Camera> cameras = await GetAllAsync(ct);
            return _mapper.Map<CameraDto[]>(cameras);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all camera DTOs");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto[]> GetEnabledCamerasAsync(CancellationToken ct)
    {
        try
        {
            List<Camera> cameras = await GetEnabledAsync(ct);
            return _mapper.Map<CameraDto[]>(cameras);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting enabled camera DTOs");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<CameraDto>> GetByPrinterIdAsync(Guid printerId, CancellationToken ct)
    {
        if (printerId == Guid.Empty)
        {
            throw new ArgumentException("Printer ID cannot be empty", nameof(printerId));
        }

        try
        {
            List<Camera> cameras = await _unitOfWork.Cameras.GetByPrinterIdAsync(printerId, ct);
            return cameras.Select(MapToDto).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cameras for printer {PrinterId}", printerId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto> CreateForPrinterAsync(Guid printerId, CreateCameraDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (printerId == Guid.Empty)
        {
            throw new ArgumentException("Printer ID cannot be empty", nameof(printerId));
        }

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Camera name is required", nameof(dto));
        }

        try
        {
            // Validate printer exists
            Printer? printer = await _printersService.FindByIdAsync(printerId, ct);
            if (printer == null)
            {
                throw new InvalidOperationException($"Printer with ID '{printerId}' not found");
            }

            // Check for duplicate name
            if (await ExistsByNameAsync(dto.Name, ct))
            {
                throw new InvalidOperationException($"A camera with name '{dto.Name}' already exists");
            }

            Camera camera = new()
            {
                Id = Guid.NewGuid(),
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim(),
                StreamUrl = dto.StreamUrl?.Trim(),
                SnapshotUrl = dto.SnapshotUrl?.Trim(),
                IsEnabled = dto.IsEnabled,
                SortOrder = dto.SortOrder,
                Location = dto.Location?.Trim(),
                PrinterId = printerId,
                Source = dto.Source ?? CameraSource.Standalone,
                CameraType = dto.CameraType ?? CameraType.General,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _unitOfWork.Cameras.Add(camera);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Created camera {CameraName} with ID {CameraId} for printer {PrinterId}", camera.Name, camera.Id, printerId);

            return MapToDto(camera);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error creating camera {DtoName} for printer {PrinterId}", dto.Name, printerId);
            throw;
        }
    }

    /// <summary>
    /// Maps a Camera entity to CameraDto with all new fields.
    /// </summary>
    private static CameraDto MapToDto(Camera camera)
    {
        return new CameraDto
        {
            Id = camera.Id,
            Name = camera.Name,
            Description = camera.Description,
            StreamUrl = camera.StreamUrl,
            SnapshotUrl = camera.SnapshotUrl,
            IsEnabled = camera.IsEnabled,
            SortOrder = camera.SortOrder,
            Location = camera.Location,
            CreatedAt = camera.CreatedAt,
            UpdatedAt = camera.UpdatedAt,
            PrinterId = camera.PrinterId,
            Source = camera.Source,
            CameraType = camera.CameraType,
            HealthStatus = camera.HealthStatus,
            LastHealthCheck = camera.LastHealthCheck,
            IsStandalone = !camera.PrinterId.HasValue
        };
    }

    /// <inheritdoc />
    public async Task<List<DisplayCameraDto>> GetDisplayCamerasAsync(CancellationToken ct)
    {
        try
        {
            List<Camera> cameras = await _unitOfWork.Cameras.GetEnabledWithPrinterAsync(ct);
            return cameras.Select(c => new DisplayCameraDto
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                StreamUrl = c.StreamUrl,
                SnapshotUrl = c.SnapshotUrl,
                IsEnabled = c.IsEnabled,
                SortOrder = c.SortOrder,
                Location = c.Location,
                PrinterId = c.PrinterId,
                PrinterName = c.Printer?.Name,
                IsStandalone = !c.PrinterId.HasValue,
                Source = c.Source,
                CameraType = c.CameraType,
                HealthStatus = c.HealthStatus
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting display cameras");
            throw;
        }
    }
}

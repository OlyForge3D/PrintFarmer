using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Service for managing standalone cameras and aggregating all camera feeds.
/// </summary>
public class CameraService : ICameraService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUnifiedLoggingService _logger;
    private readonly IMapper _mapper;
    private readonly IPrintersService _printersService;

    public CameraService(
        IUnitOfWork unitOfWork,
        IUnifiedLoggingService logger,
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
            _logger.LogError(ex, $"Error finding camera with ID {id}");
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
            _logger.LogError(ex, $"Error finding camera with name {name}");
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
            _logger.LogError(ex, $"Error checking camera existence by name {name}");
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
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _unitOfWork.Cameras.Add(camera);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Created camera {CameraName} with ID {CameraId}", camera.Name, camera.Id);

            return _mapper.Map<CameraDto>(camera);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, $"Error creating camera {dto.Name}");
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

            camera.UpdatedAt = DateTime.UtcNow;

            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Updated camera {CameraName} with ID {CameraId}", camera.Name, camera.Id);

            return _mapper.Map<CameraDto>(camera);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, $"Error updating camera {id}");
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
            _logger.LogError(ex, $"Error deleting camera {id}");
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

            return _mapper.Map<CameraDto>(camera);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error toggling camera {id} enabled status");
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
}

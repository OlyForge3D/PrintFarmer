using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Service for managing standalone cameras and aggregating all camera feeds.
/// Provides CRUD operations for standalone cameras and retrieves combined
/// camera feeds from both standalone cameras and printer-attached cameras.
/// </summary>
public interface ICameraService
{
    /// <summary>
    /// Gets all standalone cameras.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<Camera>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Gets all enabled standalone cameras.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<Camera>> GetEnabledAsync(CancellationToken ct);

    /// <summary>
    /// Finds a camera by its ID.
    /// </summary>
    /// <param name="id">The unique identifier of the camera.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<Camera?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Finds a camera by name.
    /// </summary>
    /// <param name="name">The name of the camera to find.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<Camera?> FindByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Checks if a camera with the given name already exists.
    /// </summary>
    /// <param name="name">The name to check for existence.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<bool> ExistsByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Creates a new standalone camera from a DTO.
    /// </summary>
    /// <param name="dto">The data transfer object containing camera creation data.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<CameraDto> CreateAsync(CreateCameraDto dto, CancellationToken ct);

    /// <summary>
    /// Updates an existing camera.
    /// </summary>
    /// <param name="id">The unique identifier of the camera to update.</param>
    /// <param name="dto">The data transfer object containing updated camera data.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<CameraDto?> UpdateAsync(Guid id, UpdateCameraDto dto, CancellationToken ct);

    /// <summary>
    /// Deletes a camera by its ID.
    /// </summary>
    /// <param name="id">The unique identifier of the camera to delete.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Toggles the enabled status of a camera.
    /// </summary>
    /// <param name="id">The unique identifier of the camera.</param>
    /// <param name="isEnabled">The new enabled status.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<CameraDto?> ToggleEnabledAsync(Guid id, bool isEnabled, CancellationToken ct);

    /// <summary>
    /// Gets all cameras as DTOs.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<CameraDto[]> GetAllDtosAsync(CancellationToken ct);

    /// <summary>
    /// Gets all enabled cameras as DTOs.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<CameraDto[]> GetEnabledCamerasAsync(CancellationToken ct);

    /// <summary>
    /// Gets all cameras attached to a specific printer.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<CameraDto>> GetByPrinterIdAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Creates a new camera attached to a specific printer.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer.</param>
    /// <param name="dto">The data transfer object containing camera creation data.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<CameraDto> CreateForPrinterAsync(Guid printerId, CreateCameraDto dto, CancellationToken ct);

    /// <summary>
    /// Gets all enabled cameras (standalone and printer-attached) with printer names resolved.
    /// Used for the Camera View display page.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<List<DisplayCameraDto>> GetDisplayCamerasAsync(CancellationToken ct);
}

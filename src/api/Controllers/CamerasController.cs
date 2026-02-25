using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Startup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Camera = Farm.Infrastructure.Domain.Camera;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing standalone cameras and retrieving all camera feeds.
/// Cameras can be standalone webcams or attached to printers (Moonraker, PrusaLink, etc.).
/// </summary>
[ApiController]
[Route("api/cameras")]
[Tags("Cameras")]
[Authorize]
public class CamerasController(
    ICameraService cameraService,
    IStartupStatus startupStatus,
    ILogger<CamerasController> logger) : ControllerBase
{
    private readonly ICameraService _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
    private readonly IStartupStatus _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
    private readonly ILogger<CamerasController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Gets all standalone cameras.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all standalone cameras</returns>
    /// <response code="200">Returns the list of cameras</response>
    /// <response code="503">If the system is still initializing</response>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<CameraDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<CameraDto>>> GetCamerasAsync(CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            CameraDto[] cameras = await _cameraService.GetAllDtosAsync(ct);
            return Ok(cameras);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in GetCamerasAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Gets all enabled standalone cameras.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of enabled standalone cameras</returns>
    /// <response code="200">Returns the list of enabled cameras</response>
    /// <response code="503">If the system is still initializing</response>
    [AllowAnonymous]
    [HttpGet("enabled")]
    [ProducesResponseType(typeof(IEnumerable<CameraDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<CameraDto>>> GetEnabledCamerasAsync(CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            CameraDto[] cameras = await _cameraService.GetEnabledCamerasAsync(ct);
            return Ok(cameras);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in GetEnabledCamerasAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Gets a specific camera by ID.
    /// </summary>
    /// <param name="id">The camera ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The camera details</returns>
    /// <response code="200">Returns the camera</response>
    /// <response code="404">If the camera is not found</response>
    /// <response code="503">If the system is still initializing</response>
    [AllowAnonymous]
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CameraDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<CameraDto>> GetCameraAsync(Guid id, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            Camera? camera = await _cameraService.FindByIdAsync(id, ct);
            if (camera == null)
            {
                return NotFound(new { message = "Camera not found" });
            }

            // Map manually since we have the entity
            CameraDto dto = new()
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
                IsStandalone = true
            };
            return Ok(dto);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in GetCameraAsync for ID {CameraId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Creates a new standalone camera.
    /// </summary>
    /// <param name="request">The camera creation request</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The created camera</returns>
    /// <response code="201">Returns the created camera</response>
    /// <response code="400">If the request is invalid</response>
    /// <response code="503">If the system is still initializing</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost]
    [ProducesResponseType(typeof(CameraDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<CameraDto>> CreateCameraAsync(CreateCameraDto request, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (request == null)
            {
                return BadRequest(new { message = "Request body cannot be null" });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { message = "Camera name is required" });
            }

            CameraDto camera = await _cameraService.CreateAsync(request, ct);
            return StatusCode(201, camera);
        }
        catch (InvalidOperationException ex)
        {
            if (ex.Message.Contains("already exists"))
            {
                return BadRequest(new { message = ex.Message });
            }

            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in CreateCameraAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Updates an existing camera.
    /// </summary>
    /// <param name="id">The camera ID</param>
    /// <param name="request">The camera update request</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The updated camera</returns>
    /// <response code="200">Returns the updated camera</response>
    /// <response code="404">If the camera is not found</response>
    /// <response code="503">If the system is still initializing</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(CameraDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<CameraDto>> UpdateCameraAsync(Guid id, UpdateCameraDto request, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (request == null)
            {
                return BadRequest(new { message = "Request body cannot be null" });
            }

            CameraDto? camera = await _cameraService.UpdateAsync(id, request, ct);
            return camera == null ? NotFound(new { message = "Camera not found" }) : Ok(camera);
        }
        catch (InvalidOperationException ex)
        {
            if (ex.Message.Contains("already exists"))
            {
                return BadRequest(new { message = ex.Message });
            }

            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in UpdateCameraAsync for ID {CameraId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Deletes a camera by ID.
    /// </summary>
    /// <param name="id">The camera ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content on success</returns>
    /// <response code="204">Camera deleted successfully</response>
    /// <response code="404">If the camera is not found</response>
    /// <response code="503">If the system is still initializing</response>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> DeleteCameraAsync(Guid id, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            bool deleted = await _cameraService.DeleteAsync(id, ct);
            return deleted ? NoContent() : NotFound(new { message = "Camera not found" });
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in DeleteCameraAsync for ID {CameraId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Toggles a camera's enabled status.
    /// </summary>
    /// <param name="id">The camera ID</param>
    /// <param name="request">The toggle request containing the new enabled status</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The updated camera</returns>
    /// <response code="200">Returns the updated camera</response>
    /// <response code="404">If the camera is not found</response>
    /// <response code="503">If the system is still initializing</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPatch("{id}/toggle")]
    [ProducesResponseType(typeof(CameraDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<CameraDto>> ToggleCameraAsync(Guid id, ToggleCameraDto request, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (request == null)
            {
                return BadRequest(new { message = "Request body cannot be null" });
            }

            CameraDto? camera = await _cameraService.ToggleEnabledAsync(id, request.IsEnabled, ct);
            return camera == null ? NotFound(new { message = "Camera not found" }) : Ok(camera);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in ToggleCameraAsync for ID {CameraId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }
}

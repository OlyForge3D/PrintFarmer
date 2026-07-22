using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Startup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Camera = Farm.Infrastructure.Domain.Camera;

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
    IPrinterCameraEndpointDetectionService cameraEndpointDetectionService,
    IStartupStatus startupStatus,
    ILogger<CamerasController> logger) : ControllerBase
{
    private readonly ICameraService _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
    private readonly IPrinterCameraEndpointDetectionService _cameraEndpointDetectionService = cameraEndpointDetectionService ?? throw new ArgumentNullException(nameof(cameraEndpointDetectionService));
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
    /// Gets all enabled cameras (standalone and printer-attached) for display in the Camera View.
    /// Includes printer names resolved via navigation properties.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of display cameras with printer names</returns>
    /// <response code="200">Returns the list of display cameras</response>
    /// <response code="503">If the system is still initializing</response>
    [AllowAnonymous]
    [HttpGet("display")]
    [ProducesResponseType(typeof(IEnumerable<DisplayCameraDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<DisplayCameraDto>>> GetDisplayCamerasAsync(CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            List<DisplayCameraDto> cameras = await _cameraService.GetDisplayCamerasAsync(ct);
            return Ok(cameras);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in GetDisplayCamerasAsync: {Message}", ex.Message);
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
                PrinterId = camera.PrinterId,
                PrinterName = camera.Printer?.Name,
                Source = camera.Source,
                CameraType = camera.CameraType,
                HealthStatus = camera.HealthStatus,
                LastHealthCheck = camera.LastHealthCheck,
                IsStandalone = !camera.PrinterId.HasValue
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
    /// Gets all cameras attached to a specific printer.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of cameras for the specified printer</returns>
    /// <response code="200">Returns the list of cameras for the printer</response>
    /// <response code="503">If the system is still initializing</response>
    [AllowAnonymous]
    [HttpGet("by-printer/{printerId}")]
    [ProducesResponseType(typeof(IEnumerable<CameraDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<CameraDto>>> GetCamerasByPrinterAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            List<CameraDto> cameras = await _cameraService.GetByPrinterIdAsync(printerId, ct);
            return Ok(cameras);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in GetCamerasByPrinterAsync for PrinterId {PrinterId}: {Message}", printerId.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Detects camera stream and snapshot endpoints for a configured printer.
    /// </summary>
    /// <param name="request">Printer camera endpoint detection request</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Detected camera endpoint URLs, if supported and configured</returns>
    /// <response code="200">Returns detection result; detected is false when unsupported or probing fails</response>
    /// <response code="404">If the printer is not found</response>
    /// <response code="503">If the system is still initializing</response>
    [AllowAnonymous]
    [HttpPost("detect-endpoints")]
    [ProducesResponseType(typeof(CameraEndpointDetectionDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<CameraEndpointDetectionDto>> DetectCameraEndpointsAsync(DetectCameraEndpointsRequest request, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (request == null || request.PrinterId == Guid.Empty)
            {
                return BadRequest(new { message = "Printer ID is required" });
            }

            Farm.Infrastructure.Discovery.PrinterCameraProbeResult? result = await _cameraEndpointDetectionService.DetectAsync(request.PrinterId, ct);
            if (result is null)
            {
                return NotFound(new { message = "Printer not found" });
            }

            return Ok(new CameraEndpointDetectionDto
            {
                StreamUrl = result.StreamUrl,
                SnapshotUrl = result.SnapshotUrl,
                Detected = result.Detected,
                Source = result.Source
            });
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[CamerasController] Camera endpoint detection failed for printer {PrinterId}: {Message}", request?.PrinterId.ToString(), ex.Message);
            return Ok(new CameraEndpointDetectionDto
            {
                Detected = false,
                Source = "unknown"
            });
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

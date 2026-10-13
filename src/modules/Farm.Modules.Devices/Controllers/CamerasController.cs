using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Startup;
using Farm.Modules.Devices.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Camera = Farm.Infrastructure.Domain.Camera;

namespace Farm.Modules.Devices.Controllers;

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
    ILogger<CamerasController> logger,
    IHttpClientFactory httpClientFactory,
    IEgressGuard egressGuard,
    IQueueResourceAuthorizationService? queueResourceAuthorization = null) : ControllerBase
{
    private readonly ICameraService _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
    private readonly IPrinterCameraEndpointDetectionService _cameraEndpointDetectionService = cameraEndpointDetectionService ?? throw new ArgumentNullException(nameof(cameraEndpointDetectionService));
    private readonly IStartupStatus _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
    private readonly ILogger<CamerasController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly IEgressGuard _egressGuard = egressGuard ?? throw new ArgumentNullException(nameof(egressGuard));
    private readonly IQueueResourceAuthorizationService? _queueResourceAuthorization = queueResourceAuthorization;

    /// <summary>
    /// Gets all cameras the caller can access, optionally filtered by linked printer and/or effective location.
    /// </summary>
    /// <param name="printerId">Optional: only cameras linked to this printer.</param>
    /// <param name="locationId">Optional: only cameras whose effective location is this location.</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all standalone cameras</returns>
    /// <response code="200">Returns the list of cameras</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<CameraDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<CameraDto>>> GetCamerasAsync(
        [FromQuery] Guid? printerId,
        [FromQuery] Guid? locationId,
        CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (printerId.HasValue && !await CanAccessCameraPrinterAsync(printerId, ct))
            {
                return Ok(new List<CameraDto>());
            }

            List<CameraDto> cameras = printerId.HasValue || locationId.HasValue
                ? await _cameraService.QueryDtosAsync(printerId, locationId, enabledOnly: false, ct)
                : (await _cameraService.GetAllDtosAsync(ct)).ToList();
            List<CameraDto> accessible = await FilterAccessibleCamerasAsync(cameras, ct);
            return Ok(accessible);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in GetCamerasAsync: {Message}", ex.Message);
            return CameraProblem("camera_list_failed", "Cameras could not be read.");
        }
    }

    /// <summary>
    /// Gets all enabled cameras the caller can access, optionally filtered by linked printer and/or effective location.
    /// </summary>
    /// <param name="printerId">Optional: only cameras linked to this printer.</param>
    /// <param name="locationId">Optional: only cameras whose effective location is this location.</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of enabled standalone cameras</returns>
    /// <response code="200">Returns the list of enabled cameras</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpGet("enabled")]
    [ProducesResponseType(typeof(IEnumerable<CameraDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<CameraDto>>> GetEnabledCamerasAsync(
        [FromQuery] Guid? printerId,
        [FromQuery] Guid? locationId,
        CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (printerId.HasValue && !await CanAccessCameraPrinterAsync(printerId, ct))
            {
                return Ok(new List<CameraDto>());
            }

            List<CameraDto> cameras = printerId.HasValue || locationId.HasValue
                ? await _cameraService.QueryDtosAsync(printerId, locationId, enabledOnly: true, ct)
                : (await _cameraService.GetEnabledCamerasAsync(ct)).ToList();
            List<CameraDto> accessible = await FilterAccessibleCamerasAsync(cameras, ct);
            return Ok(accessible);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in GetEnabledCamerasAsync: {Message}", ex.Message);
            return CameraProblem("camera_list_failed", "Cameras could not be read.");
        }
    }

    /// <summary>
    /// Gets all enabled cameras (standalone and printer-attached) for display in the Camera View.
    /// Includes printer names resolved via navigation properties.
    /// </summary>
    /// <param name="printerId">Optional: only cameras linked to this printer.</param>
    /// <param name="locationId">Optional: only cameras whose effective location is this location.</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of display cameras with printer names</returns>
    /// <response code="200">Returns the list of display cameras</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpGet("display")]
    [ProducesResponseType(typeof(IEnumerable<DisplayCameraDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<DisplayCameraDto>>> GetDisplayCamerasAsync(
        [FromQuery] Guid? printerId,
        [FromQuery] Guid? locationId,
        CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (printerId.HasValue && !await CanAccessCameraPrinterAsync(printerId, ct))
            {
                return Ok(new List<DisplayCameraDto>());
            }

            List<DisplayCameraDto> cameras = printerId.HasValue || locationId.HasValue
                ? await _cameraService.QueryDisplayCamerasAsync(printerId, locationId, ct)
                : await _cameraService.GetDisplayCamerasAsync(ct);
            cameras = await FilterAccessibleCamerasAsync(cameras, ct);
            foreach (DisplayCameraDto camera in cameras)
            {
                camera.StreamProxyUrl = string.IsNullOrWhiteSpace(camera.StreamUrl) ? null : GetCameraProxyPath(camera.Id, "stream");
                camera.SnapshotProxyUrl = string.IsNullOrWhiteSpace(camera.SnapshotUrl) ? null : GetCameraProxyPath(camera.Id, "snapshot");
            }

            return Ok(cameras);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in GetDisplayCamerasAsync: {Message}", ex.Message);
            return CameraProblem("camera_list_failed", "Cameras could not be read.");
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

            if (!await CanAccessCameraPrinterAsync(camera, ct))
            {
                return NotFound(new { message = "Camera not found" });
            }

            List<CameraDto> visible = await FilterAccessibleCamerasAsync([CameraDtoMapper.ToDto(camera)], ct);
            CameraDto? dto = visible.FirstOrDefault();
            return dto is null ? NotFound(new { message = "Camera not found" }) : Ok(dto);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in GetCameraAsync for ID {CameraId}: {Message}", id.ToString(), ex.Message);
            return CameraProblem("camera_read_failed", "The camera could not be read.");
        }
    }

    /// <summary>
    /// Gets the stored stream/snapshot targets of a camera for the admin edit form.
    /// Embedded URL credentials are stripped and reported via the HasCredentials flags.
    /// </summary>
    /// <param name="id">The camera ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <response code="200">Returns the editable camera configuration</response>
    /// <response code="404">If the camera is not found or not accessible</response>
    /// <response code="500">Generic correlated problem; failure details are logged server-side only</response>
    /// <response code="503">If the system is still initializing</response>
    [RequirePermission("cameras", "admin")]
    [HttpGet("{id:guid}/config")]
    [CameraConfigErrorFilter]
    [ProducesResponseType(typeof(CameraConfigDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<CameraConfigDto>> GetCameraConfigAsync(Guid id, CancellationToken ct)
    {
        // Unexpected failures are sanitized by CameraConfigErrorFilter; client aborts reach
        // GlobalExceptionMiddleware as cancellations.
        if (!_startupStatus.IsReady)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }

        Camera? camera = await _cameraService.FindByIdAsync(id, ct);
        if (camera == null || !await CanAccessCameraPrinterAsync(camera, ct))
        {
            return NotFound(new { message = "Camera not found" });
        }

        (string? streamUrl, bool streamHasCredentials) = StripCredentials(CameraContractClassifier.NormalizeTarget(camera.StreamUrl));
        (string? snapshotUrl, bool snapshotHasCredentials) = StripCredentials(CameraContractClassifier.NormalizeTarget(camera.SnapshotUrl));
        return Ok(new CameraConfigDto
        {
            Id = camera.Id,
            StreamUrl = streamUrl,
            SnapshotUrl = snapshotUrl,
            StreamUrlHasCredentials = streamHasCredentials,
            SnapshotUrlHasCredentials = snapshotHasCredentials
        });
    }

    private static (string? Url, bool HasCredentials) StripCredentials(string? url)
    {
        string? redacted = UrlCredentialRedactor.Redact(url);
        if (string.Equals(redacted, url, StringComparison.Ordinal))
        {
            return (url, false);
        }

        // An unparseable credentialed value redacts to a placeholder, which is not a usable URL.
        return (Uri.TryCreate(redacted, UriKind.Absolute, out _) ? redacted : null, true);
    }

    /// <summary>
    /// Gets all cameras attached to a specific printer.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of cameras for the specified printer</returns>
    /// <response code="200">Returns the list of cameras for the printer</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpGet("by-printer/{printerId}")]
    [ProducesResponseType(typeof(IEnumerable<CameraDto>), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<CameraDto>>> GetCamerasByPrinterAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (!await CanAccessCameraPrinterAsync(printerId, ct))
            {
                return NotFound(new { message = "Printer not found" });
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
            return CameraProblem("camera_list_failed", "Cameras could not be read.");
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
    [HttpPost("detect-endpoints")]
    [ProducesResponseType(typeof(CameraEndpointDetectionDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<CameraEndpointDetectionDto>> DetectCameraEndpointsAsync(DetectCameraEndpointsRequest request, CancellationToken ct)
    {
        if (!_startupStatus.IsReady)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }

        if (request == null || request.PrinterId == Guid.Empty)
        {
            return BadRequest(new { message = "Printer ID is required" });
        }

        // Authorization must happen outside the probe's try/catch below: if the authorization
        // check itself throws, we must fail closed (propagate/500), not swallow it into the
        // "probing failed" 200 response used for detection-only failures.
        if (!await CanAccessCameraPrinterAsync(request.PrinterId, ct))
        {
            return NotFound(new { message = "Printer not found" });
        }

        try
        {
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
    [RequirePermission("cameras", "admin")]
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

            if (!await CanAccessRequestedPrintersAsync(request.PrinterIds, request.PrinterId, ct))
            {
                return InvalidCameraReference();
            }

            CameraDto camera = await _cameraService.CreateAsync(request, ct);
            return StatusCode(201, camera);
        }
        catch (ArgumentException)
        {
            return InvalidCameraReference();
        }
        catch (InvalidOperationException ex)
        {
            if (ex.Message.Contains("already exists"))
            {
                return CameraProblem(
                    "camera_name_conflict",
                    "A camera with this name already exists.",
                    StatusCodes.Status400BadRequest);
            }

            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in CreateCameraAsync: {Message}", ex.Message);
            return CameraProblem("camera_create_failed", "The camera could not be created.");
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
    [RequirePermission("cameras", "admin")]
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

            Camera? existing = await _cameraService.FindByIdAsync(id, ct);
            if (existing is null || !await CanAccessCameraPrinterAsync(existing, ct))
            {
                return NotFound(new { message = "Camera not found" });
            }

            if (!await CanAccessRequestedPrintersAsync(request.PrinterIds, request.PrinterId, ct))
            {
                return InvalidCameraReference();
            }

            // Links to printers the caller cannot see are redacted from reads, so a round-tripped printer
            // list must not be able to remove them.
            IReadOnlyCollection<Guid> hiddenPrinterIds = await GetInaccessibleLinkedPrinterIdsAsync(existing, ct);
            CameraDto? camera = await _cameraService.UpdateAsync(id, request, hiddenPrinterIds, ct);
            if (camera is null)
            {
                return NotFound(new { message = "Camera not found" });
            }

            CameraDto? visible = (await FilterAccessibleCamerasAsync([camera], ct)).FirstOrDefault();
            return visible is null ? NotFound(new { message = "Camera not found" }) : Ok(visible);
        }
        catch (ArgumentException)
        {
            return InvalidCameraReference();
        }
        catch (InvalidOperationException ex)
        {
            if (ex.Message.Contains("already exists"))
            {
                return CameraProblem(
                    "camera_name_conflict",
                    "A camera with this name already exists.",
                    StatusCodes.Status400BadRequest);
            }

            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in UpdateCameraAsync for ID {CameraId}: {Message}", id.ToString(), ex.Message);
            return CameraProblem("camera_update_failed", "The camera could not be updated.");
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
    [RequirePermission("cameras", "admin")]
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

            Camera? existing = await _cameraService.FindByIdAsync(id, ct);
            if (existing is null || !await CanAccessCameraPrinterAsync(existing, ct))
            {
                return NotFound(new { message = "Camera not found" });
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
            return CameraProblem("camera_delete_failed", "The camera could not be deleted.");
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
    [RequirePermission("cameras", "admin")]
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

            Camera? existing = await _cameraService.FindByIdAsync(id, ct);
            if (existing is null || !await CanAccessCameraPrinterAsync(existing, ct))
            {
                return NotFound(new { message = "Camera not found" });
            }

            CameraDto? camera = await _cameraService.ToggleEnabledAsync(id, request.IsEnabled, ct);
            if (camera is null)
            {
                return NotFound(new { message = "Camera not found" });
            }

            CameraDto? visible = (await FilterAccessibleCamerasAsync([camera], ct)).FirstOrDefault();
            return visible is null ? NotFound(new { message = "Camera not found" }) : Ok(visible);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CamerasController] Exception in ToggleCameraAsync for ID {CameraId}: {Message}", id.ToString(), ex.Message);
            return CameraProblem("camera_update_failed", "The camera could not be updated.");
        }
    }

    /// <summary>
    /// Links a camera to a printer. Idempotent; the optional body controls whether the camera becomes the
    /// printer's primary camera (each printer keeps exactly one primary camera).
    /// </summary>
    /// <param name="id">The camera ID</param>
    /// <param name="printerId">The printer ID</param>
    /// <param name="request">Optional body: <c>{ "isPrimary": true }</c></param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <response code="200">Returns the updated camera</response>
    /// <response code="404">If the camera or printer is not found or not accessible</response>
    [RequirePermission("cameras", "admin")]
    [HttpPost("{id:guid}/printers/{printerId:guid}")]
    [ProducesResponseType(typeof(CameraDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<CameraDto>> LinkCameraToPrinterAsync(
        Guid id,
        Guid printerId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] LinkCameraPrinterRequest? request,
        CancellationToken ct)
    {
        if (!_startupStatus.IsReady)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }

        Camera? camera = await _cameraService.FindByIdAsync(id, ct);
        if (camera is null || !await CanAccessCameraPrinterAsync(camera, ct))
        {
            return NotFound(new { message = "Camera not found" });
        }

        if (!await CanAccessCameraPrinterAsync(printerId, ct))
        {
            return NotFound(new { message = "Printer not found" });
        }

        try
        {
            CameraDto? updated = await _cameraService.LinkToPrinterAsync(id, printerId, request?.IsPrimary, ct);
            if (updated is null)
            {
                return NotFound(new { message = "Camera not found" });
            }

            List<CameraDto> visible = await FilterAccessibleCamerasAsync([updated], ct);
            CameraDto? dto = visible.FirstOrDefault();
            return dto is null ? NotFound(new { message = "Camera not found" }) : Ok(dto);
        }
        catch (ArgumentException)
        {
            return NotFound(new { message = "Printer not found" });
        }
    }

    /// <summary>
    /// Unlinks a camera from a printer. The camera itself is kept.
    /// </summary>
    /// <param name="id">The camera ID</param>
    /// <param name="printerId">The printer ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <response code="204">The link was removed</response>
    /// <response code="404">If the camera is not found, not accessible, or not linked to the printer</response>
    [RequirePermission("cameras", "admin")]
    [HttpDelete("{id:guid}/printers/{printerId:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> UnlinkCameraFromPrinterAsync(Guid id, Guid printerId, CancellationToken ct)
    {
        if (!_startupStatus.IsReady)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }

        Camera? camera = await _cameraService.FindByIdAsync(id, ct);
        if (camera is null || !await CanAccessCameraPrinterAsync(camera, ct) || !await CanAccessCameraPrinterAsync(printerId, ct))
        {
            return NotFound(new { message = "Camera not found" });
        }

        CameraPrinterRemovalResult result = await _cameraService.UnlinkFromPrinterAsync(id, printerId, ct);
        return result == CameraPrinterRemovalResult.Unlinked
            ? NoContent()
            : NotFound(new { message = "Camera is not linked to this printer" });
    }

    /// <summary>
    /// Removes a camera from a printer (Edit Printer). Unlinks the camera and deletes it only when it was
    /// auto-discovered and no printer links remain; user-created cameras are kept as unlinked cameras.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="cameraId">The camera ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <response code="200">Returns <c>{ "result": "unlinked" | "deleted" }</c></response>
    /// <response code="404">If the printer or camera is not found, not accessible, or not linked</response>
    [RequirePermission("printers", "admin")]
    [HttpDelete("/api/printers/{printerId:guid}/cameras/{cameraId:guid}")]
    [ProducesResponseType(typeof(RemoveCameraFromPrinterResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<RemoveCameraFromPrinterResponse>> RemoveCameraFromPrinterAsync(
        Guid printerId,
        Guid cameraId,
        CancellationToken ct)
    {
        if (!_startupStatus.IsReady)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }

        if (!await CanAccessCameraPrinterAsync(printerId, ct))
        {
            return NotFound(new { message = "Printer not found" });
        }

        CameraPrinterRemovalResult result = await _cameraService.RemoveFromPrinterAsync(printerId, cameraId, ct);
        return result switch
        {
            CameraPrinterRemovalResult.Unlinked or CameraPrinterRemovalResult.Deleted =>
                Ok(new RemoveCameraFromPrinterResponse(result)),
            _ => NotFound(new { message = "Camera is not linked to this printer" }),
        };
    }

    /// <summary>Streams camera content without disclosing its private target URL.</summary>
    [HttpGet("{id:guid}/stream")]
    [ProducesResponseType(typeof(FileStreamResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(502)]
    public Task<IActionResult> ProxyCameraStreamAsync(Guid id, CancellationToken ct) =>
        ProxyCameraAsync(id, useSnapshot: false, ct);

    /// <summary>Returns a camera snapshot without disclosing its private target URL.</summary>
    [HttpGet("{id:guid}/snapshot")]
    [ProducesResponseType(typeof(FileStreamResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(502)]
    public Task<IActionResult> ProxyCameraSnapshotAsync(Guid id, CancellationToken ct) =>
        ProxyCameraAsync(id, useSnapshot: true, ct);

    private static string GetCameraProxyPath(Guid cameraId, string kind) =>
        $"/api/cameras/{cameraId:D}/{kind}";

    private async Task<IActionResult> ProxyCameraAsync(Guid id, bool useSnapshot, CancellationToken ct)
    {
        Camera? camera = await _cameraService.FindByIdAsync(id, ct);
        if (camera is null || !camera.IsEnabled)
        {
            return NotFound();
        }

        if (!await CanAccessCameraPrinterAsync(camera, ct))
        {
            return NotFound();
        }

        string? target = useSnapshot ? camera.SnapshotUrl : camera.StreamUrl;
        if (string.IsNullOrWhiteSpace(target))
        {
            return NotFound();
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? targetUri) ||
            (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Camera {CameraId} has a non-HTTP(S) target", id);
            return CameraProblem(
                "camera_target_invalid",
                "The configured camera target is invalid.",
                StatusCodes.Status502BadGateway);
        }

        EgressCheckResult egressCheck = await _egressGuard.CheckAsync(targetUri.ToString(), ct);
        if (!egressCheck.IsAllowed)
        {
            _logger.LogWarning(
                "Camera {CameraId} target denied by egress guard: {Reason}",
                id,
                LogSanitizer.Sanitize(egressCheck.DenyReason));
            return CameraProblem(
                "camera_target_invalid",
                "The configured camera target is invalid.",
                StatusCodes.Status502BadGateway);
        }

        // Reuse the exact address the egress guard just vetted for the real connection instead
        // of letting the hostname be re-resolved independently — otherwise a DNS-rebinding
        // attacker could swap the record between the check above and the connection below.
        Uri connectUri = egressCheck.ResolvedAddress is not null
            ? EgressGuard.CreatePinnedUri(targetUri, egressCheck.ResolvedAddress)
            : targetUri;

        try
        {
            HttpClient client = _httpClientFactory.CreateClient("VettedEgress");
            using var request = new HttpRequestMessage(HttpMethod.Get, connectUri);
            if (connectUri != targetUri)
            {
                request.Headers.Host = targetUri.IsDefaultPort
                    ? targetUri.Host
                    : $"{targetUri.Host}:{targetUri.Port}";
            }

            HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Camera {CameraId} returned {StatusCode}", id, response.StatusCode);
                response.Dispose();
                return CameraProblem(
                    "camera_upstream_failed",
                    "The camera did not return a successful response.",
                    StatusCodes.Status502BadGateway);
            }

            Stream content = await response.Content.ReadAsStreamAsync(ct);
            HttpContext.Response.RegisterForDispose(response);
            string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            if (response.Content.Headers.ContentLength is long contentLength)
            {
                HttpContext.Response.ContentLength = contentLength;
            }

            return File(content, contentType);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CameraProblem(
                "camera_upstream_timeout",
                "The camera request timed out.",
                StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Camera proxy request failed for camera {CameraId}", id);
            return CameraProblem(
                "camera_upstream_unavailable",
                "The camera is unavailable.",
                StatusCodes.Status502BadGateway);
        }
    }

    private ObjectResult CameraProblem(
        string code,
        string title,
        int statusCode = StatusCodes.Status500InternalServerError) =>
        Problem(
            statusCode: statusCode,
            title: title,
            type: $"https://printfarmer.dev/problems/{code}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            });

    /// <summary>
    /// Enforces the same PrinterGroup access rules on a printer-attached camera as
    /// <c>PrintersController.CanAccessPrinterAsync</c> applies to printer-scoped reads and the
    /// camera proxy (issue #1292), so <c>CamerasController</c> cannot be used as a parallel,
    /// unscoped route to the same printer-attached cameras (issue #1421).
    ///
    /// Standalone cameras (<paramref name="printerId"/> is <see langword="null"/>) have no
    /// <see cref="Farm.Infrastructure.Domain.PrinterGroup"/> to scope against; they remain
    /// visible to any authenticated caller by deliberate, documented design.
    /// </summary>
    private async Task<bool> CanAccessCameraPrinterAsync(Guid? printerId, CancellationToken ct)
    {
        if (!printerId.HasValue)
        {
            return true;
        }

        return _queueResourceAuthorization is not null &&
            await _queueResourceAuthorization.CanAccessPrinterAsync(
                User,
                printerId.Value,
                PrinterGroupAccessLevel.View,
                ct);
    }

    /// <summary>
    /// A camera is accessible when it has no printer links (unlinked cameras are visible to any
    /// authenticated caller by design) or when the caller can view ANY of its linked printers.
    /// </summary>
    private async Task<bool> CanAccessCameraPrinterAsync(Camera camera, CancellationToken ct)
    {
        Guid[] printerIds = camera.PrinterLinks.Select(l => l.PrinterId).Distinct().ToArray();
        if (printerIds.Length == 0)
        {
            return true;
        }

        if (_queueResourceAuthorization is null)
        {
            return false;
        }

        IReadOnlySet<Guid> allowed = await _queueResourceAuthorization.FilterAccessiblePrinterIdsAsync(
            User,
            printerIds,
            PrinterGroupAccessLevel.View,
            ct);
        return allowed.Count > 0;
    }

    /// <summary>
    /// Returns the camera's linked printers the caller cannot view. These links are hidden from the caller's
    /// reads and must be preserved when the caller replaces the camera's printer list.
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>> GetInaccessibleLinkedPrinterIdsAsync(Camera camera, CancellationToken ct)
    {
        Guid[] printerIds = camera.PrinterLinks.Select(l => l.PrinterId).Distinct().ToArray();
        if (printerIds.Length == 0)
        {
            return [];
        }

        if (_queueResourceAuthorization is null)
        {
            return printerIds;
        }

        IReadOnlySet<Guid> allowed = await _queueResourceAuthorization.FilterAccessiblePrinterIdsAsync(
            User,
            printerIds,
            PrinterGroupAccessLevel.View,
            ct);
        return printerIds.Where(id => !allowed.Contains(id)).ToArray();
    }

    /// <summary>
    /// Applies the same PrinterGroup access rules as <see cref="CanAccessCameraPrinterAsync(Camera, CancellationToken)"/>
    /// to a collection of cameras, so cameras whose linked printers are all restricted are omitted from
    /// list/display responses instead of merely blocking per-id reads (mirrors
    /// <c>PrintersController.FilterAccessiblePrintersAsync</c>). Unlinked cameras are always retained.
    /// Linked printers the caller cannot view are redacted from the returned DTOs.
    /// </summary>
    private async Task<List<T>> FilterAccessibleCamerasAsync<T>(List<T> cameras, CancellationToken ct)
        where T : ICameraPrinterLinksDto
    {
        Guid[] printerIds = cameras
            .SelectMany(c => GetLinkedPrinterIds(c))
            .Distinct()
            .ToArray();
        if (printerIds.Length == 0)
        {
            return cameras;
        }

        if (_queueResourceAuthorization is null)
        {
            // Fail closed: an unavailable authorization dependency must not silently disclose every
            // printer-attached camera.
            return cameras.Where(c => GetLinkedPrinterIds(c).Count == 0).ToList();
        }

        IReadOnlySet<Guid> allowed = await _queueResourceAuthorization.FilterAccessiblePrinterIdsAsync(
            User,
            printerIds,
            PrinterGroupAccessLevel.View,
            ct);

        List<T> result = [];
        foreach (T camera in cameras)
        {
            List<Guid> linked = GetLinkedPrinterIds(camera);
            if (linked.Count == 0)
            {
                result.Add(camera);
                continue;
            }

            if (!linked.Any(allowed.Contains))
            {
                continue;
            }

            RedactInaccessiblePrinters(camera, allowed);
            result.Add(camera);
        }

        return result;
    }

    private static List<Guid> GetLinkedPrinterIds(ICameraPrinterLinksDto camera)
    {
        if (camera.PrinterIds.Count > 0)
        {
            return camera.PrinterIds;
        }

        return camera.PrinterId is Guid printerId ? [printerId] : [];
    }

    private static void RedactInaccessiblePrinters(ICameraPrinterLinksDto camera, IReadOnlySet<Guid> allowed)
    {
        if (camera.PrinterIds.Count == 0 || camera.PrinterIds.All(allowed.Contains))
        {
            return;
        }

        camera.PrinterIds = camera.PrinterIds.Where(allowed.Contains).ToList();
        camera.Printers = camera.Printers.Where(p => allowed.Contains(p.Id)).ToList();
        if (camera.PrinterId is not Guid primary || !allowed.Contains(primary))
        {
            CameraPrinterSummaryDto? first = camera.Printers.FirstOrDefault();
            camera.PrinterId = first?.Id;
            camera.PrinterName = first?.Name;
        }
    }

    /// <summary>
    /// Verifies the caller can view every printer referenced by a create/update request, so cameras cannot be
    /// attached to printers outside the caller's PrinterGroup scope.
    /// </summary>
    private async Task<bool> CanAccessRequestedPrintersAsync(
        IReadOnlyCollection<Guid>? printerIds,
        Guid? legacyPrinterId,
        CancellationToken ct)
    {
        Guid[] requested = (printerIds ?? (legacyPrinterId.HasValue ? [legacyPrinterId.Value] : []))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (requested.Length == 0)
        {
            return true;
        }

        if (_queueResourceAuthorization is null)
        {
            return false;
        }

        IReadOnlySet<Guid> allowed = await _queueResourceAuthorization.FilterAccessiblePrinterIdsAsync(
            User,
            requested,
            PrinterGroupAccessLevel.View,
            ct);
        return requested.All(allowed.Contains);
    }

    private ObjectResult InvalidCameraReference() =>
        CameraProblem(
            "camera_invalid_reference",
            "A referenced printer or location does not exist.",
            StatusCodes.Status400BadRequest);
}

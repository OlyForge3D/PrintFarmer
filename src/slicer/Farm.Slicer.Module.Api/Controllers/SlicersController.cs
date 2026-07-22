using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Controllers;

/// <summary>
/// API endpoints for slicer service registration and lifecycle management.
/// </summary>
[ApiController]
[Route("api/slicers")]

// Slicer workers authenticate through the slicer API-key filters, not PrintFarmer bearer tokens.
[AllowAnonymous]
public class SlicersController(ISlicersService service) : ControllerBase
{
    private readonly ISlicersService _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>
    /// Lists all registered slicer services.
    /// </summary>
    [HttpGet]
    [RequireSlicerApiKey]
    public async Task<IActionResult> ListAsync()
    {
        IReadOnlyList<SlicerService> list = await _service.ListAsync(HttpContext.RequestAborted);
        return Ok(list.Select(ToResponseDto).ToList());
    }

    /// <summary>
    /// Registers a new slicer service.
    /// </summary>
    /// <param name="dto">Registration data.</param>
    [HttpPost("register")]
    [RequireSlicerApiKey]
    public async Task<IActionResult> RegisterAsync([FromBody] RegisterSlicerDto dto)
    {
        CancellationToken ct = HttpContext.RequestAborted;
        (Guid id, string? apiKey) = await _service.RegisterAsync(dto, ct);
        string location = $"/api/slicers/{id}";
        return Created(location, new { id, apiKey });
    }

    /// <summary>
    /// Gets a specific slicer service by ID.
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    [HttpGet("{id}")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        SlicerService? svc = await _service.GetAsync(id, HttpContext.RequestAborted);
        return svc == null ? NotFound() : Ok(ToResponseDto(svc));
    }

    /// <summary>
    /// Processes a heartbeat from a slicer service.
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    /// <param name="dto">Heartbeat data.</param>
    [HttpPost("{id}/heartbeat")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> HeartbeatAsync(Guid id, [FromBody] HeartbeatDto dto)
    {
        bool ok = await _service.HeartbeatAsync(id, dto, HttpContext.RequestAborted);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>
    /// Deregisters a slicer service.
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    [HttpPost("{id}/deregister")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> DeregisterAsync(Guid id)
    {
        bool ok = await _service.DeregisterAsync(id, HttpContext.RequestAborted);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>
    /// Rotates the API key for a slicer service.
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    [HttpPost("{id}/rotate-key")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> RotateApiKeyAsync(Guid id)
    {
        string? newApiKey = await _service.RotateApiKeyAsync(id, HttpContext.RequestAborted);
        return newApiKey == null ? NotFound() : Ok(new { id, apiKey = newApiKey });
    }

    private static SlicerServiceResponseDto ToResponseDto(SlicerService service)
    {
        return new SlicerServiceResponseDto
        {
            Id = service.Id,
            Name = service.Name,
            SlicerType = service.SlicerType,
            Version = service.Version,
            Host = service.Host,
            UiManifestUrl = service.UiManifestUrl,
            CapabilitiesJson = service.CapabilitiesJson,
            MaxConcurrentJobs = service.MaxConcurrentJobs,
            Status = service.Status,
            LastSeen = service.LastSeen,
            ApiKeyRotatedAt = service.ApiKeyRotatedAt,
            CreatedAt = service.CreatedAt,
            UpdatedAt = service.UpdatedAt,
            Tags = service.Tags,
            InstanceId = service.InstanceId,
        };
    }
}

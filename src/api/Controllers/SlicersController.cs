using Farm.Infrastructure.Domain;
using Farm.Web.Api.Infrastructure.Filters;
using Farm.Web.Api.Services.SlicerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
// Registration and list use static key, all others use per-service key
[RequireSlicerApiKey]
public class SlicersController(Services.Slicing.ISlicersService service) : ControllerBase
{
    private readonly Services.Slicing.ISlicersService _service = service ?? throw new ArgumentNullException(nameof(service));


    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        IReadOnlyList<SlicerService> list = await _service.ListAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
        return Ok(list);
    }

    // Registration uses static key

    [HttpPost("register")]
    public async Task<IActionResult> RegisterAsync([FromBody] RegisterSlicerDto dto)
    {
        SlicerService svc = new SlicerService
        {
            Id = Guid.NewGuid(),
            Name = dto.Name ?? "orca-service",
            SlicerType = dto.SlicerType,
            Version = dto.Version,
            Host = dto.Host,
            UiManifestUrl = dto.UiManifestUrl,
            CapabilitiesJson = dto.CapabilitiesJson,
            MaxConcurrentJobs = dto.MaxConcurrentJobs,
            Status = "Online",
            LastSeen = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Tags = dto.Tags
        };

        // Simple api key generation - rotate or secure later
        svc.ApiKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("=", "");

        CancellationToken ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        (Guid id, string? apiKey) = await _service.RegisterAsync(dto, ct);
        string location = $"/api/slicers/{id}";
        return Created(location, new { id, apiKey });
    }


    [HttpGet("{id}")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        SlicerService? svc = await _service.GetAsync(id, HttpContext?.RequestAborted ?? CancellationToken.None);
        return svc == null ? NotFound() : Ok(svc);
    }


    [HttpPost("{id}/heartbeat")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> HeartbeatAsync(Guid id, [FromBody] HeartbeatDto dto)
    {
        CancellationToken ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        bool ok = await _service.HeartbeatAsync(id, dto, ct);
        return ok ? NoContent() : NotFound();
    }


    [HttpPost("{id}/deregister")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> DeregisterAsync(Guid id)
    {
        CancellationToken ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        bool ok = await _service.DeregisterAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    [HttpPost("{id}/rotate-key")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> RotateApiKeyAsync(Guid id)
    {
        CancellationToken ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        string? newApiKey = await _service.RotateApiKeyAsync(id, ct);
        return newApiKey == null ? NotFound() : Ok(new { id, apiKey = newApiKey });
    }
}

// DTOs moved to Farm.Infrastructure.Contracts.Slicing.SlicerDtos.cs

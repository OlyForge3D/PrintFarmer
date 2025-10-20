using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.SlicerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

using Farm.Web.Api.Infrastructure.Filters;

[ApiController]
[Route("api/[controller]")]
// Registration and list use static key, all others use per-service key
[RequireSlicerApiKey]
public class SlicersController : ControllerBase
{
    private readonly Farm.Web.Api.Services.Slicing.ISlicersService _service;

    public SlicersController(Farm.Web.Api.Services.Slicing.ISlicersService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }


    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var list = await _service.ListAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
        return Ok(list);
    }

    // Registration uses static key

    [HttpPost("register")]
    public async Task<IActionResult> RegisterAsync([FromBody] Farm.Web.Shared.Contracts.Slicing.RegisterSlicerDto dto)
    {
        var svc = new SlicerService
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

        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        var (id, apiKey) = await _service.RegisterAsync(dto, ct);
        var location = $"/api/slicers/{id}";
        return Created(location, new { id, apiKey });
    }


    [HttpGet("{id}")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var svc = await _service.GetAsync(id, HttpContext?.RequestAborted ?? CancellationToken.None);
        if (svc == null)
        {
            return NotFound();
        }
        return Ok(svc);
    }


    [HttpPost("{id}/heartbeat")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> HeartbeatAsync(Guid id, [FromBody] Farm.Web.Shared.Contracts.Slicing.HeartbeatDto dto)
    {
        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        var ok = await _service.HeartbeatAsync(id, dto, ct);
        return ok ? NoContent() : NotFound();
    }


    [HttpPost("{id}/deregister")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> DeregisterAsync(Guid id)
    {
        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        var ok = await _service.DeregisterAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    [HttpPost("{id}/rotate-key")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> RotateApiKeyAsync(Guid id)
    {
        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        var newApiKey = await _service.RotateApiKeyAsync(id, ct);
        if (newApiKey == null)
        {
            return NotFound();
        }
        return Ok(new { id, apiKey = newApiKey });
    }
}

// DTOs moved to Farm.Web.Shared.Contracts.Slicing.SlicerDtos.cs

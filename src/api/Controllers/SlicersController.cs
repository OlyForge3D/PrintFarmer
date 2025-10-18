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
    public async Task<IActionResult> HeartbeatAsync(Guid id, [FromBody] Farm.Web.Shared.Contracts.Slicing.HeartbeatDto dto)
    {
        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        var ok = await _service.HeartbeatAsync(id, dto, ct);
        return ok ? NoContent() : NotFound();
    }

    [HttpPost("{id}/deregister")]
    public async Task<IActionResult> DeregisterAsync(Guid id)
    {
        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        var ok = await _service.DeregisterAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }
}

// DTOs moved to Farm.Web.Shared.Contracts.Slicing.SlicerDtos.cs

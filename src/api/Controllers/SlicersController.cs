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
    private readonly AppDbContext _db;
    private readonly IHubContext<SlicerProgressHub> _hubContext;

    public SlicersController(AppDbContext db, IHubContext<SlicerProgressHub> hubContext)
    {
        _db = db;
        _hubContext = hubContext;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var list = await _db.SlicerServices.OrderBy(s => s.Name).ToListAsync();
        return Ok(list);
    }

    [HttpPost("register")]
    public async Task<IActionResult> RegisterAsync([FromBody] RegisterSlicerDto dto)
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

        _db.SlicerServices.Add(svc);
        await _db.SaveChangesAsync();

        // Broadcast registration event to connected clients
        try
        {
            var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
            await _hubContext.Clients.All.SendAsync("SlicerRegistered", new
            {
                id = svc.Id,
                name = svc.Name,
                version = svc.Version,
                host = svc.Host,
                maxConcurrentJobs = svc.MaxConcurrentJobs,
                status = svc.Status
            }, ct);
        }
        catch
        {
            // Non-fatal: don't fail registration if broadcast fails
        }

        var location = $"/api/slicers/{svc.Id}";
        return Created(location, new { id = svc.Id, apiKey = svc.ApiKey });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var svc = await _db.SlicerServices.FindAsync(id);
        if (svc == null)
        {
            return NotFound();
        }

        return Ok(svc);
    }

    [HttpPost("{id}/heartbeat")]
    public async Task<IActionResult> HeartbeatAsync(Guid id, [FromBody] HeartbeatDto dto)
    {
        var svc = await _db.SlicerServices.FindAsync(id);
        if (svc == null)
        {
            return NotFound();
        }

        svc.LastSeen = DateTime.UtcNow;
        svc.Status = dto.Status ?? svc.Status;
        if (dto.FreeSlots.HasValue)
        {
            // store free slots in Tags JSON for now - refinement later
            svc.Tags = dto.FreeSlots.Value.ToString();
        }

        svc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Broadcast heartbeat/status update (best-effort)
        try
        {
            var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
            await _hubContext.Clients.All.SendAsync("SlicerHeartbeat", new
            {
                id = svc.Id,
                status = svc.Status,
                freeSlots = dto.FreeSlots
            }, ct);
        }
        catch
        {
            // ignore
        }

        return NoContent();
    }

    [HttpPost("{id}/deregister")]
    public async Task<IActionResult> DeregisterAsync(Guid id)
    {
        var svc = await _db.SlicerServices.FindAsync(id);
        if (svc == null)
        {
            return NotFound();
        }

        _db.SlicerServices.Remove(svc);
        await _db.SaveChangesAsync();

        // Broadcast deregistration event
        try
        {
            var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
            await _hubContext.Clients.All.SendAsync("SlicerDeregistered", new { id = svc.Id }, ct);
        }
        catch
        {
            // ignore
        }

        return NoContent();
    }
}

public class RegisterSlicerDto
{
    public string? Name { get; set; }
    public int SlicerType { get; set; } = 1; // default to OrcaSlicer
    public string? Version { get; set; }
    public string? Host { get; set; }
    public string? UiManifestUrl { get; set; }
    public string? CapabilitiesJson { get; set; }
    public int MaxConcurrentJobs { get; set; } = 1;
    public string? Tags { get; set; }
}

public class HeartbeatDto
{
    public string? Status { get; set; }
    public int? FreeSlots { get; set; }
}

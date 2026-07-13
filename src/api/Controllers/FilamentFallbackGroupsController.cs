using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// CRUD for per-printer filament fallback groups (issue #711, F6).
/// Ordered same-material chains over existing toolhead IDs.
/// </summary>
[ApiController]
[Route("api/printers/{printerId:guid}/fallback-groups")]
[Authorize]
public class FilamentFallbackGroupsController(
    IFilamentFallbackGroupService service,
    IHubContext<PrinterHub> printerHub,
    ILogger<FilamentFallbackGroupsController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<FilamentFallbackGroupDto>), 200)]
    public async Task<ActionResult<IReadOnlyList<FilamentFallbackGroupDto>>> ListAsync(Guid printerId, CancellationToken ct)
    {
        IReadOnlyList<FilamentFallbackGroupDto> groups = await service.ListForPrinterAsync(printerId, ct);
        return Ok(groups);
    }

    [HttpGet("{groupId:guid}")]
    [ProducesResponseType(typeof(FilamentFallbackGroupDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<FilamentFallbackGroupDto>> GetAsync(Guid printerId, Guid groupId, CancellationToken ct)
    {
        FilamentFallbackGroupDto? dto = await service.GetAsync(printerId, groupId, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost]
    [ProducesResponseType(typeof(FilamentFallbackGroupDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<FilamentFallbackGroupDto>> CreateAsync(
        Guid printerId,
        [FromBody] CreateFilamentFallbackGroupRequest request,
        CancellationToken ct)
    {
        try
        {
            FilamentFallbackGroupDto dto = await service.CreateAsync(printerId, request, ct);
            await BroadcastAsync(printerId, ct);
            return CreatedAtAction(nameof(GetAsync), new { printerId, groupId = dto.Id }, dto);
        }
        catch (FilamentFallbackGroupValidationException ex)
        {
            logger.LogInformation("Fallback group create rejected: {Message}", ex.Message);
            return BadRequest(new ProblemDetails { Title = "Invalid fallback group", Detail = ex.Message, Status = 400 });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails { Title = "Not found", Detail = ex.Message, Status = 404 });
        }
    }

    [HttpPut("{groupId:guid}")]
    [ProducesResponseType(typeof(FilamentFallbackGroupDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<FilamentFallbackGroupDto>> UpdateAsync(
        Guid printerId,
        Guid groupId,
        [FromBody] UpdateFilamentFallbackGroupRequest request,
        CancellationToken ct)
    {
        try
        {
            FilamentFallbackGroupDto dto = await service.UpdateAsync(printerId, groupId, request, ct);
            await BroadcastAsync(printerId, ct);
            return Ok(dto);
        }
        catch (FilamentFallbackGroupValidationException ex)
        {
            logger.LogInformation("Fallback group update rejected: {Message}", ex.Message);
            return BadRequest(new ProblemDetails { Title = "Invalid fallback group", Detail = ex.Message, Status = 400 });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails { Title = "Not found", Detail = ex.Message, Status = 404 });
        }
    }

    [HttpDelete("{groupId:guid}")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> DeleteAsync(Guid printerId, Guid groupId, CancellationToken ct)
    {
        await service.DeleteAsync(printerId, groupId, ct);
        await BroadcastAsync(printerId, ct);
        return NoContent();
    }

    private async Task BroadcastAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            await printerHub.Clients.All.SendAsync("fallbackgroupsupdated", new { printerId }, ct);
        }
        catch (Exception ex)
        {
            // Broadcasts are best-effort; do not fail the request if the hub is unavailable.
            logger.LogWarning(ex, "Failed to broadcast fallbackgroupsupdated for printer {PrinterId}", printerId);
        }
    }
}

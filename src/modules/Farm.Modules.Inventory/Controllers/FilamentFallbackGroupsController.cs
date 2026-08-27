using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Web.Api.Infrastructure.OperatorFeatures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Inventory.Controllers;

/// <summary>
/// CRUD for per-printer filament fallback groups (issue #711, F6).
/// Ordered same-material chains over existing toolhead IDs.
/// </summary>
/// <remarks>
/// Every endpoint is gated by the <see cref="OperatorFeature.MultiSlotFallback"/> operator
/// feature. When the operator has switched multi-slot fallback off, all endpoints return
/// 404 (mirroring <c>FilamentCoverageController</c>) and no <c>fallbackgroupsupdated</c>
/// SignalR event is emitted (issue #711, FIX E).
///
/// Read endpoints require any authenticated user; configuration mutations
/// (create/update/delete) additionally require the <c>farm_admin</c> role, matching
/// <c>PrintersController</c> and <c>MaintenanceController</c> (issue #711, round-5 FIX 4).
/// </remarks>
[ApiController]
[Route("api/printers/{printerId:guid}/fallback-groups")]
[Authorize]
public class FilamentFallbackGroupsController(
    IFilamentFallbackGroupService service,
    IOperatorFeatureGate featureGate,
    IHubContext<PrinterHub> printerHub,
    ILogger<FilamentFallbackGroupsController> logger) : ControllerBase
{
    private bool FallbackEnabled => featureGate.IsEnabled(OperatorFeature.MultiSlotFallback);

    private NotFoundObjectResult FeatureDisabled()
        => OperatorFeatureProblemDetails.NotFound(featureGate, OperatorFeature.MultiSlotFallback);

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<FilamentFallbackGroupDto>), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IReadOnlyList<FilamentFallbackGroupDto>>> ListAsync(Guid printerId, CancellationToken ct)
    {
        if (!FallbackEnabled)
        {
            return FeatureDisabled();
        }

        IReadOnlyList<FilamentFallbackGroupDto> groups = await service.ListForPrinterAsync(printerId, ct);
        return Ok(groups);
    }

    [HttpGet("{groupId:guid}")]
    [ProducesResponseType(typeof(FilamentFallbackGroupDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<FilamentFallbackGroupDto>> GetAsync(Guid printerId, Guid groupId, CancellationToken ct)
    {
        if (!FallbackEnabled)
        {
            return FeatureDisabled();
        }

        FilamentFallbackGroupDto? dto = await service.GetAsync(printerId, groupId, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Resolves a currently-available fallback slot (physical dock or MMU/AMS gate) on the
    /// printer that carries the requested material, excluding the source toolhead. Read-only
    /// evidence for runout-attention downgrade logic and external callers (issue #711, FIX D).
    /// </summary>
    [HttpGet("available")]
    [ProducesResponseType(typeof(AvailableFallbackMember), 200)]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<AvailableFallbackMember>> GetAvailableFallbackAsync(
        Guid printerId,
        [FromQuery] Guid sourceToolheadId,
        [FromQuery] string material,
        CancellationToken ct)
    {
        if (!FallbackEnabled)
        {
            return FeatureDisabled();
        }

        if (string.IsNullOrWhiteSpace(material))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid request", Detail = "The 'material' query parameter is required.", Status = 400 });
        }

        AvailableFallbackMember? member = await service.FindAvailableFallbackAsync(printerId, sourceToolheadId, material, ct);
        return member is null ? NoContent() : Ok(member);
    }

    [HttpPost]
    [RequirePermission("filament_type", "admin")]
    [ProducesResponseType(typeof(FilamentFallbackGroupDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<FilamentFallbackGroupDto>> CreateAsync(
        Guid printerId,
        [FromBody] CreateFilamentFallbackGroupRequest request,
        CancellationToken ct)
    {
        if (!FallbackEnabled)
        {
            return FeatureDisabled();
        }

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
    [RequirePermission("filament_type", "admin")]
    [ProducesResponseType(typeof(FilamentFallbackGroupDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<FilamentFallbackGroupDto>> UpdateAsync(
        Guid printerId,
        Guid groupId,
        [FromBody] UpdateFilamentFallbackGroupRequest request,
        CancellationToken ct)
    {
        if (!FallbackEnabled)
        {
            return FeatureDisabled();
        }

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
    [RequirePermission("filament_type", "admin")]
    [ProducesResponseType(204)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteAsync(Guid printerId, Guid groupId, CancellationToken ct)
    {
        if (!FallbackEnabled)
        {
            return FeatureDisabled();
        }

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

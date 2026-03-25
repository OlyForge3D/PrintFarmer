using Farm.Infrastructure.Services.AutoDispatch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the auto-dispatch ready-gate workflow for printers.
/// After a print completes on an auto-dispatch-enabled printer, the operator must confirm
/// the bed is clear before the next queued job is dispatched.
/// </summary>
[ApiController]
[Route("api/auto-dispatch")]
[Authorize]
public class AutoDispatchController(
    IAutoDispatchService autoDispatchService,
    ILogger<AutoDispatchController> logger) : ControllerBase
{
    /// <summary>
    /// Get the auto-dispatch status for a printer.
    /// </summary>
    [HttpGet("{printerId:guid}/status")]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> GetStatusAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var status = await autoDispatchService.GetStatusAsync(printerId, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Mark the printer as ready (bed is clear). Returns the next queued job
    /// and filament pre-flight check result. If auto-dispatch is in Auto mode,
    /// the job is dispatched by the background service; the dispatch endpoint
    /// is idempotent so a redundant client call is harmless.
    /// </summary>
    [HttpPost("{printerId:guid}/ready")]
    [ProducesResponseType(typeof(AutoDispatchReadyResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchReadyResult>> MarkReadyAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var result = await autoDispatchService.MarkReadyAsync(printerId, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("[AutoDispatchReadyGate] MarkReady failed for printer {PrinterId}: {Error}", printerId, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Skip the next queued job (cancels it). If more jobs are queued,
    /// the printer stays in PendingReady; otherwise transitions to None.
    /// </summary>
    [HttpPost("{printerId:guid}/skip")]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> SkipNextAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var status = await autoDispatchService.SkipNextJobAsync(printerId, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel the auto-dispatch ready-gate workflow. Returns the printer to None state
    /// without affecting queued jobs.
    /// </summary>
    [HttpPost("{printerId:guid}/cancel")]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> CancelAutoAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var status = await autoDispatchService.CancelAutoAsync(printerId, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Pre-confirm the bed is clear. Allows the next queued job to dispatch
    /// immediately without waiting for PendingReady confirmation.
    /// </summary>
    [HttpPost("{printerId:guid}/pre-clear")]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> MarkPreClearAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var status = await autoDispatchService.MarkPreClearAsync(printerId, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("[AutoDispatchReadyGate] PreClear failed for printer {PrinterId}: {Error}", printerId, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Enable or disable auto-dispatch for a printer.
    /// </summary>
    [HttpPut("{printerId:guid}/enabled")]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> SetEnabledAsync(
        Guid printerId,
        [FromBody] SetAutoDispatchEnabledRequest request,
        CancellationToken ct)
    {
        try
        {
            var status = await autoDispatchService.SetEnabledAsync(printerId, request.Enabled, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get auto-dispatch status for all printers.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(AutoDispatchGlobalStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AutoDispatchGlobalStatusDto>> GetAllStatusAsync(CancellationToken ct)
    {
        var status = await autoDispatchService.GetAllStatusAsync(ct);
        return Ok(status);
    }

    /// <summary>
    /// Enable or disable auto-dispatch for all printers at once.
    /// </summary>
    [HttpPut("enabled")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(List<AutoDispatchStatusDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AutoDispatchStatusDto>>> SetAllEnabledAsync(
        [FromBody] SetAutoDispatchEnabledRequest request,
        CancellationToken ct)
    {
        var statuses = await autoDispatchService.SetAllEnabledAsync(request.Enabled, ct);
        return Ok(statuses);
    }
}

public class SetAutoDispatchEnabledRequest
{
    public bool Enabled { get; set; }
}

using Farm.Infrastructure.Services.AutoPrint;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the auto-print ready-gate workflow for printers.
/// After a print completes on an auto-print-enabled printer, the operator must confirm
/// the bed is clear before the next queued job is dispatched.
/// </summary>
[ApiController]
[Route("api/autoprint")]
[Authorize]
public class AutoPrintController(
    IAutoPrintService autoPrintService,
    ILogger<AutoPrintController> logger) : ControllerBase
{
    /// <summary>
    /// Get the auto-print status for a printer.
    /// </summary>
    [HttpGet("{printerId:guid}/status")]
    [ProducesResponseType(typeof(AutoPrintStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoPrintStatusDto>> GetStatusAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var status = await autoPrintService.GetStatusAsync(printerId, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Mark the printer as ready (bed is clear). Returns the next queued job
    /// and filament pre-flight check result. The job is NOT automatically dispatched;
    /// the client should call the dispatch endpoint if the filament check passes.
    /// </summary>
    [HttpPost("{printerId:guid}/ready")]
    [ProducesResponseType(typeof(AutoPrintReadyResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoPrintReadyResult>> MarkReadyAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var result = await autoPrintService.MarkReadyAsync(printerId, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("[AutoPrint] MarkReady failed for printer {PrinterId}: {Error}", printerId, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Skip the next queued job (cancels it). If more jobs are queued,
    /// the printer stays in PendingReady; otherwise transitions to None.
    /// </summary>
    [HttpPost("{printerId:guid}/skip")]
    [ProducesResponseType(typeof(AutoPrintStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoPrintStatusDto>> SkipNextAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var status = await autoPrintService.SkipNextJobAsync(printerId, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel the auto-print workflow. Returns the printer to None state
    /// without affecting queued jobs.
    /// </summary>
    [HttpPost("{printerId:guid}/cancel")]
    [ProducesResponseType(typeof(AutoPrintStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoPrintStatusDto>> CancelAutoAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            var status = await autoPrintService.CancelAutoAsync(printerId, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Enable or disable auto-print for a printer.
    /// </summary>
    [HttpPut("{printerId:guid}/enabled")]
    [ProducesResponseType(typeof(AutoPrintStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoPrintStatusDto>> SetEnabledAsync(
        Guid printerId,
        [FromBody] SetAutoPrintEnabledRequest request,
        CancellationToken ct)
    {
        try
        {
            var status = await autoPrintService.SetEnabledAsync(printerId, request.Enabled, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get auto-print status for all printers.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(List<AutoPrintStatusDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AutoPrintStatusDto>>> GetAllStatusAsync(CancellationToken ct)
    {
        var statuses = await autoPrintService.GetAllStatusAsync(ct);
        return Ok(statuses);
    }

    /// <summary>
    /// Enable or disable auto-print for all printers at once.
    /// </summary>
    [HttpPut("enabled")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(List<AutoPrintStatusDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AutoPrintStatusDto>>> SetAllEnabledAsync(
        [FromBody] SetAutoPrintEnabledRequest request,
        CancellationToken ct)
    {
        var statuses = await autoPrintService.SetAllEnabledAsync(request.Enabled, ct);
        return Ok(statuses);
    }
}

public class SetAutoPrintEnabledRequest
{
    public bool Enabled { get; set; }
}

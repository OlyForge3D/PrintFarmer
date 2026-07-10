using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Spoolman;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Spool coverage and runout prediction endpoints (issue #709 — F4). Reuses
/// existing toolhead/spool bindings, per-extruder gcode metadata, active +
/// assigned queued jobs, and live progress. Never invents a runout when
/// upstream data is missing — the client sees <see cref="FilamentCoverageStatus.Unknown"/>
/// with a machine-readable reason instead.
///
/// <para>
/// <b>Rebase-note (#725):</b> once <c>IOperatorFeatureGate.FilamentCoverageEnabled</c>
/// exists, gate both endpoints at the top with the shared operator feature
/// gate. When the flag is off, return <c>404</c> with a ProblemDetails payload
/// carrying <c>extensions["code"] = "featureDisabled"</c> per the #725
/// contract, and skip both the coverage computation and any broadcast.
/// </para>
/// </summary>
[ApiController]
[Route("api/printers")]
[Authorize]
[Tags("Filament Coverage")]
public class FilamentCoverageController(
    IFilamentCoverageService coverageService,
    ILogger<FilamentCoverageController> logger) : ControllerBase
{
    private readonly IFilamentCoverageService _coverageService = coverageService;
    private readonly ILogger<FilamentCoverageController> _logger = logger;

    /// <summary>
    /// Returns filament coverage and predicted runout for a single printer.
    /// </summary>
    /// <param name="id">Printer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Coverage snapshot including per-toolhead detail.</response>
    /// <response code="404">Printer does not exist.</response>
    [HttpGet("{id:guid}/filament-coverage")]
    [ProducesResponseType(typeof(PrinterFilamentCoverageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrinterFilamentCoverageDto>> GetForPrinterAsync(System.Guid id, CancellationToken ct)
    {
        PrinterFilamentCoverageDto? coverage = await _coverageService.GetForPrinterAsync(id, ct);
        if (coverage is null)
        {
            _logger.LogWarning("[FilamentCoverage] Printer {Id} not found", id);
            return NotFound(new { message = $"Printer {id} not found" });
        }

        return Ok(coverage);
    }

    /// <summary>
    /// Returns filament coverage for every printer in the fleet. Intended for
    /// the Farm grid: a single call feeds every card.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Fleet coverage snapshot.</response>
    [HttpGet("filament-coverage")]
    [ProducesResponseType(typeof(FleetFilamentCoverageDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<FleetFilamentCoverageDto>> GetForFleetAsync(CancellationToken ct)
    {
        FleetFilamentCoverageDto fleet = await _coverageService.GetForFleetAsync(ct);
        return Ok(fleet);
    }
}

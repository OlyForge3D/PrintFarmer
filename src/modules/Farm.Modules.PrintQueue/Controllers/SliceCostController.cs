using Farm.Infrastructure.Services.Cost;
using Farm.Web.Api.Controllers.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Exposes Spoolman filament cost data so the frontend can estimate print cost before slicing.
/// </summary>
[ApiController]
[Route("api/slice-cost")]
[Authorize]
public class SliceCostController(IFilamentCostProvider costProvider) : ControllerBase
{
    /// <summary>
    /// Returns the filament cost per gram for a given spool or filament product.
    /// At least one of <paramref name="spoolId"/> or <paramref name="filamentId"/> must be supplied.
    /// </summary>
    /// <param name="spoolId">Spoolman spool ID. Takes precedence over <paramref name="filamentId"/>.</param>
    /// <param name="filamentId">Spoolman filament product ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cost per gram and the source used to resolve it.</returns>
    /// <response code="200">Cost data returned; <c>costPerGram</c> may be null if Spoolman is unreachable.</response>
    /// <response code="400">Neither spoolId nor filamentId was provided.</response>
    [HttpGet("per-gram")]
    [ProducesResponseType(typeof(SliceCostResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCostPerGramAsync(
        [FromQuery] int? spoolId,
        [FromQuery] int? filamentId,
        CancellationToken ct)
    {
        if (spoolId is null && filamentId is null)
        {
            return BadRequest(new { error = "Provide spoolId or filamentId." });
        }

        if (spoolId is not null)
        {
            decimal? cost = await costProvider.GetSpoolCostPerGramAsync(spoolId.Value, ct);
            return Ok(new SliceCostResponse
            {
                CostPerGram = cost,
                Source = cost is not null ? "spool" : null
            });
        }
        else
        {
            decimal? cost = await costProvider.GetFilamentCostPerGramAsync(filamentId!.Value, ct);
            return Ok(new SliceCostResponse
            {
                CostPerGram = cost,
                Source = cost is not null ? "filament" : null
            });
        }
    }
}

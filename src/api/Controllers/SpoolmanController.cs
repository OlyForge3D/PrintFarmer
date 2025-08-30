using System;
using Farm.Web.Api.Services;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for integrating with Spoolman filament management system.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SpoolmanController(SpoolmanService spoolman) : ControllerBase
{
    /// <summary>
    /// Gets the current Spoolman integration configuration.
    /// </summary>
    /// <returns>Current Spoolman configuration including server URL and connection settings</returns>
    /// <response code="200">Returns the current Spoolman configuration</response>
    [HttpGet("config")]
    public ActionResult<SpoolmanConfigDto?> GetConfig() => spoolman.GetConfig();

    /// <summary>
    /// Updates the Spoolman integration configuration.
    /// </summary>
    /// <param name="config">New Spoolman configuration settings</param>
    /// <returns>No content if successful</returns>
    /// <response code="204">If the configuration was successfully updated</response>
    /// <response code="400">If the configuration data is invalid</response>
    [HttpPost("config")]
    public IActionResult SetConfig(SpoolmanConfigDto config)
    {
        spoolman.SetConfig(config);
        return NoContent();
    }

    /// <summary>
    /// Gets all spools from the connected Spoolman server.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all filament spools from Spoolman</returns>
    /// <response code="200">Returns the list of spools from Spoolman</response>
    /// <response code="503">If Spoolman is not configured or unavailable</response>
    [HttpGet("spools")]
    public async Task<ActionResult<IEnumerable<SpoolmanSpoolDto>>> GetSpools(CancellationToken ct)
        => Ok(await spoolman.ListSpoolsAsync(ct));
}

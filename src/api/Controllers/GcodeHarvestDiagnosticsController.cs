using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Diagnostics and test endpoints for G-code harvesting
/// </summary>
[ApiController]
[Route("api/gcode-harvest")]
[Tags("G-code Harvesting Diagnostics")]
public class GcodeHarvestDiagnosticsController(
IUnifiedLoggingService logger,
IGcodeHarvestService harvestService) : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IGcodeHarvestService _harvestService = harvestService;

    /// <summary>
    /// Extract metadata from an uploaded G-code file
    /// </summary>
    [HttpPost("analyze")]
    [ProducesResponseType(typeof(GcodeMetadataDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeMetadataDto>> AnalyzeGcodeAsync(
        IFormFile file,
        CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }

        if (!file.FileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("File must be a .gcode file");
        }

        try
        {
            using Stream stream = file.OpenReadStream();
            GcodeMetadataDto metadata = await _harvestService.ExtractMetadataAsync(stream, ct);
            return Ok(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to analyze G-code file {file.FileName}: {ex.Message}");
            return StatusCode(500, "Failed to analyze G-code file");
        }
    }

    // Test endpoints moved to GcodeHarvestTestController under /api/gcode-harvest/test/*

    /// <summary>
    /// Test endpoint to enable debug logging
    /// </summary>
    [HttpPost("debug-logs")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public IActionResult EnableDebugLogs()
    {
        try
        {
            _logger.LogInformation("Debug logging was requested");
            _logger.LogWarning("Enabling verbose logging for MoonrakerClient and GcodeHarvestService");
            return Ok(new { success = true, message = "Debug logging enabled (request logged)" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error enabling debug logs: {ex.Message}");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }
}

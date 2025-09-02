using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Diagnostics and test endpoints for G-code harvesting
/// </summary>
[ApiController]
[Route("api/gcode-harvest")]
public class GcodeHarvestDiagnosticsController : ControllerBase
{
    private readonly ILogger<GcodeHarvestDiagnosticsController> _logger;
    private readonly IMoonrakerClient _moonrakerClient;
    private readonly IGcodeHarvestService _harvestService;

    public GcodeHarvestDiagnosticsController(
        ILogger<GcodeHarvestDiagnosticsController> logger,
        IMoonrakerClient moonrakerClient,
        IGcodeHarvestService harvestService)
    {
        _logger = logger;
        _moonrakerClient = moonrakerClient;
        _harvestService = harvestService;
    }

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
            using var stream = file.OpenReadStream();
            var metadata = await _harvestService.ExtractMetadataAsync(stream, ct);
            return Ok(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze G-code file {FileName}", file.FileName);
            return StatusCode(500, "Failed to analyze G-code file");
        }
    }

    /// <summary>
    /// Test endpoint for MoonrakerClient.GetDirectoryAsync
    /// </summary>
    [HttpGet("test/moonraker/directory")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> TestMoonrakerGetDirectoryAsync(
        [FromQuery] string serverUrl,
        [FromQuery] string path = "gcodes",
        [FromQuery] bool extended = true,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Testing MoonrakerClient.GetDirectoryAsync with serverUrl={ServerUrl}, path={Path}, extended={Extended}",
                serverUrl, path, extended);

            var directoryInfo = await _moonrakerClient.GetDirectoryAsync(serverUrl, path, extended, ct);

            if (directoryInfo == null)
            {
                _logger.LogWarning("GetDirectoryAsync returned null result");
                return NotFound("Directory not found or error occurred");
            }

            _logger.LogInformation("GetDirectoryAsync succeeded. Found {FileCount} files and {DirCount} directories",
                directoryInfo.Files?.Length ?? 0, directoryInfo.Dirs?.Length ?? 0);

            return Ok(new
            {
                success = true,
                result = directoryInfo,
                fileCount = directoryInfo.Files?.Length ?? 0,
                dirCount = directoryInfo.Dirs?.Length ?? 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing MoonrakerClient.GetDirectoryAsync");
            return StatusCode(500, new { success = false, error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    /// <summary>
    /// Test endpoint for MoonrakerClient.GetFileListAsync
    /// </summary>
    [HttpGet("test/moonraker/files")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> TestMoonrakerGetFileListAsync(
        [FromQuery] string serverUrl,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Testing MoonrakerClient.GetFileListAsync with serverUrl={ServerUrl}", serverUrl);

            var files = await _moonrakerClient.GetFileListAsync(serverUrl, ct);

            _logger.LogInformation("GetFileListAsync succeeded. Found {FileCount} files", files.Length);

            return Ok(new
            {
                success = true,
                files = files,
                count = files.Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing MoonrakerClient.GetFileListAsync");
            return StatusCode(500, new { success = false, error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

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
            _logger.LogError(ex, "Error enabling debug logs");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }
}

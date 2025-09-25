using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Test endpoints for G-code harvest flows (Moonraker sources)
/// </summary>
[ApiController]
[Route("api/gcode-harvest/test/moonraker")]
public class GcodeHarvestTestController(
    IUnifiedLoggingService logger,
    IMoonrakerClient moonrakerClient) : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IMoonrakerClient _moonrakerClient = moonrakerClient;

    /// <summary>
    /// Test endpoint for MoonrakerClient.GetDirectoryAsync
    /// </summary>
    [HttpGet("directory")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> TestMoonrakerGetDirectoryAsync(
        [FromQuery] string serverUrl,
        [FromQuery] string path = "gcodes",
        [FromQuery] bool extended = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return BadRequest("serverUrl is required");
        }

        try
        {
            _logger.LogInformation($"Testing MoonrakerClient.GetDirectoryAsync with serverUrl={serverUrl}, path={path}, extended={extended}");

            Services.DirectoryInfo? directoryInfo = await _moonrakerClient.GetDirectoryAsync(serverUrl, path, extended, ct);

            if (directoryInfo == null)
            {
                _logger.LogWarning($"GetDirectoryAsync returned null result");
                return NotFound("Directory not found or error occurred");
            }

            _logger.LogInformation($"GetDirectoryAsync succeeded. Found {directoryInfo.Files?.Length ?? 0} files and {directoryInfo.Dirs?.Length ?? 0} directories");

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
            _logger.LogError($"Error testing MoonrakerClient.GetDirectoryAsync: {ex.Message}");
            return StatusCode(500, new { success = false, error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    /// <summary>
    /// Test endpoint for MoonrakerClient.GetFileListAsync
    /// </summary>
    [HttpGet("files")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> TestMoonrakerGetFileListAsync(
        [FromQuery] string serverUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return BadRequest("serverUrl is required");
        }

        try
        {
            _logger.LogInformation($"Testing MoonrakerClient.GetFileListAsync with serverUrl={serverUrl}");

            string[] files = await _moonrakerClient.GetFileListAsync(serverUrl, ct);

            _logger.LogInformation($"GetFileListAsync succeeded. Found {files.Length} files");

            return Ok(new
            {
                success = true,
                files = files,
                count = files.Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error testing MoonrakerClient.GetFileListAsync: {ex.Message}");
            return StatusCode(500, new { success = false, error = ex.Message, stackTrace = ex.StackTrace });
        }
    }
}

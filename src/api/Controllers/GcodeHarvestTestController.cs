using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Test endpoints for G-code harvest flows (Moonraker sources)
/// </summary>
[ApiController]
[Route("api/gcode-harvest/test/moonraker")]
public class GcodeHarvestTestController : ControllerBase
{
    private readonly ILogger<GcodeHarvestTestController> _logger;
    private readonly IMoonrakerClient _moonrakerClient;

    public GcodeHarvestTestController(
        ILogger<GcodeHarvestTestController> logger,
        IMoonrakerClient moonrakerClient)
    {
        _logger = logger;
        _moonrakerClient = moonrakerClient;
    }

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
            _logger.LogInformation("Testing MoonrakerClient.GetDirectoryAsync with serverUrl={ServerUrl}, path={Path}, extended={Extended}",
                serverUrl, path, extended);

            Services.DirectoryInfo? directoryInfo = await _moonrakerClient.GetDirectoryAsync(serverUrl, path, extended, ct);

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
            _logger.LogInformation("Testing MoonrakerClient.GetFileListAsync with serverUrl={ServerUrl}", serverUrl);

            string[] files = await _moonrakerClient.GetFileListAsync(serverUrl, ct);

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
}

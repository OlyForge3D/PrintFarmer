using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for testing client APIs
/// </summary>
[ApiController]
[Route("api/client-test")]
public class ClientTestController : ControllerBase
{
    private readonly ILogger<ClientTestController> _logger;
    private readonly IMoonrakerClient _moonrakerClient;
    private readonly IPrusaLinkClient _prusaLinkClient;

    public ClientTestController(
        ILogger<ClientTestController> logger,
    IMoonrakerClient moonrakerClient,
    IPrusaLinkClient prusaLinkClient)
    {
        _logger = logger;
        _moonrakerClient = moonrakerClient;
        _prusaLinkClient = prusaLinkClient;
    }

    /// <summary>
    /// Test endpoint for MoonrakerClient.GetDirectoryAsync
    /// </summary>
    [HttpGet("moonraker/directory")]
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

            var directoryInfo = await _moonrakerClient.GetDirectoryAsync(serverUrl, path, extended, ct);

            if (directoryInfo == null)
            {
                _logger.LogWarning("GetDirectoryAsync returned null result");
                return NotFound("Directory not found or error occurred");
            }

            _logger.LogInformation("GetDirectoryAsync succeeded. Found {FileCount} files and {DirCount} directories",
                directoryInfo.Files?.Length ?? 0, directoryInfo.Dirs?.Length ?? 0);

            // Return detailed info including all file data and structure
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
    [HttpGet("moonraker/files")]
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
    /// Test endpoint for MoonrakerClient.GetFileRootsAsync
    /// </summary>
    [HttpGet("moonraker/roots")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> TestMoonrakerGetFileRootsAsync(
        [FromQuery] string serverUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return BadRequest("serverUrl is required");
        }
        try
        {
            _logger.LogInformation("Testing MoonrakerClient.GetFileRootsAsync with serverUrl={ServerUrl}", serverUrl);

            var roots = await _moonrakerClient.GetFileRootsAsync(serverUrl, ct);

            _logger.LogInformation("GetFileRootsAsync succeeded. Found {RootCount} roots", roots.Length);

            return Ok(new
            {
                success = true,
                roots = roots,
                count = roots.Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing MoonrakerClient.GetFileRootsAsync");
            return StatusCode(500, new { success = false, error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    /// <summary>
    /// Test endpoint for PrusaLinkClient.GetFileListAsync
    /// </summary>
    [HttpGet("prusalink/files")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> TestPrusaLinkGetFileListAsync(
        [FromQuery] string serverUrl,
        [FromQuery] string apiKey = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return BadRequest("serverUrl is required");
        }
        try
        {
            _logger.LogInformation("Testing PrusaLinkClient.GetFileListAsync with serverUrl={ServerUrl}", serverUrl);

            var files = await _prusaLinkClient.GetFileListAsync(serverUrl, apiKey, ct);

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
            _logger.LogError(ex, "Error testing PrusaLinkClient.GetFileListAsync");
            return StatusCode(500, new { success = false, error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    // Removed duplicate unimplemented overload to satisfy analyzers
}

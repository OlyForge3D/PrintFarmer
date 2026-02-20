using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for Moonraker API diagnostics
/// </summary>
[ApiController]
[Route("api/moonraker-test")]
[Tags("Moonraker Diagnostics")]
[Authorize(Roles = "farm_admin")]
public class MoonrakerDiagnosticsController(
    IMoonrakerDiagnosticsService diagnosticsService,
    IUnifiedLoggingService logger) : ControllerBase
{
    private readonly IMoonrakerDiagnosticsService _diagnosticsService = diagnosticsService;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Test endpoint to invoke GetFileRootsAsync directly
    /// </summary>
    /// <param name="url">The Moonraker server URL to query.</param>
    [HttpGet("roots")]
    [ProducesResponseType(typeof(FileRoot[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<FileRoot[]>> GetFileRootsAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest("url is required");
        }

        _logger.LogInformation($"MoonrakerDiagnostics: GetFileRoots called for {url}", null, null);

        try
        {
            // Delegate to diagnostics service (which encapsulates retry logic)
            FileRoot[]? roots = await _diagnosticsService.GetFileRootsAsync(url);
            if (roots is null)
            {
                return Problem($"GetFileRootsAsync failed after retries", statusCode: 500);
            }

            if (roots.Length == 0)
            {
                return NotFound("No roots found");
            }

            _logger.LogInformation($"GetFileRootsAsync succeeded, found {roots.Length} roots", null, null);
            return Ok(roots);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"MoonrakerDiagnostics: Error calling GetFileRootsAsync", null, null);
            return Problem($"Error: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>
    /// Test endpoint to invoke GetDirectoryAsync directly
    /// </summary>
    /// <param name="url">The Moonraker server URL to query.</param>
    /// <param name="path">The directory path to retrieve (defaults to "gcodes").</param>
    [HttpGet("directory")]
    [ProducesResponseType(typeof(MoonrakerDirectoryInfo), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<MoonrakerDirectoryInfo>> GetDirectoryAsync(string url, string path = "gcodes")
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest("url is required");
        }

        _logger.LogInformation($"MoonrakerDiagnostics: GetDirectory called for {url}, path {path}", null, null);

        try
        {
            // Delegate to diagnostics service (which encapsulates retry logic)
            MoonrakerDirectoryInfo? directory = await _diagnosticsService.GetDirectoryAsync(url, path);
            if (directory is null)
            {
                return Problem($"GetDirectoryAsync failed after retries", statusCode: 500);
            }

            _logger.LogInformation($"GetDirectoryAsync completed", null, null);
            return Ok(directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"MoonrakerDiagnostics: Error calling GetDirectoryAsync", null, null);
            return Problem($"Error: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>
    /// Test endpoint to invoke GetDetailedFileListAsync directly
    /// </summary>
    /// <param name="url">The Moonraker server URL to query.</param>
    /// <param name="root">The root directory (defaults to "gcodes").</param>
    /// <param name="path">Optional subdirectory path within the root.</param>
    [HttpGet("filelist")]
    [ProducesResponseType(typeof(MoonrakerFileInfo[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<MoonrakerFileInfo[]>> GetDetailedFileListAsync(string url, string root = "gcodes", string? path = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest("url is required");
        }

        _logger.LogInformation($"MoonrakerDiagnostics: GetDetailedFileList called for {url}, root {root}, path {path ?? string.Empty}", null, null);

        try
        {
            // Delegate to diagnostics service (which encapsulates retry logic)
            MoonrakerFileInfo[]? fileList = await _diagnosticsService.GetDetailedFileListAsync(url, root, path);
            if (fileList is null)
            {
                return Problem($"GetDetailedFileListAsync failed after retries", statusCode: 500);
            }

            if (fileList.Length == 0)
            {
                _logger.LogInformation($"GetDetailedFileListAsync returned 0 files", null, null);
                return Ok(Array.Empty<MoonrakerFileInfo>());
            }

            _logger.LogInformation($"GetDetailedFileListAsync returned {fileList.Length} files", null, null);
            return Ok(fileList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"MoonrakerDiagnostics: Error calling GetDetailedFileListAsync", null, null);
            return Problem($"Error: {ex.Message}", statusCode: 500);
        }
    }

    // removed unused overload
}

using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for Moonraker API diagnostics
/// </summary>
[ApiController]
[Route("api/moonraker-test")]
[Tags("Moonraker Diagnostics")]
public class MoonrakerDiagnosticsController(
    IMoonrakerClient moonrakerClient,
    IUnifiedLoggingService logger) : ControllerBase
{
    private readonly IMoonrakerClient _moonrakerClient = moonrakerClient;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Test endpoint to invoke GetFileRootsAsync directly
    /// </summary>
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
            // Apply retry logic
            const int MaxRetries = 3;
            const int InitialDelayMs = 500;

            int retryCount = 0;
            bool success = false;
            FileRoot[]? roots = null;
            Exception? lastException = null;

            while (!success && retryCount < MaxRetries)
            {
                try
                {
                    if (retryCount > 0)
                    {
                        int delay = InitialDelayMs * (int)Math.Pow(2, retryCount - 1);
                        _logger.LogInformation($"Retry {retryCount}/{MaxRetries} after {delay}ms", null, null);
                        await Task.Delay(delay);
                    }

                    roots = await _moonrakerClient.GetFileRootsAsync(url);
                    success = true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retryCount++;
                    _logger.LogWarning(ex, $"GetFileRootsAsync attempt {retryCount}/{MaxRetries} failed", null, null);
                }
            }

            if (success)
            {
                if (roots is { Length: > 0 })
                {
                    _logger.LogInformation($"GetFileRootsAsync succeeded, found {roots.Length} roots", null, null);
                    return Ok(roots);
                }

                return NotFound("No roots found");
            }

            // failure path
            _logger.LogError(lastException!, $"GetFileRootsAsync failed after retries", null, null);
            return Problem($"GetFileRootsAsync failed after {MaxRetries} attempts", statusCode: 500);
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
    [HttpGet("directory")]
    [ProducesResponseType(typeof(Farm.Web.Api.Services.DirectoryInfo), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Api.Services.DirectoryInfo>> GetDirectoryAsync(string url, string path = "gcodes")
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest("url is required");
        }
        _logger.LogInformation($"MoonrakerDiagnostics: GetDirectory called for {url}, path {path}", null, null);

        try
        {
            // Apply retry logic
            const int MaxRetries = 3;
            const int InitialDelayMs = 500;

            int retryCount = 0;
            bool success = false;
            Farm.Web.Api.Services.DirectoryInfo? directory = null;
            Exception? lastException = null;

            while (!success && retryCount < MaxRetries)
            {
                try
                {
                    if (retryCount > 0)
                    {
                        int delay = InitialDelayMs * (int)Math.Pow(2, retryCount - 1);
                        _logger.LogInformation($"Retry GetDirectoryAsync {retryCount}/{MaxRetries} after {delay}ms", null, null);
                        await Task.Delay(delay);
                    }

                    directory = await _moonrakerClient.GetDirectoryAsync(url, path, extended: true);
                    success = true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retryCount++;
                    _logger.LogWarning(ex, $"GetDirectoryAsync attempt {retryCount}/{MaxRetries} failed", null, null);
                }
            }

            if (success)
            {
                if (directory is not null)
                {
                    _logger.LogInformation($"GetDirectoryAsync completed, directoryInfo is not null", null, null);
                    return Ok(directory);
                }

                _logger.LogInformation($"GetDirectoryAsync completed, directoryInfo is null", null, null);
                return NotFound("Directory info not found");
            }

            // failure path
            _logger.LogError(lastException!, $"GetDirectoryAsync failed after retries", null, null);
            return Problem($"GetDirectoryAsync failed after {MaxRetries} attempts", statusCode: 500);
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

        _logger.LogInformation($"MoonrakerDiagnostics: GetDetailedFileList called for {url}, root {root}, path {(path ?? string.Empty)}", null, null);

        try
        {
            // Apply retry logic
            const int MaxRetries = 3;
            const int InitialDelayMs = 500;

            int retryCount = 0;
            bool success = false;
            MoonrakerFileInfo[]? fileList = null;
            Exception? lastException = null;

            while (!success && retryCount < MaxRetries)
            {
                try
                {
                    if (retryCount > 0)
                    {
                        int delay = InitialDelayMs * (int)Math.Pow(2, retryCount - 1);
                        _logger.LogInformation($"Retry GetDetailedFileListAsync {retryCount}/{MaxRetries} after {delay}ms", null, null);
                        await Task.Delay(delay);
                    }

                    fileList = await _moonrakerClient.GetDetailedFileListAsync(url, root, path);
                    success = true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retryCount++;
                    _logger.LogWarning(ex, $"GetDetailedFileListAsync attempt {retryCount}/{MaxRetries} failed", null, null);
                }
            }

            if (success)
            {
                if (fileList is { Length: > 0 })
                {
                    _logger.LogInformation($"GetDetailedFileListAsync returned {fileList.Length} files", null, null);
                    return Ok(fileList);
                }

                _logger.LogInformation($"GetDetailedFileListAsync returned 0 files", null, null);
                return Ok(Array.Empty<MoonrakerFileInfo>());
            }

            // failure path
            _logger.LogError(lastException!, $"GetDetailedFileListAsync failed after retries", null, null);
            return Problem($"GetDetailedFileListAsync failed after {MaxRetries} attempts", statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"MoonrakerDiagnostics: Error calling GetDetailedFileListAsync", null, null);
            return Problem($"Error: {ex.Message}", statusCode: 500);
        }
    }

    // removed unused overload
}

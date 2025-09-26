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
        _logger.LogInformation("MoonrakerDiagnostics: GetFileRoots called for {Url}", url);

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
                        _logger.LogInformation("Retry {RetryCount}/{MaxRetries} after {DelayMs}ms",
                            retryCount, MaxRetries, delay);
                        await Task.Delay(delay);
                    }

                    roots = await _moonrakerClient.GetFileRootsAsync(url);
                    success = true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retryCount++;
                    _logger.LogWarning(ex, "GetFileRootsAsync attempt {RetryCount}/{MaxRetries} failed",
                        retryCount, MaxRetries);
                }
            }

            if (success)
            {
                if (roots is { Length: > 0 })
                {
                    _logger.LogInformation("GetFileRootsAsync succeeded, found {RootCount} roots", roots.Length);
                    return Ok(roots);
                }

                return NotFound("No roots found");
            }

            // failure path
            _logger.LogError(lastException!, "GetFileRootsAsync failed after retries");
            return Problem($"GetFileRootsAsync failed after {MaxRetries} attempts", statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoonrakerDiagnostics: Error calling GetFileRootsAsync");
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
        _logger.LogInformation("MoonrakerDiagnostics: GetDirectory called for {Url}, path {Path}", url, path);

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
                        _logger.LogInformation("Retry GetDirectoryAsync {RetryCount}/{MaxRetries} after {DelayMs}ms",
                            retryCount, MaxRetries, delay);
                        await Task.Delay(delay);
                    }

                    directory = await _moonrakerClient.GetDirectoryAsync(url, path, extended: true);
                    success = true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retryCount++;
                    _logger.LogWarning(ex, "GetDirectoryAsync attempt {RetryCount}/{MaxRetries} failed",
                        retryCount, MaxRetries);
                }
            }

            if (success)
            {
                if (directory is not null)
                {
                    _logger.LogInformation("GetDirectoryAsync completed, directoryInfo is not null");
                    return Ok(directory);
                }

                _logger.LogInformation("GetDirectoryAsync completed, directoryInfo is null");
                return NotFound("Directory info not found");
            }

            // failure path
            _logger.LogError(lastException!, "GetDirectoryAsync failed after retries");
            return Problem($"GetDirectoryAsync failed after {MaxRetries} attempts", statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoonrakerDiagnostics: Error calling GetDirectoryAsync");
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

        _logger.LogInformation("MoonrakerDiagnostics: GetDetailedFileList called for {Url}, root {Root}, path {Path}", url, root, path ?? string.Empty);

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
                        _logger.LogInformation("Retry GetDetailedFileListAsync {RetryCount}/{MaxRetries} after {DelayMs}ms",
                            retryCount, MaxRetries, delay);
                        await Task.Delay(delay);
                    }

                    fileList = await _moonrakerClient.GetDetailedFileListAsync(url, root, path);
                    success = true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retryCount++;
                    _logger.LogWarning(ex, "GetDetailedFileListAsync attempt {RetryCount}/{MaxRetries} failed",
                        retryCount, MaxRetries);
                }
            }

            if (success)
            {
                if (fileList is { Length: > 0 })
                {
                    _logger.LogInformation("GetDetailedFileListAsync returned {Count} files", fileList.Length);
                    return Ok(fileList);
                }

                _logger.LogInformation("GetDetailedFileListAsync returned 0 files");
                return Ok(Array.Empty<MoonrakerFileInfo>());
            }

            // failure path
            _logger.LogError(lastException!, "GetDetailedFileListAsync failed after retries");
            return Problem($"GetDetailedFileListAsync failed after {MaxRetries} attempts", statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoonrakerDiagnostics: Error calling GetDetailedFileListAsync");
            return Problem($"Error: {ex.Message}", statusCode: 500);
        }
    }

    // removed unused overload
}

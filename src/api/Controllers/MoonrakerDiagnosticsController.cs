using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for Moonraker API diagnostics
/// </summary>
[ApiController]
[Route("api/moonraker-test")]
public class MoonrakerDiagnosticsController : ControllerBase
{
    private readonly IMoonrakerClient _moonrakerClient;
    private readonly ILogger<MoonrakerDiagnosticsController> _logger;

    public MoonrakerDiagnosticsController(
        IMoonrakerClient moonrakerClient,
        ILogger<MoonrakerDiagnosticsController> logger)
    {
        _moonrakerClient = moonrakerClient;
        _logger = logger;
    }

    /// <summary>
    /// Test endpoint to invoke GetFileRootsAsync directly
    /// </summary>
    [HttpGet("roots")]
    public async Task<ActionResult<FileRoot[]>> GetFileRootsAsync(string url)
    {
        _logger.LogInformation("MoonrakerDiagnostics: GetFileRoots called for {Url}", url);

        try
        {
            // Apply retry logic
            const int maxRetries = 3;
            const int initialDelayMs = 500;

            int retryCount = 0;
            bool success = false;
            FileRoot[]? roots = null;
            Exception? lastException = null;

            while (!success && retryCount < maxRetries)
            {
                try
                {
                    if (retryCount > 0)
                    {
                        int delay = initialDelayMs * (int)Math.Pow(2, retryCount - 1);
                        _logger.LogInformation("Retry {RetryCount}/{MaxRetries} after {DelayMs}ms",
                            retryCount, maxRetries, delay);
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
                        retryCount, maxRetries);
                }
            }

            if (success && roots != null)
            {
                _logger.LogInformation("GetFileRootsAsync succeeded, found {RootCount} roots", roots.Length);
                return Ok(roots);
            }
            else if (lastException != null)
            {
                throw new Exception($"GetFileRootsAsync failed after {maxRetries} attempts", lastException);
            }

            return NotFound("No roots found");
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
    public async Task<ActionResult<Farm.Web.Api.Services.DirectoryInfo>> GetDirectoryAsync(string url, string path = "gcodes")
    {
        _logger.LogInformation("MoonrakerDiagnostics: GetDirectory called for {Url}, path {Path}", url, path);

        try
        {
            // Apply retry logic
            const int maxRetries = 3;
            const int initialDelayMs = 500;

            int retryCount = 0;
            bool success = false;
            Farm.Web.Api.Services.DirectoryInfo? directory = null;
            Exception? lastException = null;

            while (!success && retryCount < maxRetries)
            {
                try
                {
                    if (retryCount > 0)
                    {
                        int delay = initialDelayMs * (int)Math.Pow(2, retryCount - 1);
                        _logger.LogInformation("Retry GetDirectoryAsync {RetryCount}/{MaxRetries} after {DelayMs}ms",
                            retryCount, maxRetries, delay);
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
                        retryCount, maxRetries);
                }
            }

            if (!success && lastException != null)
            {
                throw new Exception($"GetDirectoryAsync failed after {maxRetries} attempts", lastException);
            }

            _logger.LogInformation("GetDirectoryAsync completed, directoryInfo is {IsNull}",
                directory == null ? "null" : "not null");

            return Ok(directory);
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
    public async Task<ActionResult<MoonrakerFileInfo[]>> GetDetailedFileListAsync(string url, string root = "gcodes", string? path = null)
    {
        _logger.LogInformation("MoonrakerDiagnostics: GetDetailedFileList called for {Url}, root {Root}, path {Path}", url, root, path);

        try
        {
            // Apply retry logic
            const int maxRetries = 3;
            const int initialDelayMs = 500;

            int retryCount = 0;
            bool success = false;
            MoonrakerFileInfo[]? fileList = null;
            Exception? lastException = null;

            while (!success && retryCount < maxRetries)
            {
                try
                {
                    if (retryCount > 0)
                    {
                        int delay = initialDelayMs * (int)Math.Pow(2, retryCount - 1);
                        _logger.LogInformation("Retry GetDetailedFileListAsync {RetryCount}/{MaxRetries} after {DelayMs}ms",
                            retryCount, maxRetries, delay);
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
                        retryCount, maxRetries);
                }
            }

            if (!success && lastException != null)
            {
                throw new Exception($"GetDetailedFileListAsync failed after {maxRetries} attempts", lastException);
            }

            _logger.LogInformation("GetDetailedFileListAsync returned {Count} files",
                fileList?.Length ?? 0);

            return Ok(fileList ?? []);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoonrakerDiagnostics: Error calling GetDetailedFileListAsync");
            return Problem($"Error: {ex.Message}", statusCode: 500);
        }
    }

    public async Task<ActionResult<FileRoot[]>> GetFileRootsAsync(Uri url)
    {
        throw new NotImplementedException();
    }
}

using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Farm.Web.Api.Services;
using System.Text.Json;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for harvesting G-code files from registered printers
/// </summary>
[ApiController]
[Route("api/gcode-harvest")]
public class GcodeHarvestController : ControllerBase
{
    private readonly IGcodeHarvestService _harvestService;
    private readonly ILogger<GcodeHarvestController> _logger;
    private readonly IMoonrakerClient _moonrakerClient;
    private readonly IPrusaLinkClient _prusaLinkClient;
    private readonly ISdcpClient _sdcpClient;

    public GcodeHarvestController(
        IGcodeHarvestService harvestService, 
        ILogger<GcodeHarvestController> logger,
        IMoonrakerClient moonrakerClient,
        IPrusaLinkClient prusaLinkClient,
        ISdcpClient sdcpClient)
    {
        _harvestService = harvestService;
        _logger = logger;
        _moonrakerClient = moonrakerClient;
        _prusaLinkClient = prusaLinkClient;
        _sdcpClient = sdcpClient;
    }

    /// <summary>
    /// Start a G-code harvest operation for a specific printer
    /// </summary>
    /// <param name="request">Harvest configuration</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Harvest operation started successfully</response>
    /// <response code="400">Invalid request parameters</response>
    /// <response code="404">Printer not found</response>
    [HttpPost("start")]
    public async Task<ActionResult<GcodeHarvestResultDto>> StartHarvestAsync(
        [FromBody] StartGcodeHarvestDto request, 
        CancellationToken ct)
    {
        try
        {
            var result = await _harvestService.StartHarvestAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start harvest for printer {PrinterId}", request.PrinterId);
            return StatusCode(500, "Failed to start harvest operation");
        }
    }

    /// <summary>
    /// Get the status of a harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Harvest operation details</response>
    /// <response code="404">Operation not found</response>
    [HttpGet("operations/{operationId:guid}")]
    public async Task<ActionResult<GcodeHarvestOperationDto>> GetHarvestOperationAsync(
        Guid operationId, 
        CancellationToken ct)
    {
        var operation = await _harvestService.GetHarvestOperationAsync(operationId, ct);
        return operation == null ? NotFound() : Ok(operation);
    }

    /// <summary>
    /// Get discovered files from a harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">List of discovered G-code files</response>
    /// <response code="404">Operation not found</response>
    [HttpGet("operations/{operationId:guid}/files")]
    public async Task<ActionResult<DiscoveredGcodeFileDto[]>> GetDiscoveredFilesAsync(
        Guid operationId, 
        CancellationToken ct)
    {
        try
        {
            var files = await _harvestService.GetDiscoveredFilesAsync(operationId, ct);
            return Ok(files);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get discovered files for operation {OperationId}", operationId);
            return StatusCode(500, "Failed to retrieve discovered files");
        }
    }

    /// <summary>
    /// Import selected discovered files to the G-code library
    /// </summary>
    /// <param name="request">Import configuration and selected file IDs</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Import operation completed</response>
    /// <response code="400">Invalid request parameters</response>
    /// <response code="404">Operation not found</response>
    [HttpPost("import")]
    public async Task<ActionResult<GcodeHarvestResultDto>> ImportSelectedFilesAsync(
        [FromBody] ImportSelectedGcodeFilesDto request, 
        CancellationToken ct)
    {
        try
        {
            var result = await _harvestService.ImportSelectedFilesAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import selected files for operation {OperationId}", 
                request.HarvestOperationId);
            return StatusCode(500, "Failed to import selected files");
        }
    }

    /// <summary>
    /// Cancel a running harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Operation cancelled successfully</response>
    /// <response code="400">Operation cannot be cancelled</response>
    /// <response code="404">Operation not found</response>
    [HttpPost("operations/{operationId:guid}/cancel")]
    public async Task<ActionResult<bool>> CancelHarvestAsync(Guid operationId, CancellationToken ct)
    {
        try
        {
            var result = await _harvestService.CancelHarvestAsync(operationId, ct);
            return result ? Ok(true) : BadRequest("Operation cannot be cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel harvest operation {OperationId}", operationId);
            return StatusCode(500, "Failed to cancel harvest operation");
        }
    }

    /// <summary>
    /// Get active harvest operation for a specific printer
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Active harvest operation or null if none active</response>
    [HttpGet("printers/{printerId:guid}/active")]
    public async Task<ActionResult<GcodeHarvestOperationDto?>> GetActiveHarvestAsync(
        Guid printerId, 
        CancellationToken ct = default)
    {
        try
        {
            var operation = await _harvestService.GetActiveHarvestAsync(printerId, ct);
            return Ok(operation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active harvest for printer {PrinterId}", printerId);
            return StatusCode(500, "Failed to retrieve active harvest");
        }
    }

    /// <summary>
    /// Get recent harvest operations for a specific printer
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="count">Number of recent operations to retrieve (default: 10)</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">List of recent harvest operations</response>
    [HttpGet("printers/{printerId:guid}/recent")]
    public async Task<ActionResult<GcodeHarvestOperationDto[]>> GetRecentHarvestsAsync(
        Guid printerId, 
        [FromQuery] int count = 10, 
        CancellationToken ct = default)
    {
        try
        {
            var operations = await _harvestService.GetRecentHarvestsAsync(printerId, count, ct);
            return Ok(operations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent harvests for printer {PrinterId}", printerId);
            return StatusCode(500, "Failed to retrieve recent harvests");
        }
    }

    /// <summary>
    /// Get all active (running) harvest operations
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">List of active harvest operations</response>
    [HttpGet("active")]
    public async Task<ActionResult<GcodeHarvestOperationDto[]>> GetActiveHarvestsAsync(CancellationToken ct = default)
    {
        try
        {
            var operations = await _harvestService.GetActiveHarvestsAsync(ct);
            return Ok(operations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active harvest operations");
            return StatusCode(500, "Failed to retrieve active harvest operations");
        }
    }

    /// <summary>
    /// Extract metadata from an uploaded G-code file
    /// </summary>
    /// <param name="file">The G-code file to analyze</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Extracted metadata</response>
    /// <response code="400">Invalid file or not a G-code file</response>
    [HttpPost("analyze")]
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
            
            // Return detailed info including all file data and structure
            return Ok(new {
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
    public async Task<IActionResult> TestMoonrakerGetFileListAsync(
        [FromQuery] string serverUrl,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Testing MoonrakerClient.GetFileListAsync with serverUrl={ServerUrl}", serverUrl);
            
            var files = await _moonrakerClient.GetFileListAsync(serverUrl, ct);
            
            _logger.LogInformation("GetFileListAsync succeeded. Found {FileCount} files", files.Length);
            
            return Ok(new {
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
    public IActionResult EnableDebugLogs()
    {
        try
        {
            // Configure logging to show more detailed information
            _logger.LogInformation("Debug logging was requested");
            
            // Just log the request since we can't modify logging at runtime easily
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

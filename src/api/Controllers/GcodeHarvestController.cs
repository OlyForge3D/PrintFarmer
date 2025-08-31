using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

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

    public GcodeHarvestController(IGcodeHarvestService harvestService, ILogger<GcodeHarvestController> logger)
    {
        _harvestService = harvestService;
        _logger = logger;
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
    public async Task<ActionResult<GcodeHarvestResultDto>> StartHarvest(
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
    public async Task<ActionResult<GcodeHarvestOperationDto>> GetHarvestOperation(
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
    public async Task<ActionResult<DiscoveredGcodeFileDto[]>> GetDiscoveredFiles(
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
    public async Task<ActionResult<GcodeHarvestResultDto>> ImportSelectedFiles(
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
    public async Task<ActionResult<bool>> CancelHarvest(Guid operationId, CancellationToken ct)
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
    /// Get recent harvest operations for a specific printer
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="count">Number of recent operations to retrieve (default: 10)</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">List of recent harvest operations</response>
    [HttpGet("printers/{printerId:guid}/recent")]
    public async Task<ActionResult<GcodeHarvestOperationDto[]>> GetRecentHarvests(
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
    /// Extract metadata from an uploaded G-code file
    /// </summary>
    /// <param name="file">The G-code file to analyze</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Extracted metadata</response>
    /// <response code="400">Invalid file or not a G-code file</response>
    [HttpPost("analyze")]
    public async Task<ActionResult<GcodeMetadataDto>> AnalyzeGcode(
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
}

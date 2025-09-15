using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for harvesting G-code files from registered printers
/// </summary>
[ApiController]
[Route("api/gcode-harvest")]
[Tags("G-code Harvesting")]
public class GcodeHarvestController : ControllerBase
{
    private readonly IGcodeHarvestService _harvestService;
    private readonly ILogger<GcodeHarvestController> _logger;

    public GcodeHarvestController(
        IGcodeHarvestService harvestService,
    ILogger<GcodeHarvestController> logger)
    {
        _harvestService = harvestService;
        _logger = logger;
    }

    /// <summary>
    /// Start a G-code harvest operation for a specific printer
    /// </summary>
    /// <param name="request">Harvest configuration (IncludeSubdirectories, MaxFileSizeBytes, ModifiedAfter, FileExtensions, MinFileSizeBytes, DuplicateHandling: skip|overwrite|rename)</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Harvest operation started successfully</response>
    /// <response code="400">Invalid request parameters</response>
    /// <response code="404">Printer not found</response>
    /// <remarks>
    /// Sample request:
    /// {
    ///   "printerId": "11111111-1111-1111-1111-111111111111",
    ///   "includeSubdirectories": true,
    ///   "fileExtensions": ["gcode","gco"],
    ///   "minFileSizeBytes": 1024,
    ///   "maxFileSizeBytes": 104857600,
    ///   "modifiedAfter": "2025-09-01T00:00:00Z",
    ///   "duplicateHandling": "skip"
    /// }
    /// </remarks>
    [HttpPost("start")]
    [ProducesResponseType(typeof(GcodeHarvestResultDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeHarvestResultDto>> StartHarvestAsync(
        [FromBody] StartGcodeHarvestDto request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }
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
    [ProducesResponseType(typeof(GcodeHarvestOperationDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeHarvestOperationDto>> GetHarvestOperationAsync(
        Guid operationId,
        CancellationToken ct)
    {
        try
        {
            var operation = await _harvestService.GetHarvestOperationAsync(operationId, ct);
            return operation == null ? NotFound() : Ok(operation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get harvest operation {OperationId}", operationId);
            return StatusCode(500, "Failed to retrieve harvest operation");
        }
    }

    /// <summary>
    /// Get discovered files from a harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">List of discovered G-code files</response>
    /// <response code="404">Operation not found</response>
    [HttpGet("operations/{operationId:guid}/files")]
    [ProducesResponseType(typeof(DiscoveredGcodeFileDto[]), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
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
    /// Get discovered files (paged) for a harvest operation
    /// </summary>
    /// <param name="operationId">Harvest operation ID</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Page size (max 500)</param>
    /// <param name="search">Optional case-sensitive filename substring filter</param>
    /// <param name="ct">Cancellation token</param>
    [HttpGet("operations/{operationId:guid}/files/paged")]
    [ProducesResponseType(typeof(PagedResult<DiscoveredGcodeFileDto>), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PagedResult<DiscoveredGcodeFileDto>>> GetDiscoveredFilesPagedAsync(
        Guid operationId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        try
        {
            var op = await _harvestService.GetHarvestOperationAsync(operationId, ct);
            if (op == null)
            {
                return NotFound();
            }
            var result = await _harvestService.GetDiscoveredFilesPagedAsync(operationId, page, pageSize, search, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get paged discovered files for operation {OperationId}", operationId);
            return StatusCode(500, "Failed to retrieve discovered files (paged)");
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
    [ProducesResponseType(typeof(GcodeHarvestResultDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeHarvestResultDto>> ImportSelectedFilesAsync(
        [FromBody] ImportSelectedGcodeFilesDto request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }
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
    [ProducesResponseType(typeof(bool), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
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
    [ProducesResponseType(typeof(GcodeHarvestOperationDto), 200)]
    [ProducesResponseType(500)]
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
    [ProducesResponseType(typeof(GcodeHarvestOperationDto[]), 200)]
    [ProducesResponseType(500)]
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
    [ProducesResponseType(typeof(GcodeHarvestOperationDto[]), 200)]
    [ProducesResponseType(500)]
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
    /// Get all harvest operations with optional filtering
    /// </summary>
    /// <param name="printerId">Optional printer ID to filter by</param>
    /// <param name="status">Optional status to filter by</param>
    /// <param name="limit">Maximum number of operations to return (default: 100)</param>
    /// <param name="offset">Number of operations to skip (default: 0)</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">List of harvest operations</response>
    [HttpGet("operations")]
    [ProducesResponseType(typeof(GcodeHarvestOperationDto[]), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeHarvestOperationDto[]>> GetAllHarvestsAsync(
        [FromQuery] Guid? printerId = null,
        [FromQuery] string? status = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        try
        {
            var operations = await _harvestService.GetHarvestOperationsAsync(printerId, status, limit, offset, ct);
            return Ok(operations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get harvest operations");
            return StatusCode(500, "Failed to retrieve harvest operations");
        }
    }

    // Diagnostics and test endpoints moved to GcodeHarvestDiagnosticsController
}

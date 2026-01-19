using Farm.Infrastructure;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.GcodeHarvest;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for harvesting G-code files from registered printers
/// </summary>
[ApiController]
[Route("api/gcode-harvest")]
[Tags("G-code Harvesting")]
public class GcodeHarvestController(
    IGcodeHarvestService harvestService,
    IGcodeHarvestQueue harvestQueue,
    IUnifiedLoggingService logger) : ControllerBase
{
    private readonly IGcodeHarvestService _harvestService = harvestService;
    private readonly IGcodeHarvestQueue _harvestQueue = harvestQueue;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Queue a G-code harvest operation for a specific printer
    /// </summary>
    /// <param name="request">Harvest configuration (IncludeSubdirectories, MaxFileSizeBytes, ModifiedAfter, FileExtensions, MinFileSizeBytes, DuplicateHandling: skip|overwrite|rename)</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="202">Harvest operation queued successfully</response>
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
    [ProducesResponseType(typeof(QueueHarvestResponseDto), 202)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<QueueHarvestResponseDto>> StartHarvestAsync(
        [FromBody] StartGcodeHarvestDto request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            _logger.LogInformation($"Queueing harvest operation for printer {request.PrinterId}");

            // Queue the harvest operation for background processing
            Farm.Infrastructure.Domain.GcodeHarvestQueueItem queueItem = await _harvestQueue.EnqueueAsync(request.PrinterId, request);

            var response = new QueueHarvestResponseDto(
                queueItem.Id,
                $"Harvest operation queued. Queue item ID: {queueItem.Id}",
                (GcodeHarvestQueueItemStatus)(int)queueItem.Status);

            return Accepted(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to queue harvest for printer {request.PrinterId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to queue harvest operation");
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
            GcodeHarvestOperationDto? operation = await _harvestService.GetHarvestOperationAsync(operationId, ct);
            return operation == null ? NotFound() : Ok(operation);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get harvest operation {operationId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve harvest operation");
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
            DiscoveredGcodeFileDto[] files = await _harvestService.GetDiscoveredFilesAsync(operationId, ct);
            return Ok(files);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get discovered files for operation {operationId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve discovered files");
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
            GcodeHarvestOperationDto? op = await _harvestService.GetHarvestOperationAsync(operationId, ct);
            if (op == null)
            {
                return NotFound();
            }

            PagedResult<DiscoveredGcodeFileDto> result = await _harvestService.GetDiscoveredFilesPagedAsync(operationId, page, pageSize, search, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get paged discovered files for operation {operationId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve discovered files (paged)");
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
            GcodeHarvestResultDto result = await _harvestService.ImportSelectedFilesAsync(request, ct);

            // If there were failures, log them for debugging but still return the result
            if (result.FailedFileIds?.Length > 0)
            {
                _logger.LogWarning(
                    $"Import operation {request.HarvestOperationId} completed with {result.FailedFileIds.Length} failures. " +
                    $"Imported: {result.ImportedFiles}, Skipped: {result.SkippedFileIds?.Length ?? 0}, Failed: {result.FailedFileIds.Length}");
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithSource(ex, $"Failed to import selected files for operation {request.HarvestOperationId}");

            // Log inner exceptions for better debugging
            if (ex.InnerException != null)
            {
                _logger.LogErrorWithSource(ex.InnerException, "Inner exception details");
            }

            // Return a result object with error information instead of throwing 500
            var errorResult = new GcodeHarvestResultDto(
                request.HarvestOperationId,
                false,
                $"Import operation failed: {ex.Message}",
                0, // discoveredFiles
                0, // importedFiles
                null) // errors
            {
                ErrorDetails = new Dictionary<string, string>
                {
                    { "_operation", $"Exception during import: {ex.Message}" },
                    { "_inner_exception", ex.InnerException?.Message ?? "No inner exception" }
                }
            };

            return StatusCode(StatusCodes.Status200OK, errorResult);
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
            bool result = await _harvestService.CancelHarvestAsync(operationId, ct);
            return result ? Ok(true) : BadRequest("Operation cannot be cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to cancel harvest operation {operationId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to cancel harvest operation");
        }
    }

    /// <summary>
    /// Restart/refresh file discovery for a stalled or paused harvest operation
    /// Clears previously discovered files and restarts the discovery process from scratch
    /// </summary>
    /// <param name="operationId">The harvest operation ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Discovery restart initiated successfully</response>
    /// <response code="400">Operation cannot be restarted</response>
    /// <response code="404">Operation not found</response>
    [HttpPost("operations/{operationId:guid}/restart-discovery")]
    [ProducesResponseType(typeof(bool), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<bool>> RestartDiscoveryAsync(Guid operationId, CancellationToken ct)
    {
        try
        {
            bool result = await _harvestService.RestartDiscoveryAsync(operationId, ct);
            return result ? Ok(true) : BadRequest("Operation discovery cannot be restarted");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to restart discovery for harvest operation {operationId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to restart discovery");
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
            GcodeHarvestOperationDto? operation = await _harvestService.GetActiveHarvestAsync(printerId, ct);
            return Ok(operation);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get active harvest for printer {printerId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve active harvest");
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
            GcodeHarvestOperationDto[] operations = await _harvestService.GetRecentHarvestsAsync(printerId, count, ct);
            return Ok(operations);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get recent harvests for printer {printerId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve recent harvests");
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
            GcodeHarvestOperationDto[] operations = await _harvestService.GetActiveHarvestsAsync(ct);
            return Ok(operations);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get active harvest operations: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve active harvest operations");
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
            GcodeHarvestOperationDto[] operations = await _harvestService.GetHarvestOperationsAsync(printerId, status, limit, offset, ct);
            return Ok(operations);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get harvest operations: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve harvest operations");
        }
    }

    /// <summary>
    /// Skip a discovered file in a harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation ID</param>
    /// <param name="fileId">The discovered file ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">File skipped successfully</response>
    /// <response code="404">Operation or file not found</response>
    [HttpPost("operations/{operationId:guid}/files/{fileId:guid}/skip")]
    [ProducesResponseType(typeof(bool), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<bool>> SkipDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct)
    {
        try
        {
            bool result = await _harvestService.SkipDiscoveredFileAsync(operationId, fileId, ct);
            return result ? Ok(true) : NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to skip file {fileId} in operation {operationId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to skip file");
        }
    }

    /// <summary>
    /// Retry a failed discovered file in a harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation ID</param>
    /// <param name="fileId">The discovered file ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">File retry started successfully</response>
    /// <response code="404">Operation or file not found</response>
    [HttpPost("operations/{operationId:guid}/files/{fileId:guid}/retry")]
    [ProducesResponseType(typeof(bool), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<bool>> RetryDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct)
    {
        try
        {
            bool result = await _harvestService.RetryDiscoveredFileAsync(operationId, fileId, ct);
            return result ? Ok(true) : NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to retry file {fileId} in operation {operationId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retry file");
        }
    }

    /// <summary>
    /// Get all queued harvest operations
    /// </summary>
    /// <param name="printerId">Optional: filter by printer ID</param>
    /// <param name="status">Optional: filter by status</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">List of queued operations</response>
    [HttpGet("queue")]
    [ProducesResponseType(typeof(GcodeHarvestQueueItemDto[]), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeHarvestQueueItemDto[]>> GetQueueAsync(
        [FromQuery] Guid? printerId = null,
        [FromQuery] GcodeHarvestQueueItemStatus? status = null,
        CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<Farm.Infrastructure.Domain.GcodeHarvestQueueItem> items = await _harvestQueue.GetQueuedItemsAsync(status);

            // Filter by printer ID if provided
            IEnumerable<Farm.Infrastructure.Domain.GcodeHarvestQueueItem> filtered = printerId.HasValue
                ? items.Where(i => i.PrinterId == printerId.Value)
                : items;

            GcodeHarvestQueueItemDto[] dtos = filtered.Select(item => new GcodeHarvestQueueItemDto(
                item.Id,
                item.PrinterId,
                item.Printer?.Name ?? "Unknown",
                item.QueuedAt,
                item.ProcessingStartedAt,
                item.CompletedAt,
                (GcodeHarvestQueueItemStatus)(int)item.Status,
                item.Priority,
                item.ErrorMessage,
                item.FilesFound,
                item.FilesAdded)).ToArray();

            return Ok(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get queue items: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve queue");
        }
    }

    /// <summary>
    /// Get pending harvest operations for a printer
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">List of pending operations for printer</response>
    [HttpGet("queue/pending/{printerId:guid}")]
    [ProducesResponseType(typeof(GcodeHarvestQueueItemDto[]), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeHarvestQueueItemDto[]>> GetPendingForPrinterAsync(
        Guid printerId,
        CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<Farm.Infrastructure.Domain.GcodeHarvestQueueItem> items = await _harvestQueue.GetPendingForPrinterAsync(printerId);

            GcodeHarvestQueueItemDto[] dtos = items.Select(item => new GcodeHarvestQueueItemDto(
                item.Id,
                item.PrinterId,
                item.Printer?.Name ?? "Unknown",
                item.QueuedAt,
                item.ProcessingStartedAt,
                item.CompletedAt,
                (GcodeHarvestQueueItemStatus)(int)item.Status,
                item.Priority,
                item.ErrorMessage,
                item.FilesFound,
                item.FilesAdded)).ToArray();

            return Ok(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get pending operations for printer {printerId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve pending operations");
        }
    }

    /// <summary>
    /// Queue a harvest operation for a single G-code file on a printer
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="filename">The filename of the G-code file to harvest (e.g., "gcodes/model.gcode")</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="202">Harvest operation queued successfully</response>
    /// <response code="400">Invalid request parameters</response>
    /// <response code="404">Printer not found</response>
    [HttpPost("printers/{printerId:guid}/files/harvest")]
    [ProducesResponseType(typeof(QueueHarvestResponseDto), 202)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<QueueHarvestResponseDto>> HarvestSingleFileAsync(
        Guid printerId,
        [FromQuery] string filename,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return BadRequest("Filename is required");
        }

        try
        {
            _logger.LogInformation($"Harvesting single file '{filename}' on printer {printerId}");

            // Call the harvest service to download, process, and add file to library
            GcodeHarvestResultDto result = await _harvestService.HarvestSingleFileDirectAsync(printerId, filename, ct);

            if (!result.Success)
            {
                _logger.LogWarning($"Failed to harvest file '{filename}': {result.Message}");
                return BadRequest(result);
            }

            _logger.LogInformation($"Successfully harvested file '{filename}' with ID {result.ImportedFileIds.FirstOrDefault()}");

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to harvest file '{filename}' on printer {printerId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to harvest file");
        }
    }

    /// <summary>
    /// Cancel a queued harvest operation
    /// </summary>
    /// <param name="queueItemId">The queue item ID to cancel</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Operation cancelled successfully</response>
    /// <response code="404">Queue item not found or cannot be cancelled</response>
    [HttpPost("queue/{queueItemId:guid}/cancel")]
    [ProducesResponseType(typeof(bool), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<bool>> CancelQueuedOperationAsync(
        Guid queueItemId,
        CancellationToken ct = default)
    {
        try
        {
            bool cancelled = await _harvestQueue.CancelAsync(queueItemId);
            return cancelled ? Ok(true) : NotFound("Queue item not found or already processing");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to cancel queue item {queueItemId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to cancel operation");
        }
    }
}

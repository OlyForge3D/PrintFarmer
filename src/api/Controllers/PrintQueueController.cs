using Farm.Api.DTOs;
using Farm.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Api.Controllers;

/// <summary>
/// API endpoints for print queue management
/// </summary>
[ApiController]
[Route("api/printQueue")]
[Authorize]
[Produces("application/json")]
public class PrintQueueController(
    IPrintQueueService printQueueService,
    ILogger<PrintQueueController> logger
) : ControllerBase
{
    private readonly IPrintQueueService _printQueueService = printQueueService ?? throw new ArgumentNullException(nameof(printQueueService));
    private readonly ILogger<PrintQueueController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // ============= QUERY ENDPOINTS =============

    /// <summary>
    /// Get all queued and printing jobs with file metadata
    /// </summary>
    /// <param name="filterStatus">Filter by job status (Queued, Printing, Paused, etc.)</param>
    /// <param name="filterModel">Filter by printer model name</param>
    /// <param name="filterMaterial">Filter by material type</param>
    /// <param name="limit">Maximum number of results (default 100, max 1000)</param>
    /// <param name="offset">Number of results to skip (default 0)</param>
    [HttpGet("")]
    [ProducesResponseType(typeof(List<QueuedPrintJobWithFileMetaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAllQueueAsync(
        [FromQuery] string? filterStatus,
        [FromQuery] string? filterModel,
        [FromQuery] string? filterMaterial,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (limit <= 0 || limit > 1000)
                return BadRequest(new { error = "Limit must be between 1 and 1000" });

            if (offset < 0)
                return BadRequest(new { error = "Offset must be >= 0" });

            var jobs = await _printQueueService.GetAllQueuedJobsAsync(
                filterStatus, filterModel, filterMaterial, limit, offset, cancellationToken);

            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving queue");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve queue" });
        }
    }

    /// <summary>
    /// Get print jobs for a specific printer
    /// </summary>
    [HttpGet("printer/{printerId}")]
    [ProducesResponseType(typeof(List<QueuedPrintJobDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPrinterQueueAsync(
        [FromRoute] string printerId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(printerId))
                return BadRequest(new { error = "Printer ID is required" });

            var jobs = await _printQueueService.GetPrinterQueueAsync(printerId, limit, cancellationToken);
            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving printer queue");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve printer queue" });
        }
    }

    /// <summary>
    /// Get overall queue statistics
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(QueueStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await _printQueueService.GetQueueStatsAsync(cancellationToken);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving queue statistics");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve statistics" });
        }
    }

    /// <summary>
    /// Get printer model statistics with queue counts
    /// </summary>
    [HttpGet("stats/models")]
    [ProducesResponseType(typeof(List<QueuePrinterModelStatsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetModelStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await _printQueueService.GetModelStatsAsync(cancellationToken);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving model statistics");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve model statistics" });
        }
    }

    /// <summary>
    /// Get print queue history (Phase 2)
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(QueueHistoryPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetQueueHistoryAsync(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string sortBy = "completedAt",
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (limit <= 0 || limit > 1000)
                return BadRequest(new { error = "Limit must be between 1 and 1000" });

            var history = await _printQueueService.GetQueueHistoryAsync(limit, offset, sortBy, cancellationToken);
            return Ok(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving queue history");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve history" });
        }
    }

    // ============= COMMAND ENDPOINTS =============

    /// <summary>
    /// Enqueue a new print job
    /// </summary>
    [HttpPost("")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> EnqueueJobAsync(
        [FromBody] EnqueueQueueJobRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (request == null)
                return BadRequest(new { error = "Request body is required" });

            if (string.IsNullOrEmpty(request.GcodeFileId))
                return BadRequest(new { error = "G-code file ID is required" });

            var userId = User.FindFirst("sub")?.Value ?? "system";
            var job = await _printQueueService.EnqueueJobAsync(request, userId, cancellationToken);

            return CreatedAtAction(nameof(GetAllQueueAsync), new { id = job.Id }, job);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enqueueing print job");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to enqueue job" });
        }
    }

    /// <summary>
    /// Update a print job
    /// </summary>
    [HttpPut("jobs/{jobId}")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateJobAsync(
        [FromRoute] string jobId,
        [FromBody] UpdateQueueJobRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
                return BadRequest(new { error = "Job ID is required" });

            var userId = User.FindFirst("sub")?.Value ?? "system";
            var job = await _printQueueService.UpdateJobAsync(jobId, request, userId, cancellationToken);

            return Ok(job);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update job" });
        }
    }

    /// <summary>
    /// Update job priority for reordering
    /// </summary>
    [HttpPut("jobs/{jobId}/priority")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateJobPriorityAsync(
        [FromRoute] string jobId,
        [FromBody] UpdateQueueJobPriorityRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
                return BadRequest(new { error = "Job ID is required" });

            var userId = User.FindFirst("sub")?.Value ?? "system";
            var job = await _printQueueService.UpdateJobPriorityAsync(jobId, request.NewPriority, userId, cancellationToken);

            return Ok(job);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job priority for {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update priority" });
        }
    }

    /// <summary>
    /// Pause a printing job
    /// </summary>
    [HttpPost("jobs/{jobId}/pause")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PauseJobAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
                return BadRequest(new { error = "Job ID is required" });

            var userId = User.FindFirst("sub")?.Value ?? "system";
            var job = await _printQueueService.PauseJobAsync(jobId, userId, cancellationToken);

            return Ok(job);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to pause job" });
        }
    }

    /// <summary>
    /// Resume a paused job
    /// </summary>
    [HttpPost("jobs/{jobId}/resume")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ResumeJobAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
                return BadRequest(new { error = "Job ID is required" });

            var userId = User.FindFirst("sub")?.Value ?? "system";
            var job = await _printQueueService.ResumeJobAsync(jobId, userId, cancellationToken);

            return Ok(job);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to resume job" });
        }
    }

    /// <summary>
    /// Cancel a print job
    /// </summary>
    [HttpDelete("jobs/{jobId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CancelJobAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
                return BadRequest(new { error = "Job ID is required" });

            var userId = User.FindFirst("sub")?.Value ?? "system";
            await _printQueueService.CancelJobAsync(jobId, userId, cancellationToken);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to cancel job" });
        }
    }

    /// <summary>
    /// Rerun a completed job (add it back to queue)
    /// </summary>
    [HttpPost("jobs/{jobId}/rerun")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RerunJobAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
                return BadRequest(new { error = "Job ID is required" });

            var userId = User.FindFirst("sub")?.Value ?? "system";
            var job = await _printQueueService.RerunJobAsync(jobId, userId, cancellationToken);

            return Ok(job);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rerunning job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to rerun job" });
        }
    }

    // ============= BULK OPERATIONS =============

    /// <summary>
    /// Cancel multiple print jobs
    /// </summary>
    [HttpPost("bulk/cancel")]
    [ProducesResponseType(typeof(QueueBulkOperationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> BulkCancelJobsAsync(
        [FromBody] BulkCancelQueueJobsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (request?.JobIds == null || request.JobIds.Count == 0)
                return BadRequest(new { error = "Job IDs list is required and cannot be empty" });

            var userId = User.FindFirst("sub")?.Value ?? "system";
            var result = await _printQueueService.BulkCancelJobsAsync(request.JobIds, userId, cancellationToken);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in bulk cancel operation");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to cancel jobs" });
        }
    }

    /// <summary>
    /// Reorder multiple print jobs in queue
    /// </summary>
    [HttpPost("bulk/reorder")]
    [ProducesResponseType(typeof(QueueBulkOperationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> BulkReorderJobsAsync(
        [FromBody] BulkReorderQueueJobsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (request?.Moves == null || request.Moves.Count == 0)
                return BadRequest(new { error = "Moves list is required and cannot be empty" });

            var userId = User.FindFirst("sub")?.Value ?? "system";
            var result = await _printQueueService.BulkReorderJobsAsync(request.Moves, userId, cancellationToken);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in bulk reorder operation");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to reorder jobs" });
        }
    }

    /// <summary>
    /// Seed queue history from printer APIs (Phase 2)
    /// </summary>
    [HttpPost("history/seed")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SeedHistoryAsync(
        [FromBody] SeedQueueHistoryRequest? request = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var printerIds = request?.PrinterIds;
            var daysBack = request?.DaysBack ?? 30;

            await _printQueueService.SeedHistoryFromPrintersAsync(printerIds, daysBack, cancellationToken);

            return Accepted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding queue history");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to seed history" });
        }
    }
}

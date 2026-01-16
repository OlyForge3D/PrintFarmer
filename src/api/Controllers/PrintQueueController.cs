using Farm.Api.Services.Interfaces;
using Farm.Web.Api.DTOs.PrintQueue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Api.Controllers;

/// <summary>
/// API endpoints for print queue analytics and historical data
/// </summary>
[ApiController]
[Route("api/job-queue-analytics")]
[Authorize]
[Produces("application/json")]
public class JobQueueAnalyticsController(
    IPrintQueueService printQueueService,
    ILogger<JobQueueAnalyticsController> logger
) : ControllerBase
{
    private readonly IPrintQueueService _printQueueService = printQueueService ?? throw new ArgumentNullException(nameof(printQueueService));
    private readonly ILogger<JobQueueAnalyticsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // ============= QUERY ENDPOINTS =============

    /// <summary>
    /// Get all queued and printing jobs with file metadata
    /// </summary>
    /// <param name="filterStatus">Filter by job status (Queued, Printing, Paused, etc.)</param>
    /// <param name="filterModel">Filter by printer model name</param>
    /// <param name="filterMaterial">Filter by material type</param>
    /// <param name="limit">Maximum number of results (default 100, max 1000)</param>
    /// <param name="offset">Number of results to skip (default 0)</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
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
            {
                return BadRequest(new { error = "Limit must be between 1 and 1000" });
            }

            if (offset < 0)
            {
                return BadRequest(new { error = "Offset must be >= 0" });
            }

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
            {
                return BadRequest(new { error = "Printer ID is required" });
            }

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
            {
                return BadRequest(new { error = "Limit must be between 1 and 1000" });
            }

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
            {
                return BadRequest(new { error = "Request body is required" });
            }

            if (string.IsNullOrEmpty(request.GcodeFileId))
            {
                return BadRequest(new { error = "G-code file ID is required" });
            }

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
            {
                return BadRequest(new { error = "Job ID is required" });
            }

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
            {
                return BadRequest(new { error = "Job ID is required" });
            }

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
            {
                return BadRequest(new { error = "Job ID is required" });
            }

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
            {
                return BadRequest(new { error = "Job ID is required" });
            }

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
            {
                return BadRequest(new { error = "Job ID is required" });
            }

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
            {
                return BadRequest(new { error = "Job IDs list is required and cannot be empty" });
            }

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
            {
                return BadRequest(new { error = "Moves list is required and cannot be empty" });
            }

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
    /// Get detailed information about a specific job including notes and tags
    /// </summary>
    /// <param name="jobId">The ID of the job to retrieve</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("jobs/{jobId}")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetJobDetailsAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            var job = await _printQueueService.GetJobByIdAsync(jobId, cancellationToken);

            if (job == null)
            {
                return NotFound(new { error = $"Job '{jobId}' not found" });
            }

            return Ok(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving job details for {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve job details" });
        }
    }

    /// <summary>
    /// Update job details including notes and priority
    /// </summary>
    /// <param name="jobId">The ID of the job to update</param>
    /// <param name="updates">The job fields to update</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPut("jobs/{jobId}")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateJobDetailsAsync(
        [FromRoute] string jobId,
        [FromBody] UpdateJobDetailsRequest updates,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            if (updates == null)
            {
                return BadRequest(new { error = "Update data is required" });
            }

            var updatedJob = await _printQueueService.UpdateJobDetailsAsync(jobId, updates, cancellationToken);

            if (updatedJob == null)
            {
                return NotFound(new { error = $"Job '{jobId}' not found" });
            }

            _logger.LogInformation("Job {JobId} details updated", jobId);
            return Ok(updatedJob);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid update request for job {JobId}", jobId);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job details for {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update job details" });
        }
    }

    /// <summary>
    /// Update job notes
    /// </summary>
    /// <param name="jobId">The ID of the job</param>
    /// <param name="request">The notes update request</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPut("jobs/{jobId}/notes")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateJobNotesAsync(
        [FromRoute] string jobId,
        [FromBody] UpdateJobNotesRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            if (request == null)
            {
                return BadRequest(new { error = "Notes request is required" });
            }

            if (request.Notes?.Length > 500)
            {
                return BadRequest(new { error = "Notes must be 500 characters or less" });
            }

            var success = await _printQueueService.UpdateJobNotesAsync(jobId, request.Notes, cancellationToken);

            if (!success)
            {
                return NotFound(new { error = $"Job '{jobId}' not found" });
            }

            _logger.LogInformation("Notes updated for job {JobId}", jobId);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating notes for job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update notes" });
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

    // ============= TIMELINE & ANALYTICS ENDPOINTS (Phase 3C) =============

    /// <summary>
    /// Get timeline events for visualization with optional filtering
    /// </summary>
    [HttpGet("timeline")]
    [ProducesResponseType(typeof(List<TimelineEventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTimelineAsync(
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? printerId,
        [FromQuery] string? filterStatus,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (limit <= 0 || limit > 1000)
            {
                return BadRequest(new { error = "Limit must be between 1 and 1000" });
            }

            // Validate date range
            if (dateFrom.HasValue && dateTo.HasValue && dateFrom > dateTo)
            {
                return BadRequest(new { error = "dateFrom must be before dateTo" });
            }

            var events = await _printQueueService.GetTimelineAsync(
                dateFrom, dateTo, printerId, filterStatus, limit, cancellationToken);

            _logger.LogInformation("Retrieved timeline with {Count} events", events.Count());
            return Ok(events);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving timeline");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve timeline" });
        }
    }

    /// <summary>
    /// Get complete state history for a specific job
    /// </summary>
    [HttpGet("jobs/{jobId}/state-history")]
    [ProducesResponseType(typeof(JobStateHistoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetJobStateHistoryAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            var history = await _printQueueService.GetJobStateHistoryAsync(jobId, cancellationToken);

            _logger.LogInformation("Retrieved state history for job {JobId}", jobId);
            return Ok(history);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid job ID: {JobId}", jobId);
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving state history for job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve state history" });
        }
    }

    /// <summary>
    /// Get duration analytics comparing estimated vs actual durations
    /// </summary>
    [HttpGet("duration-analytics")]
    [ProducesResponseType(typeof(DurationAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetDurationAnalyticsAsync(
        [FromQuery] string? printerId,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            // Validate date range
            if (dateFrom.HasValue && dateTo.HasValue && dateFrom > dateTo)
            {
                return BadRequest(new { error = "dateFrom must be before dateTo" });
            }

            var analytics = await _printQueueService.GetDurationAnalyticsAsync(
                printerId, dateFrom, dateTo, cancellationToken);

            _logger.LogInformation("Retrieved duration analytics for {TotalJobs} jobs", analytics.TotalJobs);
            return Ok(analytics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving duration analytics");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve analytics" });
        }
    }
}

using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Webhooks;
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
    IPrintJobManagementService printJobManagementService,
    ILogger<JobQueueAnalyticsController> logger,
    IWebhookService webhookService) : ControllerBase
{
    private readonly IPrintJobManagementService _printJobManagementService = printJobManagementService ?? throw new ArgumentNullException(nameof(printJobManagementService));
    private readonly ILogger<JobQueueAnalyticsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IWebhookService _webhookService = webhookService;

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
        CancellationToken cancellationToken = default)
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

            List<QueuedPrintJobWithFileMetaDto> jobs = await _printJobManagementService.GetAllQueuedJobsAsync(
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
    /// <param name="printerId">The unique identifier of the printer</param>
    /// <param name="limit">Maximum number of jobs to return (default 50)</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("printer/{printerId}")]
    [ProducesResponseType(typeof(List<QueuedPrintJobDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPrinterQueueAsync(
        [FromRoute] string printerId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(printerId))
            {
                return BadRequest(new { error = "Printer ID is required" });
            }

            List<QueuedPrintJobDto> jobs = await _printJobManagementService.GetPrinterQueueAsync(printerId, limit, cancellationToken);
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
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(QueueStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            QueueStatsDto stats = await _printJobManagementService.GetQueueStatsAsync(cancellationToken);
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
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("stats/models")]
    [ProducesResponseType(typeof(List<QueuePrinterModelStatsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetModelStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<QueuePrinterModelStatsDto> stats = await _printJobManagementService.GetModelStatsAsync(cancellationToken);
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
    /// <param name="limit">Maximum number of results (default 50, max 1000)</param>
    /// <param name="offset">Number of results to skip (default 0)</param>
    /// <param name="sortBy">Field to sort by (default completedAt, options: newest, oldest, duration, name, status)</param>
    /// <param name="statuses">Comma-separated list of statuses to filter by (completed, failed, cancelled)</param>
    /// <param name="dateStart">Start date filter (ISO 8601 format, inclusive)</param>
    /// <param name="dateEnd">End date filter (ISO 8601 format, inclusive)</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("history")]
    [ProducesResponseType(typeof(QueueHistoryPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetQueueHistoryAsync(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string sortBy = "completedAt",
        [FromQuery] string? statuses = null,
        [FromQuery] DateTime? dateStart = null,
        [FromQuery] DateTime? dateEnd = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (limit <= 0 || limit > 1000)
            {
                return BadRequest(new { error = "Limit must be between 1 and 1000" });
            }

            // Parse comma-separated statuses into a list
            List<string>? statusList = null;
            if (!string.IsNullOrWhiteSpace(statuses))
            {
                statusList = statuses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }

            QueueHistoryPageDto history = await _printJobManagementService.GetQueueHistoryAsync(
                limit, offset, sortBy, statusList, dateStart, dateEnd, cancellationToken);
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
    /// <param name="request">The job enqueue request containing G-code file ID and options</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> EnqueueJobAsync(
        [FromBody] EnqueueQueueJobRequest request,
        CancellationToken cancellationToken = default)
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

            string userId = User.FindFirst("sub")?.Value ?? "system";
            QueuedPrintJobDto job = await _printJobManagementService.EnqueueJobAsync(request, userId, cancellationToken);

            _webhookService.Enqueue("job.queued", new { jobId = job.Id, jobName = job.Name, priority = job.Priority });

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
    /// <param name="jobId">The unique identifier of the job</param>
    /// <param name="request">The priority update request containing the new priority value</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPut("jobs/{jobId}/priority")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateJobPriorityAsync(
        [FromRoute] string jobId,
        [FromBody] UpdateQueueJobPriorityRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            string userId = User.FindFirst("sub")?.Value ?? "system";
            QueuedPrintJobDto job = await _printJobManagementService.UpdateJobPriorityAsync(jobId, request.NewPriority, userId, cancellationToken);

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
    /// <param name="jobId">The unique identifier of the job to pause</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("jobs/{jobId}/pause")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PauseJobAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            string userId = User.FindFirst("sub")?.Value ?? "system";
            QueuedPrintJobDto job = await _printJobManagementService.PauseJobAsync(jobId, userId, cancellationToken);

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
    /// <param name="jobId">The unique identifier of the job to resume</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("jobs/{jobId}/resume")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ResumeJobAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            string userId = User.FindFirst("sub")?.Value ?? "system";
            QueuedPrintJobDto job = await _printJobManagementService.ResumeJobAsync(jobId, userId, cancellationToken);

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
    /// <param name="jobId">The unique identifier of the job to cancel</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpDelete("jobs/{jobId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CancelJobAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            string userId = User.FindFirst("sub")?.Value ?? "system";
            await _printJobManagementService.CancelJobAsync(jobId, userId, cancellationToken);

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
    /// <param name="jobId">The unique identifier of the completed job to rerun</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("jobs/{jobId}/rerun")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RerunJobAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            string userId = User.FindFirst("sub")?.Value ?? "system";
            QueuedPrintJobDto job = await _printJobManagementService.RerunJobAsync(jobId, userId, cancellationToken);

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
    /// <param name="request">The bulk cancel request containing job IDs to cancel</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("bulk/cancel")]
    [ProducesResponseType(typeof(QueueBulkOperationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> BulkCancelJobsAsync(
        [FromBody] BulkCancelQueueJobsRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request?.JobIds == null || request.JobIds.Count == 0)
            {
                return BadRequest(new { error = "Job IDs list is required and cannot be empty" });
            }

            string userId = User.FindFirst("sub")?.Value ?? "system";
            QueueBulkOperationResultDto result = await _printJobManagementService.BulkCancelJobsAsync(request.JobIds, userId, cancellationToken);

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
    /// <param name="request">The bulk reorder request containing job moves</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("bulk/reorder")]
    [ProducesResponseType(typeof(QueueBulkOperationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> BulkReorderJobsAsync(
        [FromBody] BulkReorderQueueJobsRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request?.Moves == null || request.Moves.Count == 0)
            {
                return BadRequest(new { error = "Moves list is required and cannot be empty" });
            }

            string userId = User.FindFirst("sub")?.Value ?? "system";
            QueueBulkOperationResultDto result = await _printJobManagementService.BulkReorderJobsAsync(request.Moves, userId, cancellationToken);

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
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            QueuedPrintJobDto? job = await _printJobManagementService.GetJobByIdAsync(jobId, cancellationToken);

            return job == null ? NotFound(new { error = $"Job '{jobId}' not found" }) : Ok(job);
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
        CancellationToken cancellationToken = default)
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

            QueuedPrintJobDto? updatedJob = await _printJobManagementService.UpdateJobDetailsAsync(jobId, updates, cancellationToken);

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
        CancellationToken cancellationToken = default)
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

            bool success = await _printJobManagementService.UpdateJobNotesAsync(jobId, request.Notes, cancellationToken);

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
    /// Seed queue history from printer APIs.
    /// Fetches all available history (up to 10,000 jobs per printer) and uses
    /// deduplication to prevent duplicates. Safe to call multiple times.
    /// </summary>
    /// <param name="request">Optional request specifying printer IDs to seed from</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("history/seed")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SeedHistoryAsync(
        [FromBody] SeedQueueHistoryRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            List<string>? printerIds = request?.PrinterIds;

            await _printJobManagementService.SeedHistoryFromPrintersAsync(printerIds, cancellationToken);

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
    /// <param name="dateFrom">Optional start date for filtering events</param>
    /// <param name="dateTo">Optional end date for filtering events</param>
    /// <param name="printerId">Optional printer ID to filter events</param>
    /// <param name="filterStatus">Optional status filter for events</param>
    /// <param name="limit">Maximum number of events to return (default 100, max 1000)</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
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
        CancellationToken cancellationToken = default)
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

            IEnumerable<TimelineEventDto> events = await _printJobManagementService.GetTimelineAsync(
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
    /// <param name="jobId">The unique identifier of the job</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("jobs/{jobId}/state-history")]
    [ProducesResponseType(typeof(JobStateHistoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetJobStateHistoryAsync(
        [FromRoute] string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return BadRequest(new { error = "Job ID is required" });
            }

            JobStateHistoryDto history = await _printJobManagementService.GetJobStateHistoryAsync(jobId, cancellationToken);

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
    /// <param name="printerId">Optional printer ID to filter analytics</param>
    /// <param name="dateFrom">Optional start date for filtering</param>
    /// <param name="dateTo">Optional end date for filtering</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("duration-analytics")]
    [ProducesResponseType(typeof(DurationAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetDurationAnalyticsAsync(
        [FromQuery] string? printerId,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate date range
            if (dateFrom.HasValue && dateTo.HasValue && dateFrom > dateTo)
            {
                return BadRequest(new { error = "dateFrom must be before dateTo" });
            }

            DurationAnalyticsDto analytics = await _printJobManagementService.GetDurationAnalyticsAsync(
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

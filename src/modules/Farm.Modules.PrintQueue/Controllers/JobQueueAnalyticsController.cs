using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Api.Controllers;

/// <summary>
/// API endpoints for print queue analytics and historical data
/// </summary>
[ApiController]
[Route("api/job-queue-analytics")]
[Authorize]
[Produces("application/json")]

// S6960: Sonar suggests splitting this controller into 2 smaller ones. Its endpoints are
// cohesive read-only analytics/history views over the same print-queue domain and share the
// same authorization/DI surface; splitting would add controller-count/routing overhead without
// improving readability or testability. Deliberately not refactored — tracked as a design
// decision, not a defect, per issue #2094.
#pragma warning disable S6960
public class JobQueueAnalyticsController(
    IPrintJobManagementService printJobManagementService,
    IJobCostCalculationService jobCostCalculationService,
    ILogger<JobQueueAnalyticsController> logger,
    AppDbContext? db = null,
    IQueueResourceAuthorizationService? resourceAuthorization = null) : ControllerBase
{
    private readonly IPrintJobManagementService _printJobManagementService = printJobManagementService ?? throw new ArgumentNullException(nameof(printJobManagementService));
    private readonly IJobCostCalculationService _jobCostCalculationService = jobCostCalculationService ?? throw new ArgumentNullException(nameof(jobCostCalculationService));
    private readonly ILogger<JobQueueAnalyticsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Get all queued and printing jobs with file metadata
    /// </summary>
    /// <param name="filterStatus">Filter by job status (Queued, Printing, Paused, etc.)</param>
    /// <param name="filterModel">Filter by printer model name</param>
    /// <param name="filterMaterial">Filter by material type</param>
    /// <param name="deadlineStart">Filter jobs with deadline at or after this UTC timestamp</param>
    /// <param name="deadlineEnd">Filter jobs with deadline at or before this UTC timestamp</param>
    /// <param name="queuedFrom">Filter jobs queued at or after this UTC timestamp. Only honored for terminal (History-style) views; ignored for the active queue, which reflects current state and is never date-windowed.</param>
    /// <param name="queuedTo">Filter jobs queued at or before this UTC timestamp. Only honored for terminal (History-style) views; ignored for the active queue, which reflects current state and is never date-windowed.</param>
    /// <param name="sortBy">Sort mode (priority, deadline, deadline_desc)</param>
    /// <param name="limit">Maximum number of results (default 100, max 1000)</param>
    /// <param name="offset">Number of results to skip (default 0)</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(List<QueuedPrintJobWithFileMetaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAllQueueAsync(
        [FromQuery] string? filterStatus,
        [FromQuery] string? filterModel,
        [FromQuery] string? filterMaterial,
        [FromQuery] DateTime? deadlineStart = null,
        [FromQuery] DateTime? deadlineEnd = null,
        [FromQuery] DateTime? queuedFrom = null,
        [FromQuery] DateTime? queuedTo = null,
        [FromQuery] string sortBy = "priority",
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
                filterStatus, filterModel, filterMaterial, deadlineStart, deadlineEnd, sortBy, limit, offset, queuedFrom, queuedTo, cancellationToken);

            if (resourceAuthorization is null)
            {
                return Ok(jobs);
            }

            // Zero-query fast path: claims-based farm-admin check, mirroring
            // CanAccessJobAsync's short-circuit before falling back to the
            // DB-backed batched authorization check below.
            if (PrintFarmerPermissions.IsFarmAdmin(User))
            {
                return Ok(jobs);
            }

            Guid[] jobIds = jobs
                .Select(job => Guid.TryParse(job.Job.Id, out Guid id) ? id : (Guid?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToArray();
            IReadOnlySet<Guid> authorizedJobIds =
                await resourceAuthorization.FilterActorAccessibleJobIdsAsync(
                    QueueActorIdentity.Resolve(User),
                    jobIds,
                    PrinterGroupAccessLevel.View,
                    cancellationToken);
            List<QueuedPrintJobWithFileMetaDto> authorized = jobs
                .Where(job => Guid.TryParse(job.Job.Id, out Guid id) && authorizedJobIds.Contains(id))
                .ToList();

            return Ok(authorized);
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
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
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
            if (Guid.TryParse(printerId, out Guid parsedPrinterId) &&
                resourceAuthorization is not null &&
                !await resourceAuthorization.CanAccessPrinterAsync(
                    User,
                    parsedPrinterId,
                    PrinterGroupAccessLevel.View,
                    cancellationToken))
            {
                return NotFound();
            }

            if (string.IsNullOrEmpty(printerId))
            {
                return BadRequest(new { error = "Printer ID is required" });
            }

            List<QueuedPrintJobDto> jobs = await _printJobManagementService.GetPrinterQueueAsync(printerId, limit, cancellationToken);
            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving printer queue for {PrinterId}", LogSanitizer.Sanitize(printerId));
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve printer queue", details = ex.Message });
        }
    }

    /// <summary>
    /// Get compact per-printer queue summaries (queued/printing counts and the printing job's
    /// position) for every printer in one call. Replaces the N per-printer
    /// <see cref="GetPrinterQueueAsync"/> round trips the compact printer grid previously made
    /// to derive its "X of Y" label — this endpoint computes every summary from a single
    /// grouped query with no GcodeFile/AssignedPrinter includes. Access is scoped with a single
    /// batched <see cref="IQueueResourceAuthorizationService.FilterAccessiblePrinterIdsAsync"/>
    /// call (constant query count regardless of printer count) rather than looping
    /// <see cref="IQueueResourceAuthorizationService.CanAccessPrinterAsync"/> per printer.
    /// Printers the caller cannot access, and printers with no active (Queued or Printing) job,
    /// are both omitted from the response.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("printer-summaries")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(List<PrinterQueueSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPrinterQueueSummariesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<PrinterQueueSummaryDto> summaries = await _printJobManagementService.GetPrinterQueueSummariesAsync(cancellationToken);

            if (resourceAuthorization is null)
            {
                return Ok(summaries);
            }

            Guid[] printerIds = summaries.Select(summary => summary.PrinterId).Distinct().ToArray();
            IReadOnlySet<Guid> allowedPrinterIds = await resourceAuthorization.FilterAccessiblePrinterIdsAsync(
                User,
                printerIds,
                PrinterGroupAccessLevel.View,
                cancellationToken);
            List<PrinterQueueSummaryDto> authorized = summaries
                .Where(summary => allowedPrinterIds.Contains(summary.PrinterId))
                .ToList();

            return Ok(authorized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving printer queue summaries");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve printer queue summaries" });
        }
    }

    /// <summary>
    /// Get overall queue statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("stats")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
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
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
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
    /// <param name="sortBy">Field to sort by (default completedAt, options: newest, oldest, duration, name, status, deadline, deadline_desc)</param>
    /// <param name="statuses">Comma-separated list of statuses to filter by (completed, failed, cancelled)</param>
    /// <param name="dateStart">Start date filter (ISO 8601 format, inclusive)</param>
    /// <param name="dateEnd">End date filter (ISO 8601 format, inclusive)</param>
    /// <param name="deadlineStart">Deadline start filter (ISO 8601 format, inclusive)</param>
    /// <param name="deadlineEnd">Deadline end filter (ISO 8601 format, inclusive)</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("history")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
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
        [FromQuery] DateTime? deadlineStart = null,
        [FromQuery] DateTime? deadlineEnd = null,
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
                limit, offset, sortBy, statusList, dateStart, dateEnd, deadlineStart, deadlineEnd, cancellationToken);
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
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(QueuedPrintJobDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> EnqueueJobAsync(
        [FromBody] EnqueueQueueJobRequest request,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return UnprocessableEntity(new
        {
            error = "queue_creation_endpoint_moved",
            detail = "Create queue jobs through POST /api/job-queue.",
        });
    }

    /// <summary>
    /// Update job priority for reordering
    /// </summary>
    /// <param name="jobId">The unique identifier of the job</param>
    /// <param name="request">The priority update request containing the new priority value</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPut("jobs/{jobId}/priority")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
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

            if (await CheckJobPreconditionAsync(jobId, cancellationToken) is { } precondition)
            {
                return precondition;
            }

            string userId = QueueActorIdentity.Resolve(User);
            QueuedPrintJobDto job = await _printJobManagementService.UpdateJobPriorityAsync(
                jobId,
                request.NewPriority,
                userId,
                ReadIfMatch(),
                cancellationToken);

            return Ok(job);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                       QueueRevisionConflictException or
                                       QueueSemanticConflictException or
                                       DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job priority for {JobId}", LogSanitizer.Sanitize(jobId));
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update priority" });
        }
    }

    /// <summary>
    /// Pause a printing job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to pause</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("jobs/{jobId}/pause")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
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

            if (await CheckJobPreconditionAsync(jobId, cancellationToken) is { } precondition)
            {
                return precondition;
            }

            string userId = QueueActorIdentity.Resolve(User);
            QueuedPrintJobDto job = await _printJobManagementService.PauseJobAsync(
                jobId,
                userId,
                ReadIfMatch(),
                cancellationToken);

            return Accepted(job);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                       QueueRevisionConflictException or
                                       QueueSemanticConflictException or
                                       DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing job {JobId}", LogSanitizer.Sanitize(jobId));
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to pause job" });
        }
    }

    /// <summary>
    /// Resume a paused job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to resume</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("jobs/{jobId}/resume")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
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

            if (await CheckJobPreconditionAsync(jobId, cancellationToken) is { } precondition)
            {
                return precondition;
            }

            string userId = QueueActorIdentity.Resolve(User);
            QueuedPrintJobDto job = await _printJobManagementService.ResumeJobAsync(
                jobId,
                userId,
                ReadIfMatch(),
                cancellationToken);

            return Accepted(job);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                       QueueRevisionConflictException or
                                       QueueSemanticConflictException or
                                       DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming job {JobId}", LogSanitizer.Sanitize(jobId));
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to resume job" });
        }
    }

    /// <summary>
    /// Cancel a print job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to cancel</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpDelete("jobs/{jobId}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
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

            if (await CheckJobPreconditionAsync(jobId, cancellationToken) is { } precondition)
            {
                return precondition;
            }

            string userId = QueueActorIdentity.Resolve(User);
            await _printJobManagementService.CancelJobAsync(
                jobId,
                userId,
                ReadIfMatch(),
                cancellationToken);

            return NoContent();
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                       QueueRevisionConflictException or
                                       QueueSemanticConflictException or
                                       DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling job {JobId}", LogSanitizer.Sanitize(jobId));
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to cancel job" });
        }
    }

    /// <summary>
    /// Rerun a completed job (add it back to queue)
    /// </summary>
    /// <param name="jobId">The unique identifier of the completed job to rerun</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("jobs/{jobId}/rerun")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
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

            if (await CheckJobPreconditionAsync(jobId, cancellationToken) is { } precondition)
            {
                return precondition;
            }

            string userId = QueueActorIdentity.Resolve(User);
            string etag = ReadIfMatch()!;
            QueuedPrintJobDto job = await _printJobManagementService.RerunJobAsync(
                jobId,
                userId,
                etag,
                cancellationToken);

            return Ok(job);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                       QueueRevisionConflictException or
                                       DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rerunning job {JobId}", LogSanitizer.Sanitize(jobId));
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
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
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

            foreach (string jobId in request.JobIds)
            {
                request.JobETags.TryGetValue(jobId, out string? etag);
                if (await CheckJobPreconditionAsync(
                        jobId,
                        cancellationToken,
                        etag) is { } precondition)
                {
                    return precondition;
                }
            }

            string userId = QueueActorIdentity.Resolve(User);
            QueueBulkOperationResultDto result =
                await _printJobManagementService.BulkCancelJobsAsync(
                    request.JobIds,
                    userId,
                    request.JobETags,
                    cancellationToken);

            return Ok(result);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                       QueueRevisionConflictException or
                                       DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in bulk cancel operation");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to cancel jobs" });
        }
    }

    /// <summary>
    /// Get detailed information about a specific job including notes and tags
    /// </summary>
    /// <param name="jobId">The ID of the job to retrieve</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpGet("jobs/{jobId}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
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

            if (!Guid.TryParse(jobId, out Guid id) ||
                !await CanReadJobAsync(id, cancellationToken))
            {
                return NotFound(new { error = "job_not_found" });
            }

            QueuedPrintJobDto? job = await _printJobManagementService.GetJobByIdAsync(jobId, cancellationToken);

            return job == null ? NotFound(new { error = $"Job '{jobId}' not found" }) : Ok(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving job details for {JobId}", LogSanitizer.Sanitize(jobId));
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
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
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

            if (await CheckJobPreconditionAsync(jobId, cancellationToken) is { } precondition)
            {
                return precondition;
            }

            if (updates == null)
            {
                return BadRequest(new { error = "Update data is required" });
            }

            QueuedPrintJobDto? updatedJob =
                await _printJobManagementService.UpdateJobDetailsAsync(
                    jobId,
                    updates,
                    QueueActorIdentity.Resolve(User),
                    ReadIfMatch(),
                    cancellationToken);

            if (updatedJob == null)
            {
                return NotFound(new { error = $"Job '{jobId}' not found" });
            }

            _logger.LogInformation("Job {JobId} details updated", LogSanitizer.Sanitize(jobId));
            return Ok(updatedJob);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                       QueueRevisionConflictException or
                                       DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid update request for job {JobId}", LogSanitizer.Sanitize(jobId));
            return BadRequest(new { error = ex.Message });
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning(ex, "Deadline policy validation failed for job {JobId}", LogSanitizer.Sanitize(jobId));
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job details for {JobId}", LogSanitizer.Sanitize(jobId));
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
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
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

            if (await CheckJobPreconditionAsync(jobId, cancellationToken) is { } precondition)
            {
                return precondition;
            }

            if (request == null)
            {
                return BadRequest(new { error = "Notes request is required" });
            }

            if (request.Notes?.Length > 500)
            {
                return BadRequest(new { error = "Notes must be 500 characters or less" });
            }

            bool success = await _printJobManagementService.UpdateJobNotesAsync(
                jobId,
                request.Notes,
                QueueActorIdentity.Resolve(User),
                ReadIfMatch(),
                cancellationToken);

            if (!success)
            {
                return NotFound(new { error = $"Job '{jobId}' not found" });
            }

            _logger.LogInformation("Notes updated for job {JobId}", LogSanitizer.Sanitize(jobId));
            return NoContent();
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                       QueueRevisionConflictException or
                                       DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating notes for job {JobId}", LogSanitizer.Sanitize(jobId));
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
    [RequirePermission(PrintFarmerPermissions.Queue.Reconcile)]
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

    /// <summary>
    /// Remove existing duplicate history jobs created before the harvest-time dedup guard.
    /// Duplicates are seeded history jobs that share the same printer and the same whole-second
    /// actual start time. Native (non-seeded) jobs are always retained. Defaults to a dry run
    /// that only reports what would be removed; pass <c>dryRun=false</c> to actually delete.
    /// </summary>
    /// <param name="dryRun">When true (default), reports duplicates without deleting them.</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    [HttpPost("history/deduplicate")]
    [RequirePermission("queue", "admin")]
    [ProducesResponseType(typeof(DeduplicateHistoryResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeduplicateHistoryAsync(
        [FromQuery] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            DeduplicateHistoryResultDto result =
                await _printJobManagementService.DeduplicateSeededHistoryAsync(dryRun, cancellationToken);

            _logger.LogInformation(
                "History deduplication {Mode} completed: {Groups} group(s), {Jobs} job(s)",
                dryRun ? "dry-run" : "cleanup",
                result.DuplicateGroups,
                result.JobsRemoved);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deduplicating queue history");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to deduplicate history" });
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
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
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
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
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

            if (!Guid.TryParse(jobId, out Guid id) ||
                !await CanReadJobAsync(id, cancellationToken))
            {
                return NotFound(new { error = "job_not_found" });
            }

            JobStateHistoryDto history = await _printJobManagementService.GetJobStateHistoryAsync(jobId, cancellationToken);

            _logger.LogInformation("Retrieved state history for job {JobId}", LogSanitizer.Sanitize(jobId));
            return Ok(history);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid job ID: {JobId}", LogSanitizer.Sanitize(jobId));
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving state history for job {JobId}", LogSanitizer.Sanitize(jobId));
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
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
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

    /// <summary>
    /// Get detailed cost breakdown for a specific job
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("jobs/{id}/cost")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(JobCostBreakdownDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetJobCostBreakdownAsync(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await CanReadJobAsync(id, cancellationToken))
            {
                return NotFound(new { error = "job_not_found" });
            }

            var job = await _printJobManagementService.GetJobCostBreakdownAsync(id, cancellationToken);

            if (job == null)
            {
                return NotFound(new { error = $"Job {id} not found" });
            }

            return Ok(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cost breakdown for job {JobId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve cost breakdown" });
        }
    }

    /// <summary>
    /// Update job cost with manual overrides
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <param name="request">Cost override values</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpPut("jobs/{id}/cost")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(JobCostBreakdownDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateJobCostAsync(
        [FromRoute] Guid id,
        [FromBody] UpdateJobCostRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request == null)
            {
                return BadRequest(new { error = "Request body is required" });
            }

            if (await CheckJobPreconditionAsync(id.ToString(), cancellationToken) is { } precondition)
            {
                return precondition;
            }

            var updated = await _printJobManagementService.UpdateJobCostAsync(
                id,
                request.MaterialCostUsd,
                request.EnergyCostUsd,
                request.MachineTimeCostUsd,
                request.LaborCostUsd,
                QueueActorIdentity.Resolve(User),
                ReadIfMatch(),
                cancellationToken);

            if (updated == null)
            {
                return NotFound(new { error = $"Job {id} not found" });
            }

            return Ok(updated);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                       QueueRevisionConflictException or
                                       DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating cost for job {JobId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update cost" });
        }
    }

    /// <summary>
    /// Recalculates costs for all completed jobs that are missing cost data.
    /// Uses the current cost settings and price cascade (including default material prices).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of jobs that were successfully recalculated.</returns>
    [HttpPost("recalculate-costs")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RecalculateAllCostsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Bulk cost recalculation requested.");
            int recalculated = await _jobCostCalculationService.RecalculateAllAsync(cancellationToken);
            return Ok(new { recalculated, message = $"Successfully recalculated costs for {recalculated} jobs." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during bulk cost recalculation.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to recalculate costs." });
        }
    }

    private async Task<IActionResult?> CheckJobPreconditionAsync(
        string jobId,
        CancellationToken cancellationToken,
        string? explicitEtag = null)
    {
        string? supplied = explicitEtag ?? ReadIfMatch();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new { error = "precondition_required", detail = "If-Match is required." });
        }

        if (db is null || !Guid.TryParse(jobId, out Guid id))
        {
            return BadRequest(new { error = "Invalid job ID." });
        }

        if (resourceAuthorization is not null &&
            !await resourceAuthorization.CanAccessJobAsync(
                User,
                id,
                PrinterGroupAccessLevel.Submit,
                cancellationToken))
        {
            return NotFound(new { error = "job_not_found" });
        }

        long? actualRevision = await db.PrintJobs
            .AsNoTracking()
            .Where(job => job.Id == id)
            .Select(job => (long?)job.Revision)
            .SingleOrDefaultAsync(cancellationToken);
        if (actualRevision is null)
        {
            return NotFound(new { error = "job_not_found" });
        }

        byte[] actual = RevisionETag.EncodeBytes(actualRevision.Value);
        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(supplied);
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "If-Match must be a base-64 encoded ETag." });
        }

        return expected.SequenceEqual(actual)
            ? null
            : StatusCode(
                StatusCodes.Status412PreconditionFailed,
                new { error = "revision_conflict", detail = "The job changed; re-fetch and retry." });
    }

    private Task<bool> CanReadJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        resourceAuthorization is null
            ? Task.FromResult(true)
            : resourceAuthorization.CanAccessJobAsync(
                User,
                jobId,
                PrinterGroupAccessLevel.View,
                cancellationToken);

    private ObjectResult MapRevisionException(Exception exception) => exception switch
    {
        QueuePreconditionRequiredException precondition => StatusCode(
            StatusCodes.Status428PreconditionRequired,
            new { error = "precondition_required", detail = precondition.Message }),
        QueueRevisionConflictException conflict => StatusCode(
            StatusCodes.Status412PreconditionFailed,
            new { error = "revision_conflict", detail = conflict.Message }),
        QueueSemanticConflictException conflict => StatusCode(
            StatusCodes.Status409Conflict,
            new { error = "semantic_conflict", detail = conflict.Message }),
        DbUpdateConcurrencyException => StatusCode(
            StatusCodes.Status412PreconditionFailed,
            new
            {
                error = "revision_conflict",
                detail = "The resource changed during the update. Re-fetch the ETag and retry.",
            }),
        _ => StatusCode(
            StatusCodes.Status500InternalServerError,
            new { error = "unexpected_error" }),
    };

    private string? ReadIfMatch()
    {
        string? value = Request.Headers.IfMatch.FirstOrDefault();
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().TrimStart('W', '/').Trim('"');
    }
}
#pragma warning restore S6960

using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the print job queue and printer assignment
/// </summary>
[ApiController]
[Route("api/job-queue")]
[Tags("Print Job Queue")]
[Authorize]
public class JobQueueController(
    IJobQueueService queueService,
    IPrintJobManagementService printJobManagementService,
    IPrintJobCompletionService printJobCompletionService,
    IJobDispatchService jobDispatchService,
    IBatchDispatchService batchDispatchService,
    IBedClearAcknowledgementService bedClearAcknowledgementService,
    IPrinterStatusCacheReader printerStatusCache,
    IPrintFarmerTelemetryService telemetryService,
    ILogger<JobQueueController> logger) : ControllerBase
{
    /// <summary>
    /// Get queue overview with optional compatibility filtering.
    /// Filters printers by model, nozzle diameter, and/or material type.
    /// All filtering is case-insensitive. Nozzle matching uses ±0.01mm tolerance.
    /// </summary>
    /// <param name="model">Optional printer model name or slicer alias (e.g., "COREONEL", "Prusa MK4")</param>
    /// <param name="nozzle">Optional required nozzle diameter in mm (e.g., 0.4)</param>
    /// <param name="material">Optional required material type (e.g., "PLA", "PCTG")</param>
    [HttpGet]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(IEnumerable<QueueOverviewDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<QueueOverviewDto>>> GetQueueAsync(
        [FromQuery] string? model = null,
        [FromQuery] decimal? nozzle = null,
        [FromQuery] string? material = null)
    {
        try
        {
            IReadOnlyList<QueueOverviewDto> dtos = await queueService.GetQueueOverviewAsync(model, nozzle, material, CancellationToken.None);
            return Ok(dtos);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving print job queue");
            return Problem("An error occurred while retrieving the queue", statusCode: 500);
        }
    }

    /// <summary>
    /// Add a new job to the queue
    /// </summary>
    /// <param name="request">The print job request containing G-code file ID and optional settings.</param>
    [HttpPost]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> QueueJobAsync([FromBody] QueuePrintJobDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            // Parse userId from claims for ACL enforcement — fail closed for authenticated requests
            string? userIdStr = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User?.FindFirst("sub")?.Value;

            if (!Guid.TryParse(userIdStr, out Guid parsed))
            {
                logger.LogWarning("Queue job denied: unable to resolve user identity from claims (raw value: {UserIdStr})", userIdStr);
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    new { error = "Unable to verify group access — user identity could not be resolved." });
            }

            Guid? userId = parsed;

            JobQueuePrintJobDto? added = await queueService.AddJobToQueueAsync(request, userId, CancellationToken.None);
            if (added == null)
            {
                return NotFound($"G-code file with ID {request.GcodeFileId} not found or no available printer");
            }

            if (!string.IsNullOrWhiteSpace(added.RowVersion))
            {
                Response.Headers.ETag = $"\"{added.RowVersion}\"";
            }

            if (added.IsIdempotentReplay)
            {
                // Explicit replay signal so clients can distinguish "created" from
                // "your earlier identical request already created this".
                Response.Headers["Idempotency-Replayed"] = "true";
                return Ok(added);
            }

            Response.Headers["Idempotency-Replayed"] = "false";

            string location = $"/api/job-queue/{added.Id}";
            return Created(location, added);
        }
        catch (QueueJobIdempotencyConflictException ex)
        {
            return Conflict(new { error = "idempotency_payload_mismatch", detail = ex.Message });
        }
        catch (QueueGroupAccessDeniedException)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "You do not have permission to submit jobs to this printer group." });
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error queueing print job for file {RequestGcodeFileId}", request.GcodeFileId);
            return Problem("An error occurred while queueing the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Get a specific job
    /// </summary>
    /// <param name="id">The unique identifier of the job to retrieve.</param>
    [HttpGet("{id:guid}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> GetJobAsync(Guid id)
    {
        try
        {
            JobQueuePrintJobDto? dto = await queueService.GetJobAsync(id, CancellationToken.None);

            if (dto is null)
            {
                return NotFound($"Print job with ID {id} not found");
            }

            // Authoritative GET carries BOTH revision tokens: the job ETag (standard
            // ETag header) and the dispatch-state ETag (custom header) so clients can
            // supply If-Match for job mutations and for bed-clear acknowledgements.
            if (!string.IsNullOrWhiteSpace(dto.RowVersion))
            {
                Response.Headers.ETag = $"\"{dto.RowVersion}\"";
            }

            if (!string.IsNullOrWhiteSpace(dto.DispatchStateRowVersion))
            {
                Response.Headers["X-Dispatch-State-ETag"] = $"\"{dto.DispatchStateRowVersion}\"";
            }

            return Ok(dto);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving print job {Id}", id);
            return Problem("An error occurred while retrieving the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Reads and normalizes the <c>If-Match</c> header value (strips weak/quote syntax).
    /// </summary>
    private string? ReadIfMatch()
    {
        string? raw = Request.Headers.IfMatch.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().TrimStart('W', '/').Trim('"');
    }

    /// <summary>
    /// Maps queue revision/precondition exceptions to their correct HTTP status codes:
    /// 428 when a required precondition header is absent, 412 when a supplied revision is
    /// stale, and 409 when the request conflicts with the resource's current semantics.
    /// </summary>
    private ObjectResult MapRevisionException(Exception ex) => ex switch
    {
        QueuePreconditionRequiredException pre => StatusCode(
            StatusCodes.Status428PreconditionRequired,
            new { error = "precondition_required", detail = pre.Message }),

        QueueRevisionConflictException rev => StatusCode(
            StatusCodes.Status412PreconditionFailed,
            new { error = "revision_conflict", detail = rev.Message }),

        QueueSemanticConflictException sem => Conflict(
            new { error = "semantic_conflict", detail = sem.Message }),

        Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => StatusCode(
            StatusCodes.Status412PreconditionFailed,
            new
            {
                error = "revision_conflict",
                detail = "The resource changed during the update. Re-fetch the ETag and retry.",
            }),

        ValidationException val => BadRequest(new { error = val.Message }),

        _ => Problem("An unexpected error occurred.", statusCode: 500),
    };

    /// <summary>
    /// Update job status, priority, or assignment
    /// </summary>
    /// <param name="id">The unique identifier of the job to update.</param>
    /// <param name="request">The update request containing new status, priority, or assignment.</param>
    [HttpPut("{id:guid}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(412)]
    [ProducesResponseType(428)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> UpdateJobAsync(Guid id, [FromBody] UpdatePrintJobStatusDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        // If-Match is mandatory on this endpoint: it mutates assignment, priority and
        // status, all of which invalidate bed-clear acknowledgements.
        request.IfMatchJobRowVersion = ReadIfMatch() ?? string.Empty;

        try
        {
            JobQueuePrintJobDto? updated = await queueService.UpdateJobAsync(id, request, CancellationToken.None);
            if (updated == null)
            {
                // Service returns null for not found or invalid assignment; translate to proper HTTP
                return NotFound($"Print job with ID {id} not found or invalid printer assignment");
            }

            if (!string.IsNullOrWhiteSpace(updated.RowVersion))
            {
                Response.Headers.ETag = $"\"{updated.RowVersion}\"";
            }

            return Ok(updated);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                         QueueRevisionConflictException or
                                         QueueSemanticConflictException or
                                         Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException or
                                         ValidationException)
        {
            return MapRevisionException(ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating print job {Id}", id);
            return Problem("An error occurred while updating the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Dispatch a queued/assigned job to its printer to start printing.
    /// The job must have an assigned printer and be in Queued or Assigned status.
    /// </summary>
    /// <param name="id">The unique identifier of the job to dispatch.</param>
    /// <returns>The updated job with Starting/Printing status.</returns>
    [HttpPost("{id:guid}/dispatch")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(QueuedPrintJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DispatchJobAsync(Guid id)
    {
        try
        {
            string? userId = User.Identity?.Name ?? "anonymous";
            QueuedPrintJobDto result = await printJobManagementService.DispatchJobAsync(
                id.ToString(), userId, ReadIfMatch(), CancellationToken.None);
            telemetryService.RecordPrinterOperation("dispatch", result.AssignedPrinterId ?? id.ToString(), true);
            return Ok(result);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                         QueueRevisionConflictException or
                                         QueueSemanticConflictException or
                                         Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            telemetryService.RecordPrinterOperation("dispatch", id.ToString(), false);
            return MapRevisionException(ex);
        }
        catch (InvalidOperationException ex)
        {
            telemetryService.RecordPrinterOperation("dispatch", id.ToString(), false);
            logger.LogWarning("Cannot dispatch job {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching print job {Id}", id);
            return Problem("An error occurred while dispatching the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Cancel a job, stopping the print if currently printing.
    /// Works for jobs in Queued, Assigned, Starting, Printing, or Paused status.
    /// If the job is currently printing on a printer, this will send a cancel command to stop the print.
    /// </summary>
    /// <param name="id">The unique identifier of the job to cancel.</param>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> CancelJobAsync(Guid id)
    {
        try
        {
            string? userId = User.Identity?.Name ?? "anonymous";
            await printJobManagementService.CancelJobAsync(id.ToString(), userId, ReadIfMatch(), CancellationToken.None);
            telemetryService.RecordPrinterOperation("cancel_job", id.ToString(), true);
            return NoContent();
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                         QueueRevisionConflictException or
                                         QueueSemanticConflictException or
                                         Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            telemetryService.RecordPrinterOperation("cancel_job", id.ToString(), false);
            return MapRevisionException(ex);
        }
        catch (InvalidOperationException ex)
        {
            telemetryService.RecordPrinterOperation("cancel_job", id.ToString(), false);
            logger.LogWarning("Cannot cancel job {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cancelling print job {Id}", id);
            return Problem("An error occurred while cancelling the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Abort the current print attempt but keep the job in the queue.
    /// Sends cancel to the printer hardware and returns the job to Queued status.
    /// Only works when the job is actively printing (Printing, Starting, or Paused status).
    /// </summary>
    /// <param name="id">The unique identifier of the job whose current print to abort.</param>
    [HttpPost("{id:guid}/abort-print")]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> AbortPrintAsync(Guid id)
    {
        try
        {
            string? userId = User.Identity?.Name ?? "anonymous";
            await printJobManagementService.AbortPrintAsync(id.ToString(), userId, ReadIfMatch(), CancellationToken.None);
            telemetryService.RecordPrinterOperation("abort", id.ToString(), true);
            return NoContent();
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                         QueueRevisionConflictException or
                                         QueueSemanticConflictException or
                                         Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            telemetryService.RecordPrinterOperation("abort", id.ToString(), false);
            return MapRevisionException(ex);
        }
        catch (InvalidOperationException ex)
        {
            telemetryService.RecordPrinterOperation("abort", id.ToString(), false);
            logger.LogWarning("Cannot abort print for job {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error aborting print for job {Id}", id);
            return Problem("An error occurred while aborting the print", statusCode: 500);
        }
    }

    /// <summary>
    /// Rerun a completed job by creating a new copy in the queue.
    /// The original job must be in a terminal state (Completed, Failed, or Cancelled).
    /// </summary>
    /// <param name="id">The unique identifier of the completed job to rerun.</param>
    [HttpPost("{id:guid}/rerun")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(QueuedPrintJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> RerunJobAsync(Guid id)
    {
        try
        {
            string userId = User.Identity?.Name ?? "anonymous";
            QueuedPrintJobDto result = await printJobManagementService.RerunJobAsync(id.ToString(), userId, CancellationToken.None);
            telemetryService.RecordPrinterOperation("rerun", result.AssignedPrinterId ?? id.ToString(), true);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            telemetryService.RecordPrinterOperation("rerun", id.ToString(), false);
            logger.LogWarning("Cannot rerun job {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error rerunning print job {Id}", id);
            return Problem("An error occurred while rerunning the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete a job from the queue
    /// </summary>
    /// <param name="id">The unique identifier of the job to delete.</param>
    [HttpDelete("{id:guid}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DeleteJobAsync(Guid id)
    {
        try
        {
            bool ok = await queueService.RemoveJobAsync(id, ReadIfMatch() ?? string.Empty, CancellationToken.None);
            return !ok ? BadRequest("Cannot delete the job (not found or currently printing)") : NoContent();
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                         QueueRevisionConflictException or
                                         QueueSemanticConflictException or
                                         Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException or
                                         ValidationException)
        {
            return MapRevisionException(ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting print job {Id}", id);
            return Problem("An error occurred while deleting the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Synchronize orphaned jobs that are stuck in "Printing" status but the printer is now idle.
    /// This can happen if the API was restarted/redeployed while a print was in progress.
    /// Checks the current printer state from the status cache and marks jobs as completed/failed accordingly.
    /// </summary>
    /// <returns>The number of jobs that were synchronized.</returns>
    [HttpPost("sync-orphaned")]
    [RequirePermission(PrintFarmerPermissions.Queue.Reconcile)]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> SyncOrphanedJobsAsync()
    {
        try
        {
            logger.LogInformation("[JobQueueController] Manual sync of orphaned jobs requested");

            // Create a lookup function that gets printer state from cache
            string? LookupPrinterState(Guid printerId)
            {
                PrinterStatusDto? status = printerStatusCache.GetStatus(printerId);
                return status?.State;
            }

            int syncedCount = await printJobCompletionService.SyncOrphanedPrintingJobsAsync(
                LookupPrinterState,
                CancellationToken.None);

            return Ok(new { syncedCount, message = $"Synchronized {syncedCount} orphaned job(s)" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error synchronizing orphaned jobs");
            return Problem("An error occurred while synchronizing orphaned jobs", statusCode: 500);
        }
    }

    /// <summary>
    /// Find and score candidate printers for a job using multi-factor analysis.
    /// Returns all printers ranked by compatibility, with eliminated printers at the end.
    /// </summary>
    /// <param name="id">The print job ID to find candidates for.</param>
    [HttpGet("{id:guid}/candidates")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(List<DispatchCandidateDto>), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> GetCandidatesAsync(Guid id)
    {
        try
        {
            List<DispatchCandidateDto> candidates = await jobDispatchService.FindCandidatesAsync(id, CancellationToken.None);
            return Ok(candidates);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Cannot find candidates for job {Id}: {Message}", id, ex.Message);
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error finding candidates for job {Id}", id);
            return Problem("An error occurred while finding candidates", statusCode: 500);
        }
    }

    /// <summary>
    /// Dispatch a job to a specific printer selected from scored candidates.
    /// Assigns the job, records the dispatch score, and triggers print start.
    /// </summary>
    /// <param name="id">The print job ID to dispatch.</param>
    /// <param name="request">The dispatch request containing the target printer ID.</param>
    [HttpPost("{id:guid}/dispatch-to")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(QueuedPrintJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DispatchToAsync(Guid id, [FromBody] DispatchJobDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            string userId = User.Identity?.Name ?? "anonymous";
            QueuedPrintJobDto result = await jobDispatchService.DispatchJobAsync(id, request.PrinterId, userId, CancellationToken.None);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Cannot dispatch job {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching job {Id} to printer {PrinterId}", id, request.PrinterId);
            return Problem("An error occurred while dispatching the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Batch-dispatch multiple queued jobs to their best-fit printers.
    /// Uses the configured load-balancing strategy (or an override per request).
    /// </summary>
    /// <param name="request">Batch dispatch parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("batch-dispatch")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(BatchDispatchResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> BatchDispatchAsync([FromBody] BatchDispatchRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        if (!request.DispatchAll && (request.JobIds is null || request.JobIds.Count == 0))
        {
            return BadRequest("Either set DispatchAll to true or provide at least one job ID.");
        }

        try
        {
            string userId = User.Identity?.Name ?? "anonymous";
            BatchDispatchResult result = await batchDispatchService.BatchDispatchAsync(request, userId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during batch dispatch");
            return Problem("An error occurred during batch dispatch", statusCode: 500);
        }
    }

    /// <summary>
    /// Acknowledge that the printer bed is clear for a specific job and authorize dispatch to start.
    /// This is an exact-job, one-use, expiring acknowledgement: it binds to the specified job and
    /// printer revision. Reorder, a higher-priority insertion, cancellation, changed compatibility
    /// data, or expiry will invalidate it. A new acknowledgement is required after requeue/abort/rerun.
    /// </summary>
    /// <param name="jobId">The exact job being acknowledged.</param>
    /// <param name="request">Acknowledgement request body with printer ID and idempotency key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 202 for new accepted asynchronous command;
    /// 200 for exact replay or job already Starting/Printing;
    /// 404 for unknown job;
    /// 409 wrong_job / printer_busy / job_not_dispatchable / idempotency_payload_mismatch;
    /// 412 dispatch_revision_conflict;
    /// 428 precondition_required;
    /// 422 calibration_job_incompatible / filament_check_failed;
    /// 503 printer_offline_or_stale.
    /// </returns>
    [HttpPost("{jobId:guid}/acknowledge-bed-clear-and-start")]
    [RequirePermission(PrintFarmerPermissions.Queue.AcknowledgeBedClear)]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(202)]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(412)]
    [ProducesResponseType(422)]
    [ProducesResponseType(428)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> AcknowledgeBedClearAndStartAsync(
        Guid jobId,
        [FromBody] AcknowledgeBedClearRequestDto request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        // Require a stable idempotency key.
        // Intentional: Idempotency-Key and If-Match are standard HTTP headers not modelled as action parameters.
#pragma warning disable S6932 // Use model binding instead of accessing the raw request data
        string? idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault()
            ?? request.IdempotencyKey;

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new { error = "precondition_required", detail = "A stable Idempotency-Key header is required for bed-clear acknowledgements." });
        }

        // Require If-Match for optimistic concurrency on the dispatch state.
        string? ifMatchHeader = Request.Headers["If-Match"].FirstOrDefault();
#pragma warning restore S6932
        byte[]? ifMatchBytes = null;
        if (!string.IsNullOrWhiteSpace(ifMatchHeader))
        {
            string etag = ifMatchHeader.Trim('"', ' ');
            try
            {
                ifMatchBytes = Convert.FromBase64String(etag);
            }
            catch (FormatException)
            {
                return BadRequest(new { error = "If-Match header must be a base-64 encoded ETag." });
            }
        }

        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            logger.LogWarning("AcknowledgeBedClear denied: unable to resolve user identity.");
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Unable to verify user identity from claims." });
        }

        string actorSubject = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? userId.ToString();

        var ackRequest = new AcknowledgeBedClearRequest(
            JobId: jobId,
            PrinterId: request.PrinterId,
            ActorSubject: actorSubject,
            IdempotencyKey: idempotencyKey,
            IfMatchDispatchState: ifMatchBytes,
            ExpectedPrinterConfigRevision: request.ExpectedPrinterConfigRevision);

        try
        {
            AcknowledgeBedClearResult result = await bedClearAcknowledgementService.AcknowledgeAsync(ackRequest, ct);

            return result.Outcome switch
            {
                BedClearAckOutcome.Accepted => StatusCode(
                    StatusCodes.Status202Accepted,
                    BuildAckResponse(result, "Bed-clear acknowledged; dispatch will start shortly.")),

                BedClearAckOutcome.Replayed or BedClearAckOutcome.AlreadyStartingOrPrinting => Ok(
                    BuildAckResponse(result, "Acknowledged (replayed or already starting).")),

                BedClearAckOutcome.JobNotFound => NotFound(
                    new { error = "job_not_found", detail = result.ErrorDetail }),

                BedClearAckOutcome.WrongJob => Conflict(
                    new { error = "wrong_job", detail = result.ErrorDetail }),

                BedClearAckOutcome.PrinterBusy => Conflict(
                    new { error = "printer_busy", detail = result.ErrorDetail }),

                BedClearAckOutcome.JobNotDispatchable => Conflict(
                    new { error = "job_not_dispatchable", detail = result.ErrorDetail }),

                BedClearAckOutcome.IdempotencyMismatch => Conflict(
                    new { error = "idempotency_payload_mismatch", detail = result.ErrorDetail }),

                BedClearAckOutcome.DispatchRevisionConflict => StatusCode(
                    StatusCodes.Status412PreconditionFailed,
                    new { error = "dispatch_revision_conflict", detail = result.ErrorDetail }),

                BedClearAckOutcome.PreconditionRequired => StatusCode(
                    StatusCodes.Status428PreconditionRequired,
                    new { error = "precondition_required", detail = result.ErrorDetail }),

                BedClearAckOutcome.CalibrationJobIncompatible => UnprocessableEntity(
                    new { error = "calibration_job_incompatible", detail = result.ErrorDetail }),

                BedClearAckOutcome.FilamentCheckFailed => UnprocessableEntity(
                    new { error = "filament_check_failed", detail = result.ErrorDetail }),

                BedClearAckOutcome.PrinterOfflineOrStale => StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { error = "printer_offline_or_stale", detail = result.ErrorDetail }),

                BedClearAckOutcome.Forbidden => StatusCode(
                    StatusCodes.Status403Forbidden,
                    new { error = "forbidden", detail = result.ErrorDetail }),

                _ => Problem("Unexpected acknowledgement outcome.", statusCode: 500),
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing bed-clear acknowledgement for job {JobId}", jobId);
            return Problem("An error occurred while processing the acknowledgement.", statusCode: 500);
        }
    }

    /// <summary>Builds the acknowledge response body including ETag values.</summary>
    private static object BuildAckResponse(AcknowledgeBedClearResult result, string message) =>
        new
        {
            message,
            jobETag = result.JobETag is not null ? Convert.ToBase64String(result.JobETag) : null,
            dispatchStateETag = result.DispatchStateETag is not null ? Convert.ToBase64String(result.DispatchStateETag) : null,
        };
}

using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Idempotency;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Infrastructure.Idempotency;
using Farm.Web.Api.Infrastructure.OperatorFeatures;
using Farm.Web.Api.Infrastructure.PartsInventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    IPartHarvestService partHarvestService,
    IOperatorFeatureGate operatorFeatureGate,
    ILogger<JobQueueController> logger,
    AppDbContext? db = null,
    IQueueResourceAuthorizationService? resourceAuthorization = null) : ControllerBase
{
    /// <summary>
    /// Get queue overview with optional compatibility filtering.
    /// Filters printers by model, nozzle diameter, and/or material type.
    /// All filtering is case-insensitive. Nozzle matching uses ±0.01mm tolerance.
    /// Access is scoped with a single batched
    /// <see cref="IQueueResourceAuthorizationService.FilterAccessiblePrinterIdsAsync"/> call
    /// (constant query count regardless of printer count) rather than looping
    /// <see cref="IQueueResourceAuthorizationService.CanAccessPrinterAsync"/> per printer (#1729).
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
            if (resourceAuthorization is null)
            {
                return Ok(dtos);
            }

            Guid[] printerIds = dtos.Select(dto => dto.PrinterId).Distinct().ToArray();
            IReadOnlySet<Guid> allowedPrinterIds = await resourceAuthorization.FilterAccessiblePrinterIdsAsync(
                User,
                printerIds,
                PrinterGroupAccessLevel.View,
                CancellationToken.None);
            List<QueueOverviewDto> authorized = dtos
                .Where(dto => allowedPrinterIds.Contains(dto.PrinterId))
                .ToList();

            return Ok(authorized);
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
    /// <param name="idempotencyKey">Stable calibration command key from the HTTP header.</param>
    [HttpPost]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> QueueJobAsync(
        [FromBody] QueuePrintJobDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            // Calibration idempotency is an HTTP command concern. Always overwrite the
            // body field so a promoted calibration artifact cannot launder a body-only key.
            request.IdempotencyKey = idempotencyKey;

            // Parse userId from claims for ACL enforcement — fail closed for authenticated requests
            string? userIdStr = QueueActorIdentity.Resolve(User);

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
        catch (CalibrationQueueResourceNotFoundException ex)
        {
            return NotFound(new { error = "calibration_resource_not_found", detail = ex.Message });
        }
        catch (CalibrationQueueIncompatibleException ex)
        {
            return UnprocessableEntity(new
            {
                error = "calibration_job_incompatible",
                detail = ex.Message,
            });
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound(new { error = "queue_job_not_found" });
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
            if (resourceAuthorization is not null &&
                !await resourceAuthorization.CanAccessJobAsync(
                    User,
                    id,
                    PrinterGroupAccessLevel.View,
                    CancellationToken.None))
            {
                return NotFound();
            }

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
    /// Returns authorized durable queue events after a monotonic sequence cursor.
    /// SignalR events are hints; this endpoint is the refetch authority after gaps.
    /// </summary>
    [HttpGet("changes")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetChangesAsync(
        [FromQuery] long afterSequence = 0,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (afterSequence < 0 || limit is < 1 or > 500)
        {
            return BadRequest(new
            {
                error = "invalid_cursor",
                detail = "afterSequence must be non-negative and limit must be between 1 and 500.",
            });
        }

        if (db is null || resourceAuthorization is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "queue_change_feed_unavailable" });
        }

        List<QueueDispatchOutbox> candidates = await db.QueueDispatchOutbox
            .AsNoTracking()
            .Where(evt =>
                evt.Sequence > afterSequence &&
                evt.EventType != BedClearAcknowledgementService.BackendStartCommandEventType &&
                evt.EventType != BackendControlCommandConsumerService.EventType)
            .OrderBy(evt => evt.Sequence)
            .Take(Math.Min(2000, limit * 4))
            .ToListAsync(ct);

        var events = new List<QueueEventEnvelope>(limit);
        long nextSequence = afterSequence;
        foreach (QueueDispatchOutbox evt in candidates)
        {
            nextSequence = evt.Sequence;
            bool canAccess = string.Equals(
                evt.AggregateType,
                nameof(Printer),
                StringComparison.Ordinal)
                ? evt.PrinterId.HasValue &&
                  await resourceAuthorization.CanAccessPrinterAsync(
                      User,
                      evt.PrinterId.Value,
                      PrinterGroupAccessLevel.View,
                      ct)
                : await resourceAuthorization.CanAccessJobAsync(
                    User,
                    evt.AggregateId,
                    PrinterGroupAccessLevel.View,
                    ct);
            if (!canAccess)
            {
                continue;
            }

            Guid? eventJobId = string.Equals(
                evt.AggregateType,
                nameof(PrintJob),
                StringComparison.Ordinal)
                ? evt.AggregateId
                : null;
            events.Add(QueueEventEnvelope.FromOutbox(
                eventId: evt.Id,
                sequence: evt.Sequence,
                occurredAtUtc: evt.CreatedAtUtc,
                eventType: evt.EventType,
                jobId: eventJobId,
                printerId: evt.PrinterId,
                projectId: evt.ProjectId,
                calibrationAttemptId: evt.CalibrationAttemptId,
                jobStatus: evt.JobStatus,
                jobKind: evt.JobKind,
                jobRevision: evt.AggregateRowVersion,
                dispatchStateRevision: evt.DispatchStateRowVersion,
                attemptId: evt.AttemptId,
                attemptNumber: evt.AttemptNumber,
                attemptOutcome: evt.AttemptOutcome,
                bedClearState: evt.BedClearState,
                bedClearCommandId: evt.BedClearCommandId,
                bedClearExpiresAtUtc: evt.BedClearExpiresAtUtc,
                failureCode: evt.FailureCode,
                failureRetryable: evt.FailureRetryable,
                failureRequiresReconciliation: evt.FailureRequiresReconciliation,
                payloadJson: evt.PayloadJson,
                jobLogicalRevision: evt.JobRevision,
                dispatchStateLogicalRevision: evt.DispatchStateRevision,
                schemaVersion: evt.SchemaVersion));
            if (events.Count == limit)
            {
                break;
            }
        }

        bool hasMore = await db.QueueDispatchOutbox
            .AsNoTracking()
            .AnyAsync(
                evt =>
                evt.Sequence > nextSequence &&
                evt.EventType != BedClearAcknowledgementService.BackendStartCommandEventType &&
                evt.EventType != BackendControlCommandConsumerService.EventType,
                ct);
        return Ok(new
        {
            afterSequence,
            nextSequence,
            hasMore,
            events,
        });
    }

    /// <summary>
    /// Returns the current outbox watermark (highest committed sequence) so a
    /// client can seed its change-feed cursor at connect time instead of
    /// starting from zero and replaying the entire durable outbox history on
    /// every fresh page load (issue #1727). Cheap: a single MAX query with no
    /// per-row authorization filtering, since only a number is exposed here,
    /// never event content.
    /// </summary>
    [HttpGet("changes/watermark")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(QueueChangeWatermarkDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<QueueChangeWatermarkDto>> GetChangeWatermarkAsync(
        CancellationToken ct = default)
    {
        if (db is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "queue_change_feed_unavailable" });
        }

        long latestSequence = await db.QueueDispatchOutbox
            .AsNoTracking()
            .Select(evt => (long?)evt.Sequence)
            .MaxAsync(ct) ?? 0;

        return Ok(new QueueChangeWatermarkDto(latestSequence));
    }

    /// <summary>
    /// Returns every current queue resource the authenticated actor may subscribe to.
    /// This snapshot is not paginated so reconnects cannot omit jobs beyond a UI page.
    /// </summary>
    [HttpGet("subscription-resources")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(
        typeof(QueueSubscriptionResourcesDto),
        StatusCodes.Status200OK)]
    public async Task<ActionResult<QueueSubscriptionResourcesDto>>
        GetSubscriptionResourcesAsync(CancellationToken ct = default)
    {
        if (db is null || resourceAuthorization is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "queue_subscription_resources_unavailable" });
        }

        Guid[] currentJobIds = await db.PrintJobs
            .AsNoTracking()
            .Where(job =>
                job.Status == PrintJobStatus.Queued ||
                job.Status == PrintJobStatus.Assigned ||
                job.Status == PrintJobStatus.Starting ||
                job.Status == PrintJobStatus.Printing ||
                job.Status == PrintJobStatus.Paused)
            .Select(job => job.Id)
            .ToArrayAsync(ct);
        IReadOnlySet<Guid> authorized =
            await resourceAuthorization.FilterActorAccessibleJobIdsAsync(
                QueueActorIdentity.Resolve(User),
                currentJobIds,
                PrinterGroupAccessLevel.View,
                ct);
        Guid[] authorizedIds = authorized.ToArray();
        var resources = await db.PrintJobs
            .AsNoTracking()
            .Where(job => authorizedIds.Contains(job.Id))
            .Select(job => new
            {
                job.Id,
                job.AssignedPrinterId,
                ProjectId = job.CalibrationProjectId ?? job.ProjectId,
            })
            .ToArrayAsync(ct);

        return Ok(new QueueSubscriptionResourcesDto(
            resources
                .Where(resource => resource.AssignedPrinterId.HasValue)
                .Select(resource => resource.AssignedPrinterId!.Value)
                .Distinct()
                .ToArray(),
            resources.Select(resource => resource.Id).ToArray(),
            resources
                .Where(resource => resource.ProjectId.HasValue)
                .Select(resource => resource.ProjectId!.Value)
                .Distinct()
                .ToArray()));
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
            new
            {
                error = "revision_conflict",
                detail = rev.Message,
                jobETag = rev.CurrentJobRowVersion is null
                    ? null
                    : Convert.ToBase64String(rev.CurrentJobRowVersion),
                dispatchStateETag = rev.CurrentDispatchStateRowVersion is null
                    ? null
                    : Convert.ToBase64String(rev.CurrentDispatchStateRowVersion),
            }),

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
            JobQueuePrintJobDto? updated = await queueService.UpdateJobAsync(
                id,
                request,
                GetActorSubject(),
                CancellationToken.None);
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
            string userId = GetActorSubject();
            QueuedPrintJobDto result = await printJobManagementService.DispatchJobAsync(
                id.ToString(), userId, ReadIfMatch() ?? string.Empty, CancellationToken.None);
            bool accepted = result.DispatchResult?.Outcome == DispatchAttemptOutcome.Accepted;
            telemetryService.RecordPrinterOperation(
                "dispatch",
                result.AssignedPrinterId ?? id.ToString(),
                accepted);
            WriteJobEtag(result.RowVersion);
            return MapDispatchResponse(result);
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
            string userId = GetActorSubject();
            await printJobManagementService.CancelJobAsync(
                id.ToString(),
                userId,
                ReadIfMatch() ?? string.Empty,
                CancellationToken.None);
            telemetryService.RecordPrinterOperation("cancel_job", id.ToString(), true);
            return Accepted(new { jobId = id, status = "control_command_queued" });
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
            string userId = GetActorSubject();
            await printJobManagementService.AbortPrintAsync(
                id.ToString(),
                userId,
                ReadIfMatch() ?? string.Empty,
                CancellationToken.None);
            telemetryService.RecordPrinterOperation("abort", id.ToString(), true);
            return Accepted(new { jobId = id, status = "control_command_queued" });
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
            string userId = GetActorSubject();
            QueuedPrintJobDto result = await printJobManagementService.RerunJobAsync(
                id.ToString(),
                userId,
                ReadIfMatch() ?? string.Empty,
                CancellationToken.None);
            telemetryService.RecordPrinterOperation("rerun", result.AssignedPrinterId ?? id.ToString(), true);
            WriteJobEtag(result.RowVersion);
            return Ok(result);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                         QueueRevisionConflictException or
                                         QueueSemanticConflictException or
                                         Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            telemetryService.RecordPrinterOperation("rerun", id.ToString(), false);
            return MapRevisionException(ex);
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
    /// Harvest a completed print job into printed-part stock.
    /// Atomically stamps the job as harvested, increments the mapped SKU(s),
    /// and records ledger entries. Idempotent: replaying against an already
    /// harvested job returns the original result without applying deltas twice.
    /// </summary>
    /// <param name="id">Completed print job to harvest.</param>
    /// <param name="request">Optional bin code, quantity override, or manual SKU mapping fallback.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id:guid}/harvest")]
    [Idempotent(IdempotencyRouteKeys.JobQueueHarvest)]
    [ProducesResponseType(typeof(HarvestJobResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<HarvestJobResponse>> HarvestJobAsync(
        Guid id,
        [FromBody] HarvestJobRequest? request,
        CancellationToken ct)
    {
        if (!await operatorFeatureGate.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, ct).ConfigureAwait(false))
        {
            return OperatorFeatureProblemDetails.NotFound(
                operatorFeatureGate,
                OperatorFeature.PrintedPartsInventory);
        }

        HarvestJobRequest payload = request ?? new HarvestJobRequest();
        string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? User.FindFirstValue("oid");

        HarvestResult result = await partHarvestService.HarvestJobAsync(id, payload, userId, ct);
        return result.Outcome switch
        {
            PartInventoryOutcome.Ok => Ok(result.Response),
            PartInventoryOutcome.IdempotentReplay => Ok(result.Response),
            PartInventoryOutcome.JobNotFound => NotFound(new { message = result.Message }),
            PartInventoryOutcome.JobNotCompleted => Conflict(new { message = result.Message }),
            PartInventoryOutcome.BinNotFound => BadRequest(new { message = result.Message }),
            PartInventoryOutcome.WrongBin when result.WrongBin is not null
                => PartsInventoryProblemDetails.WrongBin(result.WrongBin),
            PartInventoryOutcome.NoMappings when result.MappingRequired is not null
                => PartsInventoryProblemDetails.PartMappingRequired(result.MappingRequired),
            PartInventoryOutcome.PartNotFound => NotFound(new { message = result.Message }),
            PartInventoryOutcome.InvalidRequest => BadRequest(new { message = result.Message }),
            PartInventoryOutcome.Conflict => Conflict(new { message = result.Message }),
            PartInventoryOutcome.FeatureDisabled => OperatorFeatureProblemDetails.NotFound(
                operatorFeatureGate,
                OperatorFeature.PrintedPartsInventory),
            _ => Problem(result.Message ?? "Harvest failed.", statusCode: 500),
        };
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
            bool ok = await queueService.RemoveJobAsync(
                id,
                ReadIfMatch() ?? string.Empty,
                GetActorSubject(),
                CancellationToken.None);
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
            string actorSubject = GetActorSubject();
            logger.LogInformation(
                "[JobQueueController] Manual sync of orphaned jobs requested by {ActorSubject}",
                actorSubject);

            // Create a lookup function that gets printer state from cache
            string? LookupPrinterState(Guid printerId)
            {
                PrinterStatusDto? status = printerStatusCache.GetStatus(printerId);
                return status?.State;
            }

            int syncedCount = await printJobCompletionService.SyncOrphanedPrintingJobsAsync(
                LookupPrinterState,
                actorSubject,
                CancellationToken.None);

            return Ok(new { syncedCount, message = $"Synchronized {syncedCount} orphaned job(s)" });
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Unable to resolve the authenticated actor for orphaned job synchronization");
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Unable to verify the authenticated queue actor." });
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
            if (resourceAuthorization is not null &&
                !await resourceAuthorization.CanAccessJobAsync(
                    User,
                    id,
                    PrinterGroupAccessLevel.View,
                    CancellationToken.None))
            {
                return NotFound();
            }

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
            string userId = GetActorSubject();
            QueuedPrintJobDto result = await jobDispatchService.DispatchJobAsync(
                id,
                request.PrinterId,
                userId,
                ReadIfMatch() ?? string.Empty,
                CancellationToken.None);
            WriteJobEtag(result.RowVersion);
            return MapDispatchResponse(result);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                         QueueRevisionConflictException or
                                         QueueSemanticConflictException or
                                         Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
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
            string userId = GetActorSubject();
            BatchDispatchResult result = await batchDispatchService.BatchDispatchAsync(request, userId, ct);
            return Ok(result);
        }
        catch (Exception ex) when (ex is QueuePreconditionRequiredException or
                                         QueueRevisionConflictException or
                                         QueueSemanticConflictException or
                                         Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return MapRevisionException(ex);
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

        // Require independent preconditions for the exact job and dispatch-state rows.
        string? ifMatchHeader = Request.Headers["If-Match"].FirstOrDefault();
        string? dispatchIfMatchHeader =
            Request.Headers["X-Dispatch-State-If-Match"].FirstOrDefault();
#pragma warning restore S6932
        if (string.IsNullOrWhiteSpace(ifMatchHeader) ||
            string.IsNullOrWhiteSpace(dispatchIfMatchHeader))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new
                {
                    error = "precondition_required",
                    detail = "Both If-Match and X-Dispatch-State-If-Match are required.",
                });
        }

        byte[]? ifMatchJobBytes;
        byte[]? ifMatchDispatchBytes;
        try
        {
            ifMatchJobBytes = DecodeEtag(ifMatchHeader);
            ifMatchDispatchBytes = DecodeEtag(dispatchIfMatchHeader);
        }
        catch (FormatException)
        {
            return BadRequest(new
            {
                error = "If-Match headers must contain base-64 encoded ETags.",
            });
        }

        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            logger.LogWarning("AcknowledgeBedClear denied: unable to resolve user identity.");
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Unable to verify user identity from claims." });
        }

        string actorSubject = QueueActorIdentity.Resolve(User);

        var ackRequest = new AcknowledgeBedClearRequest(
            JobId: jobId,
            PrinterId: request.PrinterId,
            ActorSubject: actorSubject,
            IdempotencyKey: idempotencyKey,
            IfMatchDispatchState: ifMatchDispatchBytes,
            ExpectedPrinterConfigRevision: request.ExpectedPrinterConfigRevision,
            IfMatchJob: ifMatchJobBytes);

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
                    new
                    {
                        error = "dispatch_revision_conflict",
                        detail = result.ErrorDetail,
                        jobETag = result.JobETag is null
                            ? null
                            : Convert.ToBase64String(result.JobETag),
                        dispatchStateETag = result.DispatchStateETag is null
                            ? null
                            : Convert.ToBase64String(result.DispatchStateETag),
                    }),

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
            bedClearCommandId = result.BedClearCommandId,
            bedClearIdempotencyKeySha256 = result.BedClearIdempotencyKeySha256,
        };

    private ObjectResult MapDispatchResponse(QueuedPrintJobDto result) =>
        result.DispatchResult?.Outcome switch
        {
            DispatchAttemptOutcome.Accepted => Ok(result),
            DispatchAttemptOutcome.Unknown => StatusCode(StatusCodes.Status202Accepted, result),
            DispatchAttemptOutcome.Rejected or DispatchAttemptOutcome.FailedBeforeStart => Conflict(result),
            _ => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "dispatch_outcome_unavailable", job = result }),
        };

    private void WriteJobEtag(string? rowVersion)
    {
        if (!string.IsNullOrWhiteSpace(rowVersion))
        {
            Response.Headers.ETag = $"\"{rowVersion}\"";
        }
    }

    private static byte[]? DecodeEtag(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.FromBase64String(
                value.Trim().TrimStart('W', '/').Trim('"'));

    private string GetActorSubject() => QueueActorIdentity.Resolve(User);
}

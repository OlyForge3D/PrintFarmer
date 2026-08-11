using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the auto-dispatch ready-gate workflow for printers.
/// After a print completes on an auto-dispatch-enabled printer, the operator must confirm
/// the bed is clear before the next queued job is dispatched.
/// </summary>
[ApiController]
[Route("api/auto-dispatch")]
[Authorize]
public class AutoDispatchController(
    IAutoDispatchService autoDispatchService,
    ILogger<AutoDispatchController> logger,
    IQueueResourceAuthorizationService? resourceAuthorization = null,
    AppDbContext? db = null) : ControllerBase
{
    /// <summary>
    /// Get the auto-dispatch status for a printer.
    /// </summary>
    [HttpGet("{printerId:guid}/status")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> GetStatusAsync(Guid printerId, CancellationToken ct)
    {
        if (resourceAuthorization is not null &&
            !await resourceAuthorization.CanAccessPrinterAsync(
                User,
                printerId,
                PrinterGroupAccessLevel.View,
                ct))
        {
            return NotFound();
        }

        try
        {
            var status = await autoDispatchService.GetStatusAsync(printerId, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Confirm that the bed is clear and attempt to dispatch the exact reviewed
    /// queue-head job. Filament incompatibility or unknown data returns a
    /// confirmation challenge instead of dispatching.
    /// </summary>
    [HttpPost("{printerId:guid}/ready")]
    [RequirePermission(PrintFarmerPermissions.Queue.AcknowledgeBedClear)]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(AutoDispatchReadyResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AutoDispatchReadyResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(AutoDispatchReadyResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<AutoDispatchReadyResult>> MarkReadyAsync(
        Guid printerId,
        CancellationToken ct,
        [FromQuery] bool confirmFilamentOverride = false,
        [FromHeader(Name = "X-Job-If-Match")] string? jobIfMatch = null,
        [FromHeader(Name = "X-Filament-Check-If-Match")] string? filamentCheckIfMatch = null)
    {
        if (await CheckDispatchPreconditionAsync(printerId, ct) is { } precondition)
        {
            return precondition;
        }

        if (resourceAuthorization is not null &&
            !await resourceAuthorization.CanAccessPrinterAsync(
                User,
                printerId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            return NotFound();
        }

        if (confirmFilamentOverride && string.IsNullOrWhiteSpace(jobIfMatch))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new
                {
                    error = "precondition_required",
                    detail = "X-Job-If-Match is required to confirm a filament override.",
                });
        }

        if (confirmFilamentOverride && string.IsNullOrWhiteSpace(filamentCheckIfMatch))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new
                {
                    error = "precondition_required",
                    detail = "X-Filament-Check-If-Match is required to confirm a filament override.",
                });
        }

        try
        {
            byte[] expectedDispatch = DecodeEtag(Request.Headers.IfMatch[0]!);
            byte[]? expectedOverrideJob = confirmFilamentOverride
                ? DecodeEtag(jobIfMatch!)
                : null;
            byte[]? expectedFilamentCheck = confirmFilamentOverride
                ? DecodeEtag(filamentCheckIfMatch!)
                : null;
            string actorSubject = QueueActorIdentity.Resolve(User);
            var result = await autoDispatchService.MarkReadyAsync(
                printerId,
                expectedDispatch,
                confirmFilamentOverride,
                actorSubject,
                expectedOverrideJob,
                expectedFilamentCheck,
                ct);
            if (result.RequiresFilamentOverride ||
                result.FilamentCheckChanged)
            {
                return Conflict(result);
            }

            return result.DispatchReconciliationPending
                ? Accepted(result)
                : Ok(result);
        }
        catch (FormatException)
        {
            return BadRequest(new
            {
                error = "invalid_etag",
                detail = "One or more revision headers are not valid base-64 ETags.",
            });
        }
        catch (QueueRevisionConflictException)
        {
            return StatusCode(
                StatusCodes.Status412PreconditionFailed,
                new { error = "job_revision_conflict" });
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevisionConflict();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("[AutoDispatchReadyGate] MarkReady failed for printer {PrinterId}: {Error}", printerId, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Skip the next queued job (cancels it). If more jobs are queued,
    /// the printer stays in PendingReady; otherwise transitions to None.
    /// </summary>
    [HttpPost("{printerId:guid}/skip")]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> SkipNextAsync(
        Guid printerId,
        CancellationToken ct,
        [FromHeader(Name = "X-Job-If-Match")] string? jobIfMatch = null)
    {
        if (await CheckDispatchPreconditionAsync(printerId, ct) is { } precondition)
        {
            return precondition;
        }

        if (await CheckQueueHeadPreconditionAsync(
                printerId,
                jobIfMatch,
                ct) is { } jobPrecondition)
        {
            return jobPrecondition;
        }

        if (resourceAuthorization is not null &&
            !await resourceAuthorization.CanAccessPrinterAsync(
                User,
                printerId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            return NotFound();
        }

        try
        {
            byte[] expectedDispatch = DecodeEtag(Request.Headers.IfMatch[0]!);
            byte[] expectedJob = DecodeEtag(jobIfMatch!);
            var status = await autoDispatchService.SkipNextJobAsync(
                printerId,
                expectedDispatch,
                expectedJob,
                ct);
            return Ok(status);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevisionConflict();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel the auto-dispatch ready-gate workflow. Returns the printer to None state
    /// without affecting queued jobs.
    /// </summary>
    [HttpPost("{printerId:guid}/cancel")]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> CancelAutoAsync(Guid printerId, CancellationToken ct)
    {
        if (await CheckDispatchPreconditionAsync(printerId, ct) is { } precondition)
        {
            return precondition;
        }

        if (resourceAuthorization is not null &&
            !await resourceAuthorization.CanAccessPrinterAsync(
                User,
                printerId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            return NotFound();
        }

        try
        {
            byte[] expectedDispatch = DecodeEtag(Request.Headers.IfMatch[0]!);
            var status = await autoDispatchService.CancelAutoAsync(
                printerId,
                expectedDispatch,
                ct);
            return Ok(status);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevisionConflict();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Pre-confirm the bed is clear. Allows the next queued job to dispatch
    /// immediately without waiting for PendingReady confirmation.
    /// </summary>
    [HttpPost("{printerId:guid}/pre-clear")]
    [RequirePermission(PrintFarmerPermissions.Queue.AcknowledgeBedClear)]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> MarkPreClearAsync(Guid printerId, CancellationToken ct)
    {
        if (await CheckDispatchPreconditionAsync(printerId, ct) is { } precondition)
        {
            return precondition;
        }

        if (resourceAuthorization is not null &&
            !await resourceAuthorization.CanAccessPrinterAsync(
                User,
                printerId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            return NotFound();
        }

        try
        {
            var status = await autoDispatchService.MarkPreClearAsync(
                printerId,
                QueueActorIdentity.Resolve(User),
                DecodeEtag(Request.Headers.IfMatch[0]!),
                ct);
            return Ok(status);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevisionConflict();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("[AutoDispatchReadyGate] PreClear failed for printer {PrinterId}: {Error}", printerId, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Enable or disable auto-dispatch for a printer.
    /// </summary>
    [HttpPut("{printerId:guid}/enabled")]
    [RequirePermission(PrintFarmerPermissions.DispatchSettings.Manage)]
    [ProducesResponseType(typeof(AutoDispatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutoDispatchStatusDto>> SetEnabledAsync(
        Guid printerId,
        [FromBody] SetAutoDispatchEnabledRequest request,
        CancellationToken ct,
        [FromHeader(Name = "X-Printer-If-Match")] string? printerIfMatch = null)
    {
        if (await CheckDispatchPreconditionAsync(printerId, ct) is { } precondition)
        {
            return precondition;
        }

        if (await CheckPrinterPreconditionAsync(
                printerId,
                printerIfMatch,
                ct) is { } printerPrecondition)
        {
            return printerPrecondition;
        }

        if (resourceAuthorization is not null &&
            !await resourceAuthorization.CanAccessPrinterAsync(
                User,
                printerId,
                PrinterGroupAccessLevel.Manage,
                ct))
        {
            return NotFound();
        }

        try
        {
            var status = await autoDispatchService.SetEnabledAsync(
                printerId,
                request.Enabled,
                DecodeEtag(Request.Headers.IfMatch[0]!),
                DecodeEtag(printerIfMatch!),
                ct);
            return Ok(status);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevisionConflict();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get auto-dispatch status for all printers.
    /// </summary>
    [HttpGet("status")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(AutoDispatchGlobalStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AutoDispatchGlobalStatusDto>> GetAllStatusAsync(CancellationToken ct)
    {
        AutoDispatchGlobalStatusDto status = await autoDispatchService.GetAllStatusAsync(ct);
        if (resourceAuthorization is not null)
        {
            var authorized = new List<AutoDispatchStatusDto>(status.Printers.Count);
            foreach (AutoDispatchStatusDto printer in status.Printers)
            {
                if (await resourceAuthorization.CanAccessPrinterAsync(
                        User,
                        printer.PrinterId,
                        PrinterGroupAccessLevel.View,
                        ct))
                {
                    authorized.Add(printer);
                }
            }

            status.Printers = authorized;
        }

        return Ok(status);
    }

    /// <summary>
    /// Enable or disable auto-dispatch for all printers at once.
    /// </summary>
    [HttpPut("enabled")]
    [RequirePermission(PrintFarmerPermissions.DispatchSettings.Manage)]
    [ProducesResponseType(typeof(List<AutoDispatchStatusDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AutoDispatchStatusDto>>> SetAllEnabledAsync(
        [FromBody] SetAutoDispatchEnabledRequest request,
        CancellationToken ct)
    {
        if (request.ExpectedVersions is null || request.ExpectedVersions.Count == 0)
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new
                {
                    error = "precondition_required",
                    detail = "Per-printer dispatch and printer ETags are required.",
                });
        }

        try
        {
            Dictionary<Guid, AutoDispatchExpectedVersions> expected = request.ExpectedVersions
                .ToDictionary(
                    pair => pair.Key,
                    pair => new AutoDispatchExpectedVersions(
                        DecodeEtag(pair.Value.DispatchStateETag),
                        DecodeEtag(pair.Value.PrinterETag)));
            var statuses = await autoDispatchService.SetAllEnabledAsync(
                request.Enabled,
                expected,
                ct);
            return Ok(statuses);
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "Expected ETags must be base-64 encoded." });
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevisionConflict();
        }
        catch (QueuePreconditionRequiredException exception)
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new { error = "precondition_required", detail = exception.Message });
        }
    }

    private async Task<ObjectResult?> CheckDispatchPreconditionAsync(
        Guid printerId,
        CancellationToken ct)
    {
        if (db is null)
        {
            return null;
        }

        string? supplied = Request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new { error = "precondition_required", detail = "If-Match is required." });
        }

        long? actualRevision = await db.PrinterDispatchStates
            .AsNoTracking()
            .Where(state => state.PrinterId == printerId)
            .Select(state => (long?)state.Revision)
            .SingleOrDefaultAsync(ct);
        if (actualRevision is null)
        {
            return NotFound(new { error = "printer_not_found" });
        }

        byte[] actual = RevisionETag.EncodeBytes(actualRevision.Value);
        try
        {
            byte[] expected = Convert.FromBase64String(
                supplied.Trim().TrimStart('W', '/').Trim('"'));
            return expected.SequenceEqual(actual)
                ? null
                : StatusCode(
                    StatusCodes.Status412PreconditionFailed,
                    new { error = "dispatch_revision_conflict" });
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "If-Match must be a base-64 encoded ETag." });
        }
    }

    private async Task<ObjectResult?> CheckQueueHeadPreconditionAsync(
        Guid printerId,
        string? supplied,
        CancellationToken ct)
    {
        if (db is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(supplied))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new
                {
                    error = "precondition_required",
                    detail = "X-Job-If-Match is required to skip the current queue head.",
                });
        }

        long? actualRevision = await db.PrintJobs
            .AsNoTracking()
            .Where(job =>
                job.AssignedPrinterId == printerId &&
                (job.Status == PrintJobStatus.Queued ||
                 job.Status == PrintJobStatus.Assigned))
            .OrderByPriorityDescending()
            .Select(job => (long?)job.Revision)
            .FirstOrDefaultAsync(ct);
        if (actualRevision is null)
        {
            return Conflict(new { error = "queue_empty" });
        }

        byte[] actual = RevisionETag.EncodeBytes(actualRevision.Value);
        try
        {
            byte[] expected = Convert.FromBase64String(
                supplied.Trim().TrimStart('W', '/').Trim('"'));
            return expected.SequenceEqual(actual)
                ? null
                : StatusCode(
                    StatusCodes.Status412PreconditionFailed,
                    new { error = "job_revision_conflict" });
        }
        catch (FormatException)
        {
            return BadRequest(new
            {
                error = "X-Job-If-Match must be a base-64 encoded ETag.",
            });
        }
    }

    private async Task<ObjectResult?> CheckPrinterPreconditionAsync(
        Guid printerId,
        string? supplied,
        CancellationToken ct)
    {
        if (db is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(supplied))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new
                {
                    error = "precondition_required",
                    detail = "X-Printer-If-Match is required for printer configuration changes.",
                });
        }

        long? actualRevision = await db.Printers
            .AsNoTracking()
            .Where(printer => printer.Id == printerId)
            .Select(printer => (long?)printer.Revision)
            .SingleOrDefaultAsync(ct);
        if (actualRevision is null)
        {
            return NotFound(new { error = "printer_not_found" });
        }

        byte[] actual = RevisionETag.EncodeBytes(actualRevision.Value);
        try
        {
            byte[] expected = Convert.FromBase64String(
                supplied.Trim().TrimStart('W', '/').Trim('"'));
            return expected.SequenceEqual(actual)
                ? null
                : StatusCode(
                    StatusCodes.Status412PreconditionFailed,
                    new { error = "printer_revision_conflict" });
        }
        catch (FormatException)
        {
            return BadRequest(new
            {
                error = "X-Printer-If-Match must be a base-64 encoded ETag.",
            });
        }
    }

    private static byte[] DecodeEtag(string supplied) =>
        Convert.FromBase64String(
            supplied.Trim().TrimStart('W', '/').Trim('"'));

    private ObjectResult RevisionConflict() =>
        StatusCode(
            StatusCodes.Status412PreconditionFailed,
            new { error = "dispatch_revision_conflict" });
}

public class SetAutoDispatchEnabledRequest
{
    public bool Enabled { get; set; }

    public Dictionary<Guid, AutoDispatchExpectedVersionRequest>? ExpectedVersions { get; set; }
}

public sealed class AutoDispatchExpectedVersionRequest
{
    public required string DispatchStateETag { get; set; }

    public required string PrinterETag { get; set; }
}

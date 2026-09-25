using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.PrintQueue.Controllers;

/// <summary>
/// Operator escape hatch for indeterminate pre-start dispatch claims (issue #2859). See
/// docs/JOB_QUEUE_ARCHITECTURE.md § "Indeterminate pre-start claim escape hatch".
/// Out-of-scope printers and jobs return 404 so existence is not disclosed.
/// </summary>
[ApiController]
[Route("api/dispatch")]
[Tags("Dispatch Recovery")]
[Authorize]
[RequirePermission(PrintFarmerPermissions.Queue.Read)]
public class DispatchRecoveryController(
    IDispatchRecoveryService recoveryService,
    IQueueResourceAuthorizationService resourceAuthorization) : ControllerBase
{
    /// <summary>Maximum accepted <c>Idempotency-Key</c> length.</summary>
    public const int MaxIdempotencyKeyLength = 200;

    /// <summary>Returns the printer's reconciliation resource (claim, evidence, escalation, permission).</summary>
    [HttpGet("{printerId:guid}/reconciliation")]
    public async Task<IActionResult> GetReconciliationAsync(Guid printerId, CancellationToken ct)
    {
        if (!await resourceAuthorization.CanAccessPrinterAsync(User, printerId, PrinterGroupAccessLevel.View, ct))
        {
            return NotFoundError("printer_not_found");
        }

        bool recoveryPermission = PrintFarmerPermissions.HasPermission(User, PrintFarmerPermissions.Queue.Reconcile);
        return ToResult(await recoveryService.GetReconciliationAsync(printerId, recoveryPermission, ct));
    }

    /// <summary>
    /// Records the operator's physical-check assertion and, when every fence passes, closes the
    /// indeterminate claim. Requires <c>If-Match</c> (state revision) and <c>Idempotency-Key</c>.
    /// </summary>
    [HttpPost("{printerId:guid}/reconciliation/recover")]
    [RequirePermission(PrintFarmerPermissions.Queue.Reconcile)]
    public async Task<IActionResult> RecoverAsync(
        Guid printerId,
        [FromBody] DispatchRecoveryRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKeyHeader,
        CancellationToken ct)
    {
        if (!await resourceAuthorization.CanAccessPrinterAsync(User, printerId, PrinterGroupAccessLevel.Manage, ct))
        {
            return NotFoundError("printer_not_found");
        }

        if (!TryReadRevision(out long expectedRevision, out string ifMatch, out IActionResult? etagError))
        {
            return etagError!;
        }

        if (!TryReadIdempotencyKey(idempotencyKeyHeader, out string idempotencyKey, out IActionResult? keyError))
        {
            return keyError!;
        }

        string actor;
        try
        {
            actor = QueueActorIdentity.Resolve(User);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "actor_unresolved" });
        }

        DispatchRecoveryResult result = await recoveryService.RecoverAsync(
            printerId,
            actor,
            expectedRevision,
            ifMatch,
            idempotencyKey,
            request ?? new DispatchRecoveryRequest(),
            HttpContext.TraceIdentifier,
            ct);
        return ToResult(result);
    }

    /// <summary>Returns one redacted immutable recovery-evidence record.</summary>
    [HttpGet("{printerId:guid}/reconciliation/audit/{auditId:guid}")]
    public async Task<IActionResult> GetAuditAsync(Guid printerId, Guid auditId, CancellationToken ct)
    {
        if (!PrintFarmerPermissions.HasPermission(User, PrintFarmerPermissions.Queue.Reconcile) ||
            !await resourceAuthorization.CanAccessPrinterAsync(User, printerId, PrinterGroupAccessLevel.View, ct))
        {
            return NotFoundError("audit_not_found");
        }

        DispatchRecoveryAuditDto? audit = await recoveryService.GetAuditAsync(printerId, auditId, ct);
        return audit is null ? NotFoundError("audit_not_found") : Ok(audit);
    }

    /// <summary>
    /// Clears the post-recovery operator block so the job can dispatch again. Requires
    /// <c>If-Match</c> carrying the job revision.
    /// </summary>
    [HttpPost("jobs/{jobId:guid}/recovery/clear")]
    [RequirePermission(PrintFarmerPermissions.Queue.Reconcile)]
    public async Task<IActionResult> ClearRecoveryBlockAsync(Guid jobId, CancellationToken ct)
    {
        if (!await resourceAuthorization.CanAccessJobAsync(User, jobId, PrinterGroupAccessLevel.Manage, ct))
        {
            return NotFoundError("job_not_found");
        }

        if (!TryReadRevision(out long expectedRevision, out _, out IActionResult? etagError))
        {
            return etagError!;
        }

        string actor;
        try
        {
            actor = QueueActorIdentity.Resolve(User);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "actor_unresolved" });
        }

        return ToResult(await recoveryService.ClearRecoveryBlockAsync(jobId, actor, expectedRevision, ct));
    }

    private NotFoundObjectResult NotFoundError(string code) =>
        NotFound(new { error = code });

    private ContentResult ToResult(DispatchRecoveryResult result)
    {
        if (!string.IsNullOrEmpty(result.ETag))
        {
            Response.Headers.ETag = result.ETag;
        }

        return new ContentResult
        {
            Content = result.BodyJson,
            ContentType = "application/json",
            StatusCode = result.StatusCode,
        };
    }

    private bool TryReadRevision(out long revision, out string ifMatch, out IActionResult? error)
    {
        revision = 0;
        ifMatch = Request.Headers.IfMatch.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            error = StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new { error = "precondition_required", detail = "If-Match is required." });
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(ifMatch.Trim().TrimStart('W', '/').Trim('"'));
            revision = RevisionETag.Decode(bytes);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            error = BadRequest(new { error = "invalid_if_match", detail = "If-Match must be a base-64 encoded ETag." });
            return false;
        }
    }

    private bool TryReadIdempotencyKey(string? header, out string key, out IActionResult? error)
    {
        key = header?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            error = StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new { error = "idempotency_key_required", detail = "Idempotency-Key is required." });
            return false;
        }

        if (key.Length > MaxIdempotencyKeyLength)
        {
            error = BadRequest(new { error = "invalid_idempotency_key", detail = $"Idempotency-Key must be at most {MaxIdempotencyKeyLength} characters." });
            return false;
        }

        error = null;
        return true;
    }
}

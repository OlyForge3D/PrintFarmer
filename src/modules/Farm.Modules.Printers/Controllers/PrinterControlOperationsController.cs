using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Printers.Controllers;

/// <summary>HTTP admission/status only. No physical execution is owned by a request.</summary>
[ApiController]
[Authorize]
[MotionValidation]
[Route("api/printers/{printerId:guid}/control-operations")]
public sealed class PrinterControlOperationsController(
    PrinterControlOperationService operations,
    IQueueResourceAuthorizationService authorization,
    AppDbContext db) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(PrinterControlOperationDto), 200)]
    [ProducesResponseType(typeof(PrinterControlOperationDto), 202)]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    public Task<IActionResult> SubmitAsync(Guid printerId, [FromBody] PrinterControlRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            await RequireAccessAsync(printerId, PrinterGroupAccessLevel.Submit, ct);
            if (!Guid.TryParse(idempotencyKey, out Guid id) || id == Guid.Empty)
            {
                throw new PrinterControlException(400, "invalid", "Idempotency-Key must contain one UUID.");
            }

            PrinterControlOperationDto dto = await operations.AdmitAsync(printerId, id, QueueActorIdentity.Resolve(User), request, ct);
            SetHeaders(dto);
            string location = $"/api/printers/{printerId}/control-operations/{id}";
            Response.Headers.Location = location;
            return dto.State is PrinterControlState.Succeeded or PrinterControlState.Failed or PrinterControlState.Recovered
                ? Ok(dto) : Accepted(location, dto);
        });

    [HttpGet("{operationId:guid}")]
    [ProducesResponseType(typeof(PrinterControlOperationDto), 200)]
    public Task<IActionResult> GetAsync(Guid printerId, Guid operationId, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            await RequireAccessAsync(printerId, PrinterGroupAccessLevel.View, ct);
            PrinterControlOperationDto dto = await operations.GetAsync(printerId, operationId, ct);
            SetHeaders(dto);
            return Ok(dto);
        });

    [HttpGet("current")]
    [ProducesResponseType(typeof(PrinterControlCurrentDto), 200)]
    public Task<IActionResult> CurrentAsync(Guid printerId, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            await RequireAccessAsync(printerId, PrinterGroupAccessLevel.View, ct);
            Response.Headers.CacheControl = "no-store";
            return Ok(await operations.GetCurrentAsync(printerId, ct));
        });

    [HttpPost("{operationId:guid}/recovery")]
    [ProducesResponseType(typeof(PrinterControlOperationDto), 202)]
    [Authorize(Roles = PrintFarmerPermissions.FarmAdminRole)]
    [RequirePermission(PrintFarmerPermissions.Queue.Reconcile)]
    public Task<IActionResult> RecoverAsync(Guid printerId, Guid operationId, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            await RequireAccessAsync(printerId, PrinterGroupAccessLevel.Submit, ct);
            PrinterControlOperationDto dto = await operations.BeginRecoveryAsync(
                printerId, operationId, RequireMatch(), QueueActorIdentity.Resolve(User), ct);
            SetHeaders(dto);
            return Accepted($"/api/printers/{printerId}/control-operations/{operationId}", dto);
        });

    [HttpPost("{operationId:guid}/recovery/complete")]
    [ProducesResponseType(typeof(PrinterControlOperationDto), 200)]
    [Authorize(Roles = PrintFarmerPermissions.FarmAdminRole)]
    [RequirePermission(PrintFarmerPermissions.Queue.Reconcile)]
    public Task<IActionResult> CompleteRecoveryAsync(Guid printerId, Guid operationId,
        [FromBody] PrinterControlRecoveryRequest request, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            await RequireAccessAsync(printerId, PrinterGroupAccessLevel.Submit, ct);
            PrinterControlOperationDto dto = await operations.CompleteRecoveryAsync(
                printerId, operationId, RequireMatch(), QueueActorIdentity.Resolve(User), request, ct);
            SetHeaders(dto);
            return Ok(dto);
        });

    private async Task RequireAccessAsync(Guid printerId, PrinterGroupAccessLevel level, CancellationToken ct)
    {
        if (!await authorization.CanAccessPrinterAsync(User, printerId, level, ct) ||
            !await db.Printers.AsNoTracking().AnyAsync(p => p.Id == printerId, ct))
        {
            throw new PrinterControlException(404, "not_found", "Printer not found.");
        }
    }

    private string RequireMatch()
    {
        string revision = Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new PrinterControlException(428, "missing_precondition", "If-Match is required.");
        }

        return revision;
    }

    private void SetHeaders(PrinterControlOperationDto dto)
    {
        Response.Headers.ETag = $"\"{dto.RowVersion}\"";
        Response.Headers.CacheControl = "no-store";
    }

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PrinterControlException exception)
        {
            return Problem(statusCode: exception.Status, title: exception.Code, detail: exception.Message,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
        catch (DbUpdateException)
        {
            return Problem(statusCode: 503, title: "Admission unavailable",
                detail: "Control persistence is unavailable; fetch current state before retrying with the same key.",
                extensions: new Dictionary<string, object?> { ["code"] = "admission_unavailable" });
        }
        catch (DbException)
        {
            return Problem(statusCode: 503, title: "Admission unavailable",
                detail: "Control persistence is unavailable; retry with the same idempotency key.",
                extensions: new Dictionary<string, object?> { ["code"] = "admission_unavailable" });
        }
    }
}

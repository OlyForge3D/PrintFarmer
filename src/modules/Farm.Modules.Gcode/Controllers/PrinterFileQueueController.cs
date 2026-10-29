using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Gcode.Controllers;

/// <summary>Explicit stage-then-queue commands with actor-bound durable replay.</summary>
[ApiController]
[Route("api/printer-file-index")]
[Authorize(Policy = InteractiveSessionRequirement.PolicyName)]
[Authorize(Policy = "ModelWrite")]
public sealed class PrinterFileQueueController(
    IPrinterFileCopyService copies, PrinterFileQueueService queue) : ControllerBase
{
    [HttpPost("{locationId:guid}/queue")]
    public Task<IActionResult> QueueAsync(
        Guid locationId, [FromBody] PrinterFileQueueRequest request,
        [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) =>
        ExecuteAsync(async actor => await copies.CreateAsync(actor, key, new(
            locationId, request.ObservationRevision, "/", Farm.Infrastructure.Domain.PrinterFileAction.Queue,
            request.DestinationPrinterId, request.Options), ct));

    [HttpPost("operations/{operationId:guid}/confirm-snapshot")]
    public Task<IActionResult> ConfirmAsync(
        Guid operationId, [FromBody] PrinterFileSnapshotConfirmation confirmation, CancellationToken ct) =>
        ExecuteAsync(actor => queue.ConfirmAsync(actor, operationId, confirmation, ct));

    [HttpPost("operations/{operationId:guid}/enqueue")]
    public Task<IActionResult> RetryAdmissionAsync(Guid operationId, CancellationToken ct) =>
        ExecuteAsync(actor => queue.AdmitAsync(actor, operationId, ct));

    private async Task<IActionResult> ExecuteAsync(Func<Guid, Task<PrinterFileCopyOperation?>> action)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid actor) || actor == Guid.Empty)
        {
            return Forbid();
        }

        try
        {
            PrinterFileCopyOperation? operation = await action(actor);
            return operation is null ? NotFound()
                : Accepted($"/api/printer-file-index/operations/{operation.Id}", operation);
        }
        catch (PrinterFileCommandException failure)
        {
            return StatusCode(failure.StatusCode, new { code = failure.Reason });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { code = "operation_changed" });
        }
    }
}

public sealed record PrinterFileQueueRequest(
    long ObservationRevision, Guid DestinationPrinterId, PrinterFileQueueOptions? Options = null);

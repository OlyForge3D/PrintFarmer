using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Gcode.Controllers;

[ApiController]
[Route("api/printer-file-index")]
[Authorize(Policy = InteractiveSessionRequirement.PolicyName)]
[RequirePermission(PrintFarmerPermissions.PrinterFiles.Delete)]
public sealed class PrinterFileDeleteController(IPrinterFileDeleteService deletes) : ControllerBase
{
    [HttpPost("{locationId:guid}/delete")]
    public Task<IActionResult> CreateAsync(Guid locationId, [FromBody] DeletePrinterFileRequest request,
        [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) =>
        ExecuteAsync(async actor =>
        {
            PrinterFileDeleteOperation operation = await deletes.CreateAsync(
                actor, key, new(locationId, request.ObservationRevision), ct);
            return Accepted(UrlFor(operation.Id), operation);
        });

    [HttpGet("delete-operations/{operationId:guid}")]
    public Task<IActionResult> GetAsync(Guid operationId, CancellationToken ct) =>
        ExecuteAsync(async actor => await deletes.GetAsync(actor, operationId, ct) is { } operation
            ? Ok(operation) : NotFound());

    [HttpPost("delete-operations/{operationId:guid}/confirm")]
    public Task<IActionResult> ConfirmAsync(
        Guid operationId,
        [FromBody] PrinterFileDeleteConfirmation confirmation, CancellationToken ct) =>
        ExecuteAsync(async actor => await deletes.ConfirmAsync(actor, operationId, confirmation, ct) is { } operation
            ? Ok(operation) : NotFound());

    [HttpPost("delete-operations/{operationId:guid}/reconcile")]
    public Task<IActionResult> ReconcileAsync(Guid operationId, CancellationToken ct) =>
        ExecuteAsync(async actor => await deletes.ReconcileAsync(actor, operationId, ct) is { } operation
            ? Ok(operation) : NotFound());

    private async Task<IActionResult> ExecuteAsync(Func<Guid, Task<IActionResult>> command)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid actor) || actor == Guid.Empty)
        {
            return Forbid();
        }

        try
        {
            return await command(actor);
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

    private static string UrlFor(Guid id) => $"/api/printer-file-index/delete-operations/{id}";
}

public sealed record DeletePrinterFileRequest(long ObservationRevision);

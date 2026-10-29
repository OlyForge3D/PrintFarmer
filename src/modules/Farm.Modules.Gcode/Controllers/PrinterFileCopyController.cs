using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Gcode.Controllers;

/// <summary>Interactive durable staging only; never accepts a remote URL and never queues a job.</summary>
[ApiController]
[Route("api/printer-file-index")]
[Authorize(Policy = InteractiveSessionRequirement.PolicyName)]
[Authorize(Policy = "ModelRead")]
public sealed class PrinterFileCopyController(IPrinterFileCopyService copies) : ControllerBase
{
    [HttpGet("operations")]
    public Task<IActionResult> ListAsync(CancellationToken ct) =>
        ExecuteAsync(async actor => Ok(await copies.ListAsync(actor, ct)));

    [HttpPost("{locationId:guid}/copy")]
    [Authorize(Policy = "ModelWrite")]
    public Task<IActionResult> CopyAsync(
        Guid locationId, [FromBody] CopyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) =>
        ExecuteAsync(async actor =>
        {
            PrinterFileCopyOperation operation = await copies.CreateAsync(actor, key,
                new(locationId, request.ObservationRevision, request.VirtualDirectory), ct);
            return Accepted(OperationUrl(operation.Id), operation);
        });

    [HttpGet("operations/{operationId:guid}")]
    public Task<IActionResult> GetAsync(Guid operationId, CancellationToken ct) =>
        ExecuteAsync(async actor =>
        {
            PrinterFileCopyOperation? operation = await copies.GetAsync(actor, operationId, ct);
            return operation is null ? NotFound() : Ok(operation);
        });

    [HttpPost("operations/{operationId:guid}/retry")]
    [Authorize(Policy = "ModelWrite")]
    public Task<IActionResult> RetryAsync(Guid operationId, CancellationToken ct) =>
        ExecuteAsync(async actor =>
        {
            PrinterFileCopyOperation? operation = await copies.RetryAsync(actor, operationId, ct);
            return operation is null ? NotFound() : Accepted(OperationUrl(operation.Id), operation);
        });

    [HttpPost("operations/{operationId:guid}/cancel")]
    [Authorize(Policy = "ModelWrite")]
    public Task<IActionResult> CancelAsync(Guid operationId, CancellationToken ct) =>
        ExecuteAsync(async actor =>
        {
            PrinterFileCopyOperation? operation = await copies.CancelAsync(actor, operationId, ct);
            return operation is null ? NotFound() : Accepted(OperationUrl(operation.Id), operation);
        });

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

    private static string OperationUrl(Guid id) => $"/api/printer-file-index/operations/{id}";
}

public sealed record CopyRequest(long ObservationRevision, string? VirtualDirectory = "/");

using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Printers;
using Farm.Modules.Printers.Controllers.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Printers.Controllers;

public partial class PrintersController
{
    /// <summary>Sets only the runtime speed override. Success acknowledges the command, not observed readback.</summary>
    [HttpPost("{id:guid}/speed-factor")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(CommandResult), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 501)]
    [ProducesResponseType(typeof(CommandResult), 503)]
    public Task<ActionResult<CommandResult>> SetSpeedFactorAsync(
        Guid id, [FromBody] RuntimeFactorRequest? request, CancellationToken ct) =>
        SetRuntimeFactorAsync(id, request, speed: true, ct);

    /// <summary>Sets only the runtime extrusion override. Success acknowledges the command, not observed readback.</summary>
    [HttpPost("{id:guid}/flow-factor")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(CommandResult), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 501)]
    [ProducesResponseType(typeof(CommandResult), 503)]
    public Task<ActionResult<CommandResult>> SetFlowFactorAsync(
        Guid id, [FromBody] RuntimeFactorRequest? request, CancellationToken ct) =>
        SetRuntimeFactorAsync(id, request, speed: false, ct);

    private async Task<ActionResult<CommandResult>> SetRuntimeFactorAsync(
        Guid id, RuntimeFactorRequest? request, bool speed, CancellationToken ct)
    {
        if (request?.Percent is not double percent || !PrinterRuntimeFactorLimits.IsValid(percent))
        {
            return SensorProblem(400, "Invalid runtime factor",
                "percent must be a finite number from 1 through 200 with at most two decimal places.");
        }

        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.Submit, ct))
        {
            return NotFound();
        }

        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound();
        }

        PrinterBackendCapabilitiesDto? capabilities =
            await _printerBackendCapabilitiesService.GetControlCapabilitiesAsync(id, ct);
        if (capabilities is null)
        {
            return NotFound();
        }

        if (!(speed ? capabilities.SupportsSpeedFactorControl : capabilities.SupportsFlowFactorControl))
        {
            return SensorProblem(501, "Runtime factor control not supported",
                "This printer backend does not support the requested runtime override; no command was sent.");
        }

        string operation = speed ? "set_speed_factor" : "set_flow_factor";
        async Task<bool> SendAsync(CancellationToken token)
        {
            try
            {
                return speed
                    ? await _printersService.SetSpeedFactorAsync(id, percent, token)
                    : await _printersService.SetFlowFactorAsync(id, percent, token);
            }
            catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Runtime override {Operation} timed out on printer {PrinterId}; its physical outcome is unknown",
                    operation, id);
                return false;
            }
        }

        return await ExecuteDirectBooleanControlAsync(id, operation, operation, SendAsync, ct, allowActivePrint: true);
    }
}

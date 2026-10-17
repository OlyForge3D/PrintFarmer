using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Printers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.Printers.Controllers;

public partial class PrintersController
{
    /// <summary>
    /// Sets a writable quantity of one backend-neutral sensor or fan entry. The request is
    /// validated against the sensor's advertised <c>writable</c> descriptors before any backend
    /// command is sent; unknown, unavailable, read-only, unsupported and out-of-range requests
    /// are rejected server-side.
    /// </summary>
    /// <response code="200">The backend confirmed the command.</response>
    /// <response code="400">The body, value or range is invalid.</response>
    /// <response code="404">The printer was not found or is not accessible.</response>
    /// <response code="409">The sensor is unknown, unavailable, read-only, does not accept the quantity, or the printer state does not allow control.</response>
    /// <response code="501">The printer backend does not support targeted sensor control.</response>
    [HttpPost("{id:guid}/sensors/{sensorId}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 501)]
    [ProducesResponseType(typeof(CommandResult), 502)]
    [ProducesResponseType(typeof(CommandResult), 503)]
    public async Task<ActionResult<CommandResult>> SetSensorValueAsync(
        Guid id,
        string sensorId,
        [FromBody] PrinterSensorSetRequest? request,
        CancellationToken ct)
    {
        if (request?.Quantity is not PrinterSensorQuantity quantity || request.Value is not PrinterSensorValue value)
        {
            return SensorProblem(
                StatusCodes.Status400BadRequest,
                "Invalid sensor request",
                "quantity and value are required.");
        }

        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.Submit, ct))
        {
            return NotFound();
        }

        PrinterBackendCapabilitiesDto? capabilities =
            await _printerBackendCapabilitiesService.GetByPrinterIdAsync(id, ct);
        if (capabilities is null)
        {
            return NotFound();
        }

        if (!capabilities.SupportsSensorControl)
        {
            return SensorProblem(
                StatusCodes.Status501NotImplemented,
                "Sensor control not supported",
                "This printer backend does not support targeted sensor control; no command was sent.");
        }

        (bool useActiveLease, _, ActionResult? stateFailure) = await ResolveFanControlLeaseAsync(id, ct);
        if (stateFailure is not null)
        {
            return stateFailure;
        }

        IReadOnlyList<PrinterSensorDto> advertised = _printersService.GetLiveSensors(id);
        PrinterSensorControlValidation validation =
            PrinterSensorNormalizer.ValidateControl(advertised, sensorId, quantity, value);
        if (!validation.IsValid)
        {
            int status = validation.Code is PrinterSensorControlValidationCode.InvalidValue or PrinterSensorControlValidationCode.OutOfRange
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status409Conflict;
            return SensorProblem(
                status,
                "Sensor control rejected",
                $"{validation.Detail} No command was sent.");
        }

        string targetId = validation.SensorId!;
        double target = value.Number!.Value;
        if (!useActiveLease)
        {
            return await ExecuteDirectBooleanControlAsync(
                id,
                "set_sensor_value",
                "set_sensor_value",
                token => _printersService.SetSensorValueAsync(id, targetId, quantity, target, token),
                ct);
        }

        return await ExecuteActiveCommandControlAsync(
            id,
            "set_sensor_value",
            "set_sensor_value",
            async token => await _printersService.SetSensorValueAsync(id, targetId, quantity, target, token)
                ? new CommandResult(true, null)
                : new CommandResult(false, "The backend did not confirm whether the sensor command was applied."),
            ct,
            mapUnconfirmedAsUnavailable: true);
    }

    private static ObjectResult SensorProblem(int statusCode, string title, string detail) =>
        new(new ProblemDetails { Status = statusCode, Title = title, Detail = detail })
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" },
        };
}

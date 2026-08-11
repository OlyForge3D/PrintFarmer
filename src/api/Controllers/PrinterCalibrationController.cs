using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Web.Api.Services.Calibration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/printers")]
[Authorize]
[RequirePermission(PrintFarmerPermissions.Calibration.Read)]
public sealed class PrinterCalibrationController(
    IPrinterCalibrationContextService calibrationContextService)
    : ControllerBase
{
    [HttpGet("calibration-candidates")]
    [ProducesResponseType(typeof(IReadOnlyList<CalibrationCandidateDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCandidatesAsync(
        CancellationToken cancellationToken)
    {
        CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>> result =
            await calibrationContextService.GetCandidatesAsync(
                GetProfileAccessScope(),
                cancellationToken);
        return result.Value is not null
            ? Ok(result.Value)
            : CreateProblem(result.ErrorCode);
    }

    [HttpGet("{id:guid}/calibration-context")]
    [ProducesResponseType(typeof(CalibrationContextDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetContextAsync(
        Guid id,
        [FromQuery] string? slicerType,
        [FromQuery] long? configurationRevision,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
            slicerType,
            CalibrationContractConstants.SlicerEngine,
            StringComparison.Ordinal))
        {
            return CreateProblem("unsupported_slicer_type");
        }

        string? subject =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return CreateProblem("authentication_required");
        }

        CalibrationServiceResult<CalibrationContextDto> result =
            await calibrationContextService.GetContextAsync(
                id,
                configurationRevision,
                subject,
                GetProfileAccessScope(),
                cancellationToken);
        return result.Value is not null
            ? Ok(result.Value)
            : CreateProblem(result.ErrorCode, result.CurrentConfigurationRevision);
    }

    private ObjectResult CreateProblem(
        string? code,
        long? currentConfigurationRevision = null)
    {
        (int status, string title) = code switch
        {
            "unsupported_slicer_type" =>
                (StatusCodes.Status400BadRequest, "Unsupported slicer type"),
            "authentication_required" =>
                (StatusCodes.Status401Unauthorized, "Authentication required"),
            "printer_not_found" =>
                (StatusCodes.Status404NotFound, "Printer not found"),
            "printer_configuration_changed" =>
                (StatusCodes.Status409Conflict, "Printer configuration changed"),
            "status_unavailable" =>
                (StatusCodes.Status503ServiceUnavailable, "Printer status unavailable"),
            "profile_service_unavailable" =>
                (StatusCodes.Status503ServiceUnavailable, "Profile service unavailable"),
            "profile_service_authentication_failed" =>
                (StatusCodes.Status503ServiceUnavailable, "Profile service authentication failed"),
            "profile_service_authorization_failed" =>
                (StatusCodes.Status503ServiceUnavailable, "Profile service authorization failed"),
            "profile_service_configuration_error" =>
                (StatusCodes.Status503ServiceUnavailable, "Profile service configuration error"),
            "profile_service_timeout" =>
                (StatusCodes.Status503ServiceUnavailable, "Profile service timed out"),
            _ =>
                (StatusCodes.Status500InternalServerError, "Calibration context error"),
        };

        ProblemDetails problem = new()
        {
            Status = status,
            Title = title,
            Type = $"https://printfarmer.dev/problems/{code ?? "calibration_context_error"}",
            Instance = HttpContext.Request.Path,
        };
        problem.Extensions["code"] = code ?? "calibration_context_error";
        if (currentConfigurationRevision.HasValue)
        {
            problem.Extensions["currentConfigurationRevision"] =
                currentConfigurationRevision.Value;
        }

        return StatusCode(status, problem);
    }

    private CalibrationProfileAccessScope GetProfileAccessScope()
    {
        Guid? userId = PrintFarmerPermissions.TryGetUserId(User, out Guid parsedUserId)
            ? parsedUserId
            : null;
        return new(
            userId,
            PrintFarmerPermissions.IsFarmAdmin(User));
    }
}

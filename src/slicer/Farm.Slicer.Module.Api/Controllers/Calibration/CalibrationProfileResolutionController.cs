using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers.Calibration;

/// <summary>
/// Serves the exact profiles for a caller-supplied machine/process/filament triple from the profile
/// store this host owns.
/// </summary>
/// <remarks>
/// <para>
/// Split deployments run the profile store behind the slicer host, so the main API cannot resolve
/// calibration profiles in-process. This endpoint is the documented service boundary for that hop.
/// It is deliberately narrow: it takes three identifiers, returns the already credential-free
/// <see cref="ResolvedCalibrationProfiles"/> shape, and offers no listing or search surface.
/// </para>
/// <para>
/// Security: the caller must present the end user's own bearer token and hold
/// <c>calibration:read</c>. The ownership scope is derived here from the validated JWT — a caller can
/// never supply <c>userId</c> or <c>bypassOwnership</c>, and the farm-admin bypass is only reachable
/// through the shared <see cref="PrintFarmerPermissions.IsFarmAdmin"/> helper.
/// </para>
/// </remarks>
[ApiController]
[Route(CalibrationProfileResolutionContract.RoutePrefix)]
[Tags("Calibration Profiles")]
[Authorize]
[RequirePermission(PrintFarmerPermissions.Calibration.Read)]
public sealed class CalibrationProfileResolutionController(
    ILogger<CalibrationProfileResolutionController> logger,
    ICalibrationProfileResolver? profileResolver = null)
    : ControllerBase
{
    /// <summary>Resolves the three explicitly selected calibration profiles.</summary>
    /// <param name="body">The exact three-GUID request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The credential-free resolved profile set; missing profiles stay <c>null</c>.</returns>
    [HttpPost(CalibrationProfileResolutionContract.ResolveActionRoute)]
    [Consumes("application/json")]
    [RequestSizeLimit(CalibrationProfileResolutionContract.MaxRequestBodyBytes)]
    [ProducesResponseType(typeof(ResolvedCalibrationProfiles), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResolveAsync(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        if (!CalibrationProfileResolutionContract.TryParseRequest(
                body,
                out ResolveCalibrationProfilesRequest? request))
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                CalibrationProfileResolutionContract.InvalidRequestCode,
                "The request must contain exactly machineProfileId, processProfileId and filamentProfileId.");
        }

        // The scope is always derived from this host's validated token, never from the request body.
        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        CalibrationProfileAccessScope accessScope = new(
            userId,
            PrintFarmerPermissions.IsFarmAdmin(User));

        if (profileResolver is null)
        {
            return ResolverUnavailable();
        }

        try
        {
            ResolvedCalibrationProfiles resolved = await profileResolver.ResolveAsync(
                request.MachineProfileId,
                request.ProcessProfileId,
                request.FilamentProfileId,
                accessScope,
                ct);
            return Ok(resolved);
        }
        catch (CalibrationProfileResolverUnavailableException exception)
        {
            logger.LogWarning(
                "Calibration profile resolution is unavailable ({ExceptionType})",
                exception.GetType().Name);
            return ResolverUnavailable();
        }
    }

    private ObjectResult ResolverUnavailable()
    {
        ProblemDetails problem = new()
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Profile service unavailable",
            Type =
                "https://printfarmer.dev/problems/" +
                CalibrationProfileResolutionContract.ResolverUnavailableCode,
            Instance = HttpContext.Request.Path,
        };
        problem.Extensions["code"] =
            CalibrationProfileResolutionContract.ResolverUnavailableCode;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentTypes = { "application/problem+json" },
        };
    }
}

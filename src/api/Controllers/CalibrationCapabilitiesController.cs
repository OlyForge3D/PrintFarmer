using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Services.Capabilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Farm.Web.Api.Controllers;

/// <summary>Exposes effective calibration capabilities for the authenticated caller.</summary>
[ApiController]
[Authorize]
[Route("api/calibration")]
public sealed class CalibrationCapabilitiesController(
    ICalibrationCapabilityService capabilityService) : ControllerBase
{
    private readonly ICalibrationCapabilityService _capabilityService = capabilityService;

    /// <summary>
    /// Gets permissions and currently reachable calibration foundation operations for the caller.
    /// </summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(PlatformCapabilitiesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status426UpgradeRequired)]
    public async Task<ActionResult<PlatformCapabilitiesDto>> GetCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        ApiContractNegotiation.AddResponseHeaders(Response);
        ObjectResult? negotiationFailure = ApiContractNegotiation.Negotiate(Request);
        if (negotiationFailure is not null)
        {
            return negotiationFailure;
        }

        Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
        {
            Private = true,
            MaxAge = TimeSpan.FromSeconds(15),
        };
        Response.Headers.Vary = HeaderNames.Authorization;

        PlatformCapabilitiesDto capabilities =
            await _capabilityService.GetCapabilitiesAsync(User, cancellationToken);
        return Ok(capabilities);
    }
}

using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Services.Capabilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Exposes platform capabilities for ARM/Raspberry Pi graceful degradation.
/// Unauthenticated so the frontend can gate UI before login.
/// </summary>
[ApiController]
[Route("api/system")]
[AllowAnonymous]
public class SystemCapabilitiesController(
    ICalibrationCapabilityService capabilityService) : ControllerBase
{
    private readonly ICalibrationCapabilityService _capabilityService = capabilityService;

    /// <summary>
    /// Returns the current platform capabilities, auto-detecting ARM64 to disable
    /// features that depend on native libraries without ARM builds.
    /// </summary>
    /// <returns>Platform capabilities for the running host.</returns>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(PlatformCapabilitiesDto), StatusCodes.Status200OK)]
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
            Public = true,
            MaxAge = TimeSpan.FromSeconds(30),
        };

        PlatformCapabilitiesDto capabilities =
            await _capabilityService.GetCapabilitiesAsync(user: null, cancellationToken);
        return Ok(capabilities);
    }
}

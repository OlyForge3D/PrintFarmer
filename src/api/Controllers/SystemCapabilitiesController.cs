using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.FeatureFlags;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Services.Capabilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Exposes platform capabilities for ARM/Raspberry Pi graceful degradation.
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemCapabilitiesController(
    ICalibrationCapabilityService capabilityService,
    IFeatureFlagService featureFlagService,
    IOperatorFeatureGate operatorFeatureGate) : ControllerBase
{
    private readonly ICalibrationCapabilityService _capabilityService = capabilityService;
    private readonly IFeatureFlagService _featureFlagService = featureFlagService;
    private readonly IOperatorFeatureGate _operatorFeatureGate = operatorFeatureGate;

    /// <summary>
    /// Returns the current platform capabilities, auto-detecting ARM64 to disable
    /// features that depend on native libraries without ARM builds.
    /// </summary>
    /// <returns>Platform capabilities for the running host.</returns>
    [HttpGet("capabilities")]
    [AllowAnonymous] // Public because login and setup use these non-sensitive flags before authentication.
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
            (await _capabilityService.GetCapabilitiesAsync(user: null, cancellationToken)) with
            {
                OperatorFeatures = _operatorFeatureGate.GetEffectiveFlags(),
            };
        return Ok(capabilities);
    }

    /// <summary>
    /// Returns all feature flags for phased rollout control.
    /// </summary>
    /// <returns>Dictionary of feature keys and their enabled states.</returns>
    [HttpGet("feature-flags")]
    [ResponseCache(Duration = 300)]
    [ProducesResponseType(typeof(Dictionary<string, bool>), StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, bool>> GetFeatureFlags()
    {
        var flags = _featureFlagService.GetAllFlags();
        return Ok(flags);
    }
}

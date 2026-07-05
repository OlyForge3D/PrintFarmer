using System.Runtime.InteropServices;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.FeatureFlags;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Exposes platform capabilities for ARM/Raspberry Pi graceful degradation.
/// Unauthenticated so the frontend can gate UI before login.
/// </summary>
[ApiController]
[Route("api/system")]
[AllowAnonymous] // Frontend reads capabilities and feature flags before login to decide which UI to render.
public class SystemCapabilitiesController(
    IConfiguration configuration,
    IFeatureFlagService featureFlagService) : ControllerBase
{
    private readonly IConfiguration _configuration = configuration;
    private readonly IFeatureFlagService _featureFlagService = featureFlagService;

    /// <summary>
    /// Returns the current platform capabilities, auto-detecting ARM64 to disable
    /// features that depend on native libraries without ARM builds.
    /// </summary>
    /// <returns>Platform capabilities for the running host.</returns>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(PlatformCapabilitiesDto), StatusCodes.Status200OK)]
    public ActionResult<PlatformCapabilitiesDto> GetCapabilities()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        bool isArm = arch is Architecture.Arm64 or Architecture.Arm;

        bool modelFilesEnabled = _configuration.GetValue("Platform:ModelFilesEnabled", true);
        bool slicerEnabled = _configuration.GetValue("Slicer:Enabled", true);
        bool thumbnailEnabled = _configuration.GetValue("Platform:ThumbnailGenerationEnabled", true);

        // In microservices mode the slicer module is NOT loaded in this API process,
        // but the standalone slicer-host provides the capability. Report slicing as
        // available so the frontend shows the slicer UI (nginx routes slicer paths
        // to the slicer-host container).
        bool isMicroservices = string.Equals(
            _configuration.GetValue<string>("DEPLOYMENT_MODE"),
            "microservices",
            StringComparison.OrdinalIgnoreCase);

        if (isMicroservices && !isArm)
        {
            slicerEnabled = true;
        }

        string? platformNote = isArm && (!modelFilesEnabled || !slicerEnabled || !thumbnailEnabled)
            ? "Running on ARM64 — 3D model and slicing features are disabled"
            : null;

        var dto = new PlatformCapabilitiesDto
        {
            Architecture = arch.ToString(),
            SlicingEnabled = slicerEnabled,
            ModelFilesEnabled = modelFilesEnabled,
            ThumbnailGenerationEnabled = thumbnailEnabled,
            GcodeUploadEnabled = true,
            PlatformNote = platformNote,
        };

        return Ok(dto);
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

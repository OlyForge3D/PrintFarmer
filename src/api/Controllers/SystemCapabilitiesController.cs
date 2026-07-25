using System.Runtime.InteropServices;
using Farm.Infrastructure.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Exposes platform capabilities for ARM/Raspberry Pi graceful degradation.
/// Unauthenticated so the frontend can gate UI before login.
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemCapabilitiesController(
    IConfiguration configuration) : ControllerBase
{
    private readonly IConfiguration _configuration = configuration;

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

        // Read resolved values — Program.cs writes these after applying ARM + DEPLOYMENT_MODE logic
        bool modelFilesEnabled = _configuration.GetValue("Platform:ModelFilesEnabled", true);
        bool slicerEnabled = _configuration.GetValue("Slicer:Enabled", true);
        bool thumbnailEnabled = _configuration.GetValue("Platform:ThumbnailGenerationEnabled", true);

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
}

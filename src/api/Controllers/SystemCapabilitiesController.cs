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
    IConfiguration configuration,
    ILogger<SystemCapabilitiesController> logger) : ControllerBase
{
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<SystemCapabilitiesController> _logger = logger;

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

        // Read configured values (default to true for x86/x64)
        bool modelFilesEnabled = _configuration.GetValue("Platform:ModelFilesEnabled", true);
        bool slicerEnabled = _configuration.GetValue("Slicer:Enabled", true);
        bool thumbnailEnabled = _configuration.GetValue("Platform:ThumbnailGenerationEnabled", true);

        // On ARM, auto-disable features unless the user explicitly overrode them in config/env
        if (isArm)
        {
            if (_configuration.GetSection("Platform:ModelFilesEnabled").Value is null)
            {
                modelFilesEnabled = false;
            }

            if (_configuration.GetSection("Slicer:Enabled").Value is null)
            {
                slicerEnabled = false;
            }

            if (_configuration.GetSection("Platform:ThumbnailGenerationEnabled").Value is null)
            {
                thumbnailEnabled = false;
            }

            _logger.LogInformation(
                "ARM architecture detected ({Architecture}). ModelFiles={ModelFiles}, Slicer={Slicer}, Thumbnails={Thumbnails}",
                arch, modelFilesEnabled, slicerEnabled, thumbnailEnabled);
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
}

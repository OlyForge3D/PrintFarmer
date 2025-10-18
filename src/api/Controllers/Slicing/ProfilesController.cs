using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer/profiles")]
[Tags("Slicer Profiles")]
public class ProfilesController(IUnifiedLoggingService logger, Farm.Web.Api.Services.Slicing.IProfilesService profilesService) : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly Farm.Web.Api.Services.Slicing.IProfilesService _profilesService = profilesService;

    [HttpPost]
    [ProducesResponseType(typeof(SlicerProfileResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateProfileAsync([FromBody] CreateSlicerProfileDto? request)
    {
        try
        {
            if (request is null)
            {
                return BadRequest("Request body is required");
            }
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Name is required");
            }
            if (string.IsNullOrWhiteSpace(request.SlicerType) || !Enum.TryParse<SlicerType>(request.SlicerType, true, out SlicerType slicerType))
            {
                return BadRequest("Invalid slicer type");
            }
            ProfileQuality quality = ProfileQuality.Standard;
            if (!string.IsNullOrWhiteSpace(request.Quality) && !Enum.TryParse<ProfileQuality>(request.Quality, true, out quality))
            {
                return BadRequest("Invalid quality setting");
            }
            // Map to service request and delegate creation
            var createReq = new Farm.Web.Shared.CreateSlicerProfileDto
            {
                Name = request.Name,
                Description = request.Description,
                SlicerType = request.SlicerType,
                LayerHeight = request.LayerHeight,
                InfillPercentage = request.InfillPercentage,
                PrintSpeed = request.PrintSpeed,
                NozzleTemperature = request.NozzleTemperature,
                BedTemperature = request.BedTemperature,
                EnableSupports = request.EnableSupports,
                Material = request.Material,
                Quality = request.Quality,
                IsDefault = request.IsDefault,
                IsPublic = request.IsPublic,
                AdvancedSettings = request.AdvancedSettings
            };

            var created = await _profilesService.CreateProfileAsync(createReq, CancellationToken.None);
            return Created($"/api/slicer/profiles/{created.Id}", created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to create profile: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to create profile");
        }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SlicerProfileResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfileAsync(Guid id)
    {
        var profile = await _profilesService.GetProfileAsync(id, CancellationToken.None);
        if (profile == null)
        {
            return NotFound();
        }
        return Ok(profile);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProfileAsync(Guid id)
    {
        try
        {
            await _profilesService.DeleteProfileAsync(id, CancellationToken.None);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfilesAsync([FromQuery] string? printerId = null, [FromQuery] string? slicerType = null)
    {
        try
        {
            // For list operations, delegate to service which handles defaults & filtering
            if (string.IsNullOrWhiteSpace(printerId) && string.IsNullOrWhiteSpace(slicerType))
            {
                return Ok(DefaultProfiles().Select(d => (object)new
                {
                    name = $"Default {d.Quality}",
                    slicerType = "PrusaSlicer",
                    d.LayerHeight,
                    d.InfillPercentage,
                    printSpeed = d.PrintSpeed,
                    d.NozzleTemperature,
                    d.BedTemperature,
                    supports = d.Supports,
                    d.Material,
                    d.Quality
                }));
            }

            var list = await _profilesService.GetProfilesAsync(CancellationToken.None);
            // Map to lightweight view for the client (SlicerProfileDto doesn't include Name/SlicerType)
            IEnumerable<object> mapped = list.Select(p => (object)new
            {
                p.LayerHeight,
                p.InfillPercentage,
                printSpeed = p.PrintSpeed,
                p.NozzleTemperature,
                p.BedTemperature,
                supports = p.Supports,
                p.Material,
                quality = p.Quality
            });

            var final = mapped.ToList();
            if (final.Count == 0)
            {
                return Ok(DefaultProfiles().Select(d => (object)new
                {
                    name = $"Default {d.Quality}",
                    slicerType = "PrusaSlicer",
                    d.LayerHeight,
                    d.InfillPercentage,
                    printSpeed = d.PrintSpeed,
                    d.NozzleTemperature,
                    d.BedTemperature,
                    supports = d.Supports,
                    d.Material,
                    d.Quality
                }));
            }

            return Ok(final);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get profiles: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get available profiles");
        }
    }

    private static List<SlicerProfileDto> DefaultProfiles()
    {
        return new List<SlicerProfileDto>
        {
            new() { LayerHeight = 0.3, InfillPercentage = 10, PrintSpeed = 60, NozzleTemperature = 210, BedTemperature = 60, Supports = false, Material = "PLA", Quality = "draft" },
            new() { LayerHeight = 0.2, InfillPercentage = 20, PrintSpeed = 50, NozzleTemperature = 210, BedTemperature = 60, Supports = false, Material = "PLA", Quality = "standard" },
            new() { LayerHeight = 0.15, InfillPercentage = 25, PrintSpeed = 40, NozzleTemperature = 210, BedTemperature = 60, Supports = true, Material = "PLA", Quality = "fine" }
        };
    }
}

using Farm.Web.Api.Data;
using Farm.Web.Api.Domain; // for SlicerType enum if namespace differs
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Farm.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer/profiles")]
[Tags("Slicer Profiles")]
public class ProfilesController(AppDbContext context, IUnifiedLoggingService logger) : ControllerBase
{
    private readonly AppDbContext _context = context;
    private readonly IUnifiedLoggingService _logger = logger;

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
            SlicerProfile profile = new()
            {
                Id = Guid.NewGuid(),
                Name = request.Name!,
                Description = request.Description,
                SlicerType = slicerType,
                LayerHeight = request.LayerHeight,
                InfillPercentage = request.InfillPercentage,
                PrintSpeed = request.PrintSpeed,
                NozzleTemperature = request.NozzleTemperature,
                BedTemperature = request.BedTemperature,
                EnableSupports = request.EnableSupports,
                Material = request.Material ?? "PLA",
                Quality = quality,
                IsDefault = request.IsDefault,
                IsPublic = request.IsPublic,
                CreatedAt = DateTime.UtcNow
            };
            _context.SlicerProfiles.Add(profile);
            await _context.SaveChangesAsync();
            SlicerProfileResponseDto response = new()
            {
                Id = profile.Id,
                Name = profile.Name,
                Description = profile.Description,
                SlicerType = profile.SlicerType.ToString(),
                LayerHeight = profile.LayerHeight,
                InfillPercentage = profile.InfillPercentage,
                PrintSpeed = (int)profile.PrintSpeed,
                NozzleTemperature = profile.NozzleTemperature,
                BedTemperature = profile.BedTemperature,
                EnableSupports = profile.EnableSupports,
                Material = profile.Material,
                Quality = profile.Quality.ToString(),
                IsDefault = profile.IsDefault,
                IsPublic = profile.IsPublic
            };
            return Created($"/api/slicer/profiles/{profile.Id}", response);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to create profile: {ex.Message}", ex);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to create profile");
        }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SlicerProfileResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfileAsync(Guid id)
    {
        SlicerProfile? profile = await _context.SlicerProfiles.FirstOrDefaultAsync(p => p.Id == id);
        if (profile == null)
        {
            return NotFound();
        }
        return Ok(new SlicerProfileResponseDto
        {
            Id = profile.Id,
            Name = profile.Name,
            Description = profile.Description,
            SlicerType = profile.SlicerType.ToString(),
            LayerHeight = profile.LayerHeight,
            InfillPercentage = profile.InfillPercentage,
            PrintSpeed = (int)profile.PrintSpeed,
            NozzleTemperature = profile.NozzleTemperature,
            BedTemperature = profile.BedTemperature,
            EnableSupports = profile.EnableSupports,
            Material = profile.Material,
            Quality = profile.Quality.ToString(),
            IsDefault = profile.IsDefault,
            IsPublic = profile.IsPublic
        });
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProfileAsync(Guid id)
    {
        SlicerProfile? profile = await _context.SlicerProfiles.FirstOrDefaultAsync(p => p.Id == id);
        if (profile == null)
        {
            return NotFound();
        }
        _context.SlicerProfiles.Remove(profile);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfilesAsync([FromQuery] string? printerId = null, [FromQuery] string? slicerType = null)
    {
        try
        {
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

            IQueryable<SlicerProfile> query = _context.SlicerProfiles
                .Include(p => p.PrinterModel)
                .Include(p => p.SpecificPrinter)
                .Where(p => p.IsPublic || p.CreatedByUserId == null);

            if (!string.IsNullOrEmpty(printerId) && Guid.TryParse(printerId, out Guid printerGuid))
            {
                Printer? printer = await _context.Printers.Include(p => p.Model).FirstOrDefaultAsync(p => p.Id == printerGuid);
                if (printer != null)
                {
                    query = query.Where(p => p.SpecificPrinterId == printerGuid || (p.PrinterModelId == printer.ModelId && p.SpecificPrinterId == null) || (p.PrinterModelId == null && p.SpecificPrinterId == null));
                }
            }

            if (!string.IsNullOrEmpty(slicerType) && Enum.TryParse<SlicerType>(slicerType, true, out SlicerType slicerTypeEnum))
            {
                query = query.Where(p => p.SlicerType == slicerTypeEnum);
            }

            var profiles = await query
                .OrderBy(p => p.IsDefault ? 0 : 1)
                .ThenBy(p => p.Name)
                .Select(p => new
                {
                    name = p.Name,
                    slicerType = p.SlicerType.ToString(),
                    p.LayerHeight,
                    p.InfillPercentage,
                    printSpeed = (int)p.PrintSpeed,
                    p.NozzleTemperature,
                    p.BedTemperature,
                    supports = p.EnableSupports,
                    p.Material,
                    quality = p.Quality.ToString().ToLowerInvariant()
                })
                .ToListAsync();

            if (profiles.Count == 0)
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

            Dictionary<string, int> qualityOrder = new(StringComparer.OrdinalIgnoreCase)
            {
                ["draft"] = 0,
                ["standard"] = 1,
                ["fine"] = 2
            };
            profiles = [.. profiles
                .OrderBy(p => qualityOrder.TryGetValue(p.quality, out int precedence) ? precedence : 99)
                .ThenBy(p => p.name)];

            return Ok(profiles);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get profiles: {ex.Message}", ex);
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

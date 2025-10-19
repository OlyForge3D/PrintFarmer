using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Api.Services.Slicing;
using System.Text.Json;
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

    // --- Phase 6: Import new slicer profile JSON (dedup + metadata extraction) ---
    [HttpPost("import")]
    [ProducesResponseType(typeof(SlicerProfileExtendedDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(SlicerProfileExtendedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportProfileAsync(
        [FromBody] ImportSlicerProfileDto? request,
        [FromServices] IProfileParsingService parsingService,
        [FromServices] ISlicerProfileRepository repo,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RawJson))
        {
            return BadRequest("rawJson is required");
        }
        if (string.IsNullOrWhiteSpace(request.SlicerType) || !Enum.TryParse<SlicerType>(request.SlicerType, true, out SlicerType slicerType))
        {
            return BadRequest("Invalid slicerType");
        }
        try
        {
            var (sanitizedRaw, metadataJson, hash) = parsingService.ParseAndPrepare(request.RawJson);
            // Attempt to derive basic fields from metadata
            double layerHeight = 0.2;
            int infillPct = 20;
            string material = "PLA";
            string quality = "Standard";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(metadataJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("layerHeight", out var lh) && lh.TryGetDouble(out double lhVal))
                {
                    layerHeight = lhVal;
                }

                if (root.TryGetProperty("infillPercentage", out var inf) && inf.TryGetInt32(out int infVal))
                {
                    infillPct = infVal;
                }

                if (root.TryGetProperty("filamentMaterial", out var mat) && mat.ValueKind == JsonValueKind.String)
                {
                    material = mat.GetString() ?? material;
                }

                if (root.TryGetProperty("profileType", out var qt) && qt.ValueKind == JsonValueKind.String)
                {
                    quality = qt.GetString() ?? quality;
                }
            }
            catch { /* fallback to defaults */ }
            string name = request.Name?.Trim() ?? $"{quality} {layerHeight:0.##}mm";
            SlicerProfile imported = new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = request.Description,
                SlicerType = slicerType,
                LayerHeight = layerHeight,
                InfillPercentage = infillPct,
                Material = material,
                Quality = Enum.TryParse<ProfileQuality>(quality, true, out var q) ? q : ProfileQuality.Standard,
                RawJson = sanitizedRaw,
                MetadataJson = metadataJson,
                Hash = hash,
                IsPublic = request.IsPublic,
                IsDefault = request.SetDefault,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            bool created = false;
            SlicerProfile result = await repo.AddOrUpdateFromImportAsync(imported, request.AllowSystemOverride, ct);
            if (result.Id == imported.Id)
            {
                created = true;
            }
            if (request.SetDefault)
            {
                await repo.SetDefaultAsync(result, result.CreatedByUserId, ct);
            }
            Dictionary<string, object?> metadataDict = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                using JsonDocument doc = JsonDocument.Parse(result.MetadataJson ?? "{}");
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    metadataDict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : (prop.Value.TryGetDouble(out double d) ? d : null),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    };
                }
            }
            catch { }
            SlicerProfileExtendedDto dto = new()
            {
                Id = result.Id,
                Name = result.Name,
                Description = result.Description,
                SlicerType = result.SlicerType.ToString(),
                LayerHeight = result.LayerHeight,
                InfillPercentage = result.InfillPercentage,
                PrintSpeed = result.PrintSpeed,
                NozzleTemperature = result.NozzleTemperature,
                BedTemperature = result.BedTemperature,
                EnableSupports = result.EnableSupports,
                Material = result.Material,
                Quality = result.Quality.ToString(),
                IsDefault = result.IsDefault,
                IsPublic = result.IsPublic,
                IsSystem = result.IsSystem,
                Hash = result.Hash ?? string.Empty,
                CreatedAt = result.CreatedAt,
                UpdatedAt = result.UpdatedAt,
                Metadata = metadataDict
            };
            if (created)
            {
                return Created($"/api/slicer/profiles/{dto.Id}", dto);
            }
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import slicer profile");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to import profile");
        }
    }

    // Export raw JSON for a profile
    [HttpGet("{id:guid}/export")]
    [ProducesResponseType(typeof(SlicerProfileExportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportProfileAsync(Guid id, [FromServices] ISlicerProfileRepository repo, CancellationToken ct)
    {
        SlicerProfile? profile = await repo.GetByIdAsync(id, ct);
        if (profile is null)
        {
            return NotFound();
        }

        Dictionary<string, object?> metadataDict = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(profile.MetadataJson ?? "{}");
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                metadataDict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : (prop.Value.TryGetDouble(out double d) ? d : null),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }
        }
        catch { }
        SlicerProfileExportDto dto = new()
        {
            Id = profile.Id,
            Name = profile.Name,
            SlicerType = profile.SlicerType.ToString(),
            Hash = profile.Hash ?? string.Empty,
            RawJson = profile.RawJson ?? string.Empty,
            Metadata = metadataDict
        };
        return Ok(dto);
    }

    // Set profile as default (global or user scope)
    [HttpPost("{id:guid}/set-default")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultProfileAsync(Guid id, [FromServices] ISlicerProfileRepository repo, CancellationToken ct)
    {
        SlicerProfile? profile = await repo.GetByIdAsync(id, ct);
        if (profile is null)
        {
            return NotFound();
        }

        await repo.SetDefaultAsync(profile, profile.CreatedByUserId, ct);
        return NoContent();
    }

    // Extended listing of profiles (user + public + system)
    [HttpGet("extended")]
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListExtendedAsync([FromServices] AppDbContext db, CancellationToken ct)
    {
        // Pull all profiles (simple approach; future optimization: paging & filtering)
        var profiles = await db.SlicerProfiles
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
        List<SlicerProfileListItemDto> list = new(profiles.Count);
        foreach (var p in profiles)
        {
            list.Add(new SlicerProfileListItemDto
            {
                Id = p.Id,
                Name = p.Name,
                SlicerType = p.SlicerType.ToString(),
                Material = p.Material,
                Quality = p.Quality.ToString(),
                LayerHeight = p.LayerHeight,
                InfillPercentage = p.InfillPercentage,
                IsDefault = p.IsDefault,
                IsSystem = p.IsSystem,
                IsPublic = p.IsPublic,
                Hash = p.Hash ?? string.Empty
            });
        }
        return Ok(list);
    }

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

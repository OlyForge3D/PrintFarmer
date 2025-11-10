using System.Linq;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer/profiles")]
[Tags("Slicer Profiles")]
[Authorize] // All endpoints require authentication
public class ProfilesController(IUnifiedLoggingService logger, Farm.Web.Api.Services.Slicing.IProfilesService profilesService) : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly Farm.Web.Api.Services.Slicing.IProfilesService _profilesService = profilesService;

    // --- Phase 6: Import new slicer profile JSON (dedup + metadata extraction) ---
    [HttpPost("import")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
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
    [Authorize(Policy = "farm_admin")] // Admin-only: profile export
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
    [Authorize(Policy = "farm_admin")] // Admin-only: set default profile
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
    [Authorize(Policy = "farm_admin")] // Admin-only: create profile
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
    [Authorize(Policy = "farm_admin")] // Admin-only: delete profile
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

    // --- Phase 6: OrcaSlicer bundle import/preview endpoint ---
    /// <summary>
    /// Parse and preview an OrcaSlicer config bundle without persisting. Returns structured preview of all detected presets.
    /// </summary>
    /// <param name="request">Bundle JSON payload</param>
    /// <param name="orcaParsingService">OrcaSlicer bundle parsing service</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Preview DTO with printers, filaments, and process presets</returns>
    [HttpPost("import/orca/preview")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile bundle import
    [ProducesResponseType(typeof(OrcaBundlePreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult PreviewOrcaBundle(
        [FromBody] ImportOrcaBundleDto? request,
        [FromServices] IOrcaBundleParsingService orcaParsingService,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BundleJson))
        {
            return BadRequest("BundleJson is required");
        }

        try
        {
            // Validate bundle format
            if (!orcaParsingService.IsValidOrcaBundle(request.BundleJson))
            {
                return BadRequest("Invalid OrcaSlicer bundle format. Expected JSON object with printer/filament/process sections.");
            }

            // Parse and return preview
            OrcaBundlePreviewDto preview = orcaParsingService.ParseBundle(request.BundleJson);

            _logger.LogInformation(
                $"OrcaSlicer bundle preview: {preview.Printers.Count} printers, {preview.Filaments.Count} filaments, {preview.Processes.Count} processes");

            return Ok(preview);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Invalid OrcaSlicer bundle format");
            return BadRequest(new { error = "Invalid bundle format", detail = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to preview OrcaSlicer bundle");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to parse bundle");
        }
    }

    /// <summary>
    /// Export PrintFarmer profiles to OrcaSlicer config bundle JSON format.
    /// </summary>
    /// <param name="request">Export configuration (optional filters for printers/filaments)</param>
    /// <param name="exportService">OrcaSlicer bundle export service</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Valid OrcaSlicer config bundle JSON string</returns>
    [HttpPost("export/orca")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile export
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Produces("application/json")]
    public async Task<IActionResult> ExportOrcaBundleAsync(
        [FromBody] ExportOrcaBundleRequest? request,
        [FromServices] IOrcaBundleExportService exportService,
        CancellationToken ct)
    {
        try
        {
            // Use default request if not provided
            request ??= new ExportOrcaBundleRequest();

            // Generate bundle JSON
            string bundleJson = await exportService.ExportBundleAsync(request);

            _logger.LogInformation(
                $"OrcaSlicer bundle exported: {request.PrinterModelIds?.Count ?? 0} printer filters, {request.FilamentTypeIds?.Count ?? 0} filament filters");

            // Return raw JSON with proper content type
            return Content(bundleJson, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export OrcaSlicer bundle");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to generate export bundle");
        }
    }

    // Lightweight listing of system-seeded OrcaSlicer profiles for UI verification
    // Returns minimal list item DTOs (Id, Name, Material, Quality, LayerHeight, Infill, Hash flags)
    [HttpGet("system/orca")]
    [Authorize(Policy = "farm_admin")] // Admin-only: system profile inspection
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSystemOrcaProfilesAsync([FromServices] AppDbContext db, CancellationToken ct)
    {
        var profiles = await db.SlicerProfiles
            .AsNoTracking()
            .Where(p => p.IsSystem && p.SlicerType == SlicerType.OrcaSlicer)
            .OrderBy(p => p.Name)
            .Select(p => new SlicerProfileListItemDto
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
            })
            .ToListAsync(ct);

        return Ok(profiles);
    }

    /// <summary>
    /// Get system profiles available for import for a specific registered printer.
    /// Filters profiles by matching printer model compatibility.
    /// </summary>
    /// <param name="printerId">The ID of the registered printer</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of available system profiles for the printer</returns>
    /// <response code="200">Returns list of compatible profiles</response>
    /// <response code="404">Printer not found</response>
    [HttpGet("available-for-printer/{printerId}")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAvailableProfilesForPrinterAsync(
        Guid printerId,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        // Verify printer exists
        var printer = await db.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, ct);

        if (printer is null)
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

        // Get the printer's model for compatibility matching
        var printerModel = await db.Models
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == printer.ModelId, ct);

        // Start with system profiles
        var query = db.SlicerProfiles
            .AsNoTracking()
            .Where(p => p.IsSystem && p.SlicerType == SlicerType.OrcaSlicer);

        // If we have a printer model, we could add model-specific filtering here (future enhancement)
        // For now, return all system OrcaSlicer profiles

        var profiles = await query
            .OrderBy(p => p.Material)
            .ThenBy(p => p.Quality)
            .ThenBy(p => p.LayerHeight)
            .Select(p => new SlicerProfileListItemDto
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
            })
            .ToListAsync(ct);

        return Ok(profiles);
    }

    /// <summary>
    /// Bulk import system profiles for a specific registered printer.
    /// Only OrcaSlicer system profiles can be bulk imported.
    /// </summary>
    /// <param name="printerId">The ID of the registered printer</param>
    /// <param name="profileIds">List of profile IDs to import</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of profiles imported/duplicated</returns>
    /// <response code="200">Profiles imported successfully</response>
    /// <response code="404">Printer not found</response>
    [HttpPost("bulk-import-for-printer/{printerId}")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
    [ProducesResponseType(typeof(BulkProfileImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkImportProfilesForPrinterAsync(
        Guid printerId,
        [FromBody] BulkProfileImportRequest? request,
        [FromServices] AppDbContext db,
        [FromServices] ISlicerProfileRepository profileRepo,
        CancellationToken ct)
    {
        if (request is null || request.ProfileIds == null || request.ProfileIds.Count == 0)
        {
            return BadRequest("profileIds list is required and must not be empty");
        }

        // Verify printer exists
        var printer = await db.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, ct);

        if (printer is null)
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

        // Get the profiles to import
        var profilesToImport = await db.SlicerProfiles
            .AsNoTracking()
            .Where(p => p.IsSystem && p.SlicerType == SlicerType.OrcaSlicer && request.ProfileIds.Contains(p.Id))
            .ToListAsync(ct);

        if (profilesToImport.Count == 0)
        {
            return BadRequest("No valid system profiles found for import");
        }

        // Import each profile (skipping duplicates)
        int imported = 0;
        int duplicated = 0;

        foreach (var systemProfile in profilesToImport)
        {
            try
            {
                // Create a user-owned copy of the system profile
                var userProfile = new SlicerProfile
                {
                    Id = Guid.NewGuid(),
                    Name = systemProfile.Name,
                    Description = $"Imported from system profile for {printer.Name}",
                    SlicerType = systemProfile.SlicerType,
                    LayerHeight = systemProfile.LayerHeight,
                    InfillPercentage = systemProfile.InfillPercentage,
                    Material = systemProfile.Material,
                    Quality = systemProfile.Quality,
                    PrintSpeed = systemProfile.PrintSpeed,
                    NozzleTemperature = systemProfile.NozzleTemperature,
                    BedTemperature = systemProfile.BedTemperature,
                    EnableSupports = systemProfile.EnableSupports,
                    RawJson = systemProfile.RawJson,
                    MetadataJson = systemProfile.MetadataJson,
                    Hash = systemProfile.Hash,
                    IsSystem = false,
                    IsDefault = false,
                    IsPublic = request.MakePublic ?? false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await profileRepo.AddOrUpdateFromImportAsync(userProfile, allowSystemOverride: false, ct);
                imported++;
            }
            catch (Exception ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException ||
                                      ex.InnerException?.Message?.Contains("UNIQUE constraint failed") == true)
            {
                // Profile already imported (duplicate hash)
                duplicated++;
            }
        }

        return Ok(new BulkProfileImportResultDto
        {
            PrinterId = printerId,
            PrinterName = printer.Name,
            TotalRequested = request.ProfileIds.Count,
            TotalFound = profilesToImport.Count,
            Imported = imported,
            Duplicated = duplicated
        });
    }
}

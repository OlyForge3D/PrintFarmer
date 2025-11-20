using System.Linq;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer/profiles")]
[Tags("Slicer Profiles")]
[Authorize] // All endpoints require authentication
public class ProfilesController(
    IUnifiedLoggingService logger,
    Farm.Web.Api.Services.Slicing.IProfilesService profilesService,
    ISlicerProfileRepository slicerProfileRepo,
    IPrintersRepository printersRepo) : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly Farm.Web.Api.Services.Slicing.IProfilesService _profilesService = profilesService;
    private readonly ISlicerProfileRepository _slicerProfileRepo = slicerProfileRepo;
    private readonly IPrintersRepository _printersRepo = printersRepo;

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
    public async Task<IActionResult> ListExtendedAsync(CancellationToken ct)
    {
        // Pull all profiles using repository
        var profiles = await _slicerProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
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
    public async Task<IActionResult> ListSystemOrcaProfilesAsync(CancellationToken ct)
    {
        var profiles = await _slicerProfileRepo.GetSystemOrcaProfilesAsync(ct);
        var dtos = profiles.Select(p => new SlicerProfileListItemDto
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
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Seed system OrcaSlicer profiles from the worker service into the database.
    /// This endpoint fetches profiles from the OrcaSlicer worker and imports them as system profiles (IsSystem=true).
    /// Use this to bootstrap the database with official OrcaSlicer profiles.
    /// 
    /// VERSION HANDLING:
    /// - Profiles are deduplicated by hash (Material:Quality:LayerHeight:Infill)
    /// - Version information is extracted from the OrcaSlicer worker service during seeding
    /// - Different OrcaSlicer versions will have different profiles with different characteristics
    /// - The OrcaSlicer worker returns profiles from its local installation, ensuring version-specific profiles
    /// - When OrcaSlicer is updated, re-run this endpoint to seed updated profiles
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Count of profiles imported and version information</returns>
    /// <response code="200">Profiles seeded successfully</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
    [HttpPost("system/orca/seed-from-worker")]
    [Authorize(Policy = "farm_admin")] // Admin-only: system profile seeding
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SeedSystemProfilesFromWorkerAsync(
        [FromServices] HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            // Call the OrcaSlicer worker /version endpoint to get the OrcaSlicer version
            var workerUrl = Environment.GetEnvironmentVariable("ORCASLICER_WORKER_URL") ?? "http://orcaslicer-worker:8080";

            // First, get the OrcaSlicer version from the worker
            string? orcaVersion = null;
            try
            {
                var versionResponse = await httpClient.GetAsync($"{workerUrl}/version", ct);
                if (versionResponse.IsSuccessStatusCode)
                {
                    var versionJson = await versionResponse.Content.ReadAsStringAsync(ct);
                    using var versionDoc = JsonDocument.Parse(versionJson);
                    if (versionDoc.RootElement.TryGetProperty("orcaslicerVersion", out var versionElem) && versionElem.ValueKind == JsonValueKind.String)
                    {
                        orcaVersion = versionElem.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to fetch OrcaSlicer version from worker: {ex.Message}");
                // Continue - version is optional
            }

            // Call the OrcaSlicer worker /profiles endpoint to get official profiles
            var response = await httpClient.GetAsync($"{workerUrl}/profiles", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"OrcaSlicer worker returned {response.StatusCode}");
                return StatusCode((int)response.StatusCode, "OrcaSlicer worker unavailable or returned an error");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var workerProfiles = JsonSerializer.Deserialize<List<SlicerProfileDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (workerProfiles == null || workerProfiles.Count == 0)
            {
                return Ok(new { imported = 0, skipped = 0, message = "No profiles available from worker" });
            }

            int imported = 0;
            int skipped = 0;

            // Import each profile as a system profile if it doesn't already exist
            foreach (var profile in workerProfiles)
            {
                // Generate a stable hash for deduplication (based on profile characteristics)
                // Different slicer versions will have different profiles, so they'll have different hashes
                var profileHash = $"{profile.Material}:{profile.Quality}:{profile.LayerHeight}:{profile.InfillPercentage}";

                // Check if profile already exists by hash using repository
                var existingProfile = await _slicerProfileRepo.GetByHashAsync(profileHash, ct);
                if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                {
                    skipped++;
                    continue;
                }

                // Create new system profile with OrcaSlicer version
                var systemProfile = new SlicerProfile
                {
                    Id = Guid.NewGuid(),
                    Name = $"{profile.Material} - {profile.Quality} ({profile.LayerHeight}mm)",
                    Description = $"Official OrcaSlicer system profile: {profile.Material} {profile.Quality} quality at {profile.LayerHeight}mm layer height",
                    SlicerType = SlicerType.OrcaSlicer,
                    Material = profile.Material ?? "PLA",
                    Quality = Enum.TryParse<ProfileQuality>(profile.Quality ?? "Standard", true, out var q) ? q : ProfileQuality.Standard,
                    LayerHeight = profile.LayerHeight,
                    InfillPercentage = profile.InfillPercentage,
                    PrintSpeed = profile.PrintSpeed,
                    NozzleTemperature = profile.NozzleTemperature,
                    BedTemperature = profile.BedTemperature,
                    EnableSupports = profile.Supports,
                    IsSystem = true,
                    IsPublic = true,
                    IsDefault = false,
                    Hash = profileHash,
                    SlicerVersion = orcaVersion,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _slicerProfileRepo.AddAsync(systemProfile, ct);
                imported++;
            }

            _logger.LogInformation($"Seeded {imported} system OrcaSlicer profiles from worker ({skipped} already existed). OrcaSlicer version: {orcaVersion ?? "unknown"}.");

            return Ok(new
            {
                imported,
                skipped,
                orcaslicerVersion = orcaVersion,
                message = $"Seeded {imported} system OrcaSlicer profiles from worker (OrcaSlicer v{orcaVersion ?? "unknown"})",
                details = "Profiles are version-specific based on the OrcaSlicer version in the worker. Different OrcaSlicer versions will have different profiles. Re-run this endpoint when upgrading OrcaSlicer. Query SlicerVersion field to find profiles for a specific slicer version."
            });
        }
        catch (HttpRequestException ex)
        {
            var workerUrl = Environment.GetEnvironmentVariable("ORCASLICER_WORKER_URL") ?? "http://orcaslicer-worker:8080";
            _logger.LogError($"Failed to connect to OrcaSlicer worker at {workerUrl}: {ex.Message}");
            return StatusCode(503, $"OrcaSlicer worker unavailable at {workerUrl}. Please ensure the worker service is running.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error seeding system profiles: {ex.Message}");
            return StatusCode(500, $"Error seeding profiles: {ex.Message}");
        }
    }

    /// <summary>
    /// Force reseed system OrcaSlicer profiles from the worker, clearing existing ones first.
    /// Use this if the initial seeding failed or to update profiles after an OrcaSlicer upgrade.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of profiles imported</returns>
    [HttpPost("system/orca/force-reseed-from-worker")]
    [Authorize(Policy = "farm_admin")] // Admin-only: system profile management
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ForceReseedSystemProfilesFromWorkerAsync(
        [FromServices] HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            // Delete all existing system OrcaSlicer profiles using repository
            int deletedCount = await _slicerProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);
            if (deletedCount > 0)
            {
                _logger.LogInformation($"Deleted {deletedCount} existing system OrcaSlicer profiles for force reseed");
            }

            // Call the OrcaSlicer worker /version endpoint to get the OrcaSlicer version
            var workerUrl = Environment.GetEnvironmentVariable("ORCASLICER_WORKER_URL") ?? "http://orcaslicer-worker:8080";

            // First, get the OrcaSlicer version from the worker
            string? orcaVersion = null;
            try
            {
                var versionResponse = await httpClient.GetAsync($"{workerUrl}/version", ct);
                if (versionResponse.IsSuccessStatusCode)
                {
                    var versionJson = await versionResponse.Content.ReadAsStringAsync(ct);
                    using var versionDoc = JsonDocument.Parse(versionJson);
                    if (versionDoc.RootElement.TryGetProperty("orcaslicerVersion", out var versionElem) && versionElem.ValueKind == JsonValueKind.String)
                    {
                        orcaVersion = versionElem.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to fetch OrcaSlicer version from worker: {ex.Message}");
                // Continue - version is optional
            }

            // Call the OrcaSlicer worker /profiles endpoint to get official profiles
            var response = await httpClient.GetAsync($"{workerUrl}/profiles", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"OrcaSlicer worker returned {response.StatusCode}");
                return StatusCode((int)response.StatusCode, "OrcaSlicer worker unavailable or returned an error");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var workerProfiles = JsonSerializer.Deserialize<List<SlicerProfileDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (workerProfiles == null || workerProfiles.Count == 0)
            {
                return Ok(new { imported = 0, deleted = deletedCount, message = "No profiles available from worker" });
            }

            int imported = 0;

            // Import each profile as a system profile
            foreach (var profile in workerProfiles)
            {
                // Generate a stable hash for deduplication
                var profileHash = $"{profile.Material}:{profile.Quality}:{profile.LayerHeight}:{profile.InfillPercentage}";

                // Create new system profile
                var systemProfile = new SlicerProfile
                {
                    Id = Guid.NewGuid(),
                    Name = $"{profile.Material} - {profile.Quality} ({profile.LayerHeight}mm)",
                    Description = $"Official OrcaSlicer system profile: {profile.Material} {profile.Quality} quality at {profile.LayerHeight}mm layer height",
                    SlicerType = SlicerType.OrcaSlicer,
                    Material = profile.Material ?? "PLA",
                    Quality = Enum.TryParse<ProfileQuality>(profile.Quality ?? "Standard", true, out var q) ? q : ProfileQuality.Standard,
                    LayerHeight = profile.LayerHeight,
                    InfillPercentage = profile.InfillPercentage,
                    PrintSpeed = profile.PrintSpeed,
                    NozzleTemperature = profile.NozzleTemperature,
                    BedTemperature = profile.BedTemperature,
                    EnableSupports = profile.Supports,
                    IsSystem = true,
                    IsPublic = true,
                    IsDefault = false,
                    Hash = profileHash,
                    SlicerVersion = orcaVersion,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _slicerProfileRepo.AddAsync(systemProfile, ct);
                imported++;
            }

            _logger.LogInformation($"Force-reseeded {imported} system OrcaSlicer profiles from worker (deleted {deletedCount} old ones). OrcaSlicer version: {orcaVersion ?? "unknown"}.");

            return Ok(new
            {
                imported,
                deleted = deletedCount,
                orcaslicerVersion = orcaVersion,
                message = $"Force-reseeded {imported} system OrcaSlicer profiles from worker (deleted {deletedCount} old ones)",
                details = "All existing system profiles were deleted and replaced with fresh ones from the worker."
            });
        }
        catch (HttpRequestException ex)
        {
            var workerUrl = Environment.GetEnvironmentVariable("ORCASLICER_WORKER_URL") ?? "http://orcaslicer-worker:8080";
            _logger.LogError($"Failed to connect to OrcaSlicer worker at {workerUrl}: {ex.Message}");
            return StatusCode(503, $"OrcaSlicer worker unavailable at {workerUrl}. Please ensure the worker service is running.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error force-reseeding system profiles: {ex.Message}");
            return StatusCode(500, $"Error reseeding profiles: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch available OrcaSlicer profiles from the OrcaSlicer worker service.
    /// Queries the running OrcaSlicer worker for profiles available in its local installation.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of available OrcaSlicer profiles from worker</returns>
    /// <response code="200">Returns list of profiles from OrcaSlicer worker</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
    [HttpGet("available-from-worker")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAvailableProfilesFromWorkerAsync(
        [FromServices] HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            // Call the OrcaSlicer worker /profiles endpoint
            // Worker is available at http://orcaslicer-worker:8080 (in Docker) or http://localhost:8080 (locally)
            var workerUrl = Environment.GetEnvironmentVariable("ORCASLICER_WORKER_URL") ?? "http://orcaslicer-worker:8080";
            var response = await httpClient.GetAsync($"{workerUrl}/profiles", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"OrcaSlicer worker returned {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
                return StatusCode((int)response.StatusCode, "OrcaSlicer worker unavailable or returned an error");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var profiles = JsonSerializer.Deserialize<List<SlicerProfileDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return Ok(profiles ?? new List<SlicerProfileDto>());
        }
        catch (HttpRequestException ex)
        {
            var workerUrl = Environment.GetEnvironmentVariable("ORCASLICER_WORKER_URL") ?? "http://orcaslicer-worker:8080";
            _logger.LogError($"Failed to connect to OrcaSlicer worker at {workerUrl}: {ex.Message}");
            return StatusCode(503, $"OrcaSlicer worker unavailable at {workerUrl}. Please ensure the worker service is running.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching profiles from OrcaSlicer worker: {ex.Message}");
            return StatusCode(500, $"Error fetching profiles from worker: {ex.Message}");
        }
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
        CancellationToken ct)
    {
        // Verify printer exists using repository
        var printer = await _printersRepo.FindByIdAsync(printerId, ct);
        if (printer is null)
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

        // Get all system OrcaSlicer profiles using repository
        var profiles = await _slicerProfileRepo.GetSystemOrcaProfilesAsync(ct);

        // Convert to DTOs
        var dtos = profiles.Select(p => new SlicerProfileListItemDto
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
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Bulk import system profiles for a specific registered printer.
    /// Only OrcaSlicer system profiles can be bulk imported.
    /// </summary>
    /// <param name="printerId">The ID of the registered printer</param>
    /// <param name="request">Bulk import request containing profile IDs and options</param>
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
        CancellationToken ct)
    {
        if (request is null || request.ProfileIds == null || request.ProfileIds.Count == 0)
        {
            return BadRequest("profileIds list is required and must not be empty");
        }

        // Verify printer exists using repository
        var printer = await _printersRepo.FindByIdAsync(printerId, ct);
        if (printer is null)
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

        // Get the profiles to import - fetch system profiles by ID
        var allSystemProfiles = await _slicerProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
        var profilesToImport = allSystemProfiles
            .Where(p => p.IsSystem && request.ProfileIds.Contains(p.Id))
            .ToList();

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

                await _slicerProfileRepo.AddOrUpdateFromImportAsync(userProfile, allowSystemOverride: false, ct);
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

    /// <summary>
    /// Bulk import profiles directly from the OrcaSlicer worker (without pre-seeding to database).
    /// This is the primary workflow: fetch profiles from worker, user selects which ones to import, then import directly.
    /// Profiles are created as user-owned (IsSystem=false) in the database.
    /// </summary>
    /// <param name="printerId">The ID of the registered printer</param>
    /// <param name="request">Request containing profiles from the worker and import options</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Import result with counts</returns>
    [HttpPost("bulk-import-from-worker/{printerId}")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
    [ProducesResponseType(typeof(BulkImportFromWorkerResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkImportFromWorkerAsync(
        Guid printerId,
        [FromBody] BulkImportFromWorkerRequest? request,
        CancellationToken ct)
    {
        if (request is null || request.Profiles == null || request.Profiles.Count == 0)
        {
            return BadRequest("profiles list is required and must not be empty");
        }

        // Verify printer exists using repository
        var printer = await _printersRepo.FindByIdAsync(printerId, ct);
        if (printer is null)
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

        int imported = 0;
        int duplicated = 0;

        // Import each profile from the worker
        foreach (var workerProfile in request.Profiles)
        {
            try
            {
                // Generate a hash for deduplication
                var profileHash = $"{workerProfile.Material}:{workerProfile.Quality}:{workerProfile.LayerHeight}:{workerProfile.InfillPercentage}";

                // Check if this profile already exists (by hash) using repository
                var existingProfile = await _slicerProfileRepo.GetByHashAsync(profileHash, ct);
                if (existingProfile != null && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                {
                    duplicated++;
                    continue;
                }

                // Create a user-owned profile from the worker data
                var userProfile = new SlicerProfile
                {
                    Id = Guid.NewGuid(),
                    Name = $"{workerProfile.Material} - {workerProfile.Quality} ({workerProfile.LayerHeight}mm)",
                    Description = $"Official OrcaSlicer profile imported for {printer.Name}",
                    SlicerType = SlicerType.OrcaSlicer,
                    LayerHeight = workerProfile.LayerHeight,
                    InfillPercentage = workerProfile.InfillPercentage,
                    PrintSpeed = workerProfile.PrintSpeed,
                    NozzleTemperature = workerProfile.NozzleTemperature,
                    BedTemperature = workerProfile.BedTemperature,
                    EnableSupports = workerProfile.Supports,
                    Material = workerProfile.Material ?? "PLA",
                    Quality = Enum.TryParse<ProfileQuality>(workerProfile.Quality ?? "Standard", true, out var q) ? q : ProfileQuality.Standard,
                    IsSystem = false,
                    IsDefault = false,
                    IsPublic = request.MakePublic ?? false,
                    Hash = profileHash,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _slicerProfileRepo.AddOrUpdateFromImportAsync(userProfile, allowSystemOverride: false, ct);
                imported++;
            }
            catch (Exception ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException ||
                                      ex.InnerException?.Message?.Contains("UNIQUE constraint failed") == true)
            {
                // Profile already imported (duplicate hash)
                duplicated++;
            }
        }

        return Ok(new BulkImportFromWorkerResultDto
        {
            PrinterId = printerId,
            PrinterName = printer.Name,
            Imported = imported,
            Duplicated = duplicated
        });
    }
}

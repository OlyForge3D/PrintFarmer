using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Repositories.Workers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Slicing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer/profiles")]
[Tags("Slicer Profiles")]
[Authorize] // All endpoints require authentication
public class ProfilesController(
    IUnifiedLoggingService logger,
    IProfilesService profilesService,
    IProcessProfileRepository processProfileRepo,
    IMachineProfileRepository machineProfileRepo,
    IFilamentProfileRepository filamentProfileRepo,
    IUnitOfWork unitOfWork,
    IWorkerRepository workerRepository) : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IProfilesService _profilesService = profilesService;
    private readonly IProcessProfileRepository _processProfileRepo = processProfileRepo;
    private readonly IMachineProfileRepository _machineProfileRepo = machineProfileRepo;
    private readonly IFilamentProfileRepository _filamentProfileRepo = filamentProfileRepo;
    private readonly IUnitOfWork _unitOfWork = unitOfWork;
    private readonly IWorkerRepository _workerRepository = workerRepository;

    [HttpPost("import")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
    [ProducesResponseType(typeof(ProcessProfileExtendedDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProcessProfileExtendedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportProfileAsync(
        [FromBody] ImportProcessProfileDto? request,
        [FromServices] IProfileParsingService parsingService,
        [FromServices] IProcessProfileRepository repo,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RawJson))
        {
            return BadRequest("rawJson is required");
        }
        if (string.IsNullOrWhiteSpace(request.SlicerType) || !Enum.TryParse(request.SlicerType, true, out SlicerType slicerType))
        {
            return BadRequest("Invalid slicerType");
        }
        try
        {
            (string? sanitizedRaw, string? metadataJson, string? hash) = parsingService.ParseAndPrepare(request.RawJson);
            // Attempt to derive basic fields from metadata
            double layerHeight = 0.2;
            int infillPct = 20;
            string material = "PLA";
            string quality = "Standard";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(metadataJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("layerHeight", out JsonElement lh) && lh.TryGetDouble(out double lhVal))
                {
                    layerHeight = lhVal;
                }

                if (root.TryGetProperty("infillPercentage", out JsonElement inf) && inf.TryGetInt32(out int infVal))
                {
                    infillPct = infVal;
                }

                if (root.TryGetProperty("filamentMaterial", out JsonElement mat) && mat.ValueKind == JsonValueKind.String)
                {
                    material = mat.GetString() ?? material;
                }

                if (root.TryGetProperty("profileType", out JsonElement qt) && qt.ValueKind == JsonValueKind.String)
                {
                    quality = qt.GetString() ?? quality;
                }
            }
            catch { /* fallback to defaults */ }
            string name = request.Name?.Trim() ?? $"{quality} {layerHeight:0.##}mm";
            ProcessProfile imported = new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = request.Description,
                SlicerType = slicerType,
                LayerHeight = layerHeight,
                InfillPercentage = infillPct,
                Quality = Enum.TryParse(quality, true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                RawJson = sanitizedRaw,
                MetadataJson = metadataJson,
                Hash = hash,
                IsPublic = request.IsPublic,
                IsDefault = request.SetDefault,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            bool created = false;
            ProcessProfile result = await repo.AddOrUpdateFromImportAsync(imported, request.AllowSystemOverride, ct);
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
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
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
            ProcessProfileExtendedDto dto = new()
            {
                Id = result.Id,
                Name = result.Name,
                Description = result.Description,
                SlicerType = result.SlicerType.ToString(),
                LayerHeight = result.LayerHeight,
                InfillPercentage = result.InfillPercentage,
                PrintSpeed = result.PrintSpeed,
                EnableSupports = result.EnableSupports,
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
    [ProducesResponseType(typeof(ProcessProfileExportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportProfileAsync(Guid id, [FromServices] IProcessProfileRepository repo, CancellationToken ct)
    {
        ProcessProfile? profile = await repo.GetByIdAsync(id, ct);
        if (profile is null)
        {
            return NotFound();
        }

        Dictionary<string, object?> metadataDict = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(profile.MetadataJson ?? "{}");
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
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
        ProcessProfileExportDto dto = new()
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
    public async Task<IActionResult> SetDefaultProfileAsync(Guid id, [FromServices] IProcessProfileRepository repo, CancellationToken ct)
    {
        ProcessProfile? profile = await repo.GetByIdAsync(id, ct);
        if (profile is null)
        {
            return NotFound();
        }

        await repo.SetDefaultAsync(profile, profile.CreatedByUserId, ct);
        return NoContent();
    }

    // Extended listing of all profile types (process, filament, machine) - user + public + system
    [HttpGet("extended")]
    [ProducesResponseType(typeof(ExtendedProfilesResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListExtendedAsync(CancellationToken ct)
    {
        List<ProcessProfileListItemDto> processProfiles = new List<ProcessProfileListItemDto>();
        List<FilamentProfileListItemDto> filamentProfiles = new List<FilamentProfileListItemDto>();
        List<MachineProfileListItemDto> machineProfiles = new List<MachineProfileListItemDto>();

        // Get process profiles
        IReadOnlyList<ProcessProfile> processProfileEntities = await _processProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
        foreach (ProcessProfile p in processProfileEntities)
        {
            processProfiles.Add(new ProcessProfileListItemDto
            {
                Id = p.Id,
                Name = p.Name,
                SlicerType = p.SlicerType.ToString(),
                Quality = p.Quality.ToString(),
                LayerHeight = p.LayerHeight,
                InfillPercentage = p.InfillPercentage,
                IsDefault = p.IsDefault,
                IsSystem = p.IsSystem,
                IsPublic = p.IsPublic,
                Hash = p.Hash ?? string.Empty
            });
        }

        // Get filament profiles
        IReadOnlyList<FilamentProfile> filamentProfileEntities = await _filamentProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
        foreach (FilamentProfile p in filamentProfileEntities)
        {
            filamentProfiles.Add(new FilamentProfileListItemDto
            {
                Id = p.Id,
                Name = p.Name,
                SlicerType = p.SlicerType.ToString(),
                Material = p.Material ?? string.Empty,
                NozzleTemperature = p.NozzleTemperature,
                BedTemperature = p.BedTemperature,
                PrintSpeed = p.PrintSpeed,
                IsDefault = p.IsDefault,
                IsSystem = p.IsSystem,
                IsPublic = p.IsPublic,
                Hash = p.Hash ?? string.Empty
            });
        }

        // Get machine profiles
        IReadOnlyList<MachineProfile> machineProfileEntities = await _machineProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
        foreach (MachineProfile p in machineProfileEntities)
        {
            machineProfiles.Add(new MachineProfileListItemDto
            {
                Id = p.Id,
                Name = p.Name,
                SlicerType = p.SlicerType.ToString(),
                Manufacturer = p.Manufacturer ?? string.Empty,
                IsDefault = p.IsDefault,
                IsSystem = p.IsSystem,
                IsPublic = p.IsPublic,
                Hash = p.Hash ?? string.Empty
            });
        }

        ExtendedProfilesResponseDto response = new ExtendedProfilesResponseDto
        {
            ProcessProfiles = processProfiles,
            FilamentProfiles = filamentProfiles,
            MachineProfiles = machineProfiles
        };

        return Ok(response);
    }

    [HttpPost]
    [Authorize(Policy = "farm_admin")] // Admin-only: create profile
    [ProducesResponseType(typeof(ProcessProfileResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateProfileAsync([FromBody] CreateProcessProfileDto? request)
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
            if (string.IsNullOrWhiteSpace(request.SlicerType) || !Enum.TryParse(request.SlicerType, true, out SlicerType slicerType))
            {
                return BadRequest("Invalid slicer type");
            }
            ProfileQuality quality = ProfileQuality.Standard;
            if (!string.IsNullOrWhiteSpace(request.Quality) && !Enum.TryParse(request.Quality, true, out quality))
            {
                return BadRequest("Invalid quality setting");
            }
            // Map to service request and delegate creation
            CreateProcessProfileDto createReq = new CreateProcessProfileDto
            {
                Name = request.Name,
                Description = request.Description,
                SlicerType = request.SlicerType,
                LayerHeight = request.LayerHeight,
                InfillPercentage = request.InfillPercentage,
                PrintSpeed = request.PrintSpeed,
                EnableSupports = request.EnableSupports,
                Material = request.Material,
                Quality = request.Quality,
                IsDefault = request.IsDefault,
                IsPublic = request.IsPublic,
                AdvancedSettings = request.AdvancedSettings
            };

            ProcessProfileResponseDto created = await _profilesService.CreateProfileAsync(createReq, CancellationToken.None);
            return Created($"/api/slicer/profiles/{created.Id}", created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to create profile: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to create profile");
        }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProcessProfileResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfileAsync(Guid id)
    {
        ProcessProfileResponseDto? profile = await _profilesService.GetProfileAsync(id, CancellationToken.None);
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
                    name = $"Default {d.ProcessProfile?.Quality}",
                    slicerType = "PrusaSlicer",
                    layerHeight = d.ProcessProfile?.LayerHeight ?? 0.2,
                    infillPercentage = d.ProcessProfile?.InfillPercentage ?? 20,
                    printSpeed = d.FilamentProfile?.PrintSpeed ?? d.ProcessProfile?.PrintSpeed ?? 50,
                    nozzleTemperature = d.FilamentProfile?.NozzleTemperature ?? 210,
                    bedTemperature = d.FilamentProfile?.BedTemperature ?? 60,
                    supports = d.ProcessProfile?.Supports ?? false,
                    material = d.FilamentProfile?.Material ?? "Unknown",
                    quality = d.ProcessProfile?.Quality ?? "standard"
                }));
            }

            IReadOnlyList<SlicerProfileDto> list = await _profilesService.GetProfilesAsync(CancellationToken.None);
            // Map composite SlicerProfileDto to lightweight view for the client
            IEnumerable<object> mapped = list.Select(p => (object)new
            {
                layerHeight = p.ProcessProfile?.LayerHeight ?? 0.2,
                infillPercentage = p.ProcessProfile?.InfillPercentage ?? 20,
                printSpeed = p.FilamentProfile?.PrintSpeed ?? p.ProcessProfile?.PrintSpeed ?? 50,
                nozzleTemperature = p.FilamentProfile?.NozzleTemperature ?? 210,
                bedTemperature = p.FilamentProfile?.BedTemperature ?? 60,
                supports = p.ProcessProfile?.Supports ?? false,
                material = p.FilamentProfile?.Material ?? "Unknown",
                quality = p.ProcessProfile?.Quality ?? "standard"
            });

            List<object> final = mapped.ToList();
            if (final.Count == 0)
            {
                return Ok(DefaultProfiles().Select(d => (object)new
                {
                    name = $"Default {d.ProcessProfile?.Quality}",
                    slicerType = "PrusaSlicer",
                    layerHeight = d.ProcessProfile?.LayerHeight ?? 0.2,
                    infillPercentage = d.ProcessProfile?.InfillPercentage ?? 20,
                    printSpeed = d.FilamentProfile?.PrintSpeed ?? d.ProcessProfile?.PrintSpeed ?? 50,
                    nozzleTemperature = d.FilamentProfile?.NozzleTemperature ?? 210,
                    bedTemperature = d.FilamentProfile?.BedTemperature ?? 60,
                    supports = d.ProcessProfile?.Supports ?? false,
                    material = d.FilamentProfile?.Material ?? "Unknown",
                    quality = d.ProcessProfile?.Quality ?? "standard"
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
        IReadOnlyList<ProcessProfile> profiles = await _processProfileRepo.GetSystemOrcaProfilesAsync(ct);
        List<SlicerProfileListItemDto> dtos = profiles.Select(p => new SlicerProfileListItemDto
        {
            Id = p.Id,
            Name = p.Name,
            SlicerType = p.SlicerType.ToString(),
            Quality = p.Quality.ToString()
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
            // Get OrcaSlicer worker URL from database registry
            string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
            if (string.IsNullOrEmpty(workerUrl))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker not found in registry");
            }

            // First, get the OrcaSlicer version from the worker
            string? orcaVersion = null;
            try
            {
                HttpResponseMessage versionResponse = await httpClient.GetAsync($"{workerUrl}/version", ct);
                if (versionResponse.IsSuccessStatusCode)
                {
                    string versionJson = await versionResponse.Content.ReadAsStringAsync(ct);
                    using JsonDocument versionDoc = JsonDocument.Parse(versionJson);
                    if (versionDoc.RootElement.TryGetProperty("orcaslicerVersion", out JsonElement versionElem) && versionElem.ValueKind == JsonValueKind.String)
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
            HttpResponseMessage response = await httpClient.GetAsync($"{workerUrl}/profiles", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"OrcaSlicer worker returned {response.StatusCode}");
                return StatusCode((int)response.StatusCode, "OrcaSlicer worker unavailable or returned an error");
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation($"Raw OrcaSlicer worker /profiles response: {json[..Math.Min(1000, json.Length)]}");

            // Deserialize the new AllProfilesResponseDto with three profile types grouped by manufacturer
            AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // Flatten the grouped profiles for importing
            int totalProcessCount = allProfiles?.ProcessProfiles?.Values.Sum(list => list.Count) ?? 0;
            int totalFilamentCount = allProfiles?.FilamentProfiles?.Values.Sum(list => list.Count) ?? 0;
            int totalMachineCount = allProfiles?.MachineProfiles?.Values.Sum(list => list.Count) ?? 0;

            _logger.LogInformation($"Deserialized {totalProcessCount} process + {totalFilamentCount} filament + {totalMachineCount} machine profiles from worker");

            if (allProfiles == null || (totalProcessCount == 0 && totalFilamentCount == 0 && totalMachineCount == 0))
            {
                return Ok(new { imported = 0, skipped = 0, message = "No profiles available from worker" });
            }

            int imported = 0;
            int skipped = 0;

            // Flatten all profiles from the grouped dictionaries
            List<ProcessProfileDto> processProfiles = (allProfiles.ProcessProfiles?.SelectMany(kvp => kvp.Value) ?? Enumerable.Empty<ProcessProfileDto>()).ToList();
            List<FilamentProfileDto> filamentProfiles = (allProfiles.FilamentProfiles?.SelectMany(kvp => kvp.Value) ?? Enumerable.Empty<FilamentProfileDto>()).ToList();
            List<MachineProfileDto> machineProfiles = (allProfiles.MachineProfiles?.SelectMany(kvp => kvp.Value) ?? Enumerable.Empty<MachineProfileDto>()).ToList();

            _logger.LogInformation($"Seeding {processProfiles.Count} process, {filamentProfiles.Count} filament, {machineProfiles.Count} machine profiles");

            // Import process profiles
            foreach (ProcessProfileDto profile in processProfiles)
            {
                try
                {
                    string profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                    string profileHash = ComputeSha256Hash(profileJson);

                    ProcessProfile? existingProfile = await _processProfileRepo.GetByHashAsync(profileHash, ct);
                    if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                    {
                        skipped++;
                        continue;
                    }

                    ProcessProfile systemProfile = new ProcessProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = string.IsNullOrEmpty(profile.Name) ? $"{profile.Quality} ({profile.LayerHeight}mm)" : profile.Name,
                        Description = $"OrcaSlicer process profile: {profile.Quality} quality at {profile.LayerHeight}mm layer height",
                        SlicerType = SlicerType.OrcaSlicer,
                        Quality = Enum.TryParse(profile.Quality ?? "standard", true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                        LayerHeight = profile.LayerHeight,
                        InfillPercentage = profile.InfillPercentage,
                        PrintSpeed = profile.PrintSpeed,
                        EnableSupports = profile.Supports,
                        IsSystem = true,
                        IsPublic = true,
                        IsDefault = false,
                        Hash = profileHash,
                        RawJson = profileJson,
                        SlicerVersion = orcaVersion,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _processProfileRepo.AddAsync(systemProfile, ct);
                    imported++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to import process profile: {ex.Message}");
                    skipped++;
                }
            }

            // Import filament profiles
            int filamentImported = 0;
            foreach (FilamentProfileDto profile in filamentProfiles)
            {
                try
                {
                    string profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                    string profileHash = ComputeSha256Hash(profileJson);

                    FilamentProfile? existingProfile = await _filamentProfileRepo.GetByHashAsync(profileHash, ct);
                    if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                    {
                        skipped++;
                        continue;
                    }

                    FilamentProfile systemProfile = new FilamentProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = profile.Name ?? $"{profile.Material}",
                        Material = profile.Material ?? "PLA",
                        Manufacturer = profile.Manufacturer,
                        Description = $"OrcaSlicer filament profile for {profile.Material}",
                        SlicerType = SlicerType.OrcaSlicer,
                        PrintSpeed = profile.PrintSpeed,
                        IsSystem = true,
                        Hash = profileHash,
                        RawJson = profileJson,
                        SlicerVersion = orcaVersion,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _filamentProfileRepo.AddAsync(systemProfile, ct);
                    filamentImported++;
                    imported++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to import filament profile: {ex.Message}");
                    skipped++;
                }
            }

            // Import machine profiles
            int machineImported = 0;
            foreach (MachineProfileDto profile in machineProfiles)
            {
                try
                {
                    string profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                    string profileHash = ComputeSha256Hash(profileJson);

                    MachineProfile? existingProfile = await _machineProfileRepo.GetByHashAsync(profileHash, ct);
                    if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                    {
                        skipped++;
                        continue;
                    }

                    MachineProfile systemProfile = new MachineProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = profile.Name ?? string.Empty,
                        Manufacturer = profile.Manufacturer ?? string.Empty,
                        Description = $"OrcaSlicer machine profile",
                        SlicerType = SlicerType.OrcaSlicer,
                        IsSystem = true,
                        Hash = profileHash,
                        RawJson = profileJson,
                        SettingsJson = profileJson,
                        SlicerVersion = orcaVersion,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _machineProfileRepo.AddAsync(systemProfile, ct);
                    machineImported++;
                    imported++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to import machine profile: {ex.Message}");
                    skipped++;
                }
            }

            _logger.LogInformation($"Seeded {imported} OrcaSlicer profiles ({processProfiles.Count} process, {filamentImported} filament, {machineImported} machine). Skipped: {skipped}. OrcaSlicer v{orcaVersion ?? "unknown"}");

            return Ok(new
            {
                imported,
                skipped,
                processProfiles = processProfiles.Count,
                filamentProfiles = filamentImported,
                machineProfiles = machineImported,
                orcaslicerVersion = orcaVersion,
                message = $"Seeded {imported} system OrcaSlicer profiles from worker (OrcaSlicer v{orcaVersion ?? "unknown"})"
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"Failed to connect to OrcaSlicer worker: {ex.Message}");
            return StatusCode(503, $"OrcaSlicer worker unavailable. Please ensure the worker service is running and registered.");
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
            // Delete all existing system OrcaSlicer profiles from all three tables
            int deletedProcessCount = await _processProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);
            int deletedFilamentCount = await _filamentProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);
            int deletedMachineCount = await _machineProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);
            int deletedCount = deletedProcessCount + deletedFilamentCount + deletedMachineCount;

            if (deletedCount > 0)
            {
                _logger.LogInformation($"Deleted {deletedCount} existing system OrcaSlicer profiles ({deletedProcessCount} process, {deletedFilamentCount} filament, {deletedMachineCount} machine) for force reseed");
            }

            // Get all printers in the system and extract unique nozzle sizes
            List<Printer> allPrinters = await _unitOfWork.Printers.GetAllAsync(ct);
            HashSet<double> systemNozzleSizes = allPrinters
                .Where(p => p.Model?.DefaultNozzleDiameter.HasValue ?? false)
                .Select(p => p.Model!.DefaultNozzleDiameter!.Value)
                .Distinct()
                .ToHashSet();

            _logger.LogInformation($"System has {allPrinters.Count} printers with {systemNozzleSizes.Count} unique nozzle sizes: {string.Join(", ", systemNozzleSizes.OrderBy(x => x).Select(x => $"{x}mm"))}");

            // Get OrcaSlicer worker URL from database registry
            string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
            if (string.IsNullOrEmpty(workerUrl))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker not found in registry");
            }

            // First, get the OrcaSlicer version from the worker
            string? orcaVersion = null;
            try
            {
                HttpResponseMessage versionResponse = await httpClient.GetAsync($"{workerUrl}/version", ct);
                if (versionResponse.IsSuccessStatusCode)
                {
                    string versionJson = await versionResponse.Content.ReadAsStringAsync(ct);
                    using JsonDocument versionDoc = JsonDocument.Parse(versionJson);
                    if (versionDoc.RootElement.TryGetProperty("orcaslicerVersion", out JsonElement versionElem) && versionElem.ValueKind == JsonValueKind.String)
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
            HttpResponseMessage response = await httpClient.GetAsync($"{workerUrl}/profiles", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"OrcaSlicer worker returned {response.StatusCode} from {workerUrl}/profiles");
                string errorContent = await response.Content.ReadAsStringAsync(ct);
                return StatusCode((int)response.StatusCode, $"OrcaSlicer worker unavailable or returned an error: {errorContent}");
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation($"Raw OrcaSlicer worker /profiles response (force-reseed): {json[..Math.Min(1000, json.Length)]}");

            // Deserialize the new AllProfilesResponseDto with three profile types
            AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _logger.LogInformation($"Deserialized {allProfiles?.ProcessProfiles?.Count ?? 0} process + {allProfiles?.FilamentProfiles?.Count ?? 0} filament + {allProfiles?.MachineProfiles?.Count ?? 0} machine profiles from worker response (force-reseed)");

            if (allProfiles == null || (allProfiles.ProcessProfiles?.Count == 0 && allProfiles.FilamentProfiles?.Count == 0 && allProfiles.MachineProfiles?.Count == 0))
            {
                _logger.LogInformation($"No profiles available from OrcaSlicer worker at {workerUrl}. Check if OrcaSlicer is configured with profiles in ~/.config/OrcaSlicer/profiles/");
                return Ok(new { imported = 0, deleted = deletedCount, message = "No profiles available from worker - check if OrcaSlicer is installed and configured on the worker", orcaslicerVersion = orcaVersion, systemPrinters = allPrinters.Count, systemNozzleSizes = systemNozzleSizes.Count });
            }

            int imported = 0;
            int skipped = 0;

            // Import machine profiles - only those matching system nozzle sizes
            List<MachineProfileDto> flattenedMachineProfiles = allProfiles.MachineProfiles?.SelectMany(kvp => kvp.Value).ToList() ?? new List<MachineProfileDto>();
            _logger.LogInformation($"Force-reseeding machine profiles: checking {flattenedMachineProfiles.Count} profiles against {systemNozzleSizes.Count} system nozzle sizes");
            int machineImported = 0;
            HashSet<string> importedMachineNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (MachineProfileDto? profile in flattenedMachineProfiles)
            {
                try
                {
                    // Skip if nozzle diameter not found or doesn't match system nozzles
                    if (!profile.NozzleDiameter.HasValue || !systemNozzleSizes.Contains(profile.NozzleDiameter.Value))
                    {
                        _logger.LogDebug($"Skipping machine profile '{profile.Name}' - nozzle diameter {profile.NozzleDiameter}mm not in system ({string.Join(", ", systemNozzleSizes.OrderBy(x => x).Select(x => $"{x}mm"))})");
                        skipped++;
                        continue;
                    }

                    string profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                    string profileHash = ComputeSha256Hash(profileJson);

                    MachineProfile systemProfile = new MachineProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = profile.Name ?? string.Empty,
                        Manufacturer = profile.Manufacturer ?? string.Empty,
                        Description = $"OrcaSlicer machine profile",
                        SlicerType = SlicerType.OrcaSlicer,
                        IsSystem = true,
                        Hash = profileHash,
                        RawJson = profileJson,
                        SettingsJson = profileJson,
                        SlicerVersion = orcaVersion,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _machineProfileRepo.AddAsync(systemProfile, ct);
                    machineImported++;
                    imported++;
                    _ = importedMachineNames.Add(profile.Name ?? string.Empty);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to import machine profile '{profile.Name}': {ex.Message}");
                    skipped++;
                }
            }

            // Import all process profiles (they're not machine-specific in OrcaSlicer)
            List<ProcessProfileDto> flattenedProcessProfiles = allProfiles.ProcessProfiles?.SelectMany(kvp => kvp.Value).ToList() ?? new List<ProcessProfileDto>();
            _logger.LogInformation($"Force-reseeding {flattenedProcessProfiles.Count} process profiles");
            int processImported = 0;
            foreach (ProcessProfileDto? profile in flattenedProcessProfiles)
            {
                try
                {
                    string profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                    string profileHash = ComputeSha256Hash(profileJson);

                    ProcessProfile systemProfile = new ProcessProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = string.IsNullOrEmpty(profile.Name) ? $"{profile.Quality} ({profile.LayerHeight}mm)" : profile.Name,
                        Description = $"OrcaSlicer process profile: {profile.Quality} quality at {profile.LayerHeight}mm layer height",
                        SlicerType = SlicerType.OrcaSlicer,
                        Quality = Enum.TryParse(profile.Quality ?? "standard", true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                        LayerHeight = profile.LayerHeight,
                        InfillPercentage = profile.InfillPercentage,
                        PrintSpeed = profile.PrintSpeed,
                        EnableSupports = profile.Supports,
                        IsSystem = true,
                        IsPublic = true,
                        IsDefault = false,
                        Hash = profileHash,
                        RawJson = profileJson,
                        SlicerVersion = orcaVersion,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _processProfileRepo.AddAsync(systemProfile, ct);
                    processImported++;
                    imported++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to import process profile '{profile.Name}': {ex.Message}");
                    skipped++;
                }
            }

            // Import all filament profiles (they're not machine-specific in OrcaSlicer)
            List<FilamentProfileDto> flattenedFilamentProfiles = allProfiles.FilamentProfiles?.SelectMany(kvp => kvp.Value).ToList() ?? new List<FilamentProfileDto>();
            _logger.LogInformation($"Force-reseeding {flattenedFilamentProfiles.Count} filament profiles");
            int filamentImported = 0;
            foreach (FilamentProfileDto? profile in flattenedFilamentProfiles)
            {
                try
                {
                    string profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                    string profileHash = ComputeSha256Hash(profileJson);

                    FilamentProfile systemProfile = new FilamentProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = profile.Name ?? $"{profile.Material}",
                        Material = profile.Material ?? "PLA",
                        Manufacturer = profile.Manufacturer,
                        Description = $"OrcaSlicer filament profile for {profile.Material}",
                        SlicerType = SlicerType.OrcaSlicer,
                        PrintSpeed = profile.PrintSpeed,
                        IsSystem = true,
                        Hash = profileHash,
                        RawJson = profileJson,
                        SlicerVersion = orcaVersion,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _filamentProfileRepo.AddAsync(systemProfile, ct);
                    filamentImported++;
                    imported++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to import filament profile '{profile.Name}': {ex.Message}");
                    skipped++;
                }
            }

            _logger.LogInformation($"Force-reseeded {imported} OrcaSlicer profiles: {machineImported} machine (matching {systemNozzleSizes.Count} system nozzle sizes), {processImported} process, {filamentImported} filament. Deleted {deletedCount} old profiles. Skipped {skipped}. OrcaSlicer v{orcaVersion ?? "unknown"}");

            return Ok(new
            {
                imported,
                deleted = deletedCount,
                skipped,
                processProfiles = processImported,
                filamentProfiles = filamentImported,
                machineProfiles = machineImported,
                orcaslicerVersion = orcaVersion,
                systemPrinters = allPrinters.Count,
                systemNozzleSizes = systemNozzleSizes.Count,
                message = $"Force-reseeded {imported} system OrcaSlicer profiles from worker (deleted {deletedCount} old, skipped {skipped} - only imported machines matching {systemNozzleSizes.Count} system nozzle sizes)"
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"Failed to connect to OrcaSlicer worker: {ex.Message}");
            return StatusCode(503, $"OrcaSlicer worker unavailable. Please ensure the worker service is running and registered.");
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
            // Get OrcaSlicer worker URL from database registry
            string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
            if (string.IsNullOrEmpty(workerUrl))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker not found in registry");
            }
            HttpResponseMessage response = await httpClient.GetAsync($"{workerUrl}/profiles", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"OrcaSlicer worker returned {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
                return StatusCode((int)response.StatusCode, "OrcaSlicer worker unavailable or returned an error");
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            // Deserialize the new AllProfilesResponseDto with three profile types
            AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // Return only process profiles for backward compatibility - flatten from grouped dictionary
            List<ProcessProfileDto> flattenedProcessProfiles = allProfiles?.ProcessProfiles?.SelectMany(kvp => kvp.Value).ToList() ?? new List<ProcessProfileDto>();
            return Ok(flattenedProcessProfiles);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"Failed to connect to OrcaSlicer worker: {ex.Message}");
            return StatusCode(503, $"OrcaSlicer worker unavailable. Please ensure the worker service is running and registered.");
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
        Printer? printer = await _unitOfWork.Printers.FindByIdAsync(printerId, ct);
        if (printer is null)
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

        // Get all system OrcaSlicer profiles using repository
        IReadOnlyList<ProcessProfile> profiles = await _processProfileRepo.GetSystemOrcaProfilesAsync(ct);

        // Convert to DTOs
        List<SlicerProfileListItemDto> dtos = profiles.Select(p => new SlicerProfileListItemDto
        {
            Id = p.Id,
            Name = p.Name,
            SlicerType = p.SlicerType.ToString(),
            Quality = p.Quality.ToString()
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
        Printer? printer = await _unitOfWork.Printers.FindByIdAsync(printerId, ct);
        if (printer is null)
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

        // Get the profiles to import - fetch system profiles by ID
        IReadOnlyList<ProcessProfile> allSystemProfiles = await _processProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
        List<ProcessProfile> profilesToImport = allSystemProfiles
            .Where(p => p.IsSystem && request.ProfileIds.Contains(p.Id))
            .ToList();

        if (profilesToImport.Count == 0)
        {
            return BadRequest("No valid system profiles found for import");
        }

        // Import each profile (skipping duplicates)
        int imported = 0;
        int duplicated = 0;

        foreach (ProcessProfile systemProfile in profilesToImport)
        {
            try
            {
                // Create a user-owned copy of the system profile
                ProcessProfile userProfile = new ProcessProfile
                {
                    Id = Guid.NewGuid(),
                    Name = systemProfile.Name,
                    Description = $"Imported from system profile for {printer.Name}",
                    SlicerType = systemProfile.SlicerType,
                    LayerHeight = systemProfile.LayerHeight,
                    InfillPercentage = systemProfile.InfillPercentage,
                    Quality = systemProfile.Quality,
                    PrintSpeed = systemProfile.PrintSpeed,
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

                _ = await _processProfileRepo.AddOrUpdateFromImportAsync(userProfile, allowSystemOverride: false, ct);
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
        Printer? printer = await _unitOfWork.Printers.FindByIdAsync(printerId, ct);
        if (printer is null)
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

        int imported = 0;
        int duplicated = 0;

        // Import each profile from the worker
        foreach (SlicerProfileDto workerProfile in request.Profiles)
        {
            try
            {
                // Extract properties from composite SlicerProfileDto
                ProcessProfileDto? processProfile = workerProfile.ProcessProfile;
                FilamentProfileDto? filamentProfile = workerProfile.FilamentProfile;

                // Skip if we don't have essential profile data
                if (processProfile == null)
                {
                    _logger.LogWarning("Skipping profile import: no ProcessProfile in composite SlicerProfileDto");
                    continue;
                }

                // Generate a hash for deduplication using process profile properties
                string layerHeight = processProfile.LayerHeight.ToString();
                string infill = processProfile.InfillPercentage.ToString();
                string material = filamentProfile?.Material ?? "Unknown";
                string quality = processProfile.Quality ?? "Standard";

                string profileHash = $"{material}:{quality}:{layerHeight}:{infill}";

                // Check if this profile already exists (by hash) using repository
                ProcessProfile? existingProfile = await _processProfileRepo.GetByHashAsync(profileHash, ct);
                if (existingProfile != null && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                {
                    duplicated++;
                    continue;
                }

                // Create a user-owned profile from the worker data
                ProcessProfile userProfile = new ProcessProfile
                {
                    Id = Guid.NewGuid(),
                    Name = $"{material} - {quality} ({layerHeight}mm)",
                    Description = $"Official OrcaSlicer profile imported for {printer.Name}",
                    SlicerType = SlicerType.OrcaSlicer,
                    LayerHeight = processProfile.LayerHeight,
                    InfillPercentage = processProfile.InfillPercentage,
                    PrintSpeed = processProfile.PrintSpeed,
                    EnableSupports = processProfile.Supports,
                    Quality = Enum.TryParse(quality, true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                    IsSystem = false,
                    IsDefault = false,
                    IsPublic = request.MakePublic ?? false,
                    Hash = profileHash,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _ = await _processProfileRepo.AddOrUpdateFromImportAsync(userProfile, allowSystemOverride: false, ct);
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

    /// <summary>
    /// Get the OrcaSlicer worker URL from the worker registry in the database
    /// </summary>
    private async Task<string?> GetOrcaSlicerWorkerUrlAsync()
    {
        try
        {
            // Query for any worker with OrcaSlicer capability that is online
            IReadOnlyList<Worker> allWorkers = await _workerRepository.GetAllAsync(limit: 100, offset: 0);
            Worker? orcaWorker = allWorkers.FirstOrDefault(w =>
                w.Status == "online" &&
                !string.IsNullOrEmpty(w.CapabilitiesJson) &&
                w.CapabilitiesJson.Contains("orcaslicer", StringComparison.OrdinalIgnoreCase));

            if (orcaWorker != null && !string.IsNullOrEmpty(orcaWorker.EndpointUrl))
            {
                _logger.LogInformation($"Using OrcaSlicer worker from registry: {orcaWorker.Name} at {orcaWorker.EndpointUrl}");
                return orcaWorker.EndpointUrl;
            }

            // Fallback: try to find any OrcaSlicer worker (even if offline, in case it's just between heartbeats)
            orcaWorker = allWorkers.FirstOrDefault(w =>
                !string.IsNullOrEmpty(w.CapabilitiesJson) &&
                w.CapabilitiesJson.Contains("orcaslicer", StringComparison.OrdinalIgnoreCase));

            if (orcaWorker != null && !string.IsNullOrEmpty(orcaWorker.EndpointUrl))
            {
                _logger.LogWarning($"OrcaSlicer worker '{orcaWorker.Name}' is not online, but using endpoint anyway: {orcaWorker.EndpointUrl}");
                return orcaWorker.EndpointUrl;
            }

            _logger.LogWarning("No OrcaSlicer worker found in registry");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to query worker registry: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Computes SHA256 hash of the given input string.
    /// Used for generating unique profile fingerprints based on complete profile data.
    /// </summary>
    private static string ComputeSha256Hash(string input)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hashedBytes).ToLower();
        }
    }
}

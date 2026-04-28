using System.Security.Claims;
using Farm.Infrastructure.Dtos;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// REST API controller for managing slicer profiles (process, machine, and filament profiles).
/// Provides endpoints for importing, exporting, listing, filtering, and configuring slicer profiles across
/// different slicer types (PrusaSlicer, OrcaSlicer, SuperSlicer, etc.).
/// </summary>
/// <remarks>
/// This controller delegates profile orchestration to <see cref="IProfilesService"/>, maintaining a thin controller
/// architecture. All operations are authenticated, with most requiring farm_admin policy for security.
/// </remarks>
[ApiController]
[Route("api/slicer/profiles")]
[Tags("Slicer Profiles")]
[Authorize]
public class ProfilesController(
    ILogger<ProfilesController> logger,
    IProfilesService profilesService,
    ICatalogServiceAdapter catalogService) : ControllerBase
{
    private readonly ILogger<ProfilesController> _logger = logger;
    private readonly IProfilesService _profilesService = profilesService;
    private readonly ICatalogServiceAdapter _catalogService = catalogService;

    /// <summary>
    /// Imports a process profile from raw slicer configuration JSON with deduplication and validation.
    /// </summary>
    /// <param name="request">Import request containing raw profile JSON, slicer type, and optional metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>201 Created if profile is new; 200 OK if profile already exists and was updated.</returns>
    [HttpPost("import")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(ProcessProfileExtendedDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProcessProfileExtendedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportProfileAsync(
        [FromBody] ImportProcessProfileDto? request,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RawJson))
        {
            return BadRequest("rawJson is required");
        }

        if (string.IsNullOrWhiteSpace(request.SlicerType) || !Enum.TryParse(request.SlicerType, true, out SlicerType _))
        {
            return BadRequest("Invalid slicerType");
        }

        try
        {
            (ProcessProfileExtendedDto dto, bool created) = await _profilesService.ImportProfileAsync(request, ct);
            return created
                ? Created($"/api/slicer/profiles/{dto.Id}", dto)
                : Ok(dto);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Profile import validation failed: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import slicer profile");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to import profile");
        }
    }

    /// <summary>
    /// Exports the raw slicer configuration JSON for a stored profile.
    /// </summary>
    /// <param name="id">Unique identifier of the process profile to export.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}/export")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(ProcessProfileExportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportProfileAsync(Guid id, CancellationToken ct)
    {
        try
        {
            ProcessProfileExportDto? dto = await _profilesService.ExportProfileAsync(id, ct);
            if (dto is null)
            {
                _logger.LogWarning("Profile not found for export: {Id}", id);
                return NotFound();
            }

            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export profile");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to export profile");
        }
    }

    /// <summary>
    /// Sets a process profile as the default for system-wide usage in slicing jobs.
    /// </summary>
    /// <param name="id">Unique identifier of the profile to set as default.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id:guid}/set-default")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultProfileAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await _profilesService.SetDefaultProfileAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning("Profile not found for setting default: {Id}", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set default profile");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to set default profile");
        }
    }

    /// <summary>
    /// Retrieves an extended listing of all profile types with hierarchical organization.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("extended")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ExtendedProfilesResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListExtendedAsync(CancellationToken ct)
    {
        try
        {
            ExtendedProfilesResponseDto response = await _profilesService.ListExtendedAsync(ct);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list extended profiles");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to list profiles");
        }
    }

    /// <summary>
    /// Retrieves profiles organized in a hierarchical structure by manufacturer and machine model.
    /// </summary>
    /// <param name="manufacturer">Optional filter to retrieve only profiles for a specific manufacturer.</param>
    /// <param name="machineProfileId">Optional filter to retrieve only profiles compatible with a specific machine.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("hierarchy")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(HierarchicalProfilesResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListHierarchyAsync(
        [FromQuery] string? manufacturer = null,
        [FromQuery] Guid? machineProfileId = null,
        CancellationToken ct = default)
    {
        try
        {
            HierarchicalProfilesResponseDto response = await _profilesService.ListHierarchyAsync(manufacturer, machineProfileId, ct);
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list hierarchical profiles");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to list profiles");
        }
    }

    /// <summary>
    /// Creates a new process profile from a client-provided configuration.
    /// </summary>
    /// <param name="request">Profile creation request.</param>
    [HttpPost]
    [Authorize(Policy = "farm_admin")]
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

            if (string.IsNullOrWhiteSpace(request.SlicerType) || !Enum.TryParse(request.SlicerType, true, out SlicerType _))
            {
                return BadRequest("Invalid slicer type");
            }

            if (!string.IsNullOrWhiteSpace(request.Quality) && !Enum.TryParse(request.Quality, true, out ProfileQuality _))
            {
                return BadRequest("Invalid quality setting");
            }

            CreateProcessProfileDto createReq = new()
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
            _logger.LogError(ex, "Failed to create profile: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to create profile");
        }
    }

    /// <summary>
    /// Retrieves a specific process profile by its unique identifier.
    /// </summary>
    /// <param name="id">Unique identifier of the profile.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProcessProfileResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfileAsync(Guid id)
    {
        ProcessProfileResponseDto? profile = await _profilesService.GetProfileAsync(id, CancellationToken.None);
        return profile == null ? NotFound() : Ok(profile);
    }

    /// <summary>
    /// Deletes a process profile from the system.
    /// </summary>
    /// <param name="id">Unique identifier of the profile to delete.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "farm_admin")]
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

    /// <summary>
    /// Bulk deletes multiple profiles by ID, supporting all profile types.
    /// </summary>
    /// <param name="profileIds">Collection of profile IDs to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("bulk-delete")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(BulkDeleteResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkDeleteProfilesAsync(
        [FromBody] List<Guid>? profileIds,
        CancellationToken ct)
    {
        if (profileIds is null || profileIds.Count == 0)
        {
            return BadRequest("At least one profile ID is required");
        }

        BulkDeleteResultDto result = await _profilesService.BulkDeleteProfilesAsync(profileIds, ct);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves all process profiles with optional filtering by printer or slicer type.
    /// </summary>
    /// <param name="printerId">Optional printer ID to filter profiles.</param>
    /// <param name="slicerType">Optional slicer type to filter profiles.</param>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfilesAsync([FromQuery] string? printerId = null, [FromQuery] string? slicerType = null)
    {
        try
        {
            IReadOnlyList<SlicerProfileDto> allProfiles = await _profilesService.GetProfilesAsync(CancellationToken.None);

            IReadOnlyList<object> result = allProfiles
                .Where(p => p.ProcessProfile != null)
                .Select(p => (object)new
                {
                    id = p.ProcessProfile!.Name,
                    name = p.ProcessProfile!.Name,
                    layerHeight = p.ProcessProfile!.LayerHeight,
                    infillPercentage = p.ProcessProfile!.InfillPercentage,
                    printSpeed = p.FilamentProfile?.PrintSpeed ?? p.ProcessProfile!.PrintSpeed,
                    nozzleTemperature = p.FilamentProfile?.NozzleTemperature ?? 210,
                    bedTemperature = p.FilamentProfile?.BedTemperature ?? 60,
                    supports = p.ProcessProfile!.Supports,
                    material = p.FilamentProfile?.Material ?? "Unknown",
                    quality = p.ProcessProfile!.Quality
                })
                .ToList();

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get profiles: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get available profiles");
        }
    }

    /// <summary>
    /// Parse and preview an OrcaSlicer config bundle without persisting.
    /// </summary>
    /// <param name="request">Bundle JSON payload.</param>
    /// <param name="orcaParsingService">OrcaSlicer bundle parsing service.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("import/orca/preview")]
    [Authorize(Policy = "farm_admin")]
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
            if (!orcaParsingService.IsValidOrcaBundle(request.BundleJson))
            {
                return BadRequest("Invalid OrcaSlicer bundle format. Expected JSON object with printer/filament/process sections.");
            }

            OrcaBundlePreviewDto preview = orcaParsingService.ParseBundle(request.BundleJson);

            _logger.LogInformation(
                "OrcaSlicer bundle preview: {PrinterCount} printers, {FilamentCount} filaments, {ProcessCount} processes",
                preview.Printers.Count, preview.Filaments.Count, preview.Processes.Count);

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
    /// Import selected profiles from an OrcaSlicer config bundle.
    /// </summary>
    /// <param name="request">Bundle JSON and selection criteria.</param>
    /// <param name="orcaParsingService">OrcaSlicer bundle parsing service.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("import/orca")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(ImportOrcaBundleResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportOrcaBundleResultDto>> ImportOrcaBundleAsync(
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
            if (!orcaParsingService.IsValidOrcaBundle(request.BundleJson))
            {
                return BadRequest("Invalid OrcaSlicer bundle format. Expected JSON object with printer/filament/process sections.");
            }

            OrcaBundlePreviewDto preview = orcaParsingService.ParseBundle(request.BundleJson);

            ImportOrcaBundleResultDto result = new()
            {
                Success = true,
                PrintersImported = 0,
                FilamentsImported = 0,
                ProcessesImported = 0
            };

            // Import selected printer presets
            if (request.ImportPrinters)
            {
                IEnumerable<OrcaPrinterPresetDto> printersToImport = request.SelectedPrinters != null && request.SelectedPrinters.Count > 0
                    ? preview.Printers.Where(p => request.SelectedPrinters.Contains(p.Name))
                    : preview.Printers;

                foreach (OrcaPrinterPresetDto printer in printersToImport)
                {
                    try
                    {
                        string rawJson = System.Text.Json.JsonSerializer.Serialize(printer.RawParameters);
                        ImportProcessProfileDto importDto = new()
                        {
                            Name = printer.Name,
                            Description = $"Imported OrcaSlicer printer preset: {printer.PrinterModel}",
                            RawJson = rawJson,
                            SlicerType = "OrcaSlicer",
                            AllowSystemOverride = request.AllowSystemOverride,
                            SetDefault = request.SetDefaults
                        };

                        await _profilesService.ImportProfileAsync(importDto, ct);
                        result.PrintersImported++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import printer preset: {PrinterName}", printer.Name);
                        result.Warnings.Add($"Failed to import printer '{printer.Name}': {ex.Message}");
                    }
                }
            }

            // Import selected filament presets
            if (request.ImportFilaments)
            {
                IEnumerable<OrcaFilamentPresetDto> filamentsToImport = request.SelectedFilaments != null && request.SelectedFilaments.Count > 0
                    ? preview.Filaments.Where(f => request.SelectedFilaments.Contains(f.Name))
                    : preview.Filaments;

                foreach (OrcaFilamentPresetDto filament in filamentsToImport)
                {
                    try
                    {
                        string rawJson = System.Text.Json.JsonSerializer.Serialize(filament.RawParameters);
                        ImportProcessProfileDto importDto = new()
                        {
                            Name = filament.Name,
                            Description = $"Imported OrcaSlicer filament preset: {filament.FilamentType}",
                            RawJson = rawJson,
                            SlicerType = "OrcaSlicer",
                            AllowSystemOverride = request.AllowSystemOverride,
                            SetDefault = request.SetDefaults
                        };

                        await _profilesService.ImportProfileAsync(importDto, ct);
                        result.FilamentsImported++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import filament preset: {FilamentName}", filament.Name);
                        result.Warnings.Add($"Failed to import filament '{filament.Name}': {ex.Message}");
                    }
                }
            }

            // Import selected process presets
            if (request.ImportProcesses)
            {
                IEnumerable<OrcaProcessPresetDto> processesToImport = request.SelectedProcesses != null && request.SelectedProcesses.Count > 0
                    ? preview.Processes.Where(p => request.SelectedProcesses.Contains(p.Name))
                    : preview.Processes;

                foreach (OrcaProcessPresetDto process in processesToImport)
                {
                    try
                    {
                        string rawJson = System.Text.Json.JsonSerializer.Serialize(process.RawParameters);
                        ImportProcessProfileDto importDto = new()
                        {
                            Name = process.Name,
                            Description = $"Imported OrcaSlicer process preset: {process.Quality ?? process.LayerHeight.ToString("F2") + "mm"}",
                            RawJson = rawJson,
                            SlicerType = "OrcaSlicer",
                            AllowSystemOverride = request.AllowSystemOverride,
                            SetDefault = request.SetDefaults
                        };

                        await _profilesService.ImportProfileAsync(importDto, ct);
                        result.ProcessesImported++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import process preset: {ProcessName}", process.Name);
                        result.Warnings.Add($"Failed to import process '{process.Name}': {ex.Message}");
                    }
                }
            }

            _logger.LogInformation(
                "OrcaSlicer bundle imported: {PrintersImported} printers, {FilamentsImported} filaments, {ProcessesImported} processes",
                result.PrintersImported, result.FilamentsImported, result.ProcessesImported);

            result.Success = result.Errors.Count == 0;
            return Ok(result);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Invalid OrcaSlicer bundle format");
            return BadRequest(new { error = "Invalid bundle format", detail = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import OrcaSlicer bundle");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to import bundle");
        }
    }

    /// <summary>
    /// Export PrintFarmer profiles to OrcaSlicer config bundle JSON format.
    /// </summary>
    /// <param name="request">Export configuration (optional filters).</param>
    /// <param name="exportService">OrcaSlicer bundle export service.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("export/orca")]
    [Authorize(Policy = "farm_admin")]
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
            request ??= new ExportOrcaBundleRequest();

            string bundleJson = await exportService.ExportBundleAsync(request);

            _logger.LogInformation(
                "OrcaSlicer bundle exported: {PrinterFilterCount} printer filters, {FilamentFilterCount} filament filters",
                request.PrinterModelIds?.Count ?? 0, request.FilamentTypeIds?.Count ?? 0);

            return Content(bundleJson, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export OrcaSlicer bundle");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to generate export bundle");
        }
    }

    /// <summary>
    /// List all system-seeded OrcaSlicer profiles available in the database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("system/orca")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSystemOrcaProfilesAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<SlicerProfileListItemDto> profiles = await _profilesService.ListSystemOrcaProfilesAsync(ct);
            return Ok(profiles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list system OrcaSlicer profiles");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to list profiles");
        }
    }

    /// <summary>
    /// Seed system OrcaSlicer profiles from the worker service into the database.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("system/orca/seed-from-worker")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SeedSystemProfilesFromWorkerAsync(
        [FromServices] HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            object result = await _profilesService.SeedSystemProfilesFromWorkerAsync(httpClient, ct);
            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode.HasValue)
            {
                _logger.LogWarning("OrcaSlicer worker returned {StatusCode}: {Message}", ex.StatusCode, ex.Message);
                return StatusCode((int)ex.StatusCode.Value, "OrcaSlicer worker unavailable or returned an error");
            }

            _logger.LogError("Failed to connect to OrcaSlicer worker: {Message}", ex.Message);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                "OrcaSlicer worker unavailable. Please ensure the worker service is running and registered.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding system profiles");
            return StatusCode(StatusCodes.Status500InternalServerError, "Error seeding profiles");
        }
    }

    /// <summary>
    /// Force reseed system OrcaSlicer profiles from the worker, clearing existing ones first.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("system/orca/force-reseed-from-worker")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ForceReseedSystemProfilesFromWorkerAsync(
        [FromServices] HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            object result = await _profilesService.ForceReseedSystemProfilesFromWorkerAsync(httpClient, ct);
            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode.HasValue)
            {
                _logger.LogWarning("OrcaSlicer worker returned {StatusCode}: {Message}", ex.StatusCode, ex.Message);
                return StatusCode((int)ex.StatusCode.Value, "OrcaSlicer worker unavailable or returned an error");
            }

            _logger.LogError("Failed to connect to OrcaSlicer worker: {Message}", ex.Message);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                "OrcaSlicer worker unavailable. Please ensure the worker service is running and registered.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error force-reseeding system profiles");
            return StatusCode(StatusCodes.Status500InternalServerError, "Error reseeding profiles");
        }
    }

    /// <summary>
    /// Delete all system profiles (IsSystem=true) from the database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("system/cleanup")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAllSystemProfilesAsync(CancellationToken ct)
    {
        try
        {
            object result = await _profilesService.DeleteAllSystemProfilesAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting system profiles");
            return StatusCode(StatusCodes.Status500InternalServerError, "Error deleting profiles");
        }
    }

    /// <summary>
    /// Fetch available OrcaSlicer profiles from the worker service.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("available-from-worker")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAvailableProfilesFromWorkerAsync(
        [FromServices] HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            IReadOnlyList<ProcessProfileDto> profiles = await _profilesService.GetAvailableProfilesFromWorkerAsync(httpClient, ct);
            return Ok(profiles);
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode.HasValue)
            {
                _logger.LogWarning("OrcaSlicer worker returned {StatusCode}: {Message}", ex.StatusCode, ex.Message);
                return StatusCode((int)ex.StatusCode.Value, "OrcaSlicer worker unavailable or returned an error");
            }

            _logger.LogError("Failed to connect to OrcaSlicer worker: {Message}", ex.Message);
            return ex.Message.Contains("not found in registry", StringComparison.OrdinalIgnoreCase)
                ? StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker not found in registry")
                : StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable. Please ensure the worker service is running and registered.");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching profiles from OrcaSlicer worker: {Message}", ex.Message);
            return StatusCode(500, "Error fetching profiles from worker");
        }
    }

    /// <summary>
    /// Get the full profile hierarchy from OrcaSlicer worker organized by manufacturer and model.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("worker-hierarchy")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AllProfilesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetWorkerProfilesHierarchyAsync(
        [FromServices] HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            AllProfilesResponseDto? profiles = await _profilesService.GetWorkerProfilesHierarchyAsync(httpClient, ct);
            return Ok(profiles ?? new AllProfilesResponseDto());
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("OrcaSlicer worker unavailable: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching profiles hierarchy from OrcaSlicer worker: {Message}", ex.Message);
            return StatusCode(500, "Error fetching profiles from worker");
        }
    }

    /// <summary>
    /// Get the profile hierarchy from OrcaSlicer worker filtered to only include
    /// manufacturers present in the PrintFarmer catalog.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("catalog-hierarchy")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AllProfilesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetCatalogFilteredWorkerHierarchyAsync(
        [FromServices] HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            AllProfilesResponseDto? profiles = await _profilesService.GetCatalogFilteredWorkerHierarchyAsync(httpClient, ct);
            return Ok(profiles ?? new AllProfilesResponseDto());
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("OrcaSlicer worker unavailable: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching catalog-filtered profiles from OrcaSlicer worker: {Message}", ex.Message);
            return StatusCode(500, "Error fetching profiles from worker");
        }
    }

    /// <summary>
    /// Get machine profiles for a specific manufacturer and model from the OrcaSlicer worker.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="manufacturer">Manufacturer name.</param>
    /// <param name="model">Model name.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("machine/{manufacturer}/{model}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(List<MachineProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetMachineProfilesForModelAsync(
        [FromServices] HttpClient httpClient,
        string manufacturer,
        string model,
        CancellationToken ct)
    {
        try
        {
            IReadOnlyList<MachineProfileDto> profiles = await _profilesService.GetMachineProfilesForModelAsync(httpClient, manufacturer, model, ct);
            return Ok(profiles);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("OrcaSlicer worker unavailable: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching machine profiles for {Manufacturer}/{Model}: {Message}", manufacturer, model, ex.Message);
            return StatusCode(500, "Error fetching profiles from worker");
        }
    }

    /// <summary>
    /// Get machine profiles for a printer model by its catalog ID, using its OrcaSlicer alias.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="modelId">The printer model ID from the catalog.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("machine/for-model/{modelId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(List<MachineProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetMachineProfilesForModelIdAsync(
        [FromServices] HttpClient httpClient,
        Guid modelId,
        CancellationToken ct)
    {
        try
        {
            CatalogModelInfo? model = await _catalogService.GetModelByIdAsync(modelId, ct);
            if (model == null)
            {
                return NotFound($"Printer model with ID {modelId} not found");
            }

            IReadOnlyList<SlicerModelAliasDto> aliases = await _catalogService.GetModelAliasesAsync(modelId, ct);
            List<string> orcaAliases = aliases
                .Where(a => string.Equals(a.SlicerType, "OrcaSlicer", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.SlicerModelName)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;

            if (orcaAliases.Count == 0)
            {
                _logger.LogWarning("No OrcaSlicer alias configured for model {ModelName}", model.Name);
                return NotFound($"No OrcaSlicer alias configured for model {model.Name}");
            }

            _logger.LogInformation(
                "Fetching machine profiles for model {ModelName} using {AliasCount} OrcaSlicer aliases",
                model.Name,
                orcaAliases.Count);

            IReadOnlyList<MachineProfileDto> profiles = await _profilesService.GetMachineProfilesForCatalogModelAsync(
                httpClient,
                orcaAliases,
                ct);
            return Ok(profiles);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("OrcaSlicer worker unavailable: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching machine profiles for model {ModelId}: {Message}", modelId, ex.Message);
            return StatusCode(500, "Error fetching profiles from worker");
        }
    }

    /// <summary>
    /// Get process profiles compatible with specific machine profiles from the OrcaSlicer worker.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="request">Request containing list of machine profile names.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("process/for-machines")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(List<ProcessProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetProcessProfilesForMachinesAsync(
        [FromServices] HttpClient httpClient,
        [FromBody] ForMachinesRequest request,
        CancellationToken ct)
    {
        try
        {
            IReadOnlyList<ProcessProfileDto> profiles = await _profilesService.GetProcessProfilesForMachinesAsync(httpClient, request.MachineNames, ct);
            return Ok(profiles);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("OrcaSlicer worker unavailable: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching process profiles for machines: {Message}", ex.Message);
            return StatusCode(500, "Error fetching profiles from worker");
        }
    }

    /// <summary>
    /// Get filament profiles compatible with specific machine profiles from the OrcaSlicer worker.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="request">Request containing list of machine profile names.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("filament/for-machines")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(List<FilamentProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetFilamentProfilesForMachinesAsync(
        [FromServices] HttpClient httpClient,
        [FromBody] ForMachinesRequest request,
        CancellationToken ct)
    {
        try
        {
            IReadOnlyList<FilamentProfileDto> profiles = await _profilesService.GetFilamentProfilesForMachinesAsync(httpClient, request.MachineNames, ct);
            return Ok(profiles);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("OrcaSlicer worker unavailable: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching filament profiles for machines: {Message}", ex.Message);
            return StatusCode(500, "Error fetching profiles from worker");
        }
    }

    /// <summary>
    /// Get template filament profiles from the OrcaFilamentLibrary.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("filament/templates")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(List<FilamentProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetFilamentTemplatesAsync(
        [FromServices] HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            IReadOnlyList<FilamentProfileDto> profiles = await _profilesService.GetFilamentTemplatesAsync(httpClient, ct);
            return Ok(profiles);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("OrcaSlicer worker unavailable: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching filament templates: {Message}", ex.Message);
            return StatusCode(500, "Error fetching templates from worker");
        }
    }

    /// <summary>
    /// Gets names of profiles already imported for a specific printer model.
    /// </summary>
    /// <param name="modelId">The printer model ID to check imported profiles for.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("imported-names/{modelId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ImportedProfileNamesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetImportedProfileNamesAsync(Guid modelId, CancellationToken ct)
    {
        try
        {
            ImportedProfileNamesDto result = await _profilesService.GetImportedProfileNamesForModelAsync(modelId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error getting imported profile names: {Message}", ex.Message);
            return StatusCode(500, "Error getting imported profile names");
        }
    }

    /// <summary>
    /// Get system profiles available for import for a specific registered printer.
    /// </summary>
    /// <param name="printerId">The registered printer ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("available-for-printer/{printerId}")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAvailableProfilesForPrinterAsync(
        Guid printerId,
        CancellationToken ct)
    {
        try
        {
            IReadOnlyList<SlicerProfileListItemDto> profiles = await _profilesService.GetAvailableProfilesForPrinterAsync(printerId, ct);
            return Ok(profiles);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning("Printer not found: {Message}", ex.Message);
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching available profiles for printer");
            return StatusCode(StatusCodes.Status500InternalServerError, "Error fetching profiles");
        }
    }

    /// <summary>
    /// Bulk import system OrcaSlicer profiles for a specific registered printer.
    /// </summary>
    /// <param name="printerId">The registered printer ID.</param>
    /// <param name="request">Bulk import request.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("bulk-import-for-printer/{printerId}")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(BulkProfileImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkImportProfilesForPrinterAsync(
        Guid printerId,
        [FromBody] BulkProfileImportRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("profileIds list is required and must not be empty");
        }

        try
        {
            BulkProfileImportResultDto result = await _profilesService.BulkImportProfilesForPrinterAsync(printerId, request, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Bulk import validation failed: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning("Printer not found: {Message}", ex.Message);
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk import failed");
            return StatusCode(StatusCodes.Status500InternalServerError, "Bulk import failed");
        }
    }

    /// <summary>
    /// Import selected profiles from OrcaSlicer worker for a specific printer model.
    /// Used by the Profile Import Wizard.
    /// </summary>
    /// <param name="modelId">The printer model ID from the catalog.</param>
    /// <param name="request">Selective import request with selected profiles.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("import-selected-for-model/{modelId:guid}")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(SelectiveProfileImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ImportSelectedProfilesForModelAsync(
        Guid modelId,
        [FromBody] SelectiveProfileImportRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        if (string.IsNullOrWhiteSpace(request.ManufacturerName))
        {
            return BadRequest("ManufacturerName is required");
        }

        try
        {
            CatalogModelInfo? model = await _catalogService.GetModelByIdAsync(modelId, ct);
            if (model == null)
            {
                return NotFound($"Printer model with ID {modelId} not found");
            }

            _logger.LogInformation(
                "Importing selected profiles for model {ModelName} (manufacturer: {Manufacturer})",
                model.Name, request.ManufacturerName);

            SelectiveProfileImportResultDto result = await _profilesService.ImportSelectedProfilesForModelAsync(modelId, request, ct);

            if (!string.IsNullOrEmpty(result.Error))
            {
                if (result.Error.Contains("worker", StringComparison.OrdinalIgnoreCase) &&
                    result.Error.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, result.Error);
                }
            }

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("OrcaSlicer worker unavailable: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing profiles for model {ModelId}", modelId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Profile import failed");
        }
    }

    /// <summary>
    /// Clone process profiles from a template machine to a custom printer instance.
    /// </summary>
    /// <param name="request">Clone request with source and target details.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("clone-from-template")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(CloneProfilesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CloneFromTemplateAsync([FromBody] CloneProfilesRequestDto? request, CancellationToken ct)
    {
        try
        {
            if (request is null)
            {
                return BadRequest("sourceMachineProfileId and targetPrinterId required");
            }

            CloneProfilesResponseDto result = await _profilesService.CloneFromTemplateAsync(request, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Clone validation failed: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning("Machine profile or printer not found: {Message}", ex.Message);
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clone failed");
            return BadRequest("Clone failed");
        }
    }

    /// <summary>
    /// Bulk import profiles directly from the OrcaSlicer worker without pre-seeding.
    /// </summary>
    /// <param name="printerId">The registered printer ID.</param>
    /// <param name="request">Bulk import request with profile data from worker.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("bulk-import-from-worker/{printerId}")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(BulkImportFromWorkerResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkImportFromWorkerAsync(
        Guid printerId,
        [FromBody] BulkImportFromWorkerRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("profiles list is required and must not be empty");
        }

        try
        {
            BulkImportFromWorkerResultDto result = await _profilesService.BulkImportFromWorkerAsync(printerId, request, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Bulk import validation failed: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning("Printer not found: {Message}", ex.Message);
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk import from worker failed");
            return StatusCode(StatusCodes.Status500InternalServerError, "Bulk import from worker failed");
        }
    }

    /// <summary>
    /// Clones a single profile to create a user-owned custom copy.
    /// </summary>
    /// <param name="request">Clone request with source profile ID and type.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("clone")]
    [ProducesResponseType(typeof(CloneSingleProfileResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CloneSingleProfileAsync(
        [FromBody] CloneSingleProfileRequestDto? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            Guid userId = GetCurrentUserId();

            CloneSingleProfileResponseDto result = await _profilesService.CloneSingleProfileAsync(request, userId, ct);
            return Created($"/api/slicer/profiles/{result.Id}", result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Clone profile validation failed: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning("Source profile not found: {Message}", ex.Message);
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clone profile failed");
            return StatusCode(StatusCodes.Status500InternalServerError, "Clone profile failed");
        }
    }

    /// <summary>
    /// Uploads a custom profile from raw JSON content.
    /// </summary>
    /// <param name="request">Upload request with raw JSON and profile type.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(CustomProfileDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadCustomProfileAsync(
        [FromBody] UploadProfileRequestDto? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            Guid userId = GetCurrentUserId();

            CustomProfileDto result = await _profilesService.UploadCustomProfileAsync(request, userId, ct);
            return Created($"/api/slicer/profiles/{result.Id}", result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Upload profile validation failed: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload profile failed");
            return StatusCode(StatusCodes.Status500InternalServerError, "Upload profile failed");
        }
    }

    /// <summary>
    /// Lists all custom profiles owned by the current user.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("custom")]
    [ProducesResponseType(typeof(CustomProfilesListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListCustomProfilesAsync(CancellationToken ct)
    {
        try
        {
            Guid userId = GetCurrentUserId();

            CustomProfilesListResponseDto result = await _profilesService.ListCustomProfilesAsync(userId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List custom profiles failed");
            return StatusCode(StatusCodes.Status500InternalServerError, "List custom profiles failed");
        }
    }

    /// <summary>
    /// Updates a custom profile's properties. Only non-null fields are updated.
    /// </summary>
    /// <param name="id">ID of the custom profile to update.</param>
    /// <param name="request">Update request with optional new values.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("custom/{id:guid}")]
    [ProducesResponseType(typeof(CustomProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCustomProfileAsync(
        Guid id,
        [FromBody] UpdateCustomProfileRequestDto? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            Guid userId = GetCurrentUserId();

            CustomProfileDto result = await _profilesService.UpdateCustomProfileAsync(id, request, userId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning("Custom profile not found: {Message}", ex.Message);
            return NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Update profile unauthorized: {Message}", ex.Message);
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Update profile invalid operation: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update custom profile failed");
            return StatusCode(StatusCodes.Status500InternalServerError, "Update custom profile failed");
        }
    }

    // ── Schema metadata endpoints (static, public, cached) ─────────

    /// <summary>
    /// Returns combined schema metadata for all profile types (process, machine, filament),
    /// powering schema-driven settings editors in the UI.
    /// </summary>
    [HttpGet("schemas")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    [ProducesResponseType(typeof(ProfileSchemasResponseDto), StatusCodes.Status200OK)]
    [Tags("Slicer Profile Schemas")]
    public IActionResult GetAllSchemas()
    {
        return Ok(ProfileSchemaProvider.GetAllSchemas());
    }

    /// <summary>
    /// Returns schema metadata for process profile fields.
    /// </summary>
    [HttpGet("schema/process")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    [ProducesResponseType(typeof(ProfileTypeSchemaDto), StatusCodes.Status200OK)]
    [Tags("Slicer Profile Schemas")]
    public IActionResult GetProcessSchema()
    {
        return Ok(ProfileSchemaProvider.GetProcessSchema());
    }

    /// <summary>
    /// Returns schema metadata for machine profile fields.
    /// </summary>
    [HttpGet("schema/machine")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    [ProducesResponseType(typeof(ProfileTypeSchemaDto), StatusCodes.Status200OK)]
    [Tags("Slicer Profile Schemas")]
    public IActionResult GetMachineSchema()
    {
        return Ok(ProfileSchemaProvider.GetMachineSchema());
    }

    /// <summary>
    /// Returns schema metadata for filament profile fields.
    /// </summary>
    [HttpGet("schema/filament")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    [ProducesResponseType(typeof(ProfileTypeSchemaDto), StatusCodes.Status200OK)]
    [Tags("Slicer Profile Schemas")]
    public IActionResult GetFilamentSchema()
    {
        return Ok(ProfileSchemaProvider.GetFilamentSchema());
    }

    /// <summary>
    /// Gets the current user's ID from the authentication claims.
    /// </summary>
    private Guid GetCurrentUserId()
    {
        string? userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out Guid userId))
        {
            return userId;
        }

        _logger.LogWarning("User ID not found in claims, using default user ID for development");
        return Guid.Parse("00000000-0000-0000-0000-000000000001");
    }
}

/// <summary>
/// Request DTO for fetching profiles compatible with specific machines.
/// </summary>
public record ForMachinesRequest
{
    /// <summary>
    /// List of machine profile names to find compatible profiles for.
    /// </summary>
    public IReadOnlyList<string> MachineNames { get; init; } = Array.Empty<string>();
}

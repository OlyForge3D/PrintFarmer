using System.Linq;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.DTOs;
using Farm.Web.Api.Services.Slicing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Slicing;

/// <summary>
/// REST API controller for managing slicer profiles (process, machine, and filament profiles).
/// Provides endpoints for importing, exporting, listing, filtering, and configuring slicer profiles across
/// different slicer types (PrusaSlicer, OrcaSlicer, SuperSlicer, etc.).
/// </summary>
/// <remarks>
/// This controller delegates profile orchestration to IProfilesService, maintaining a thin controller
/// architecture. All operations are authenticated, with most requiring farm_admin policy for security.
///
/// Key responsibilities:
/// - Profile import/export with validation and hash-based deduplication
/// - Hierarchical profile listing with optional filtering by manufacturer/machine model
/// - System profile seeding and reseeding from OrcaSlicer worker
/// - Bulk profile import operations (from database or worker)
/// - Profile cloning for custom printer configurations
/// - Default profile configuration per slicer type
/// - OrcaSlicer worker integration for profile discovery
/// - Process, machine, and filament profile management
///
/// Architecture note: This controller follows thin controller pattern by delegating
/// business logic to IProfilesService. It handles HTTP concerns (routing, status codes,
/// error handling) while the service handles orchestration.
///
/// All operations are authenticated. Most critical operations (import, bulk operations, seeding)
/// are restricted to farm_admin policy to prevent unauthorized modifications that could affect
/// slicing job configuration and printer compatibility.
/// Profile changes are logged for audit trails and system monitoring.
/// </remarks>
[ApiController]
[Route("api/slicer/profiles")]
[Tags("Slicer Profiles")]
[Authorize] // All endpoints require authentication
public class ProfilesController(
    IUnifiedLoggingService logger,
    IProfilesService profilesService,
    Services.Catalog.ICatalogService catalogService) : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IProfilesService _profilesService = profilesService;
    private readonly Services.Catalog.ICatalogService _catalogService = catalogService;

    /// <summary>
    /// Imports a process profile from raw slicer configuration JSON with deduplication and validation.
    /// </summary>
    /// <param name="request">Import request containing raw profile JSON, slicer type, and optional metadata</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>
    /// 201 Created if profile is new; 200 OK if profile already exists and was updated.
    /// Returns ProcessProfileExtendedDto with full profile details and metadata.
    /// </returns>
    /// <remarks>
    /// This endpoint performs comprehensive profile management:
    /// - Parses and validates raw JSON profile configuration
    /// - Extracts metadata (layer height, infill percentage, material type, quality)
    /// - Generates content hash for deduplication detection
    /// - Checks for existing profiles with same hash to prevent duplicates
    /// - Supports optional system profile override by administrators
    /// - Returns 201 Created for new profiles, 200 OK for updated existing profiles
    /// - Stores sanitized JSON for long-term storage and audit trails
    ///
    /// Requires farm_admin policy for access. Profile import is logged for audit purposes.
    /// </remarks>
    [HttpPost("import")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
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
            _logger.LogWarning($"Profile import validation failed: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import slicer profile");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to import profile");
        }
    }

    /// <summary>
    /// Exports the raw slicer configuration JSON for a stored profile with full metadata.
    /// </summary>
    /// <param name="id">Unique identifier of the process profile to export</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>ProcessProfileExportDto containing the raw JSON and all metadata fields for reimport</returns>
    /// <remarks>
    /// This endpoint retrieves a profile and returns its complete configuration including:
    /// - Raw slicer JSON for reimport to other farm instances
    /// - Extracted metadata (layer height, infill, material, quality)
    /// - Profile creation timestamp and version information
    /// - Hash for integrity verification
    ///
    /// Requires farm_admin policy for access. Exports include all data necessary to
    /// recreate the profile in another installation.
    /// </remarks>
    [HttpGet("{id:guid}/export")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile export
    [ProducesResponseType(typeof(ProcessProfileExportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportProfileAsync(Guid id, CancellationToken ct)
    {
        try
        {
            ProcessProfileExportDto? dto = await _profilesService.ExportProfileAsync(id, ct);
            if (dto is null)
            {
                _logger.LogWarning($"Profile not found for export: {id}");
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
    /// <param name="id">Unique identifier of the profile to set as default</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>204 No Content on success; 404 Not Found if profile does not exist</returns>
    /// <remarks>
    /// This endpoint marks a profile as the default choice for new slicing jobs.
    /// When no specific profile is selected, the default profile is automatically used.
    /// Only one profile per slicer type can be marked as default at a time.
    ///
    /// Requires farm_admin policy for access. Default profile changes are logged for audit trails.
    /// </remarks>
    [HttpPost("{id:guid}/set-default")]
    [Authorize(Policy = "farm_admin")] // Admin-only: set default profile
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
            _logger.LogWarning($"Profile not found for setting default: {id}");
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set default profile");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to set default profile");
        }
    }

    /// <summary>
    /// Retrieves an extended listing of all profile types with hierarchical organization and compatibility information.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>ExtendedProfilesResponseDto containing complete profiles by type, hierarchy, and compatibility metadata</returns>
    /// <remarks>
    /// This endpoint provides a comprehensive view of all available slicer profiles organized by type and hierarchy.
    /// Returns process profiles, filament profiles, and machine profiles with their compatibility conditions evaluated.
    /// Includes public system profiles, user-created profiles, and profile compatibility matrices for intelligent profile selection.
    /// Used for populating UI selectors and providing detailed profile information for job configuration.
    /// </remarks>
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
    /// <param name="manufacturer">Optional filter to retrieve only profiles for a specific manufacturer</param>
    /// <param name="machineProfileId">Optional filter to retrieve only profiles compatible with a specific machine</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>HierarchicalProfilesResponseDto containing profiles organized by manufacturer → model → profiles</returns>
    /// <remarks>
    /// This endpoint provides a hierarchical view of profiles that reflects the real-world organization:
    /// - Top level: Manufacturer (e.g., "Prusa", "Sovol")
    /// - Second level: Model (e.g., "Prusa CORE One", "Sovol SV08")
    /// - Third level: Individual profiles with compatibility information
    ///
    /// Both filters are optional and work together with AND logic:
    /// - If manufacturer is specified: Returns only that manufacturer's profiles
    /// - If machineProfileId is specified: Returns only compatible profiles for that machine
    /// - If both are specified: Both filters apply
    /// - If neither is specified: Returns all profiles in hierarchy
    ///
    /// Used for populating the Slicer Profiles admin page with organized profile listings.
    /// </remarks>
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
    /// <param name="request">Profile creation request containing name, description, slicer type, and configuration parameters</param>
    /// <returns>201 Created with the newly created profile details; 400 Bad Request if validation fails</returns>
    /// <remarks>
    /// This endpoint creates a new process profile with the specified parameters.
    /// The profile is immediately available for use in slicing jobs.
    ///
    /// Requires farm_admin policy for access. Profile creation is logged for audit purposes.
    /// All required fields (name, slicer type) must be provided in the request.
    /// </remarks>
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

    /// <summary>
    /// Retrieves a specific process profile by its unique identifier.
    /// </summary>
    /// <param name="id">Unique identifier of the profile to retrieve</param>
    /// <returns>ProcessProfileResponseDto with complete profile details on success; 404 Not Found if profile does not exist</returns>
    /// <remarks>
    /// This endpoint retrieves the complete configuration and metadata of a single profile including
    /// name, description, slicer type, and all configuration parameters. Returns the profile in a format
    /// suitable for display, editing, or use in job configuration. Returns 404 if the specified profile ID
    /// does not exist in the system.
    /// </remarks>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProcessProfileResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfileAsync(Guid id)
    {
        ProcessProfileResponseDto? profile = await _profilesService.GetProfileAsync(id, CancellationToken.None);
        return profile == null ? NotFound() : Ok(profile);
    }

    /// <summary>
    /// Deletes a process profile from the system, making it unavailable for future slicing jobs.
    /// </summary>
    /// <param name="id">Unique identifier of the profile to delete</param>
    /// <returns>204 No Content on successful deletion; 404 Not Found if profile does not exist</returns>
    /// <remarks>
    /// This endpoint permanently removes a profile from the system. Deleted profiles cannot
    /// be recovered. Any jobs using the deleted profile may be affected.
    ///
    /// Requires farm_admin policy for access. Profile deletion is logged for audit trails.
    /// Consider archiving instead of deleting to maintain historical references.
    /// </remarks>
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

    /// <summary>
    /// Bulk deletes multiple profiles by ID, supporting all profile types (machine, process, filament).
    /// </summary>
    /// <param name="profileIds">Collection of profile IDs to delete</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>BulkDeleteResultDto with counts of deleted profiles by type</returns>
    /// <remarks>
    /// Profiles are looked up in machine, process, and filament tables.
    /// Invalid or non-existent IDs are skipped (not treated as errors).
    /// Requires farm_admin policy for access.
    /// </remarks>
    [HttpPost("bulk-delete")]
    [Authorize(Policy = "farm_admin")] // Admin-only: bulk delete profiles
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
    /// <param name="printerId">Optional printer ID to filter profiles by printer-specific compatibility</param>
    /// <param name="slicerType">Optional slicer type to filter profiles (e.g., PrusaSlicer, OrcaSlicer)</param>
    /// <returns>Enumerable of profile objects filtered by the specified criteria; empty list if no matches found</returns>
    /// <remarks>
    /// This endpoint retrieves process profiles with optional filtering capabilities:
    /// - If printerId is provided, returns only profiles compatible with that printer
    /// - If slicerType is provided, returns only profiles for that specific slicer application
    /// - If both parameters are provided, applies both filters (AND logic)
    /// - If neither parameter is provided, returns all available profiles
    ///
    /// Used for populating UI profile selectors and providing filtered profile lists based on user context
    /// and printer capabilities. Returns empty list if no profiles match the filter criteria.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfilesAsync([FromQuery] string? printerId = null, [FromQuery] string? slicerType = null)
    {
        try
        {
            // Delegate to service for all filtering and profile retrieval
            IReadOnlyList<SlicerProfileDto> allProfiles = await _profilesService.GetProfilesAsync(CancellationToken.None);

            // Controller handles simple query string filtering only
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
            _logger.LogError(ex, $"Failed to get profiles: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get available profiles");
        }
    }

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

    /// <summary>
    /// List all system-seeded OrcaSlicer profiles available in the database.
    /// Returns profiles that were previously imported via the seed-from-worker endpoint.
    /// These are read-only system profiles (IsSystem=true) that serve as templates for user-owned profiles.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Collection of system OrcaSlicer profiles with metadata</returns>
    /// <response code="200">Returns list of system OrcaSlicer profiles</response>
    [HttpGet("system/orca")]
    [Authorize(Policy = "farm_admin")] // Admin-only: system profile management
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
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
                _logger.LogWarning($"OrcaSlicer worker returned {ex.StatusCode}: {ex.Message}");
                return StatusCode((int)ex.StatusCode.Value, "OrcaSlicer worker unavailable or returned an error");
            }

            _logger.LogError($"Failed to connect to OrcaSlicer worker: {ex.Message}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable. Please ensure the worker service is running and registered.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding system profiles");
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error seeding profiles: {ex.Message}");
        }
    }

    /// <summary>
    /// Force reseed system OrcaSlicer profiles from the worker, clearing existing ones first.
    /// Use this if the initial seeding failed or to update profiles after an OrcaSlicer upgrade.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of profiles imported</returns>
    /// <response code="200">Profiles force-reseeded successfully</response>
    /// <response code="401">Unauthorized - authentication required</response>
    /// <response code="403">Forbidden - farm_admin authorization policy required</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
    [HttpPost("system/orca/force-reseed-from-worker")]
    [Authorize(Policy = "farm_admin")] // Admin-only: system profile management
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
                _logger.LogWarning($"OrcaSlicer worker returned {ex.StatusCode}: {ex.Message}");
                return StatusCode((int)ex.StatusCode.Value, "OrcaSlicer worker unavailable or returned an error");
            }

            _logger.LogError($"Failed to connect to OrcaSlicer worker: {ex.Message}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable. Please ensure the worker service is running and registered.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error force-reseeding system profiles");
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error reseeding profiles: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete all system profiles (IsSystem=true) from the database.
    /// Phase 3 cleanup: removes duplicated system profiles from PostgreSQL.
    /// After this operation, system profiles are served only from OrcaSlicer worker.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Counts of deleted machine, process, and filament profiles</returns>
    /// <response code="200">System profiles deleted successfully</response>
    /// <response code="401">Unauthorized - authentication required</response>
    /// <response code="403">Forbidden - farm_admin authorization policy required</response>
    [HttpDelete("system/cleanup")]
    [Authorize(Policy = "farm_admin")] // Admin-only: system profile management
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error deleting profiles: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch available OrcaSlicer profiles from the OrcaSlicer worker service for administrative review.
    /// Discovers all profiles available in the running worker's local OrcaSlicer installation and prepares them for bulk import.
    /// </summary>
    /// <param name="httpClient">
    /// HTTP client for communicating with OrcaSlicer worker service.
    /// Uses named configuration for "OrcaSlicerWorker" endpoint discovery.
    /// </param>
    /// <param name="ct">Cancellation token for aborting the discovery operation</param>
    /// <returns>
    /// Returns list of ProcessProfileDto containing all available profiles from worker:
    /// - Id: Unique profile identifier in worker
    /// - Name: Human-readable profile name
    /// - SlicerType: Slicer application type (OrcaSlicer)
    /// - Quality: Profile quality level (Standard, Fast, Quality)
    /// - LayerHeight: Default layer height for profile
    /// - InfillPercentage: Default infill percentage
    /// - RawJson: Full OrcaSlicer configuration JSON
    /// - And other profile metadata fields
    /// </returns>
    /// <remarks>
    /// This is the first step in the worker-based profile import workflow:
    /// 1. Admin calls this endpoint to fetch profiles from OrcaSlicer worker
    /// 2. User reviews available profiles and selects which ones to import
    /// 3. User calls BulkImportFromWorkerAsync with selected profiles
    ///
    /// The service uses the worker registry to discover the worker URL. If no OrcaSlicer
    /// worker is registered or online, a 503 Service Unavailable is returned.
    ///
    /// Profiles are fetched from the worker without storing them in the database.
    /// The worker maintains its own profile catalog which is queried on demand.
    ///
    /// Only farm_admin users can call this endpoint. Worker communication is logged for audit trails.
    /// Error codes from the worker are forwarded to the caller to indicate specific failure modes.
    /// </remarks>
    /// <response code="200">
    /// Successfully fetched profiles from OrcaSlicer worker.
    /// Returns list of ProcessProfileDto entries (may be empty if no profiles available on worker).
    /// </response>
    /// <response code="401">Unauthorized - authentication required</response>
    /// <response code="403">Forbidden - farm_admin authorization policy required</response>
    /// <response code="503">OrcaSlicer worker unavailable - either not registered in worker registry, offline, or returning error</response>
    [HttpGet("available-from-worker")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
                _logger.LogWarning($"OrcaSlicer worker returned {ex.StatusCode}: {ex.Message}");
                return StatusCode((int)ex.StatusCode.Value, "OrcaSlicer worker unavailable or returned an error");
            }

            _logger.LogError($"Failed to connect to OrcaSlicer worker: {ex.Message}");
            return ex.Message.Contains("not found in registry", StringComparison.OrdinalIgnoreCase)
                ? StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker not found in registry")
                : StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable. Please ensure the worker service is running and registered.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching profiles from OrcaSlicer worker: {ex.Message}");
            return StatusCode(500, $"Error fetching profiles from worker: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the full profile hierarchy from OrcaSlicer worker organized by manufacturer and model.
    /// Proxies the worker's /api/profiles endpoint which returns all available profiles.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests to the worker service</param>
    /// <param name="ct">Cancellation token for aborting the request</param>
    /// <returns>AllProfilesResponseDto with profiles organized by manufacturer hierarchy</returns>
    /// <response code="200">Successfully fetched profiles hierarchy from OrcaSlicer worker</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
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
            _logger.LogWarning($"OrcaSlicer worker unavailable: {ex.Message}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching profiles hierarchy from OrcaSlicer worker: {ex.Message}");
            return StatusCode(500, $"Error fetching profiles from worker: {ex.Message}");
        }
    }

    /// <summary>
    /// Get machine profiles for a specific manufacturer and model from the OrcaSlicer worker.
    /// This endpoint proxies requests to the worker's /api/profiles/machine/{manufacturer}/{model} endpoint.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests to the worker service</param>
    /// <param name="manufacturer">Manufacturer name (e.g., "Elegoo", "Prusa")</param>
    /// <param name="model">Model name (e.g., "Centauri Carbon", "CORE One")</param>
    /// <param name="ct">Cancellation token for aborting the request</param>
    /// <returns>List of machine profiles matching the manufacturer and model</returns>
    /// <response code="200">Successfully fetched machine profiles from OrcaSlicer worker</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
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
            _logger.LogWarning($"OrcaSlicer worker unavailable: {ex.Message}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching machine profiles for {manufacturer}/{model}: {ex.Message}");
            return StatusCode(500, $"Error fetching profiles from worker: {ex.Message}");
        }
    }

    /// <summary>
    /// Get machine profiles for a printer model by its catalog ID.
    /// This endpoint looks up the OrcaSlicer alias for the model and fetches matching profiles.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests to the worker service</param>
    /// <param name="modelId">The printer model ID from the catalog</param>
    /// <param name="ct">Cancellation token for aborting the request</param>
    /// <returns>List of machine profiles matching the model's OrcaSlicer alias</returns>
    /// <response code="200">Successfully fetched machine profiles from OrcaSlicer worker</response>
    /// <response code="404">Printer model not found or no OrcaSlicer alias configured</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
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
            // Get the printer model
            PrinterModelDto? model = await _catalogService.GetModelByIdAsync(modelId, ct);
            if (model == null)
            {
                return NotFound($"Printer model with ID {modelId} not found");
            }

            // Get OrcaSlicer alias for this model - this IS the printer_model value
            IEnumerable<SlicerModelAliasDto> aliases = await _catalogService.GetModelAliasesAsync(modelId, ct);
            SlicerModelAliasDto? orcaAlias = aliases.FirstOrDefault(a => a.SlicerType == "OrcaSlicer");

            // The alias is the exact printer_model value to query (e.g., "Thinker X400", "RatRig V-Core 4 HYBRID 400")
            // If no alias exists, we cannot fetch profiles
            if (orcaAlias == null || string.IsNullOrWhiteSpace(orcaAlias.SlicerModelName))
            {
                _logger.LogWarning($"No OrcaSlicer alias configured for model {model.Name}");
                return NotFound($"No OrcaSlicer alias configured for model {model.Name}");
            }

            string printerModel = orcaAlias.SlicerModelName;
            _logger.LogInformation($"Fetching machine profiles for model {model.Name} using OrcaSlicer alias: {printerModel}");

            IReadOnlyList<MachineProfileDto> profiles = await _profilesService.GetMachineProfilesByAliasAsync(
                httpClient, printerModel, ct);
            return Ok(profiles);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning($"OrcaSlicer worker unavailable: {ex.Message}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching machine profiles for model {modelId}: {ex.Message}");
            return StatusCode(500, $"Error fetching profiles from worker: {ex.Message}");
        }
    }

    /// <summary>
    /// Get process profiles compatible with specific machine profiles from the OrcaSlicer worker.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests to the worker service</param>
    /// <param name="request">Request containing list of machine profile names</param>
    /// <param name="ct">Cancellation token for aborting the request</param>
    /// <returns>List of process profiles compatible with the specified machines</returns>
    /// <response code="200">Successfully fetched process profiles from OrcaSlicer worker</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
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
            _logger.LogWarning($"OrcaSlicer worker unavailable: {ex.Message}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching process profiles for machines: {ex.Message}");
            return StatusCode(500, $"Error fetching profiles from worker: {ex.Message}");
        }
    }

    /// <summary>
    /// Get filament profiles compatible with specific machine profiles from the OrcaSlicer worker.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests to the worker service</param>
    /// <param name="request">Request containing list of machine profile names</param>
    /// <param name="ct">Cancellation token for aborting the request</param>
    /// <returns>List of filament profiles compatible with the specified machines</returns>
    /// <response code="200">Successfully fetched filament profiles from OrcaSlicer worker</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
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
            _logger.LogWarning($"OrcaSlicer worker unavailable: {ex.Message}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching filament profiles for machines: {ex.Message}");
            return StatusCode(500, $"Error fetching profiles from worker: {ex.Message}");
        }
    }

    /// <summary>
    /// Get template filament profiles from the OrcaFilamentLibrary.
    /// These are universal profiles not tied to specific printers and serve as a starting point.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests to the worker service</param>
    /// <param name="ct">Cancellation token for aborting the request</param>
    /// <returns>Universal filament profiles from OrcaFilamentLibrary</returns>
    /// <response code="200">Successfully fetched template filament profiles</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
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
            _logger.LogWarning($"OrcaSlicer worker unavailable: {ex.Message}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching filament templates: {ex.Message}");
            return StatusCode(500, $"Error fetching templates from worker: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets names of profiles already imported for a specific printer model.
    /// Used by the import wizard to show which profiles have already been imported.
    /// </summary>
    /// <param name="modelId">The printer model ID to check imported profiles for</param>
    /// <param name="ct">Cancellation token for aborting the request</param>
    /// <returns>DTO containing lists of imported machine, process, and filament profile names</returns>
    /// <response code="200">Successfully retrieved imported profile names</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("imported-names/{modelId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ImportedProfileNamesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetImportedProfileNamesAsync(
        Guid modelId,
        CancellationToken ct)
    {
        try
        {
            ImportedProfileNamesDto result = await _profilesService.GetImportedProfileNamesForModelAsync(modelId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting imported profile names: {ex.Message}");
            return StatusCode(500, $"Error getting imported profile names: {ex.Message}");
        }
    }

    /// <summary>
    /// Get system profiles available for import for a specific registered printer.
    /// Filters compatible OrcaSlicer profiles by matching printer model and nozzle size from database.
    /// Supports both OrcaSlicer bundled profiles and previously imported system profiles.
    /// </summary>
    /// <param name="printerId">
    /// The unique identifier (GUID) of the registered printer instance.
    /// The printer must be previously registered in the database with model information.
    /// </param>
    /// <param name="ct">Cancellation token for aborting the retrieval operation</param>
    /// <returns>
    /// Returns list of SlicerProfileListItemDto containing compatible profiles:
    /// - Id: Unique profile identifier
    /// - Name: Profile name suitable for display in UI
    /// - SlicerType: Slicer application (OrcaSlicer, PrusaSlicer, etc.)
    /// - Type: Profile category (Process, Machine, Filament)
    /// - Quality: Profile quality indicator (Fast, Standard, Quality)
    /// - Manufacturer: Profile manufacturer if available
    /// - Compatible: Boolean indicating if profile is compatible with this printer
    /// - SystemProfile: Boolean indicating if this is a built-in system profile
    ///
    /// Results are pre-filtered to show only profiles compatible with the printer model.
    /// </returns>
    /// <remarks>
    /// This endpoint supports profile discovery workflow for registered printers:
    /// - User navigates to printer configuration UI
    /// - Admin calls this endpoint with printer ID to fetch compatible profiles
    /// - UI displays available profiles with compatibility indicators
    /// - User selects profiles for bulk import via BulkImportProfilesForPrinterAsync
    ///
    /// Filtering logic:
    /// - Profile machine model must match printer model exactly
    /// - Profile nozzle size (if specified) must match printer's active nozzle size
    /// - Only OrcaSlicer system profiles are returned (IsSystem=true)
    /// - Results sorted by manufacturer and profile name for UI display
    ///
    /// Filtering is performed in the service layer to provide flexible query capability
    /// and support future compatibility condition evaluation.
    ///
    /// Only farm_admin users can call this endpoint. Returns detailed profile metadata
    /// suitable for administrative profile management interfaces.
    /// </remarks>
    /// <response code="200">
    /// Successfully retrieved compatible profiles for printer.
    /// Returns list of SlicerProfileListItemDto (may be empty if no compatible profiles available).
    /// </response>
    /// <response code="401">Unauthorized - authentication required</response>
    /// <response code="403">Forbidden - farm_admin authorization policy required</response>
    /// <response code="404">Printer with specified ID not found in database</response>
    [HttpGet("available-for-printer/{printerId}")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
            _logger.LogWarning($"Printer not found: {ex.Message}");
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
    /// This is the secondary profile import workflow: import pre-seeded system profiles from database.
    /// Only OrcaSlicer system profiles can be bulk imported through this endpoint.
    /// </summary>
    /// <param name="printerId">
    /// The unique identifier (GUID) of the registered printer instance.
    /// The printer must be previously registered in the database.
    /// </param>
    /// <param name="request">
    /// Bulk import request containing:
    /// - ProfileIds: List of profile IDs to import (must not be empty)
    /// - SkipDuplicates: If true (default), skip profiles that already exist in database by hash
    /// - AllowSystemOverride: If true, allow overwriting existing system profiles with same name
    /// </param>
    /// <param name="ct">Cancellation token for aborting the bulk import operation</param>
    /// <returns>
    /// Returns BulkProfileImportResultDto containing:
    /// - ImportedCount: Number of profiles newly created in database
    /// - DuplicateCount: Number of profiles skipped (already in database)
    /// - ErrorCount: Number of profiles that failed import
    /// - TotalCount: Total profiles processed
    /// </returns>
    /// <remarks>
    /// This endpoint implements the secondary profile import workflow:
    /// 1. Admin uses SeedSystemProfilesFromWorkerAsync to pre-load profiles from worker to database
    /// 2. Admin calls GetAvailableProfilesForPrinterAsync to get compatible profiles
    /// 3. User selects profiles and calls this endpoint to bulk import them
    ///
    /// Bulk import operation characteristics:
    /// - Profiles are imported for the specific printer instance
    /// - Each profile is validated before import (hash check, compatibility check)
    /// - Deduplication uses SHA256 hash to prevent duplicate storage
    /// - Only OrcaSlicer system profiles (IsSystem=true) are imported
    /// - Import preserves profile metadata, configurations, and hierarchy
    /// - Operation is all-or-nothing: if errors occur, partial results are returned
    ///
    /// Error handling:
    /// - Invalid profile IDs are tracked in error count
    /// - Duplicate profiles are skipped if SkipDuplicates=true
    /// - System profiles can be overwritten if AllowSystemOverride=true
    /// - Each error is logged with specific failure reason
    ///
    /// Only farm_admin users can call this endpoint. All bulk import operations are logged
    /// for audit trails and system monitoring.
    /// </remarks>
    /// <response code="200">Bulk import completed (may include duplicates or errors in response)</response>
    /// <response code="400">Bad request - missing or empty profileIds list, invalid request structure</response>
    /// <response code="401">Unauthorized - authentication required</response>
    /// <response code="403">Forbidden - farm_admin authorization policy required</response>
    /// <response code="404">Printer with specified ID not found in database</response>
    /// <response code="500">Server error during bulk import processing</response>
    [HttpPost("bulk-import-for-printer/{printerId}")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
    [ProducesResponseType(typeof(BulkProfileImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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
            _logger.LogWarning($"Bulk import validation failed: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning($"Printer not found: {ex.Message}");
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
    /// This endpoint is used by the Profile Import Wizard to import user-selected profiles.
    /// </summary>
    /// <param name="modelId">
    /// The unique identifier (GUID) of the printer model in the catalog.
    /// The model must exist and have an OrcaSlicer alias configured.
    /// </param>
    /// <param name="request">
    /// Selective import request containing:
    /// - ManufacturerName: The manufacturer name matching the OrcaSlicer bundle (e.g., "Prusa", "Elegoo")
    /// - SelectedMachineProfiles: List of machine profile names to import
    /// - SelectedProcessProfiles: List of process profile names to import
    /// - SelectedFilamentProfiles: List of filament profile names to import
    /// </param>
    /// <param name="ct">Cancellation token for aborting the import operation</param>
    /// <returns>
    /// Returns SelectiveProfileImportResultDto containing:
    /// - MachineProfilesImported: Number of machine profiles imported
    /// - ProcessProfilesImported: Number of process profiles imported
    /// - FilamentProfilesImported: Number of filament profiles imported
    /// - TotalImported: Sum of all imported profiles
    /// - Skipped: Number of profiles skipped (duplicates)
    /// - Error: Error message if import failed, null otherwise
    /// </returns>
    /// <remarks>
    /// This endpoint implements the Profile Import Wizard workflow:
    /// 1. User creates a printer and gets a task to import profiles
    /// 2. User navigates to Profile Import Wizard
    /// 3. Wizard fetches available profiles from OrcaSlicer worker
    /// 4. User selects which profiles to import
    /// 5. This endpoint is called to persist the selected profiles
    /// 6. Task is marked complete and wizard navigates back to dashboard
    ///
    /// Import behavior:
    /// - Profiles are fetched from OrcaSlicer worker on demand
    /// - Deduplication is performed by profile hash (SHA256)
    /// - Imported profiles are marked as system profiles (IsSystem=true, IsPublic=true)
    /// - Profiles are associated with the specified printer model
    ///
    /// Error handling:
    /// - Returns 404 if printer model not found
    /// - Returns 503 if OrcaSlicer worker unavailable
    /// - Returns partial results if some profiles fail to import
    /// </remarks>
    /// <response code="200">Import completed (check TotalImported and Error for details)</response>
    /// <response code="400">Bad request - missing manufacturer name or empty profile lists</response>
    /// <response code="401">Unauthorized - authentication required</response>
    /// <response code="403">Forbidden - farm_admin authorization policy required</response>
    /// <response code="404">Printer model not found</response>
    /// <response code="503">OrcaSlicer worker unavailable</response>
    [HttpPost("import-selected-for-model/{modelId:guid}")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(SelectiveProfileImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
            // Validate that the model exists
            PrinterModelDto? model = await _catalogService.GetModelByIdAsync(modelId, ct);
            if (model == null)
            {
                return NotFound($"Printer model with ID {modelId} not found");
            }

            _logger.LogInformation($"Importing selected profiles for model {model.Name} (manufacturer: {request.ManufacturerName})");

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
            _logger.LogWarning($"OrcaSlicer worker unavailable: {ex.Message}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "OrcaSlicer worker unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error importing profiles for model {modelId}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Profile import failed");
        }
    }

    /// <summary>
    /// Clone process profiles from a template machine to a custom printer instance.
    /// Allows users to create custom printers (e.g., "Prusa CORE One L - Custom") using profiles
    /// from a similar machine (e.g., "Prusa CORE One 0.4 nozzle") as a starting point for customization.
    /// </summary>
    /// <param name="request">
    /// Clone request containing:
    /// - SourceMachineProfileId: The ID of the machine profile to clone from (e.g., template)
    /// - TargetPrinterId: The ID of the custom printer to apply cloned profiles to
    /// - CustomMachineName: Optional custom name for the cloned machine variant
    /// </param>
    /// <param name="ct">Cancellation token for aborting the clone operation</param>
    /// <returns>
    /// Returns CloneProfilesResponseDto containing:
    /// - ClonedCount: Number of profiles successfully cloned
    /// - SkippedCount: Number of profiles skipped (already exist)
    /// - ErrorCount: Number of profiles that failed cloning
    /// - TotalCount: Total profiles processed
    /// - Message: Human-readable summary of the operation
    /// </returns>
    /// <remarks>
    /// This endpoint enables custom printer profile management:
    /// - User has a custom printer variant not in the standard system profiles
    /// - User selects a similar "template" machine from system profiles
    /// - System clones all profiles from that machine for the custom printer
    /// - User can then customize individual profiles as needed
    ///
    /// Clone operation characteristics:
    /// - Only process profiles are cloned (compatible with the source machine)
    /// - Cloned profiles are marked as user-owned (IsSystem=false, IsPublic=false)
    /// - Each cloned profile generates a new ID and is independent
    /// - Machine compatibility is updated to reference the custom printer
    /// - Original profiles are never modified; clones are completely independent
    /// - Deduplication prevents re-cloning if profiles already exist
    ///
    /// Use cases:
    /// - Custom printer variants: User has "Prusa CORE One L" but system only has "Prusa CORE One"
    /// - Extended configurations: User clones profiles and modifies them for specific use cases
    /// - Backup/restore: Clone profiles to create backups for specific printer instances
    ///
    /// Only farm_admin users can call this endpoint. Clone operations are logged for audit trails.
    /// </remarks>
    /// <response code="200">Profiles cloned successfully (may include errors in response)</response>
    /// <response code="400">Bad request - invalid request body structure, missing required fields, or validation failure</response>
    /// <response code="401">Unauthorized - authentication required</response>
    /// <response code="403">Forbidden - farm_admin authorization policy required</response>
    /// <response code="404">Source machine profile or target printer not found</response>
    /// <response code="500">Server error during clone operation</response>
    [HttpPost("clone-from-template")]
    [Authorize(Policy = "farm_admin")]
    [ProducesResponseType(typeof(CloneProfilesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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
            _logger.LogWarning($"Clone validation failed: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning($"Machine profile or printer not found: {ex.Message}");
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clone failed");
            return BadRequest($"Clone failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Bulk import profiles directly from the OrcaSlicer worker without pre-seeding to database.
    /// This is the primary/recommended profile import workflow:
    /// 1. Fetch profiles from worker using GetAvailableProfilesFromWorkerAsync
    /// 2. User selects which profiles to import
    /// 3. Call this endpoint to import selected profiles directly from worker
    ///
    /// Profiles are created as user-owned (IsSystem=false) in the database.
    /// </summary>
    /// <param name="printerId">
    /// The unique identifier (GUID) of the registered printer instance.
    /// Profiles will be associated with this printer for compatibility checking.
    /// The printer must be previously registered in the database.
    /// </param>
    /// <param name="request">
    /// Request containing:
    /// - Profiles: Array of ProcessProfileDto objects from the worker to import
    /// - SkipDuplicates: If true (default), skip profiles that already exist in database by hash
    /// - AllowSystemOverride: If true, allow overwriting existing system profiles with same name
    ///
    /// Each profile in the Profiles array should contain:
    /// - Name: Profile name
    /// - SlicerType: Slicer application type (OrcaSlicer)
    /// - Quality: Quality level (Fast, Standard, Quality)
    /// - RawJson: Full OrcaSlicer configuration JSON
    /// - And other profile configuration fields
    /// </param>
    /// <param name="ct">Cancellation token for aborting the bulk import operation</param>
    /// <returns>
    /// Returns BulkImportFromWorkerResultDto containing:
    /// - ImportedCount: Number of profiles newly created in database
    /// - DuplicateCount: Number of profiles skipped (already in database by hash)
    /// - ErrorCount: Number of profiles that failed import
    /// - TotalCount: Total profiles processed (Imported + Duplicate + Error)
    /// </returns>
    /// <remarks>
    /// This is the PRIMARY profile import workflow for most use cases:
    ///
    /// Workflow steps:
    /// 1. Admin calls GetAvailableProfilesFromWorkerAsync to fetch available profiles
    /// 2. User reviews and selects profiles in the UI
    /// 3. Frontend calls this endpoint with selected profiles from step 1
    /// 4. Service imports selected profiles directly to database
    /// 5. Profiles are immediately available for slicing jobs
    ///
    /// Advantages of this workflow vs. seed + import:
    /// - Direct import is faster (no intermediate storage in database)
    /// - User selects only profiles they need (no need to pre-seed all)
    /// - Worker profiles are always fresh (not cached in database)
    /// - Simpler workflow for most administrators
    ///
    /// Import operation characteristics:
    /// - Profiles are imported from worker (network I/O required)
    /// - Each profile is validated and hashed before import
    /// - Deduplication prevents duplicate storage (same hash = skip)
    /// - Profiles are stored as user-owned (IsSystem=false, IsPublic=false)
    /// - Profiles are immediately searchable and usable in slicing jobs
    /// - Full operation is logged for audit trails
    ///
    /// Error handling:
    /// - Invalid profiles are tracked separately (error count)
    /// - Duplicate profiles are skipped if SkipDuplicates=true
    /// - Each error is logged with specific failure reason
    /// - Operation returns partial results if errors occur
    /// - Failed profiles are reported but don't stop the import process
    ///
    /// Security:
    /// - Only farm_admin users can call this endpoint
    /// - Imported profiles are validated against slicer type compatibility
    /// - Printer association ensures profiles are scoped appropriately
    /// - All operations are logged for audit trails
    ///
    /// Performance considerations:
    /// - Bulk import is optimized for importing 10-100+ profiles efficiently
    /// - Network latency is minimized by importing all profiles in one request
    /// - Database writes are batched for performance
    /// - Deduplication check is performed using fast hash comparison
    /// </remarks>
    /// <response code="200">Bulk import completed (may include duplicates or errors in response data)</response>
    /// <response code="400">Bad request - missing profiles list, invalid request structure, or validation failure</response>
    /// <response code="401">Unauthorized - authentication required</response>
    /// <response code="403">Forbidden - farm_admin authorization policy required</response>
    /// <response code="404">Printer with specified ID not found in database</response>
    /// <response code="500">Server error during bulk import processing</response>
    [HttpPost("bulk-import-from-worker/{printerId}")]
    [Authorize(Policy = "farm_admin")] // Admin-only: profile import
    [ProducesResponseType(typeof(BulkImportFromWorkerResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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
            _logger.LogWarning($"Bulk import validation failed: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning($"Printer not found: {ex.Message}");
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
    /// <param name="request">Clone request with source profile ID, type, and optional custom name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Details of the cloned profile</returns>
    /// <remarks>
    /// Creates a new profile with IsSystem=false and CreatedByUserId set to the current user.
    /// The cloned profile copies all settings from the source but gets a new ID.
    /// Supported profile types: "machine", "filament", "process".
    /// </remarks>
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
            // Get user ID from claims (assumes authentication is configured)
            Guid userId = GetCurrentUserId();

            CloneSingleProfileResponseDto result = await _profilesService.CloneSingleProfileAsync(request, userId, ct);
            return CreatedAtAction(nameof(GetProfileAsync), new { id = result.Id }, result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning($"Clone profile validation failed: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning($"Source profile not found: {ex.Message}");
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
    /// <param name="request">Upload request with raw JSON, profile type, and optional name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Details of the uploaded custom profile</returns>
    /// <remarks>
    /// Creates a new profile with IsSystem=false and CreatedByUserId set to the current user.
    /// Supported profile types: "machine", "filament", "process".
    /// The raw JSON should be a valid OrcaSlicer profile configuration.
    /// </remarks>
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
            return CreatedAtAction(nameof(GetProfileAsync), new { id = result.Id }, result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning($"Upload profile validation failed: {ex.Message}");
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
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of custom profiles with summary counts</returns>
    /// <remarks>
    /// Returns only profiles where IsSystem=false and CreatedByUserId matches the current user.
    /// Includes counts broken down by profile type (machine, filament, process).
    /// </remarks>
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
    /// Updates a custom profile's properties.
    /// </summary>
    /// <param name="id">ID of the custom profile to update</param>
    /// <param name="request">Update request with optional new name, rawJson, or description</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Updated custom profile details</returns>
    /// <remarks>
    /// Only non-null fields in the request will be updated.
    /// Cannot update system profiles - clone them first to create a custom version.
    /// Only the profile owner can update their custom profiles.
    /// </remarks>
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
            _logger.LogWarning($"Custom profile not found: {ex.Message}");
            return NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning($"Update profile unauthorized: {ex.Message}");
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning($"Update profile invalid operation: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update custom profile failed");
            return StatusCode(StatusCodes.Status500InternalServerError, "Update custom profile failed");
        }
    }

    /// <summary>
    /// Gets the current user's ID from the authentication claims.
    /// </summary>
    /// <returns>The user's GUID</returns>
    private Guid GetCurrentUserId()
    {
        // Try to get user ID from claims
        string? userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out Guid userId))
        {
            return userId;
        }

        // Fallback for development/testing - use a default user ID
        // In production, this should throw an exception
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

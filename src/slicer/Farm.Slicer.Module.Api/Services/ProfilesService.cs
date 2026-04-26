using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Service for managing slicer profiles with consolidated mapping, validation, and orchestration logic.
/// Implements IProfilesService to handle CRUD operations for process, machine, and filament profiles
/// across multiple slicer types (OrcaSlicer, PrusaSlicer, SuperSlicer, etc.) with proper error handling,
/// deduplication, and external worker integration.
/// </summary>
/// <remarks>
/// This service is responsible for:
/// - Profile import/export with hash-based deduplication
/// - Hierarchical profile organization by manufacturer and machine model
/// - System profile seeding and reseeding from OrcaSlicer worker
/// - Bulk import operations (from database or worker)
/// - Profile cloning for custom printer configurations
/// - Default profile management per slicer type
/// - Worker HTTP communication abstraction
/// - Profile metadata parsing and validation
/// - Database transaction coordination
///
/// All operations maintain data integrity through validation, error handling,
/// and proper logging. External worker communication uses HttpClient with
/// error handling for offline scenarios.
/// </remarks>
/// <remarks>
/// Initializes a new instance of the ProfilesService with required dependencies.
/// </remarks>
/// <param name="repo">Repository for SlicerProfile CRUD operations</param>
/// <param name="logger">Unified logging service for diagnostic and error logs</param>
/// <param name="processProfileRepo">Repository for process profile operations</param>
/// <param name="machineProfileRepo">Repository for machine profile operations</param>
/// <param name="filamentProfileRepo">Repository for filament profile operations</param>
/// <param name="unitOfWork">Unit of work for coordinating database transactions</param>
/// <param name="catalogService">Service for manufacturer and printer model catalog lookups</param>
/// <param name="parsingService">Service for parsing and validating raw profile JSON</param>
/// <param name="slicerHubContext">SignalR hub context used to publish slicer-related notifications</param>
/// <param name="slicersService">Service for querying registered slicer workers</param>
/// <exception cref="ArgumentNullException">Thrown if any required dependency is null</exception>
public class ProfilesService(
    IProfilesRepository repo,
    ILogger<ProfilesService> logger,
    IProcessProfileRepository processProfileRepo,
    IMachineProfileRepository machineProfileRepo,
    IFilamentProfileRepository filamentProfileRepo,
    IUnitOfWork unitOfWork,
    ICatalogService catalogService,
    IProfileParsingService parsingService,
    IHubContext<SlicerHub> slicerHubContext,
    ISlicersService slicersService) : IProfilesService
{
    private readonly IProfilesRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    private readonly ILogger<ProfilesService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IHubContext<SlicerHub> _slicerHubContext = slicerHubContext ?? throw new ArgumentNullException(nameof(slicerHubContext));

    private readonly IProcessProfileRepository _processProfileRepo = processProfileRepo ?? throw new ArgumentNullException(nameof(processProfileRepo));
    private readonly IMachineProfileRepository _machineProfileRepo = machineProfileRepo ?? throw new ArgumentNullException(nameof(machineProfileRepo));
    private readonly IFilamentProfileRepository _filamentProfileRepo = filamentProfileRepo ?? throw new ArgumentNullException(nameof(filamentProfileRepo));
    private readonly IUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly ICatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    private readonly ISlicersService _slicersService = slicersService ?? throw new ArgumentNullException(nameof(slicersService));
    private readonly IProfileParsingService _parsingService = parsingService ?? throw new ArgumentNullException(nameof(parsingService));

    /// <summary>
    /// Imports a process profile from raw slicer configuration JSON with deduplication and validation.
    /// </summary>
    /// <param name="req">The import request containing raw profile JSON, slicer type, and optional metadata</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A tuple containing:
    /// - dto: The imported ProcessProfileExtendedDto with full profile details and metadata
    /// - created: True if the profile is new; false if an existing profile was updated
    /// </returns>
    /// <remarks>
    /// This method performs comprehensive profile management:
    /// - Parses and validates raw JSON profile configuration
    /// - Extracts metadata (layer height, infill percentage, material type, quality)
    /// - Generates content hash for deduplication detection
    /// - Checks for existing profiles with same hash to prevent duplicates
    /// - Supports optional system profile override by administrators
    /// - Returns 201 Created for new profiles, 200 OK for updated existing profiles
    ///
    /// The method is idempotent: importing the same profile multiple times will update the existing one
    /// rather than creating duplicates (based on content hash).
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if request is null</exception>
    /// <exception cref="ArgumentException">Thrown if rawJson is missing or slicerType is invalid</exception>
    public async Task<(ProcessProfileExtendedDto Dto, bool Created)> ImportProfileAsync(ImportProcessProfileDto req, CancellationToken ct)
    {
        _logger.LogInformation("[ImportProfileAsync] Starting profile import with name: {ReqName}, slicerType: {ReqSlicerType}, allowSystemOverride: {ReqAllowSystemOverride}", req.Name, req.SlicerType, req.AllowSystemOverride);

        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.RawJson))
        {
            _logger.LogError("[ImportProfileAsync] Failed: rawJson is required");
            throw new ArgumentException("rawJson is required", nameof(req));
        }

        if (string.IsNullOrWhiteSpace(req.SlicerType) || !Enum.TryParse(req.SlicerType, true, out SlicerType slicerType))
        {
            _logger.LogError("[ImportProfileAsync] Failed: Invalid slicerType '{ReqSlicerType}'", req.SlicerType);
            throw new ArgumentException("Invalid slicerType", nameof(req));
        }

        (string? sanitizedRaw, string? settingsJson, string? hash) = _parsingService.ParseAndPrepare(req.RawJson);
        int settingsLength = settingsJson?.Length ?? 0;
        _logger.LogDebug("[ImportProfileAsync] Profile parsed successfully. Hash: {Hash}, SettingsJson length: {SettingsLength}", hash, settingsLength);

        // Attempt to derive basic fields from metadata
        double layerHeight = 0.2;
        int infillPct = 20;
        string material = "PLA";
        string quality = "Standard";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(settingsJson ?? "{}");
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("layerHeight", out JsonElement lh) && lh.TryGetDouble(out double lhVal))
            {
                layerHeight = lhVal;
            }

            if (root.TryGetProperty("infillPercentage", out JsonElement inf) && inf.TryGetInt32(out int infVal))
            {
                infillPct = infVal;
            }

            if (root.TryGetProperty("material", out JsonElement mat) && mat.ValueKind == JsonValueKind.String)
            {
                material = mat.GetString() ?? material;
            }

            if (root.TryGetProperty("quality", out JsonElement q) && q.ValueKind == JsonValueKind.String)
            {
                quality = q.GetString() ?? quality;
            }

            _logger.LogDebug("[ImportProfileAsync] Metadata extracted: layerHeight={LayerHeight}, infillPct={InfillPct}, material={Material}, quality={Quality}", layerHeight, infillPct, material, quality);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[ImportProfileAsync] Failed to extract metadata: {ExMessage}. Using defaults.", ex.Message);
        }

        // Map quality
        ProfileQuality qualEnum = Enum.TryParse(quality, true, out ProfileQuality qParsed) ? qParsed : ProfileQuality.Standard;

        ProcessProfile imported = new ProcessProfile
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(req.Name) ? $"{material} - {qualEnum} ({layerHeight}mm)" : req.Name.Trim(),
            Description = req.Description,
            SlicerType = slicerType,
            RawJson = sanitizedRaw,
            SettingsJson = settingsJson,
            Hash = hash,
            IsSystem = false,
            IsDefault = req.SetDefault,
            IsPublic = req.IsPublic,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _logger.LogDebug("[ImportProfileAsync] Attempting to persist profile: {ImportedName} (ID: {ImportedId})", imported.Name, imported.Id);
        ProcessProfile saved = await _processProfileRepo.AddOrUpdateFromImportAsync(imported, allowSystemOverride: req.AllowSystemOverride, ct);
        bool created = saved.Id == imported.Id;

        if (created)
        {
            _logger.LogInformation("[ImportProfileAsync] New profile created successfully: {SavedName} (ID: {SavedId})", saved.Name, saved.Id);
        }
        else
        {
            _logger.LogInformation("[ImportProfileAsync] Existing profile updated: {SavedName} (ID: {SavedId})", saved.Name, saved.Id);
        }

        Dictionary<string, object?> metadata = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(saved.SettingsJson))
        {
            try
            {
                Dictionary<string, object?>? parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(saved.SettingsJson);
                if (parsed != null)
                {
                    foreach ((string key, object? value) in parsed)
                    {
                        metadata[key] = value;
                    }
                }
            }
            catch
            {
            }
        }

        ProcessProfileExtendedDto dto = new ProcessProfileExtendedDto
        {
            Id = saved.Id,
            Name = saved.Name,
            Description = saved.Description,
            SlicerType = saved.SlicerType.ToString(),
            LayerHeight = saved.LayerHeight,
            InfillPercentage = saved.InfillPercentage,
            PrintSpeed = saved.PrintSpeed,
            EnableSupports = saved.EnableSupports,
            Quality = saved.Quality.ToString(),
            IsDefault = saved.IsDefault,
            IsPublic = saved.IsPublic,
            IsSystem = saved.IsSystem,
            Hash = saved.Hash ?? string.Empty,
            CreatedAt = saved.CreatedAt,
            UpdatedAt = saved.UpdatedAt,
            Metadata = metadata
        };

        return (dto, created);
    }

    /// <summary>
    /// Exports the raw slicer configuration JSON for a stored profile with full metadata.
    /// </summary>
    /// <param name="id">The unique identifier of the process profile to export</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A ProcessProfileExportDto containing the raw JSON and all metadata fields for reimport,
    /// or null if the profile does not exist.
    /// </returns>
    /// <remarks>
    /// This method retrieves a profile and returns its complete configuration including:
    /// - Raw slicer JSON for reimport to other farm instances
    /// - Extracted metadata (layer height, infill, material, quality)
    /// - Profile creation timestamp and version information
    /// - Hash for integrity verification
    ///
    /// The exported profile can be reimported into another PrintFarmer instance using ImportProfileAsync.
    /// Exports include all data necessary to recreate the profile in another installation.
    /// </remarks>
    public async Task<ProcessProfileExportDto?> ExportProfileAsync(Guid id, CancellationToken ct)
    {
        _logger.LogInformation("[ExportProfileAsync] Exporting profile with ID: {Id}", id);
        ProcessProfile? profile = await _processProfileRepo.GetByIdAsync(id, ct);
        if (profile is null)
        {
            _logger.LogWarning("[ExportProfileAsync] Profile not found for export with ID: {Id}", id);
            return null;
        }

        _logger.LogDebug("[ExportProfileAsync] Found profile: {ProfileName}, parsing settings...", profile.Name);
        Dictionary<string, object?> settingsDict = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(profile.SettingsJson ?? "{}");
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                settingsDict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : (prop.Value.TryGetDouble(out double d) ? d : null),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }

            string keysJoined = string.Join(", ", settingsDict.Keys);
            _logger.LogDebug("[ExportProfileAsync] Settings parsed successfully. Keys: {Keys}", keysJoined);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[ExportProfileAsync] Failed to parse metadata: {ExMessage}", ex.Message);
        }

        _logger.LogInformation("[ExportProfileAsync] Successfully exported profile: {ProfileName}", profile.Name);
        return new ProcessProfileExportDto
        {
            Id = profile.Id,
            Name = profile.Name,
            SlicerType = profile.SlicerType.ToString(),
            Hash = profile.Hash ?? string.Empty,
            RawJson = profile.RawJson ?? string.Empty,
            Metadata = settingsDict
        };
    }

    /// <summary>
    /// Sets a process profile as the default for system-wide usage in slicing jobs.
    /// </summary>
    /// <param name="id">The unique identifier of the profile to set as default</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>True if the operation was successful; false if the profile does not exist</returns>
    /// <remarks>
    /// This method marks a profile as the default choice for new slicing jobs.
    /// When no specific profile is selected, the default profile is automatically used.
    /// Only one profile per slicer type can be marked as default at a time.
    ///
    /// Setting a new default automatically unsets the previous default for the same slicer type.
    /// Default profile changes are logged for audit trails and change tracking.
    /// </remarks>
    public async Task<bool> SetDefaultProfileAsync(Guid id, CancellationToken ct)
    {
        _logger.LogInformation("[SetDefaultProfileAsync] Setting default profile with ID: {Id}", id);
        ProcessProfile? profile = await _processProfileRepo.GetByIdAsync(id, ct);
        if (profile is null)
        {
            _logger.LogError("[SetDefaultProfileAsync] Profile not found with ID: {Id}", id);
            return false;
        }

        _logger.LogDebug("[SetDefaultProfileAsync] Found profile: {ProfileName}, SlicerType: {ProfileSlicerType}", profile.Name, profile.SlicerType);
        await _processProfileRepo.SetDefaultAsync(profile, profile.CreatedByUserId, ct);
        _logger.LogInformation("[SetDefaultProfileAsync] Successfully set default profile: {ProfileName}", profile.Name);
        return true;
    }

    /// <summary>
    /// Retrieves all available profiles organized by type with full extended details.
    /// </summary>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// An ExtendedProfilesResponseDto containing:
    /// - ProcessProfiles: All process/quality profiles grouped by manufacturer
    /// - FilamentProfiles: All filament/material profiles grouped by manufacturer
    /// - MachineProfiles: All machine/hardware profiles grouped by manufacturer
    /// </returns>
    /// <remarks>
    /// This method provides the most comprehensive view of available profiles, organized by type.
    /// It includes both system-provided and user-created profiles, with manufacturer-based grouping.
    ///
    /// This is primarily used for UI components that need to display all available profile options
    /// organized by category and manufacturer.
    /// </remarks>
    public async Task<ExtendedProfilesResponseDto> ListExtendedAsync(CancellationToken ct)
    {
        _logger.LogInformation("[ListExtendedAsync] Retrieving all extended profiles");
        List<ProcessProfileListItemDto> processProfiles = new();
        List<FilamentProfileListItemDto> filamentProfiles = new();
        List<MachineProfileListItemDto> machineProfiles = new();

        IReadOnlyList<ProcessProfile> processProfileEntities = await _processProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
        _logger.LogDebug("[ListExtendedAsync] Found {ProcessProfileEntitiesCount} process profiles for OrcaSlicer", processProfileEntities.Count);
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

        return new ExtendedProfilesResponseDto
        {
            ProcessProfiles = processProfiles,
            FilamentProfiles = filamentProfiles,
            MachineProfiles = machineProfiles
        };
    }

    /// <summary>
    /// Retrieves profiles organized in a hierarchical structure by manufacturer and machine model.
    /// </summary>
    /// <param name="manufacturer">Optional filter to retrieve only profiles for a specific manufacturer</param>
    /// <param name="machineProfileId">Optional filter to retrieve only profiles compatible with a specific machine</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A HierarchicalProfilesResponseDto containing profiles organized as:
    /// Manufacturer → Machine Model → (Machine Profile + Compatible Filament/Process Profiles)
    /// </returns>
    /// <remarks>
    /// This method provides a hierarchical view of profiles that reflects the real-world organization:
    /// - Top level: Manufacturer (e.g., "Prusa", "MK4")
    /// - Second level: Model (e.g., "Prusa CORE One", "Prusa Mk4S")
    /// - Third level: Individual profiles with compatibility information
    ///
    /// Both filters are optional and work together with AND logic:
    /// - If manufacturer is specified: Returns only that manufacturer's profiles
    /// - If machineProfileId is specified: Returns only compatible profiles for that machine
    /// - If both are specified: Both filters apply
    /// - If neither is specified: Returns all profiles in hierarchy
    /// </remarks>
    public async Task<HierarchicalProfilesResponseDto> ListHierarchyAsync(string? manufacturer, Guid? machineProfileId, CancellationToken ct)
    {
        string? manufacturerFilter = string.IsNullOrWhiteSpace(manufacturer) ? null : manufacturer.Trim();

        // Return all profiles (system + custom) from database for browsing
        IReadOnlyList<MachineProfile> machineProfilesAll = await _machineProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);

        MachineProfile? selectedMachine = null;
        if (machineProfileId.HasValue)
        {
            selectedMachine = machineProfilesAll.FirstOrDefault(m => m.Id == machineProfileId.Value)
                ?? await _machineProfileRepo.GetByIdAsync(machineProfileId.Value, ct);

            if (selectedMachine is null)
            {
                throw new KeyNotFoundException($"Machine profile {machineProfileId.Value} not found");
            }

            if (string.IsNullOrWhiteSpace(manufacturerFilter))
            {
                manufacturerFilter = selectedMachine.Manufacturer;
            }
            else if (!string.Equals(selectedMachine.Manufacturer, manufacturerFilter, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("machineProfileId does not belong to the specified manufacturer");
            }
        }

        IEnumerable<MachineProfile> machineProfilesFiltered = machineProfilesAll;
        if (!string.IsNullOrWhiteSpace(manufacturerFilter))
        {
            machineProfilesFiltered = machineProfilesFiltered.Where(m => string.Equals(m.Manufacturer, manufacturerFilter, StringComparison.OrdinalIgnoreCase));
        }

        Guid? modelFilter = selectedMachine?.PrinterModelId;
        if (modelFilter.HasValue)
        {
            machineProfilesFiltered = machineProfilesFiltered.Where(m => m.PrinterModelId == modelFilter);
        }

        List<MachineProfile> machineProfiles = machineProfilesFiltered
            .Where(m => m.PrinterModelId.HasValue)
            .OrderBy(m => m.Manufacturer)
            .ThenBy(m => m.Name)
            .ToList();

        // Return all profiles (system + custom) from database for browsing
        IReadOnlyList<ProcessProfile> processProfilesAll = await _processProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
        IEnumerable<ProcessProfile> processProfilesFiltered = processProfilesAll;

        // If a specific machine profile is selected, filter by CompatiblePrinters field
        // This ensures "Qidi X-Plus 4 0.4 nozzle" only shows profiles with compatible_printers containing that exact machine name
        // Profiles without CompatiblePrinters are excluded - only show explicitly compatible profiles
        if (selectedMachine != null && !string.IsNullOrWhiteSpace(selectedMachine.Name))
        {
            processProfilesFiltered = processProfilesFiltered.Where(p =>
                !string.IsNullOrWhiteSpace(p.CompatiblePrinters) &&
                p.CompatiblePrinters.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Any(cp => string.Equals(cp.Trim(), selectedMachine.Name, StringComparison.OrdinalIgnoreCase)));
        }
        else if (modelFilter.HasValue)
        {
            processProfilesFiltered = processProfilesFiltered.Where(p => p.PrinterModelId == modelFilter || p.PrinterModelId == null);
        }
        else if (machineProfiles.Count > 0)
        {
            HashSet<Guid> modelIds = machineProfiles.Select(m => m.PrinterModelId!.Value).ToHashSet();
            processProfilesFiltered = processProfilesFiltered.Where(p => p.PrinterModelId == null || (p.PrinterModelId.HasValue && modelIds.Contains(p.PrinterModelId.Value)));
        }

        List<ProcessProfile> processProfiles = processProfilesFiltered.OrderBy(p => p.Name).ToList();

        // Return all profiles (system + custom) from database for browsing
        IReadOnlyList<FilamentProfile> filamentProfilesAll = await _filamentProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
        IEnumerable<FilamentProfile> filamentProfilesFiltered = filamentProfilesAll;

        // Filter filaments by CompatiblePrinters if a specific machine is selected
        // Profiles without CompatiblePrinters are excluded - only show explicitly compatible profiles
        if (selectedMachine != null && !string.IsNullOrWhiteSpace(selectedMachine.Name))
        {
            filamentProfilesFiltered = filamentProfilesFiltered.Where(f =>
                !string.IsNullOrWhiteSpace(f.CompatiblePrinters) &&
                f.CompatiblePrinters.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Any(cp => string.Equals(cp.Trim(), selectedMachine.Name, StringComparison.OrdinalIgnoreCase)));
        }
        else if (!string.IsNullOrWhiteSpace(manufacturerFilter))
        {
            filamentProfilesFiltered = filamentProfilesFiltered.Where(f => string.Equals(f.Manufacturer, manufacturerFilter, StringComparison.OrdinalIgnoreCase));
        }

        List<FilamentProfile> filamentProfiles = filamentProfilesFiltered
            .OrderBy(f => f.Material)
            .ThenBy(f => f.Name)
            .ToList();

        // Catalog lookups for model display names
        Dictionary<Guid, string> manufacturerNameById = new();
        Dictionary<Guid, Guid> manufacturerIdByModelId = new();
        Dictionary<Guid, string> modelNameById = new();

        try
        {
            (IReadOnlyList<ManufacturerDto> list, _) = await _catalogService.GetManufacturersAsync(ct);
            foreach (ManufacturerDto m in list)
            {
                manufacturerNameById[m.Id] = m.Name;
            }

            if (!string.IsNullOrWhiteSpace(manufacturerFilter))
            {
                ManufacturerDto? match = list.FirstOrDefault(m => string.Equals(m.Name, manufacturerFilter, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    (IReadOnlyList<PrinterModelDto> models, _) = await _catalogService.GetModelsAsync(match.Id, ct);
                    foreach (PrinterModelDto model in models)
                    {
                        modelNameById[model.Id] = model.Name;
                        manufacturerIdByModelId[model.Id] = match.Id;
                    }
                }
            }
            else
            {
                HashSet<Guid> neededModelIds = machineProfiles.Select(m => m.PrinterModelId!.Value).ToHashSet();
                foreach (Guid modelId in neededModelIds)
                {
                    PrinterModelDto? model = await _catalogService.GetModelByIdAsync(modelId, ct);
                    if (model != null)
                    {
                        modelNameById[model.Id] = model.Name;
                        manufacturerIdByModelId[model.Id] = model.ManufacturerId;
                    }
                }
            }
        }
        catch
        {
        }

        HierarchicalProfilesResponseDto response = new();

        List<MachineProfileListItemDto> machineDtos = machineProfiles.Select(p => new MachineProfileListItemDto
        {
            Id = p.Id,
            Name = p.Name,
            SlicerType = p.SlicerType.ToString(),
            Manufacturer = p.Manufacturer ?? string.Empty,
            IsDefault = p.IsDefault,
            IsSystem = p.IsSystem,
            IsPublic = p.IsPublic,
            Hash = p.Hash ?? string.Empty
        }).ToList();

        List<ProcessProfileListItemDto> processDtos = processProfiles.Select(p => new ProcessProfileListItemDto
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
        }).ToList();

        List<FilamentProfileListItemDto> filamentDtos = filamentProfiles.Select(p => new FilamentProfileListItemDto
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
        }).ToList();

        Dictionary<Guid, List<MachineProfileListItemDto>> machinesByModelId = new();
        foreach (MachineProfileListItemDto m in machineDtos)
        {
            MachineProfile? entity = machineProfiles.FirstOrDefault(x => x.Id == m.Id);
            if (entity?.PrinterModelId is not Guid pmid)
            {
                continue;
            }

            if (!machinesByModelId.TryGetValue(pmid, out List<MachineProfileListItemDto>? list))
            {
                list = [];
                machinesByModelId[pmid] = list;
            }

            list.Add(m);
        }

        foreach ((Guid modelId, List<MachineProfileListItemDto> modelMachines) in machinesByModelId)
        {
            string manufacturerName = manufacturerFilter ?? modelMachines[0].Manufacturer;
            if (string.IsNullOrWhiteSpace(manufacturerName) && manufacturerIdByModelId.TryGetValue(modelId, out Guid mid) && manufacturerNameById.TryGetValue(mid, out string? mName))
            {
                if (!string.IsNullOrWhiteSpace(mName))
                {
                    manufacturerName = mName;
                }
            }

            if (string.IsNullOrWhiteSpace(manufacturerName))
            {
                manufacturerName = "Unknown";
            }

            if (!response.ByHierarchy.TryGetValue(manufacturerName, out HierarchicalManufacturerProfilesDto? mfgDto))
            {
                mfgDto = new HierarchicalManufacturerProfilesDto
                {
                    Name = manufacturerName,
                    Models = new Dictionary<string, HierarchicalPrinterModelProfilesDto>()
                };
                response.ByHierarchy[manufacturerName] = mfgDto;
            }

            string modelName = modelNameById.TryGetValue(modelId, out string? n) ? n : modelMachines[0].Name;

            List<ProcessProfileListItemDto> modelProcesses = processDtos
                .Where(p =>
                {
                    ProcessProfile? ent = processProfiles.FirstOrDefault(x => x.Id == p.Id);
#pragma warning disable S2589 // Unnecessary check is valid here for logical OR conditions
                    return ent?.PrinterModelId == null || ent?.PrinterModelId == modelId;
#pragma warning restore S2589
                })
                .ToList();

            List<FilamentProfileListItemDto> modelFilaments = filamentDtos;

            string modelKey = modelId.ToString();
            mfgDto.Models[modelKey] = new HierarchicalPrinterModelProfilesDto
            {
                Name = modelName,
                ModelId = modelKey,
                MachineProfiles = modelMachines,
                ProcessProfiles = modelProcesses,
                FilamentProfiles = modelFilaments
            };
        }

        foreach (IGrouping<string, MachineProfileListItemDto> g in machineDtos.GroupBy(m => m.Manufacturer ?? string.Empty))
        {
            string key = string.IsNullOrWhiteSpace(g.Key) ? "Unknown" : g.Key;
            response.MachineProfiles[key] = g.ToList();
        }

        foreach (IGrouping<string, FilamentProfileListItemDto> g in filamentDtos.GroupBy(_ => manufacturerFilter ?? "All"))
        {
            response.FilamentProfiles[g.Key] = g.ToList();
        }

        foreach (IGrouping<string, ProcessProfileListItemDto> g in processDtos.GroupBy(_ => manufacturerFilter ?? "All"))
        {
            response.ProcessProfiles[g.Key] = g.ToList();
        }

        return response;
    }

    /// <summary>
    /// Retrieves all system-provided OrcaSlicer profiles from the database.
    /// </summary>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A read-only list of SlicerProfileListItemDto containing all system OrcaSlicer profiles
    /// with their IDs, names, slicer type, and quality level.
    /// </returns>
    /// <remarks>
    /// System profiles are pre-configured profiles provided by PrintFarmer for OrcaSlicer.
    /// These are typically optimized profiles for common printer models and materials.
    ///
    /// This method is used for:
    /// - Listing available profiles to users for printer assignment
    /// - Bulk import operations
    /// - Default profile selection
    /// - Profile availability checking
    ///
    /// Results are typically cached and refreshed via SeedSystemProfilesFromWorkerAsync.
    /// </remarks>
    public async Task<IReadOnlyList<SlicerProfileListItemDto>> ListSystemOrcaProfilesAsync(CancellationToken ct)
    {
        _logger.LogInformation("[ListSystemOrcaProfilesAsync] Listing all system OrcaSlicer profiles");
        IReadOnlyList<ProcessProfile> profiles = await _processProfileRepo.GetSystemOrcaProfilesAsync(ct);
        _logger.LogDebug("[ListSystemOrcaProfilesAsync] Found {ProfilesCount} system OrcaSlicer profiles", profiles.Count);
        var result = profiles.Select(p => new SlicerProfileListItemDto
        {
            Id = p.Id,
            Name = p.Name,
            SlicerType = p.SlicerType.ToString(),
            Quality = p.Quality.ToString(),
            LayerHeight = p.LayerHeight,
            InfillPercentage = p.InfillPercentage,
            IsSystem = p.IsSystem,
            IsDefault = p.IsDefault,
            IsPublic = p.IsPublic,
            Hash = p.Hash ?? string.Empty,
        }).ToList();
        _logger.LogInformation("[ListSystemOrcaProfilesAsync] Returning {ResultCount} system profiles", result.Count);
        return result;
    }

    #region Profile Import Helpers

    /// <summary>
    /// Fetches all profiles from the OrcaSlicer worker service.
    /// </summary>
    private async Task<(AllProfilesResponseDto? Profiles, string? OrcaVersion, string WorkerUrl)> FetchProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct)
    {
        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        string? orcaVersion = await TryGetOrcaVersionAsync(httpClient, workerUrl, ct);

        HttpResponseMessage response = await httpClient.GetAsync($"{workerUrl}/api/profiles", ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}");
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return (allProfiles, orcaVersion, workerUrl);
    }

    /// <summary>
    /// Persists a machine profile to the database. Returns true if imported, false if skipped (duplicate).
    /// </summary>
    private async Task<bool> PersistMachineProfileAsync(
        MachineProfileDto machineProfile,
        string manufacturerName,
        string modelDisplayName,
        Guid? printerModelId,
        string? orcaVersion,
        bool checkDuplicates,
        CancellationToken ct)
    {
        string profileJson = JsonSerializer.Serialize(machineProfile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
        (string sanitizedRaw, string settingsJson, string profileHash) = _parsingService.ParseAndPrepare(profileJson);

        if (checkDuplicates)
        {
            MachineProfile? existingProfile = await _machineProfileRepo.GetByHashAsync(profileHash, ct);
            if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
            {
                // If the existing profile doesn't have a PrinterModelId but we have one, update it
                // This links the profile to the printer model the user is importing for
                if (printerModelId.HasValue && !existingProfile.PrinterModelId.HasValue)
                {
                    existingProfile.PrinterModelId = printerModelId;
                    existingProfile.UpdatedAt = DateTime.UtcNow;
                    await _machineProfileRepo.UpdateAsync(existingProfile, ct);
                    _logger.LogDebug("Updated existing machine profile '{ExistingProfileName}' with PrinterModelId {PrinterModelId}", existingProfile.Name, printerModelId);
                    return true; // Treated as imported since we updated the link
                }

                return false; // Skipped - already linked or no model to link
            }
        }

        MachineProfile systemProfile = new MachineProfile
        {
            Id = Guid.NewGuid(),
            Name = machineProfile.Name ?? string.Empty,
            Manufacturer = machineProfile.Manufacturer ?? manufacturerName,
            Description = $"OrcaSlicer machine profile for {modelDisplayName}",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = printerModelId,
            IsSystem = true,
            IsPublic = true,
            Hash = profileHash,
            RawJson = sanitizedRaw,
            SettingsJson = settingsJson,
            SlicerVersion = orcaVersion,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _machineProfileRepo.AddAsync(systemProfile, ct);
        return true; // Imported
    }

    /// <summary>
    /// Persists a filament profile to the database. Returns true if imported, false if skipped (duplicate).
    /// </summary>
    private async Task<bool> PersistFilamentProfileAsync(
        FilamentProfileDto filamentProfile,
        string manufacturerName,
        string modelDisplayName,
        string? orcaVersion,
        bool checkDuplicates,
        CancellationToken ct)
    {
        string profileJson = JsonSerializer.Serialize(filamentProfile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
        (string sanitizedRaw, string settingsJson, string profileHash) = _parsingService.ParseAndPrepare(profileJson);

        if (checkDuplicates)
        {
            FilamentProfile? existingProfile = await _filamentProfileRepo.GetByHashAsync(profileHash, ct);
            if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
            {
                return false; // Skipped
            }
        }

        FilamentProfile systemProfile = new FilamentProfile
        {
            Id = Guid.NewGuid(),
            Name = filamentProfile.Name ?? $"{filamentProfile.Material}",
            Material = filamentProfile.Material ?? "PLA",
            Manufacturer = filamentProfile.Manufacturer ?? manufacturerName,
            Description = $"OrcaSlicer filament profile for {modelDisplayName} - {filamentProfile.Material}",
            SlicerType = SlicerType.OrcaSlicer,
            PrintSpeed = filamentProfile.PrintSpeed,
            NozzleTemperature = filamentProfile.NozzleTemperature,
            BedTemperature = filamentProfile.BedTemperature,
            IsSystem = true,
            IsPublic = true,
            Hash = profileHash,
            RawJson = sanitizedRaw,
            SettingsJson = settingsJson,
            SlicerVersion = orcaVersion,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _filamentProfileRepo.AddAsync(systemProfile, ct);
        return true; // Imported
    }

    /// <summary>
    /// Persists a process profile to the database. Returns true if imported, false if skipped (duplicate).
    /// </summary>
    private async Task<bool> PersistProcessProfileAsync(
        ProcessProfileDto processProfile,
        string modelDisplayName,
        Guid? printerModelId,
        string? orcaVersion,
        bool checkDuplicates,
        CancellationToken ct)
    {
        string profileJson = JsonSerializer.Serialize(processProfile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
        string profileHash = ComputeSha256Hash(profileJson);

        if (checkDuplicates)
        {
            ProcessProfile? existingProfile = await _processProfileRepo.GetByHashAsync(profileHash, ct);
            if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
            {
                return false; // Skipped
            }
        }

        ProcessProfile systemProfile = new ProcessProfile
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrEmpty(processProfile.Name) ? $"{processProfile.Quality} ({processProfile.LayerHeight}mm)" : processProfile.Name,
            Description = $"OrcaSlicer process profile for {modelDisplayName}: {processProfile.Quality} quality at {processProfile.LayerHeight}mm layer height",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = printerModelId,
            Quality = Enum.TryParse(processProfile.Quality ?? "standard", true, out ProfileQuality q) ? q : ProfileQuality.Standard,
            LayerHeight = processProfile.LayerHeight,
            InfillPercentage = processProfile.InfillPercentage,
            PrintSpeed = processProfile.PrintSpeed,
            EnableSupports = processProfile.Supports,
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
        return true; // Imported
    }

    #endregion

    /// <inheritdoc />
    public async Task<SelectiveProfileImportResultDto> ImportSelectedProfilesForModelAsync(
        Guid printerModelId,
        SelectiveProfileImportRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation("[ImportSelectedProfilesForModel] Importing selected profiles for model {PrinterModelId}", printerModelId);
        _logger.LogDebug("[ImportSelectedProfilesForModel] Selected: {SelectedMachineProfilesCount} machines, {SelectedProcessProfilesCount} processes, {SelectedFilamentProfilesCount} filaments", request.SelectedMachineProfiles.Count, request.SelectedProcessProfiles.Count, request.SelectedFilamentProfiles.Count);

        SelectiveProfileImportResultDto result = new()
        {
            PrinterModelId = printerModelId
        };

        if (request.SelectedMachineProfiles.Count == 0 &&
            request.SelectedProcessProfiles.Count == 0 &&
            request.SelectedFilamentProfiles.Count == 0)
        {
            result.Error = "No profiles selected for import";
            return result;
        }

        try
        {
            PrinterModelDto? catalogModel = await _catalogService.GetModelByIdAsync(printerModelId, ct);
            if (catalogModel is null)
            {
                result.Error = $"Printer model with ID {printerModelId} not found in catalog";

                return result;
            }

            IEnumerable<SlicerModelAliasDto> modelAliases = await _catalogService.GetModelAliasesAsync(printerModelId, ct);
            List<string> orcaAliases = modelAliases
                .Where(a => string.Equals(a.SlicerType, "OrcaSlicer", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.SlicerModelName)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (orcaAliases.Count == 0)
            {
                result.Error = $"No OrcaSlicer aliases configured for model '{catalogModel.Name}'";

                return result;
            }

            // Use IHttpClientFactory if available, otherwise create new HttpClient
            using HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

            IReadOnlyList<MachineProfileDto> aliasMachines = await GetMachineProfilesForCatalogModelAsync(httpClient, orcaAliases, ct);
            if (aliasMachines.Count == 0)
            {
                result.Error = $"No OrcaSlicer machine profiles found for aliases configured for model '{catalogModel.Name}'";

                return result;
            }

            HashSet<string> aliasMatchedMachineNames = new(
                aliasMachines.Select(m => m.Name).Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            // Fetch all profiles from worker
            (AllProfilesResponseDto? allProfiles, string? orcaVersion, string workerUrl) = await FetchProfilesFromWorkerAsync(httpClient, ct);

            if (allProfiles?.ByHierarchy == null || allProfiles.ByHierarchy.Count == 0)
            {
                result.Error = "No profiles available from OrcaSlicer worker";
                return result;
            }

            // Find the manufacturer's profiles (case-insensitive lookup)
            string? matchedManufacturerKey = allProfiles.ByHierarchy.Keys
                .FirstOrDefault(k => string.Equals(k, request.ManufacturerName, StringComparison.OrdinalIgnoreCase));

            if (matchedManufacturerKey == null ||
                !allProfiles.ByHierarchy.TryGetValue(matchedManufacturerKey, out ManufacturerProfilesDto? manufacturerProfiles) ||
                manufacturerProfiles?.Models == null)
            {
                result.Error = $"Manufacturer '{request.ManufacturerName}' not found in worker profiles";
                return result;
            }

            // Build sets for fast lookup
            HashSet<string> selectedMachines = new(request.SelectedMachineProfiles, StringComparer.OrdinalIgnoreCase);
            HashSet<string> selectedProcesses = new(request.SelectedProcessProfiles, StringComparer.OrdinalIgnoreCase);
            HashSet<string> selectedFilaments = new(request.SelectedFilamentProfiles, StringComparer.OrdinalIgnoreCase);

            static bool IsCompatibleWithAliasMatchedMachine(IEnumerable<string> compatiblePrinters, HashSet<string> aliasMatchedMachineNames)
            {
                return compatiblePrinters.Any(aliasMatchedMachineNames.Contains);
            }

            // Iterate through all models to find matching profiles
            foreach ((string? _, PrinterModelProfilesDto? modelProfiles) in manufacturerProfiles.Models)
            {
                if (modelProfiles == null)
                {
                    continue;
                }

                string modelDisplayName = modelProfiles.Name ?? "Unknown";

                // Import selected machine profiles
                if (modelProfiles.MachineProfiles != null)
                {
                    foreach (MachineProfileDto machineProfile in modelProfiles.MachineProfiles)
                    {
                        if (!selectedMachines.Contains(machineProfile.Name ?? string.Empty))
                        {
                            continue;
                        }

                        if (!aliasMatchedMachineNames.Contains(machineProfile.Name ?? string.Empty))
                        {
                            _logger.LogDebug("[ImportSelectedProfilesForModel] Skipping selected machine profile '{MachineProfileName}' because it does not match a configured OrcaSlicer alias", machineProfile.Name);
                            result.Skipped++;

                            continue;
                        }

                        try
                        {
                            bool imported = await PersistMachineProfileAsync(
                                machineProfile, request.ManufacturerName, modelDisplayName,
                                printerModelId, orcaVersion, checkDuplicates: true, ct);

                            if (imported)
                            {
                                result.MachineProfilesImported++;
                            }
                            else
                            {
                                result.Skipped++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[ImportSelectedProfilesForModel] Failed to import machine profile '{MachineProfileName}': {ExMessage}", machineProfile.Name, ex.Message);
                            result.Skipped++;
                        }
                    }
                }

                // Import selected filament profiles
                if (modelProfiles.FilamentProfiles != null)
                {
                    foreach (FilamentProfileDto filamentProfile in modelProfiles.FilamentProfiles)
                    {
                        if (!selectedFilaments.Contains(filamentProfile.Name ?? string.Empty))
                        {
                            continue;
                        }

                        if (!IsCompatibleWithAliasMatchedMachine(filamentProfile.CompatiblePrinters, aliasMatchedMachineNames))
                        {
                            _logger.LogDebug("[ImportSelectedProfilesForModel] Skipping selected filament profile '{FilamentProfileName}' because it is not compatible with an alias-matched machine", filamentProfile.Name);
                            result.Skipped++;

                            continue;
                        }

                        try
                        {
                            bool imported = await PersistFilamentProfileAsync(
                                filamentProfile, request.ManufacturerName, modelDisplayName,
                                orcaVersion, checkDuplicates: true, ct);

                            if (imported)
                            {
                                result.FilamentProfilesImported++;
                            }
                            else
                            {
                                result.Skipped++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[ImportSelectedProfilesForModel] Failed to import filament profile '{FilamentProfileName}': {ExMessage}", filamentProfile.Name, ex.Message);
                            result.Skipped++;
                        }
                    }
                }

                // Import selected process profiles
                if (modelProfiles.ProcessProfiles != null)
                {
                    foreach (ProcessProfileDto processProfile in modelProfiles.ProcessProfiles)
                    {
                        if (!selectedProcesses.Contains(processProfile.Name ?? string.Empty))
                        {
                            continue;
                        }

                        if (!IsCompatibleWithAliasMatchedMachine(processProfile.CompatiblePrinters, aliasMatchedMachineNames))
                        {
                            _logger.LogDebug("[ImportSelectedProfilesForModel] Skipping selected process profile '{ProcessProfileName}' because it is not compatible with an alias-matched machine", processProfile.Name);
                            result.Skipped++;

                            continue;
                        }

                        try
                        {
                            bool imported = await PersistProcessProfileAsync(
                                processProfile, modelDisplayName, printerModelId,
                                orcaVersion, checkDuplicates: true, ct);

                            if (imported)
                            {
                                result.ProcessProfilesImported++;
                            }
                            else
                            {
                                result.Skipped++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[ImportSelectedProfilesForModel] Failed to import process profile '{ProcessProfileName}': {ExMessage}", processProfile.Name, ex.Message);
                            result.Skipped++;
                        }
                    }
                }
            }

            _logger.LogInformation("[ImportSelectedProfilesForModel] Completed: imported {ResultTotalImported} profiles ({ResultMachineProfilesImported} machine, {ResultProcessProfilesImported} process, {ResultFilamentProfilesImported} filament), skipped {ResultSkipped}", result.TotalImported, result.MachineProfilesImported, result.ProcessProfilesImported, result.FilamentProfilesImported, result.Skipped);

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[ImportSelectedProfilesForModel] Worker communication failed");
            result.Error = "Failed to communicate with OrcaSlicer worker";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ImportSelectedProfilesForModel] Import failed");
            result.Error = "Import failed";
            return result;
        }
    }

    /// <summary>
    /// Seeds the database with system OrcaSlicer profiles downloaded from the worker service.
    /// </summary>
    /// <param name="httpClient">The HttpClient to use for communicating with the OrcaSlicer worker service</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A dynamic object containing the seeding operation results, typically including:
    /// - count: Number of profiles imported
    /// - duplicates: Number of profiles skipped as duplicates
    /// - errors: Any profiles that failed to import
    /// </returns>
    /// <remarks>
    /// This method downloads the latest OrcaSlicer profiles from the configured worker service
    /// and imports them into the database as system profiles. It handles:
    /// - Discovering the worker URL from the database
    /// - Downloading profile bundles from the worker
    /// - Parsing and validating profile data
    /// - Detecting duplicates based on content hash
    /// - Updating existing profiles with new versions
    ///
    /// This operation is typically run:
    /// - During system initialization
    /// - Periodically to refresh profiles (hourly/daily)
    /// - On-demand by administrators
    ///
    /// If no worker is configured, returns an empty result.
    /// </remarks>
    public async Task<object> SeedSystemProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct)
    {
        _logger.LogInformation("[SeedSystemProfilesFromWorkerAsync] Starting system profiles seed from OrcaSlicer worker");
        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            _logger.LogError("[SeedSystemProfilesFromWorkerAsync] No OrcaSlicer worker URL found");

            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        string? orcaVersion = await TryGetOrcaVersionAsync(httpClient, workerUrl, ct);

        HttpResponseMessage response = await httpClient.GetAsync($"{workerUrl}/api/profiles", ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}");
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (allProfiles?.ByHierarchy == null || allProfiles.ByHierarchy.Count == 0)
        {
            return new { imported = 0, skipped = 0, message = "No profiles available from worker or invalid hierarchy structure" };
        }

        int imported = 0;
        int skipped = 0;

        (IReadOnlyList<ManufacturerDto> catalogManufacturers, _) = await _catalogService.GetManufacturersAsync(ct);
        (IReadOnlyList<PrinterModelDto> catalogModels, _) = await _catalogService.GetModelsAsync(null, ct);

        HashSet<string> catalogManufacturerNames = new HashSet<string>(catalogManufacturers.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        HashSet<string> catalogModelNames = new HashSet<string>(catalogModels.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        foreach ((string? manufacturerKey, ManufacturerProfilesDto? manufacturerProfiles) in allProfiles.ByHierarchy)
        {
            if (!catalogManufacturerNames.Contains(manufacturerKey ?? string.Empty))
            {
                continue;
            }

            if (manufacturerProfiles?.Models == null)
            {
                continue;
            }

            foreach ((string? _, PrinterModelProfilesDto? modelProfiles) in manufacturerProfiles.Models)
            {
                if (!catalogModelNames.Contains(modelProfiles?.Name ?? string.Empty))
                {
                    continue;
                }

                if (modelProfiles == null)
                {
                    continue;
                }

                if (modelProfiles.MachineProfiles != null)
                {
                    foreach (MachineProfileDto machineProfile in modelProfiles.MachineProfiles)
                    {
                        try
                        {
                            string profileJson = JsonSerializer.Serialize(machineProfile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                            (string sanitizedRaw, string settingsJson, string profileHash) = _parsingService.ParseAndPrepare(profileJson);

                            MachineProfile? existingProfile = await _machineProfileRepo.GetByHashAsync(profileHash, ct);
                            if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                            {
                                skipped++;
                                continue;
                            }

                            MachineProfile systemProfile = new MachineProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = machineProfile.Name ?? string.Empty,
                                Manufacturer = machineProfile.Manufacturer ?? manufacturerKey ?? string.Empty,
                                Description = $"OrcaSlicer machine profile for {modelProfiles.Name}",
                                SlicerType = SlicerType.OrcaSlicer,
                                IsSystem = true,
                                IsPublic = true,
                                Hash = profileHash,
                                RawJson = sanitizedRaw,
                                SettingsJson = settingsJson,
                                SlicerVersion = orcaVersion,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            await _machineProfileRepo.AddAsync(systemProfile, ct);
                            imported++;
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                }

                if (modelProfiles.FilamentProfiles != null)
                {
                    foreach (FilamentProfileDto filamentProfile in modelProfiles.FilamentProfiles)
                    {
                        try
                        {
                            string profileJson = JsonSerializer.Serialize(filamentProfile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                            (string sanitizedRaw, string settingsJson, string profileHash) = _parsingService.ParseAndPrepare(profileJson);

                            FilamentProfile? existingProfile = await _filamentProfileRepo.GetByHashAsync(profileHash, ct);
                            if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                            {
                                skipped++;
                                continue;
                            }

                            FilamentProfile systemProfile = new FilamentProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = filamentProfile.Name ?? $"{filamentProfile.Material}",
                                Material = filamentProfile.Material ?? "PLA",
                                Manufacturer = filamentProfile.Manufacturer,
                                Description = $"OrcaSlicer filament profile for {modelProfiles.Name} - {filamentProfile.Material}",
                                SlicerType = SlicerType.OrcaSlicer,
                                PrintSpeed = filamentProfile.PrintSpeed,
                                NozzleTemperature = filamentProfile.NozzleTemperature,
                                BedTemperature = filamentProfile.BedTemperature,
                                IsSystem = true,
                                IsPublic = true,
                                Hash = profileHash,
                                RawJson = sanitizedRaw,
                                SettingsJson = settingsJson,
                                SlicerVersion = orcaVersion,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            await _filamentProfileRepo.AddAsync(systemProfile, ct);
                            imported++;
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                }

                if (modelProfiles.ProcessProfiles != null)
                {
                    foreach (ProcessProfileDto processProfile in modelProfiles.ProcessProfiles)
                    {
                        try
                        {
                            string profileJson = JsonSerializer.Serialize(processProfile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
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
                                Name = string.IsNullOrEmpty(processProfile.Name) ? $"{processProfile.Quality} ({processProfile.LayerHeight}mm)" : processProfile.Name,
                                Description = $"OrcaSlicer process profile for {modelProfiles.Name}: {processProfile.Quality} quality at {processProfile.LayerHeight}mm layer height",
                                SlicerType = SlicerType.OrcaSlicer,
                                Quality = Enum.TryParse(processProfile.Quality ?? "standard", true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                                LayerHeight = processProfile.LayerHeight,
                                InfillPercentage = processProfile.InfillPercentage,
                                PrintSpeed = processProfile.PrintSpeed,
                                EnableSupports = processProfile.Supports,
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
                        catch
                        {
                            skipped++;
                        }
                    }
                }
            }
        }

        return new
        {
            imported,
            skipped,
            manufacturersProcessed = catalogManufacturerNames.Count,
            modelsProcessed = catalogModelNames.Count,
            orcaslicerVersion = orcaVersion,
            message = $"Seeded {imported} OrcaSlicer profiles for catalog manufacturers/models (OrcaSlicer v{orcaVersion ?? "unknown"})"
        };
    }

    /// <summary>
    /// Forces a complete reseed of system OrcaSlicer profiles by clearing existing profiles
    /// and downloading fresh ones from the worker service.
    /// </summary>
    /// <param name="httpClient">The HttpClient to use for communicating with the OrcaSlicer worker service</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A dynamic object containing the reseed operation results with import and error counts.
    /// </returns>
    /// <remarks>
    /// This is a more aggressive version of SeedSystemProfilesFromWorkerAsync that:
    /// - Removes all existing system profiles from the database
    /// - Downloads all profiles fresh from the worker
    /// - Rebuilds the entire system profile catalog
    ///
    /// Use this when:
    /// - The profile database is corrupted or inconsistent
    /// - Profiles have accumulated unwanted duplicates
    /// - A major system profile update needs to be forced
    /// - Administrative reset is required
    ///
    /// WARNING: This operation is destructive and should only be run by administrators.
    /// It will remove all user-created system profiles.
    /// </remarks>
    public async Task<object> ForceReseedSystemProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct)
    {
        int deletedProcessCount = await _processProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);
        int deletedFilamentCount = await _filamentProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);
        int deletedMachineCount = await _machineProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);
        int deletedCount = deletedProcessCount + deletedFilamentCount + deletedMachineCount;

        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        string? orcaVersion = await TryGetOrcaVersionAsync(httpClient, workerUrl, ct);

        HttpResponseMessage response = await httpClient.GetAsync($"{workerUrl}/api/profiles", ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}");
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (allProfiles?.ByHierarchy == null || allProfiles.ByHierarchy.Count == 0)
        {
            return new { imported = 0, deleted = deletedCount, message = "No profiles available from worker or invalid hierarchy structure", orcaslicerVersion = orcaVersion };
        }

        // Emit start event
        await _slicerHubContext.Clients.All.SendAsync("profileimportstarted", new
        {
            message = $"Starting profile import from OrcaSlicer worker (v{orcaVersion ?? "unknown"})..."
        }, cancellationToken: ct);

        int imported = 0;
        int skipped = 0;

        (IReadOnlyList<ManufacturerDto> catalogManufacturers, _) = await _catalogService.GetManufacturersAsync(ct);
        (IReadOnlyList<PrinterModelDto> catalogModels, _) = await _catalogService.GetModelsAsync(null, ct);

        HashSet<string> catalogManufacturerNames = new HashSet<string>(catalogManufacturers.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        HashSet<string> catalogModelNames = new HashSet<string>(catalogModels.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        foreach ((string? manufacturerKey, ManufacturerProfilesDto? manufacturerProfiles) in allProfiles.ByHierarchy)
        {
            if (!catalogManufacturerNames.Contains(manufacturerKey ?? string.Empty))
            {
                continue;
            }

            if (manufacturerProfiles?.Models == null)
            {
                continue;
            }

            foreach ((string? _, PrinterModelProfilesDto? modelProfiles) in manufacturerProfiles.Models)
            {
                if (!catalogModelNames.Contains(modelProfiles?.Name ?? string.Empty))
                {
                    continue;
                }

                if (modelProfiles == null)
                {
                    continue;
                }

                if (modelProfiles.MachineProfiles != null)
                {
                    foreach (MachineProfileDto machineProfile in modelProfiles.MachineProfiles)
                    {
                        try
                        {
                            string profileJson = JsonSerializer.Serialize(machineProfile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                            (string sanitizedRaw, string settingsJson, string profileHash) = _parsingService.ParseAndPrepare(profileJson);

                            MachineProfile systemProfile = new MachineProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = machineProfile.Name ?? string.Empty,
                                Manufacturer = machineProfile.Manufacturer ?? string.Empty,
                                Description = "OrcaSlicer machine profile",
                                SlicerType = SlicerType.OrcaSlicer,
                                IsSystem = true,
                                Hash = profileHash,
                                RawJson = sanitizedRaw,
                                SettingsJson = settingsJson,
                                SlicerVersion = orcaVersion,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            await _machineProfileRepo.AddAsync(systemProfile, ct);
                            imported++;

                            // Emit progress event
                            await _slicerHubContext.Clients.All.SendAsync("profileimported", new
                            {
                                profileName = machineProfile.Name ?? "Unknown",
                                profileType = "Machine",
                                count = imported
                            }, cancellationToken: ct);
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                }

                if (modelProfiles.FilamentProfiles != null)
                {
                    foreach (FilamentProfileDto filamentProfile in modelProfiles.FilamentProfiles)
                    {
                        try
                        {
                            string profileJson = JsonSerializer.Serialize(filamentProfile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
                            (string sanitizedRaw, string settingsJson, string profileHash) = _parsingService.ParseAndPrepare(profileJson);

                            FilamentProfile? existingProfile = await _filamentProfileRepo.GetByHashAsync(profileHash, ct);
                            if (existingProfile != null && existingProfile.IsSystem && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                            {
                                skipped++;
                                continue;
                            }

                            FilamentProfile systemProfile = new FilamentProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = filamentProfile.Name ?? $"{filamentProfile.Material}",
                                Material = filamentProfile.Material ?? "PLA",
                                Manufacturer = filamentProfile.Manufacturer,
                                Description = $"OrcaSlicer filament profile for {modelProfiles.Name} - {filamentProfile.Material}",
                                SlicerType = SlicerType.OrcaSlicer,
                                PrintSpeed = filamentProfile.PrintSpeed,
                                NozzleTemperature = filamentProfile.NozzleTemperature,
                                BedTemperature = filamentProfile.BedTemperature,
                                IsSystem = true,
                                Hash = profileHash,
                                RawJson = sanitizedRaw,
                                SettingsJson = settingsJson,
                                SlicerVersion = orcaVersion,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            await _filamentProfileRepo.AddAsync(systemProfile, ct);
                            imported++;

                            // Emit progress event
                            await _slicerHubContext.Clients.All.SendAsync("profileimported", new
                            {
                                profileName = filamentProfile.Name ?? filamentProfile.Material ?? "Unknown",
                                profileType = "Filament",
                                count = imported
                            }, cancellationToken: ct);
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                }

                if (modelProfiles.ProcessProfiles != null)
                {
                    foreach (ProcessProfileDto processProfile in modelProfiles.ProcessProfiles)
                    {
                        try
                        {
                            string profileJson = JsonSerializer.Serialize(processProfile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false });
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
                                Name = string.IsNullOrEmpty(processProfile.Name) ? $"{processProfile.Quality} ({processProfile.LayerHeight}mm)" : processProfile.Name,
                                Description = $"OrcaSlicer process profile for {modelProfiles.Name} - {processProfile.Quality} quality at {processProfile.LayerHeight}mm layer height",
                                SlicerType = SlicerType.OrcaSlicer,
                                Quality = Enum.TryParse(processProfile.Quality ?? "standard", true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                                LayerHeight = processProfile.LayerHeight,
                                InfillPercentage = processProfile.InfillPercentage,
                                PrintSpeed = processProfile.PrintSpeed,
                                EnableSupports = processProfile.Supports,
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

                            // Emit progress event
                            await _slicerHubContext.Clients.All.SendAsync("profileimported", new
                            {
                                profileName = processProfile.Name ?? $"{processProfile.Quality} ({processProfile.LayerHeight}mm)",
                                profileType = "Process",
                                count = imported
                            }, cancellationToken: ct);
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                }
            }
        }

        // Emit completion event
        await _slicerHubContext.Clients.All.SendAsync("profileimportcompleted", new
        {
            imported,
            skipped,
            deleted = deletedCount,
            message = $"Successfully imported {imported} OrcaSlicer profiles (deleted {deletedCount} old, skipped {skipped} duplicates)"
        }, cancellationToken: ct);

        return new
        {
            imported,
            deleted = deletedCount,
            skipped,
            manufacturersProcessed = catalogManufacturerNames.Count,
            modelsProcessed = catalogModelNames.Count,
            orcaslicerVersion = orcaVersion,
            message = $"Force-reseeded {imported} system OrcaSlicer profiles from {catalogManufacturerNames.Count} catalog manufacturers and {catalogModelNames.Count} printer models (deleted {deletedCount} old, skipped {skipped} duplicates)"
        };
    }

    /// <summary>
    /// Deletes all system profiles (IsSystem=true) from the database.
    /// This is used for Phase 3 cleanup to remove duplicated system profiles.
    /// After this operation, system profiles should only be fetched from OrcaSlicer worker.
    /// </summary>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>Object containing counts of deleted machine, process, and filament profiles</returns>
    /// <remarks>
    /// Custom profiles (IsSystem=false) are preserved. This operation is idempotent.
    /// </remarks>
    public async Task<object> DeleteAllSystemProfilesAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Phase3Cleanup] Starting deletion of all system profiles from database");

        int deletedMachineCount = await _machineProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);
        int deletedProcessCount = await _processProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);
        int deletedFilamentCount = await _filamentProfileRepo.DeleteSystemProfilesAsync(SlicerType.OrcaSlicer, ct);

        int totalDeleted = deletedMachineCount + deletedProcessCount + deletedFilamentCount;

        _logger.LogInformation(
            "[Phase3Cleanup] Completed deletion: {DeletedMachineCount} machine, {DeletedProcessCount} process, {DeletedFilamentCount} filament profiles (total: {TotalDeleted})", deletedMachineCount, deletedProcessCount, deletedFilamentCount, totalDeleted);

        return new
        {
            machineProfilesDeleted = deletedMachineCount,
            processProfilesDeleted = deletedProcessCount,
            filamentProfilesDeleted = deletedFilamentCount,
            totalDeleted,
            message = $"Deleted {totalDeleted} system profiles ({deletedMachineCount} machine, {deletedProcessCount} process, {deletedFilamentCount} filament). System profiles will now be served from OrcaSlicer worker."
        };
    }

    /// <summary>
    /// Retrieves the list of currently available profiles from the OrcaSlicer worker service.
    /// </summary>
    /// <param name="httpClient">The HttpClient to use for communicating with the OrcaSlicer worker service</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A read-only list of ProcessProfileDto containing profiles currently available on the worker.
    /// Returns an empty list if the worker is unreachable or has no profiles.
    /// </returns>
    /// <remarks>
    /// This method queries the worker service for its current profile inventory without importing
    /// them into the database. It's useful for:
    /// - Checking what profiles are available on a worker
    /// - Comparing worker inventory with database inventory
    /// - Validating worker connectivity
    /// - Pre-import validation
    ///
    /// The profiles returned are not persisted to the database; use ImportProfileAsync
    /// or SeedSystemProfilesFromWorkerAsync to persist them.
    /// </remarks>
    public async Task<IReadOnlyList<ProcessProfileDto>> GetAvailableProfilesFromWorkerAsync(HttpClient httpClient, CancellationToken ct)
    {
        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        HttpResponseMessage response = await httpClient.GetAsync($"{workerUrl}/api/profiles", ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return allProfiles?.ProcessProfiles?.SelectMany(kvp => kvp.Value).ToList() ?? new List<ProcessProfileDto>();
    }

    /// <summary>
    /// Fetches the full profile hierarchy from OrcaSlicer worker organized by manufacturer and model.
    /// </summary>
    /// <param name="httpClient">HTTP client for worker communication</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>AllProfilesResponseDto with profiles organized by manufacturer hierarchy, or null if worker unavailable</returns>
    public async Task<AllProfilesResponseDto?> GetWorkerProfilesHierarchyAsync(HttpClient httpClient, CancellationToken ct)
    {
        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        HttpResponseMessage response = await httpClient.GetAsync($"{workerUrl}/api/profiles", ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>
    /// Fetches machine profiles for a specific manufacturer and model from the OrcaSlicer worker.
    /// </summary>
    public async Task<IReadOnlyList<MachineProfileDto>> GetMachineProfilesForModelAsync(
        HttpClient httpClient,
        string manufacturer,
        string model,
        CancellationToken ct)
    {
        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        string url = $"{workerUrl}/api/profiles/machine/{Uri.EscapeDataString(manufacturer)}/{Uri.EscapeDataString(model)}";
        _logger.LogInformation("Fetching machine profiles from worker: {Url}", url);

        HttpResponseMessage response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        List<MachineProfileDto>? profiles = JsonSerializer.Deserialize<List<MachineProfileDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return profiles ?? new List<MachineProfileDto>();
    }

    /// <summary>
    /// Fetches machine profiles by OrcaSlicer alias (printer_model) from the worker.
    /// The alias is the exact printer_model value (e.g., "Thinker X400", "RatRig V-Core 4 HYBRID 400").
    /// </summary>
    public async Task<IReadOnlyList<MachineProfileDto>> GetMachineProfilesByAliasAsync(
        HttpClient httpClient,
        string printerModel,
        CancellationToken ct)
    {
        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        string url = $"{workerUrl}/api/profiles/machine/{Uri.EscapeDataString(printerModel)}";
        _logger.LogInformation("Fetching machine profiles by alias from worker: {Url}", url);

        HttpResponseMessage response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        List<MachineProfileDto>? profiles = JsonSerializer.Deserialize<List<MachineProfileDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return profiles ?? new List<MachineProfileDto>();
    }

    /// <summary>
    /// Fetches machine profiles for a catalog model by trying only configured OrcaSlicer aliases.
    /// </summary>
    public async Task<IReadOnlyList<MachineProfileDto>> GetMachineProfilesForCatalogModelAsync(
        HttpClient httpClient,
        IEnumerable<string> orcaAliases,
        CancellationToken ct)
    {
        List<string> aliases = orcaAliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string alias in aliases)
        {
            IReadOnlyList<MachineProfileDto> profiles = await GetMachineProfilesByAliasAsync(httpClient, alias, ct);
            if (profiles.Count > 0)
            {
                return profiles;
            }

            _logger.LogWarning("No machine profiles found for OrcaSlicer alias '{Alias}'", alias);
        }

        return [];
    }

    /// <summary>
    /// Fetches process profiles compatible with specific machines from the OrcaSlicer worker.
    /// </summary>
    public async Task<IReadOnlyList<ProcessProfileDto>> GetProcessProfilesForMachinesAsync(
        HttpClient httpClient,
        IEnumerable<string> machineNames,
        CancellationToken ct)
    {
        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        string url = $"{workerUrl}/api/profiles/process/for-machines";
        _logger.LogInformation("Fetching process profiles for machines from worker: {Url}", url);

        var request = new { machineNames = machineNames.ToList() };
        string requestJson = JsonSerializer.Serialize(request);
        using var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await httpClient.PostAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        List<ProcessProfileDto>? profiles = JsonSerializer.Deserialize<List<ProcessProfileDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return profiles ?? new List<ProcessProfileDto>();
    }

    /// <summary>
    /// Fetches filament profiles compatible with specific machines from the OrcaSlicer worker.
    /// </summary>
    public async Task<IReadOnlyList<FilamentProfileDto>> GetFilamentProfilesForMachinesAsync(
        HttpClient httpClient,
        IEnumerable<string> machineNames,
        CancellationToken ct)
    {
        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        string url = $"{workerUrl}/api/profiles/filament/for-machines";
        _logger.LogInformation("Fetching filament profiles for machines from worker: {Url}", url);

        var request = new { machineNames = machineNames.ToList() };
        string requestJson = JsonSerializer.Serialize(request);
        using var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await httpClient.PostAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        List<FilamentProfileDto>? profiles = JsonSerializer.Deserialize<List<FilamentProfileDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return profiles ?? new List<FilamentProfileDto>();
    }

    /// <summary>
    /// Fetches template filament profiles from OrcaFilamentLibrary (universal profiles).
    /// </summary>
    public async Task<IReadOnlyList<FilamentProfileDto>> GetFilamentTemplatesAsync(
        HttpClient httpClient,
        CancellationToken ct)
    {
        string? workerUrl = await GetOrcaSlicerWorkerUrlAsync();
        if (string.IsNullOrEmpty(workerUrl))
        {
            throw new HttpRequestException("OrcaSlicer worker not found in registry");
        }

        string url = $"{workerUrl}/api/profiles/filament/templates";
        _logger.LogInformation("Fetching filament templates from worker: {Url}", url);

        HttpResponseMessage response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        List<FilamentProfileDto>? profiles = JsonSerializer.Deserialize<List<FilamentProfileDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return profiles ?? new List<FilamentProfileDto>();
    }

    /// <inheritdoc />
    public async Task<ImportedProfileNamesDto> GetImportedProfileNamesForModelAsync(
        Guid printerModelId,
        CancellationToken ct)
    {
        _logger.LogInformation("[GetImportedProfileNamesForModel] Getting imported profile names for model: {PrinterModelId}", printerModelId);

        // Get all OrcaSlicer machine profiles for this model
        IReadOnlyList<MachineProfile> machineProfiles = await _machineProfileRepo.GetByEngineAsync(
            SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);

        List<string> machineNames = machineProfiles
            .Where(p => p.PrinterModelId == printerModelId && !string.IsNullOrEmpty(p.Name))
            .Select(p => p.Name!)
            .ToList();

        // Get all OrcaSlicer process profiles for this model
        IReadOnlyList<ProcessProfile> processProfiles = await _processProfileRepo.GetByEngineAsync(
            SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);

        List<string> processNames = processProfiles
            .Where(p => p.PrinterModelId == printerModelId && !string.IsNullOrEmpty(p.Name))
            .Select(p => p.Name!)
            .ToList();

        // Get all OrcaSlicer filament profiles (filaments are global, not tied to model)
        // We return all imported filament profile names since they're shared across models
        IReadOnlyList<FilamentProfile> filamentProfiles = await _filamentProfileRepo.GetByEngineAsync(
            SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);

        List<string> filamentNames = filamentProfiles
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .Select(p => p.Name!)
            .ToList();

        _logger.LogDebug("[GetImportedProfileNamesForModel] Found {MachineNamesCount} machines, {ProcessNamesCount} processes, {FilamentNamesCount} filaments for model {PrinterModelId}", machineNames.Count, processNames.Count, filamentNames.Count, printerModelId);

        return new ImportedProfileNamesDto
        {
            MachineProfileNames = machineNames,
            ProcessProfileNames = processNames,
            FilamentProfileNames = filamentNames
        };
    }

    /// <summary>
    /// Retrieves profiles that are compatible with a specific printer.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A read-only list of SlicerProfileListItemDto containing profiles compatible with the printer.
    /// </returns>
    /// <remarks>
    /// This method determines which profiles are suitable for a given printer by:
    /// - Looking up the printer in the database
    /// - Finding profiles that are compatible with the printer's capabilities
    /// - Returning system OrcaSlicer profiles that match the printer type
    ///
    /// This is typically used to populate profile selection dropdowns in the UI
    /// when a specific printer is selected.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">Thrown if the printer with the specified ID is not found</exception>
    public async Task<IReadOnlyList<SlicerProfileListItemDto>> GetAvailableProfilesForPrinterAsync(Guid printerId, CancellationToken ct)
    {
        _logger.LogInformation("[GetAvailableProfilesForPrinterAsync] Getting available profiles for printer: {PrinterId}", printerId);
        Printer? printer = await _unitOfWork.Printers.FindByIdAsync(printerId, ct);
        if (printer is null)
        {
            _logger.LogError("[GetAvailableProfilesForPrinterAsync] Printer not found with ID: {PrinterId}", printerId);
            throw new KeyNotFoundException($"Printer with ID {printerId} not found");
        }

        _logger.LogDebug("[GetAvailableProfilesForPrinterAsync] Found printer: {PrinterName} (ModelId: {ModelId})", printer.Name, printer.ModelId);

        // Resolve the OrcaSlicer aliases for this printer's model. Catalog aliases are the source of truth.
        IEnumerable<SlicerModelAliasDto> aliases = await _catalogService.GetModelAliasesAsync(printer.ModelId, ct);
        List<string> orcaAliases = aliases
            .Where(a => string.Equals(a.SlicerType, "OrcaSlicer", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.SlicerModelName)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orcaAliases.Count == 0)
        {
            _logger.LogWarning("[GetAvailableProfilesForPrinterAsync] No OrcaSlicer aliases for model {ModelId}", printer.ModelId);
            return [];
        }

        _logger.LogInformation("[GetAvailableProfilesForPrinterAsync] Using {AliasCount} OrcaSlicer aliases for printer {PrinterName}", orcaAliases.Count, printer.Name);

        try
        {
            using HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

            // Get machine profiles matching this printer's aliases from the worker
            IReadOnlyList<MachineProfileDto> machines = await GetMachineProfilesForCatalogModelAsync(httpClient, orcaAliases, ct);
            if (machines.Count == 0)
            {
                _logger.LogWarning("[GetAvailableProfilesForPrinterAsync] No machine profiles found for OrcaSlicer aliases {Aliases}", string.Join(", ", orcaAliases));
                return [];
            }

            List<string> machineNames = machines.Select(m => m.Name).ToList();
            _logger.LogDebug("[GetAvailableProfilesForPrinterAsync] Found {Count} machine profiles, fetching compatible process profiles", machines.Count);

            // Get process profiles compatible with those machines
            IReadOnlyList<ProcessProfileDto> processProfiles = await GetProcessProfilesForMachinesAsync(httpClient, machineNames, ct);
            _logger.LogInformation("[GetAvailableProfilesForPrinterAsync] Found {Count} process profiles for printer {PrinterName}", processProfiles.Count, printer.Name);

            return processProfiles.Select(p => new SlicerProfileListItemDto
            {
                Id = Guid.NewGuid(),
                Name = p.Name,
                SlicerType = "OrcaSlicer",
                Quality = p.Quality,
                LayerHeight = p.LayerHeight,
                InfillPercentage = p.InfillPercentage,
                IsSystem = true,
                IsPublic = false,
                Hash = string.Empty
            }).ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[GetAvailableProfilesForPrinterAsync] Worker unavailable, returning no printer-scoped profiles");
            return [];
        }
    }

    /// <summary>
    /// Performs a bulk import of multiple profiles for a specific printer from existing system profiles.
    /// </summary>
    /// <param name="printerId">The unique identifier of the target printer</param>
    /// <param name="request">The bulk import request containing profile IDs to import</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A BulkProfileImportResultDto containing:
    /// - PrinterId: The ID of the target printer
    /// - PrinterName: The name of the target printer
    /// - Imported: List of successfully imported profiles
    /// - Duplicated: List of profiles that were skipped as duplicates
    /// </returns>
    /// <remarks>
    /// This method enables quick profile assignment to a printer by selecting multiple profiles
    /// at once. It handles:
    /// - Validating the printer exists
    /// - Checking for duplicate profiles
    /// - Recording profile assignments
    /// - Returning detailed import results
    ///
    /// Use this for bulk-assigning profiles to newly configured printers or updating a printer's
    /// available profiles all at once.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if request is null</exception>
    /// <exception cref="ArgumentException">Thrown if profileIds list is null or empty</exception>
    /// <exception cref="KeyNotFoundException">Thrown if the printer is not found</exception>
    public async Task<BulkProfileImportResultDto> BulkImportProfilesForPrinterAsync(Guid printerId, BulkProfileImportRequest request, CancellationToken ct)
    {
        int profileCount = request.ProfileIds?.Count ?? 0;
        _logger.LogInformation("[BulkImportProfilesForPrinterAsync] Bulk importing {ProfileCount} profiles for printer: {PrinterId}", profileCount, printerId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProfileIds == null || request.ProfileIds.Count == 0)
        {
            _logger.LogError("[BulkImportProfilesForPrinterAsync] profileIds list is required and must not be empty");
            throw new ArgumentException("profileIds list is required and must not be empty", nameof(request));
        }

        Printer? printer = await _unitOfWork.Printers.FindByIdAsync(printerId, ct);
        if (printer is null)
        {
            _logger.LogError("[BulkImportProfilesForPrinterAsync] Printer not found with ID: {PrinterId}", printerId);
            throw new KeyNotFoundException($"Printer with ID {printerId} not found");
        }

        _logger.LogDebug("[BulkImportProfilesForPrinterAsync] Found printer: {PrinterName}, retrieving system profiles", printer.Name);
        IReadOnlyList<ProcessProfile> allSystemProfiles = await _processProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
        List<ProcessProfile> profilesToImport = allSystemProfiles
            .Where(p => p.IsSystem && request.ProfileIds.Contains(p.Id))
            .ToList();

        if (profilesToImport.Count == 0)
        {
            throw new ArgumentException("No valid system profiles found for import", nameof(request));
        }

        int imported = 0;
        int duplicated = 0;

        foreach (ProcessProfile systemProfile in profilesToImport)
        {
            try
            {
                ProcessProfile userProfile = new ProcessProfile
                {
                    Id = Guid.NewGuid(),
                    Name = systemProfile.Name,
                    Description = $"Imported from system profile for {printer.Name}",
                    SlicerType = systemProfile.SlicerType,
                    RawJson = systemProfile.RawJson,
                    SettingsJson = systemProfile.SettingsJson,
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
                duplicated++;
            }
        }

        return new BulkProfileImportResultDto
        {
            PrinterId = printerId,
            PrinterName = printer.Name,
            TotalRequested = request.ProfileIds.Count,
            TotalFound = profilesToImport.Count,
            Imported = imported,
            Duplicated = duplicated
        };
    }

    /// <summary>
    /// Clones profiles from a template printer to another printer or creates a new custom profile set.
    /// </summary>
    /// <param name="request">The clone request containing source printer ID, profile selections, and customization options</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A CloneProfilesResponseDto containing:
    /// - ClonedCount: Number of profiles successfully cloned
    /// - NewProfiles: List of newly created custom profiles from cloning
    /// - SkippedCount: Number of profiles skipped during cloning
    /// </returns>
    /// <remarks>
    /// This method enables quick profile setup for new printers by cloning from existing configurations.
    /// It can:
    /// - Copy profiles from one printer to another
    /// - Customize cloned profiles with new settings (layer height, infill, etc.)
    /// - Create new profiles based on templates
    /// - Preserve relationships between machine, process, and filament profiles
    ///
    /// This is particularly useful for:
    /// - Setting up identical printer configurations
    /// - Creating custom profile variants based on templates
    /// - Bulk customization of profile sets
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if request is null</exception>
    public async Task<CloneProfilesResponseDto> CloneFromTemplateAsync(CloneProfilesRequestDto request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceMachineProfileId == Guid.Empty || request.TargetPrinterId == Guid.Empty)
        {
            throw new ArgumentException("sourceMachineProfileId and targetPrinterId required", nameof(request));
        }

        MachineProfile? sourceMachine = await _machineProfileRepo.GetByIdAsync(request.SourceMachineProfileId, ct);
        if (sourceMachine == null)
        {
            throw new KeyNotFoundException("Machine profile not found");
        }

        Printer? targetPrinter = await _unitOfWork.Printers.FindByIdAsync(request.TargetPrinterId, ct);
        if (targetPrinter == null)
        {
            throw new KeyNotFoundException("Printer not found");
        }

        int cloned = 0;
        IReadOnlyList<ProcessProfile> profiles = await _processProfileRepo.GetSystemOrcaProfilesAsync(ct);
        foreach (ProcessProfile profile in profiles)
        {
            try
            {
                ProcessProfile clone = new ProcessProfile
                {
                    Id = Guid.NewGuid(),
                    Name = profile.Name,
                    Description = $"Cloned for {targetPrinter.Name}",
                    SlicerType = profile.SlicerType,
                    RawJson = profile.RawJson,
                    SettingsJson = profile.SettingsJson,
                    Hash = (profile.Hash ?? string.Empty) + "_clone",
                    IsSystem = false,
                    IsDefault = false,
                    IsPublic = false,
                    SpecificPrinterId = request.TargetPrinterId,
                    PrinterModelId = targetPrinter.ModelId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _processProfileRepo.AddAsync(clone, ct);
                cloned++;
            }
            catch
            {
            }
        }

        targetPrinter.TemplateMachineProfileId = request.SourceMachineProfileId;
        await _unitOfWork.SaveChangesAsync(ct);

        return new CloneProfilesResponseDto
        {
            SourceMachineProfileId = request.SourceMachineProfileId,
            SourceMachineName = sourceMachine.Name,
            TargetPrinterId = request.TargetPrinterId,
            TargetPrinterName = targetPrinter.Name,
            ProcessProfilesCloned = cloned,
            FilamentProfilesCloned = 0,
            TotalProfilesCloned = cloned
        };
    }

    /// <summary>
    /// Performs a bulk import of profiles directly from the OrcaSlicer worker service to a printer.
    /// </summary>
    /// <param name="printerId">The unique identifier of the target printer</param>
    /// <param name="request">The bulk import request containing worker URL and profile selection criteria</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A BulkImportFromWorkerResultDto containing:
    /// - PrinterId: The ID of the target printer
    /// - PrinterName: The name of the target printer
    /// - Imported: List of successfully imported profiles
    /// - Duplicated: List of profiles that were skipped as duplicates
    /// </returns>
    /// <remarks>
    /// This method enables direct profile import from the worker service without requiring
    /// profiles to be pre-downloaded into the database. It:
    /// - Communicates with the worker service for current profile inventory
    /// - Downloads selected profiles from the worker
    /// - Imports them into the database
    /// - Assigns them to the specified printer
    /// - Handles duplicate detection
    ///
    /// This is useful for:
    /// - Initial printer setup with latest worker profiles
    /// - Adding new profiles from the worker to an existing printer
    /// - Comparing and syncing worker inventory with database
    ///
    /// The worker URL is typically stored in the database or can be passed in the request.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if request is null</exception>
    /// <exception cref="KeyNotFoundException">Thrown if the printer is not found</exception>
    public async Task<BulkImportFromWorkerResultDto> BulkImportFromWorkerAsync(Guid printerId, BulkImportFromWorkerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Profiles == null || request.Profiles.Count == 0)
        {
            throw new ArgumentException("profiles list is required and must not be empty", nameof(request));
        }

        Printer? printer = await _unitOfWork.Printers.FindByIdAsync(printerId, ct);
        if (printer is null)
        {
            throw new KeyNotFoundException($"Printer with ID {printerId} not found");
        }

        int imported = 0;
        int duplicated = 0;

        foreach (SlicerProfileDto workerProfile in request.Profiles)
        {
            try
            {
                ProcessProfileDto? processProfile = workerProfile.ProcessProfile;
                FilamentProfileDto? filamentProfile = workerProfile.FilamentProfile;
                if (processProfile == null)
                {
                    continue;
                }

                string layerHeight = processProfile.LayerHeight.ToString();
                string infill = processProfile.InfillPercentage.ToString();
                string material = filamentProfile?.Material ?? "Unknown";
                string quality = processProfile.Quality ?? "Standard";

                string profileHash = $"{material}:{quality}:{layerHeight}:{infill}";

                ProcessProfile? existingProfile = await _processProfileRepo.GetByHashAsync(profileHash, ct);
                if (existingProfile != null && existingProfile.SlicerType == SlicerType.OrcaSlicer)
                {
                    duplicated++;
                    continue;
                }

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
                duplicated++;
            }
        }

        return new BulkImportFromWorkerResultDto
        {
            PrinterId = printerId,
            PrinterName = printer.Name,
            Imported = imported,
            Duplicated = duplicated
        };
    }

    /// <summary>
    /// Creates a new process profile with the specified configuration.
    /// </summary>
    /// <param name="req">The profile creation request containing name, slicer type, quality level, and settings</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A ProcessProfileResponseDto containing the created profile's details including ID, name,
    /// configuration parameters, and metadata.
    /// </returns>
    /// <remarks>
    /// This method creates a new custom profile from scratch with:
    /// - User-specified name and description
    /// - Slicer type (OrcaSlicer, PrusaSlicer, etc.)
    /// - Quality preset (Draft, Standard, Fine)
    /// - Detailed settings (layer height, infill, speed, temperature, supports)
    /// - Optional advanced JSON settings
    /// - Public/private visibility settings
    /// - Default profile option
    ///
    /// The created profile is immediately available for use and can be:
    /// - Assigned to printers
    /// - Exported for sharing
    /// - Set as default for the slicer type
    /// - Modified or deleted later
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if request is null</exception>
    public async Task<ProcessProfileResponseDto> CreateProfileAsync(CreateProcessProfileDto req, CancellationToken ct)
    {
        _logger.LogInformation("[CreateProfileAsync] Creating new profile: {ReqName}, slicerType: {ReqSlicerType}, quality: {ReqQuality}", req.Name, req.SlicerType, req.Quality);
        ArgumentNullException.ThrowIfNull(req);

        (SlicerType slicerType, ProfileQuality quality) = ValidateAndParseEnums(req.SlicerType, req.Quality);

        ProcessProfile profile = new ProcessProfile
        {
            Id = Guid.NewGuid(),
            Name = NormalizeString(req.Name, "Untitled Profile"),
            Description = req.Description,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            RawJson = req.AdvancedSettings ?? "{}",
            SlicerType = slicerType,
            LayerHeight = req.LayerHeight,
            InfillPercentage = req.InfillPercentage,
            PrintSpeed = req.PrintSpeed,
            EnableSupports = req.EnableSupports,
            Quality = quality,
            IsDefault = req.IsDefault,
            IsPublic = req.IsPublic
        };

        await _repo.AddAsync(profile, ct);

        _logger.LogInformation("Profile created: {ProfileId} - {ProfileName} ({ProfileSlicerType})", profile.Id, profile.Name, profile.SlicerType);

        return ToResponseDto(profile);
    }

    /// <summary>
    /// Retrieves a single profile by its unique identifier with full details.
    /// </summary>
    /// <param name="id">The unique identifier of the profile to retrieve</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A ProcessProfileResponseDto containing full profile details including configuration,
    /// metadata, and status information, or null if the profile does not exist.
    /// </returns>
    /// <remarks>
    /// This method retrieves a single profile with all its configuration details.
    /// It's typically used for:
    /// - Editing existing profiles
    /// - Viewing profile details in the UI
    /// - Validating profile existence
    /// - Getting profile metadata for export/import operations
    ///
    /// Returns null if the profile ID does not exist; no exception is thrown.
    /// </remarks>
    public async Task<ProcessProfileResponseDto?> GetProfileAsync(Guid id, CancellationToken ct)
    {
        _logger.LogInformation("[GetProfileAsync] Retrieving profile with ID: {Id}", id);
        ProcessProfile? profile = await _repo.FindByIdAsync(id, ct);
        if (profile is null)
        {
            _logger.LogWarning("[GetProfileAsync] Profile not found with ID: {Id}", id);
            return null;
        }

        _logger.LogDebug("[GetProfileAsync] Retrieved profile: {ProfileName}", profile.Name);
        return ToResponseDto(profile);
    }

    /// <summary>
    /// Retrieves all available profiles in a lightweight summary format.
    /// </summary>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <returns>
    /// A read-only list of SlicerProfileDto containing all available profiles with summary information
    /// including machine, process, and filament profile associations.
    /// </returns>
    /// <remarks>
    /// This method returns all profiles in a lightweight, associated format that includes:
    /// - Machine profile (hardware configuration)
    /// - Process profile (print quality and speed settings)
    /// - Filament profile (material properties)
    ///
    /// This is typically used for:
    /// - UI profile selectors and lists
    /// - API endpoints that need to return all available profiles
    /// - Profile searching and filtering operations
    /// - Dashboard and overview displays
    ///
    /// Results are sorted by profile name for consistent ordering.
    /// </remarks>
    public async Task<IReadOnlyList<SlicerProfileDto>> GetProfilesAsync(CancellationToken ct)
    {
        _logger.LogInformation("[GetProfilesAsync] Retrieving all slicer profiles");
        List<ProcessProfile> profiles = await _repo.GetAllAsync(ct);
        _logger.LogDebug("[GetProfilesAsync] Retrieved {ProfilesCount} profiles, sorting by name", profiles.Count);
        var result = profiles.OrderBy(p => p.Name).Select(ToSummaryDto).ToList();
        _logger.LogDebug("[GetProfilesAsync] Returning {ResultCount} profiles", result.Count);
        return result;
    }

    /// <summary>
    /// Deletes a profile from the database.
    /// </summary>
    /// <param name="id">The unique identifier of the profile to delete</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    /// <remarks>
    /// This method permanently removes a profile from the database. The deletion:
    /// - Removes the profile record from the database
    /// - Removes any associations with printers
    /// - Cannot be undone
    /// - Is logged for audit purposes
    ///
    /// After deletion:
    /// - The profile ID cannot be used to retrieve the profile
    /// - Any printers that used this profile will no longer have it available
    /// - The space is freed in the database
    ///
    /// Note: System profiles should typically not be deleted, only custom/user-created profiles.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">Thrown if the profile with the specified ID is not found</exception>
    public async Task DeleteProfileAsync(Guid id, CancellationToken ct)
    {
        _logger.LogInformation("[DeleteProfileAsync] Deleting profile with ID: {Id}", id);
        ProcessProfile? profile = await _repo.FindByIdAsync(id, ct);
        if (profile is null)
        {
            _logger.LogError("[DeleteProfileAsync] Profile not found with ID: {Id}", id);
            throw new KeyNotFoundException($"Profile with ID {id} not found");
        }

        _logger.LogDebug("[DeleteProfileAsync] Found profile: {ProfileName}, removing from repository", profile.Name);
        await _repo.RemoveAsync(profile, ct);
        _logger.LogInformation("[DeleteProfileAsync] Successfully deleted profile: {ProfileName}", profile.Name);

        _logger.LogInformation("Profile deleted: {Id} - {ProfileName}", id, profile.Name);
    }

    /// <summary>
    /// Deletes multiple profiles by ID, supporting all profile types (machine, process, filament).
    /// </summary>
    /// <param name="profileIds">Collection of profile IDs to delete</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>BulkDeleteResultDto with counts of deleted profiles by type</returns>
    /// <remarks>
    /// Profiles are looked up in machine, process, and filament tables.
    /// Invalid or non-existent IDs are skipped (not treated as errors).
    /// Returns counts of successfully deleted profiles by type.
    /// </remarks>
    public async Task<BulkDeleteResultDto> BulkDeleteProfilesAsync(IEnumerable<Guid> profileIds, CancellationToken ct)
    {
        var result = new BulkDeleteResultDto();
        var idList = profileIds.ToList();
        _logger.LogInformation("[BulkDeleteProfilesAsync] Deleting {IdListCount} profiles", idList.Count);

        foreach (var id in idList)
        {
            // Try machine profiles first
            var machineProfile = await _machineProfileRepo.GetByIdAsync(id, ct);
            if (machineProfile != null)
            {
                await _machineProfileRepo.DeleteAsync(machineProfile, ct);
                result.MachineProfilesDeleted++;
                _logger.LogDebug("[BulkDeleteProfilesAsync] Deleted machine profile: {MachineProfileName}", machineProfile.Name);
                continue;
            }

            // Try process profiles
            var processProfile = await _repo.FindByIdAsync(id, ct);
            if (processProfile != null)
            {
                await _repo.RemoveAsync(processProfile, ct);
                result.ProcessProfilesDeleted++;
                _logger.LogDebug("[BulkDeleteProfilesAsync] Deleted process profile: {ProcessProfileName}", processProfile.Name);
                continue;
            }

            // Try filament profiles
            var filamentProfile = await _filamentProfileRepo.GetByIdAsync(id, ct);
            if (filamentProfile != null)
            {
                await _filamentProfileRepo.DeleteAsync(filamentProfile, ct);
                result.FilamentProfilesDeleted++;
                _logger.LogDebug("[BulkDeleteProfilesAsync] Deleted filament profile: {FilamentProfileName}", filamentProfile.Name);
                continue;
            }

            // Not found in any table
            result.NotFound++;
            _logger.LogWarning("[BulkDeleteProfilesAsync] Profile not found: {Id}", id);
        }

        _logger.LogInformation("[BulkDeleteProfilesAsync] Deleted {ResultTotalDeleted} profiles (machine: {ResultMachineProfilesDeleted}, process: {ResultProcessProfilesDeleted}, filament: {ResultFilamentProfilesDeleted}, not found: {ResultNotFound})", result.TotalDeleted, result.MachineProfilesDeleted, result.ProcessProfilesDeleted, result.FilamentProfilesDeleted, result.NotFound);
        return result;
    }

    /// <summary>
    /// Maps ProcessProfile to ProcessProfileResponseDto with full details including timestamps.
    /// </summary>
    private static ProcessProfileResponseDto ToResponseDto(ProcessProfile profile)
    {
        return new ProcessProfileResponseDto
        {
            Id = profile.Id,
            Name = profile.Name,
            Description = profile.Description,
            SlicerType = profile.SlicerType.ToString(),
            LayerHeight = profile.LayerHeight,
            InfillPercentage = profile.InfillPercentage,
            PrintSpeed = (int)profile.PrintSpeed,
            EnableSupports = profile.EnableSupports,
            Quality = profile.Quality.ToString(),
            IsDefault = profile.IsDefault,
            IsPublic = profile.IsPublic,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt
        };
    }

    /// <summary>
    /// Maps ProcessProfile to SlicerProfileDto (summary view without timestamps).
    /// Used for list operations where minimal data is needed.
    /// </summary>
    private static SlicerProfileDto ToSummaryDto(ProcessProfile profile)
    {
        return new SlicerProfileDto
        {
            ProcessProfile = new ProcessProfileDto
            {
                Name = profile.Name,
                LayerHeight = profile.LayerHeight,
                InfillPercentage = profile.InfillPercentage,
                PrintSpeed = (int)profile.PrintSpeed,
                Supports = profile.EnableSupports,
                Quality = profile.Quality.ToString(),
                Description = profile.Description,
                Settings = string.IsNullOrEmpty(profile.AdvancedSettings)
                    ? new Dictionary<string, object>()
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(profile.AdvancedSettings) ?? new Dictionary<string, object>()
            }
        };
    }

    /// <summary>
    /// Validates and safely parses SlicerType and ProfileQuality enums.
    /// Returns sensible defaults (PrusaSlicer, Standard) on parse failure.
    /// </summary>
    /// <returns>Tuple of (SlicerType, ProfileQuality)</returns>
    private static (SlicerType SlicerType, ProfileQuality Quality) ValidateAndParseEnums(
        string? slicerTypeStr,
        string? qualityStr)
    {
        SlicerType slicerType = Enum.TryParse(slicerTypeStr, ignoreCase: true, out SlicerType st)
            ? st
            : SlicerType.PrusaSlicer;

        ProfileQuality quality = Enum.TryParse(qualityStr, ignoreCase: true, out ProfileQuality q)
            ? q
            : ProfileQuality.Standard;

        return (slicerType, quality);
    }

    /// <summary>
    /// Normalizes string input: trims whitespace and returns fallback if null or empty.
    /// </summary>
    private static string NormalizeString(string? input, string fallback = "")
    {
        return string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
    }

    private async Task<string?> GetOrcaSlicerWorkerUrlAsync()
    {
        try
        {
            // Query SlicerService entities via ISlicersService (not the old Worker table)
            IReadOnlyList<SlicerService> allSlicers = await _slicersService.ListAsync(CancellationToken.None);

            // SlicerType 1 = OrcaSlicer (per SlicerType enum)
            SlicerService? orcaWorker = allSlicers.FirstOrDefault(s =>
                s.Status == "Online" &&
                s.SlicerType == 1 &&
                !string.IsNullOrEmpty(s.Host));

            if (orcaWorker != null && !string.IsNullOrEmpty(orcaWorker.Host))
            {
                _logger.LogInformation("Using OrcaSlicer worker from registry: {OrcaWorkerName} at {OrcaWorkerHost}", orcaWorker.Name, orcaWorker.Host);
                return orcaWorker.Host;
            }

            // Fallback: any OrcaSlicer worker regardless of status
            orcaWorker = allSlicers.FirstOrDefault(s =>
                s.SlicerType == 1 &&
                !string.IsNullOrEmpty(s.Host));

            if (orcaWorker != null && !string.IsNullOrEmpty(orcaWorker.Host))
            {
                _logger.LogWarning("OrcaSlicer worker '{OrcaWorkerName}' is not online, but using endpoint anyway: {OrcaWorkerHost}", orcaWorker.Name, orcaWorker.Host);
                return orcaWorker.Host;
            }

            _logger.LogWarning("No OrcaSlicer worker found in slicer registry");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to query slicer registry: {ExMessage}", ex.Message);
            return null;
        }
    }

    private static string ComputeSha256Hash(string input)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashedBytes).ToLower();
    }

    private static async Task<string?> TryGetOrcaVersionAsync(HttpClient httpClient, string workerUrl, CancellationToken ct)
    {
        try
        {
            HttpResponseMessage versionResponse = await httpClient.GetAsync($"{workerUrl}/version", ct);
            if (!versionResponse.IsSuccessStatusCode)
            {
                return null;
            }

            string versionJson = await versionResponse.Content.ReadAsStringAsync(ct);
            using JsonDocument versionDoc = JsonDocument.Parse(versionJson);
            return versionDoc.RootElement.TryGetProperty("orcaslicerVersion", out JsonElement versionElem) && versionElem.ValueKind == JsonValueKind.String
                ? versionElem.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<CloneSingleProfileResponseDto> CloneSingleProfileAsync(CloneSingleProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string profileType = request.ProfileType?.ToLowerInvariant() ?? string.Empty;

        return profileType switch
        {
            "process" => await CloneProcessProfileAsync(request, userId, ct),
            "filament" => await CloneFilamentProfileAsync(request, userId, ct),
            "machine" => await CloneMachineProfileAsync(request, userId, ct),
            _ => throw new ArgumentException($"Invalid profile type: '{request.ProfileType}'. Must be 'machine', 'filament', or 'process'.")
        };
    }

    private async Task<CloneSingleProfileResponseDto> CloneProcessProfileAsync(CloneSingleProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        ProcessProfile? source = await _processProfileRepo.GetByIdAsync(request.SourceProfileId, ct);
        if (source == null)
        {
            throw new KeyNotFoundException($"Process profile with ID {request.SourceProfileId} not found.");
        }

        string newName = !string.IsNullOrWhiteSpace(request.Name) ? request.Name : $"{source.Name} (Custom)";

        ProcessProfile clone = new()
        {
            Id = Guid.NewGuid(),
            Name = newName,
            Description = source.Description,
            SlicerType = source.SlicerType,
            IsSystem = false,
            IsPublic = false,
            CreatedByUserId = userId,
            LayerHeight = source.LayerHeight,
            InfillPercentage = source.InfillPercentage,
            PrintSpeed = source.PrintSpeed,
            EnableSupports = source.EnableSupports,
            Quality = source.Quality,
            AdvancedSettings = source.AdvancedSettings,
            SlicerVersion = source.SlicerVersion,
            RawJson = source.RawJson,
            SettingsJson = source.SettingsJson,
            Hash = ComputeSha256Hash($"{userId}{newName}{source.RawJson}{DateTime.UtcNow.Ticks}"),
            CompatiblePrinters = source.CompatiblePrinters,
            PrinterModelId = source.PrinterModelId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _processProfileRepo.AddAsync(clone, ct);
        _logger.LogInformation("Cloned process profile '{SourceName}' to '{NewName}' for user {UserId}", source.Name, newName, userId);

        return new CloneSingleProfileResponseDto
        {
            Id = clone.Id,
            Name = clone.Name,
            ProfileType = "process",
            IsSystem = false
        };
    }

    private async Task<CloneSingleProfileResponseDto> CloneFilamentProfileAsync(CloneSingleProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        FilamentProfile? source = await _filamentProfileRepo.GetByIdAsync(request.SourceProfileId, ct);
        if (source == null)
        {
            throw new KeyNotFoundException($"Filament profile with ID {request.SourceProfileId} not found.");
        }

        string newName = !string.IsNullOrWhiteSpace(request.Name) ? request.Name : $"{source.Name} (Custom)";

        FilamentProfile clone = new()
        {
            Id = Guid.NewGuid(),
            Name = newName,
            Description = source.Description,
            SlicerType = source.SlicerType,
            IsSystem = false,
            IsPublic = false,
            CreatedByUserId = userId,
            Material = source.Material,
            Manufacturer = source.Manufacturer,
            NozzleTemperature = source.NozzleTemperature,
            BedTemperature = source.BedTemperature,
            PrintSpeed = source.PrintSpeed,
            SlicerVersion = source.SlicerVersion,
            RawJson = source.RawJson,
            SettingsJson = source.SettingsJson,
            Hash = ComputeSha256Hash($"{userId}{newName}{source.RawJson}{DateTime.UtcNow.Ticks}"),
            CompatiblePrinters = source.CompatiblePrinters,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _filamentProfileRepo.AddAsync(clone, ct);
        _logger.LogInformation("Cloned filament profile '{SourceName}' to '{NewName}' for user {UserId}", source.Name, newName, userId);

        return new CloneSingleProfileResponseDto
        {
            Id = clone.Id,
            Name = clone.Name,
            ProfileType = "filament",
            IsSystem = false
        };
    }

    private async Task<CloneSingleProfileResponseDto> CloneMachineProfileAsync(CloneSingleProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        MachineProfile? source = await _machineProfileRepo.GetByIdAsync(request.SourceProfileId, ct);
        if (source == null)
        {
            throw new KeyNotFoundException($"Machine profile with ID {request.SourceProfileId} not found.");
        }

        string newName = !string.IsNullOrWhiteSpace(request.Name) ? request.Name : $"{source.Name} (Custom)";

        MachineProfile clone = new()
        {
            Id = Guid.NewGuid(),
            Name = newName,
            Description = source.Description,
            SlicerType = source.SlicerType,
            IsSystem = false,
            IsPublic = false,
            CreatedByUserId = userId,
            Manufacturer = source.Manufacturer,
            SlicerVersion = source.SlicerVersion,
            RawJson = source.RawJson,
            SettingsJson = source.SettingsJson,
            Hash = ComputeSha256Hash($"{userId}{newName}{source.RawJson}{DateTime.UtcNow.Ticks}"),
            PrinterModelId = source.PrinterModelId,
            MachineModelProfileId = source.MachineModelProfileId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _machineProfileRepo.AddAsync(clone, ct);
        _logger.LogInformation("Cloned machine profile '{SourceName}' to '{NewName}' for user {UserId}", source.Name, newName, userId);

        return new CloneSingleProfileResponseDto
        {
            Id = clone.Id,
            Name = clone.Name,
            ProfileType = "machine",
            IsSystem = false
        };
    }

    /// <inheritdoc />
    public async Task<CustomProfileDto> UploadCustomProfileAsync(UploadProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RawJson))
        {
            throw new ArgumentException("RawJson is required.");
        }

        string profileType = request.ProfileType?.ToLowerInvariant() ?? string.Empty;

        return profileType switch
        {
            "process" => await UploadProcessProfileAsync(request, userId, ct),
            "filament" => await UploadFilamentProfileAsync(request, userId, ct),
            "machine" => await UploadMachineProfileAsync(request, userId, ct),
            _ => throw new ArgumentException($"Invalid profile type: '{request.ProfileType}'. Must be 'machine', 'filament', or 'process'.")
        };
    }

    private async Task<CustomProfileDto> UploadProcessProfileAsync(UploadProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        // Parse the raw JSON to extract profile name if not provided
        string name = request.Name ?? "Uploaded Profile";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(request.RawJson);
            if (string.IsNullOrWhiteSpace(request.Name) && doc.RootElement.TryGetProperty("name", out JsonElement nameElem))
            {
                name = nameElem.GetString() ?? name;
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid JSON: {ex.Message}");
        }

        ProcessProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = false,
            IsPublic = false,
            CreatedByUserId = userId,
            RawJson = request.RawJson,
            Hash = ComputeSha256Hash($"{userId}{name}{request.RawJson}{DateTime.UtcNow.Ticks}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _processProfileRepo.AddAsync(profile, ct);
        _logger.LogInformation("Uploaded process profile '{Name}' for user {UserId}", name, userId);

        return new CustomProfileDto
        {
            Id = profile.Id,
            Name = profile.Name,
            ProfileType = "process",
            IsSystem = false,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
            RawJson = profile.RawJson
        };
    }

    private async Task<CustomProfileDto> UploadFilamentProfileAsync(UploadProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        string name = request.Name ?? "Uploaded Filament";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(request.RawJson);
            if (string.IsNullOrWhiteSpace(request.Name) && doc.RootElement.TryGetProperty("name", out JsonElement nameElem))
            {
                name = nameElem.GetString() ?? name;
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid JSON: {ex.Message}");
        }

        FilamentProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = false,
            IsPublic = false,
            CreatedByUserId = userId,
            RawJson = request.RawJson,
            Hash = ComputeSha256Hash($"{userId}{name}{request.RawJson}{DateTime.UtcNow.Ticks}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _filamentProfileRepo.AddAsync(profile, ct);
        _logger.LogInformation("Uploaded filament profile '{Name}' for user {UserId}", name, userId);

        return new CustomProfileDto
        {
            Id = profile.Id,
            Name = profile.Name,
            ProfileType = "filament",
            IsSystem = false,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
            RawJson = profile.RawJson
        };
    }

    private async Task<CustomProfileDto> UploadMachineProfileAsync(UploadProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        string name = request.Name ?? "Uploaded Machine";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(request.RawJson);
            if (string.IsNullOrWhiteSpace(request.Name) && doc.RootElement.TryGetProperty("name", out JsonElement nameElem))
            {
                name = nameElem.GetString() ?? name;
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid JSON: {ex.Message}");
        }

        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = false,
            IsPublic = false,
            CreatedByUserId = userId,
            RawJson = request.RawJson,
            Hash = ComputeSha256Hash($"{userId}{name}{request.RawJson}{DateTime.UtcNow.Ticks}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _machineProfileRepo.AddAsync(profile, ct);
        _logger.LogInformation("Uploaded machine profile '{Name}' for user {UserId}", name, userId);

        return new CustomProfileDto
        {
            Id = profile.Id,
            Name = profile.Name,
            ProfileType = "machine",
            IsSystem = false,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
            RawJson = profile.RawJson
        };
    }

    /// <inheritdoc />
    public async Task<CustomProfilesListResponseDto> ListCustomProfilesAsync(Guid userId, CancellationToken ct)
    {
        List<CustomProfileDto> profiles = [];

        // Get custom process profiles
        IReadOnlyList<ProcessProfile> processProfiles = await _processProfileRepo.GetByUserAsync(userId, ct);
        foreach (ProcessProfile p in processProfiles.Where(p => !p.IsSystem && p.CreatedByUserId == userId))
        {
            profiles.Add(new CustomProfileDto
            {
                Id = p.Id,
                Name = p.Name,
                ProfileType = "process",
                IsSystem = false,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
                RawJson = p.RawJson
            });
        }

        // Get custom filament profiles
        IReadOnlyList<FilamentProfile> filamentProfiles = await _filamentProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: false, userId: userId, ct: ct);
        foreach (FilamentProfile p in filamentProfiles.Where(p => !p.IsSystem && p.CreatedByUserId == userId))
        {
            profiles.Add(new CustomProfileDto
            {
                Id = p.Id,
                Name = p.Name,
                ProfileType = "filament",
                IsSystem = false,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
                RawJson = p.RawJson
            });
        }

        // Get custom machine profiles
        IReadOnlyList<MachineProfile> machineProfiles = await _machineProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: false, userId: userId, ct: ct);
        foreach (MachineProfile p in machineProfiles.Where(p => !p.IsSystem && p.CreatedByUserId == userId))
        {
            profiles.Add(new CustomProfileDto
            {
                Id = p.Id,
                Name = p.Name,
                ProfileType = "machine",
                IsSystem = false,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
                RawJson = p.RawJson
            });
        }

        return new CustomProfilesListResponseDto
        {
            Profiles = profiles,
            TotalCount = profiles.Count,
            ProcessProfileCount = profiles.Count(p => p.ProfileType == "process"),
            FilamentProfileCount = profiles.Count(p => p.ProfileType == "filament"),
            MachineProfileCount = profiles.Count(p => p.ProfileType == "machine")
        };
    }

    /// <inheritdoc />
    public async Task<CustomProfileDto> UpdateCustomProfileAsync(Guid profileId, UpdateCustomProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        // Try to find the profile in each table
        ProcessProfile? processProfile = await _processProfileRepo.GetByIdAsync(profileId, ct);
        if (processProfile != null)
        {
            return await UpdateProcessProfileAsync(processProfile, request, userId, ct);
        }

        FilamentProfile? filamentProfile = await _filamentProfileRepo.GetByIdAsync(profileId, ct);
        if (filamentProfile != null)
        {
            return await UpdateFilamentProfileAsync(filamentProfile, request, userId, ct);
        }

        MachineProfile? machineProfile = await _machineProfileRepo.GetByIdAsync(profileId, ct);
        if (machineProfile != null)
        {
            return await UpdateMachineProfileAsync(machineProfile, request, userId, ct);
        }

        throw new KeyNotFoundException($"Profile with ID {profileId} not found.");
    }

    private async Task<CustomProfileDto> UpdateProcessProfileAsync(ProcessProfile profile, UpdateCustomProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        if (profile.IsSystem)
        {
            throw new InvalidOperationException("Cannot update a system profile. Clone it first to create a custom version.");
        }

        if (profile.CreatedByUserId != userId)
        {
            throw new UnauthorizedAccessException("You do not have permission to update this profile.");
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            profile.Name = request.Name;
        }

        if (!string.IsNullOrWhiteSpace(request.RawJson))
        {
            profile.RawJson = request.RawJson;
            profile.Hash = ComputeSha256Hash($"{userId}{profile.Name}{request.RawJson}{DateTime.UtcNow.Ticks}");
        }

        profile.UpdatedAt = DateTime.UtcNow;
        await _processProfileRepo.UpdateAsync(profile, ct);
        _logger.LogInformation("Updated process profile '{ProfileName}' for user {UserId}", profile.Name, userId);

        return new CustomProfileDto
        {
            Id = profile.Id,
            Name = profile.Name,
            ProfileType = "process",
            IsSystem = false,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
            RawJson = profile.RawJson
        };
    }

    private async Task<CustomProfileDto> UpdateFilamentProfileAsync(FilamentProfile profile, UpdateCustomProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        if (profile.IsSystem)
        {
            throw new InvalidOperationException("Cannot update a system profile. Clone it first to create a custom version.");
        }

        if (profile.CreatedByUserId != userId)
        {
            throw new UnauthorizedAccessException("You do not have permission to update this profile.");
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            profile.Name = request.Name;
        }

        if (!string.IsNullOrWhiteSpace(request.RawJson))
        {
            profile.RawJson = request.RawJson;
            profile.Hash = ComputeSha256Hash($"{userId}{profile.Name}{request.RawJson}{DateTime.UtcNow.Ticks}");
        }

        profile.UpdatedAt = DateTime.UtcNow;
        await _filamentProfileRepo.UpdateAsync(profile, ct);
        _logger.LogInformation("Updated filament profile '{ProfileName}' for user {UserId}", profile.Name, userId);

        return new CustomProfileDto
        {
            Id = profile.Id,
            Name = profile.Name,
            ProfileType = "filament",
            IsSystem = false,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
            RawJson = profile.RawJson
        };
    }

    private async Task<CustomProfileDto> UpdateMachineProfileAsync(MachineProfile profile, UpdateCustomProfileRequestDto request, Guid userId, CancellationToken ct)
    {
        if (profile.IsSystem)
        {
            throw new InvalidOperationException("Cannot update a system profile. Clone it first to create a custom version.");
        }

        if (profile.CreatedByUserId != userId)
        {
            throw new UnauthorizedAccessException("You do not have permission to update this profile.");
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            profile.Name = request.Name;
        }

        if (!string.IsNullOrWhiteSpace(request.RawJson))
        {
            profile.RawJson = request.RawJson;
            profile.Hash = ComputeSha256Hash($"{userId}{profile.Name}{request.RawJson}{DateTime.UtcNow.Ticks}");
        }

        profile.UpdatedAt = DateTime.UtcNow;
        await _machineProfileRepo.UpdateAsync(profile, ct);
        _logger.LogInformation("Updated machine profile '{ProfileName}' for user {UserId}", profile.Name, userId);

        return new CustomProfileDto
        {
            Id = profile.Id,
            Name = profile.Name,
            ProfileType = "machine",
            IsSystem = false,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
            RawJson = profile.RawJson
        };
    }
}

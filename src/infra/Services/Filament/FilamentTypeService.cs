using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.OpenFilamentDb;
using Farm.Infrastructure.Repositories.Filament;
using Farm.Infrastructure.Services.Filament;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Startup;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Filament;

/// <summary>
/// Service for managing 3D printer filament types and temperature presets with Spoolman integration.
/// </summary>
/// <remarks>
/// This service provides filament type management capabilities including:
/// - CRUD operations for filament types (PLA, ABS, PETG, etc.)
/// - Default temperature presets per material (hotend and bed temperatures)
/// - Bulk import from Spoolman inventory management system
/// - Material-specific temperature recommendations
/// - Startup status checking to prevent operations during initialization
/// Temperature defaults are based on common material profiles for standard printing.
/// </remarks>
/// <remarks>
/// Constructor and dependency initialization uses null-coalescing operators
/// to ensure all required services are available before service starts.
/// </remarks>
/// <remarks>
/// Initializes a new instance of the FilamentTypeService with required dependencies.
/// </remarks>
/// <param name="repo">Repository for filament type data persistence and retrieval</param>
/// <param name="startupStatus">Service for checking application startup status</param>
/// <param name="spoolmanService">Service for integrating with Spoolman inventory system</param>
/// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
public class FilamentTypeService(
    IFilamentTypeRepository repo,
    IStartupStatus startupStatus,
    ISpoolmanService spoolmanService) : IFilamentTypeService
{
    private readonly IFilamentTypeRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    private readonly IStartupStatus _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
    private readonly ISpoolmanService _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));

    /// <summary>
    /// Retrieves all filament types in the system.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Read-only list of filament type DTOs with ID, name, and default temperatures</returns>
    /// <exception cref="InvalidOperationException">Thrown when system is still initializing</exception>
    public async Task<IReadOnlyList<FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct)
    {
        return !_startupStatus.IsReady
            ? throw new InvalidOperationException("System is still initializing")
            : await _repo.GetFilamentTypesAsync(ct);
    }

    /// <summary>
    /// Retrieves a paged, optionally filtered list of filament types.
    /// </summary>
    public async Task<PagedResult<FilamentTypeDto>> GetPagedFilamentTypesAsync(int page, int pageSize, string? search, CancellationToken ct)
    {
        return !_startupStatus.IsReady
            ? throw new InvalidOperationException("System is still initializing")
            : await _repo.GetPagedAsync(page, pageSize, search, ct);
    }

    /// <summary>
    /// Retrieves all filament presets with temperature configurations.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Filament presets DTO containing all configured material types and temperatures</returns>
    /// <exception cref="InvalidOperationException">Thrown when system is still initializing</exception>
    public async Task<FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct)
    {
        return !_startupStatus.IsReady
            ? throw new InvalidOperationException("System is still initializing")
            : await _repo.GetFilamentPresetsAsync(ct);
    }

    /// <summary>
    /// Creates a new filament type with specified name and temperature defaults.
    /// </summary>
    /// <param name="req">Creation request containing name and default temperatures</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Created filament type DTO with assigned GUID</returns>
    /// <exception cref="ArgumentException">Thrown when name is null, empty, or whitespace</exception>
    /// <exception cref="InvalidOperationException">Thrown when filament type with same name already exists</exception>
    /// <remarks>
    /// Filament type names are case-sensitive and must be unique.
    /// Name is trimmed before storage to prevent accidental whitespace duplicates.
    /// </remarks>
    public async Task<FilamentTypeDto> CreateFilamentTypeAsync(CreateFilamentTypeRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Name))
        {
            throw new ArgumentException("Name is required", nameof(req));
        }

        if (req.DefaultPricePerKg is < 0)
        {
            throw new ArgumentException("Default price per kg cannot be negative", nameof(req));
        }

        if (req.DefaultDensity is < 0)
        {
            throw new ArgumentException("Default density cannot be negative", nameof(req));
        }

        string trimmed = req.Name.Trim();
        FilamentType? existing = await _repo.GetByNameAsync(trimmed, ct);
        if (existing != null)
        {
            throw new InvalidOperationException("Filament type with this name already exists");
        }

        FilamentType filamentType = new()
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            DefaultHotendTemp = req.DefaultTemperatures.Hotend,
            DefaultBedTemp = req.DefaultTemperatures.Bed,
            IsAbrasive = req.IsAbrasive,
            NeedsEnclosure = req.NeedsEnclosure,
            DefaultPricePerKg = req.DefaultPricePerKg ?? GetDefaultPricePerKg(trimmed),
            DefaultDensity = req.DefaultDensity ?? GetDefaultDensity(trimmed),
            CreatedAt = DateTime.UtcNow
        };
        await _repo.AddFilamentTypeAsync(filamentType, ct);
        await _repo.SaveChangesAsync(ct);
        return new FilamentTypeDto(filamentType.Id, filamentType.Name, new TempTargets(filamentType.DefaultHotendTemp, filamentType.DefaultBedTemp), filamentType.IsAbrasive, filamentType.NeedsEnclosure, filamentType.DefaultPricePerKg, filamentType.DefaultDensity);
    }

    /// <summary>
    /// Updates an existing filament type's name and temperature settings.
    /// </summary>
    /// <param name="id">Unique filament type identifier (GUID)</param>
    /// <param name="req">Update request containing new name and temperatures</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <exception cref="ArgumentException">Thrown when request body is null</exception>
    /// <exception cref="KeyNotFoundException">Thrown when filament type with specified ID does not exist</exception>
    /// <remarks>
    /// Name is trimmed before update if provided and non-empty.
    /// Temperature values always updated to request values.
    /// </remarks>
    public async Task UpdateFilamentTypeAsync(Guid id, UpdateFilamentTypeRequest req, CancellationToken ct)
    {
        if (req is null)
        {
            throw new ArgumentException("Request body is required", nameof(req));
        }

        if (req.DefaultPricePerKg is < 0)
        {
            throw new ArgumentException("Default price per kg cannot be negative", nameof(req));
        }

        if (req.DefaultDensity is < 0)
        {
            throw new ArgumentException("Default density cannot be negative", nameof(req));
        }

        FilamentTypeDto? dto = await _repo.GetByIdAsync(id, ct);
        if (dto is null)
        {
            throw new KeyNotFoundException("Filament type not found");
        }

        FilamentType? entity = await _repo.GetEntityByIdAsync(id, ct);
        if (entity is null)
        {
            throw new KeyNotFoundException("Filament type not found");
        }

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            entity.Name = req.Name.Trim();
        }

        entity.DefaultHotendTemp = req.DefaultTemperatures.Hotend;
        entity.DefaultBedTemp = req.DefaultTemperatures.Bed;
        entity.IsAbrasive = req.IsAbrasive;
        entity.NeedsEnclosure = req.NeedsEnclosure;
        entity.DefaultPricePerKg = req.DefaultPricePerKg;
        entity.DefaultDensity = req.DefaultDensity;
        await _repo.UpdateFilamentTypeAsync(entity, ct);
        await _repo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a filament type from the system.
    /// </summary>
    /// <param name="id">Unique filament type identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <exception cref="KeyNotFoundException">Thrown when filament type with specified ID does not exist</exception>
    public async Task DeleteFilamentTypeAsync(Guid id, CancellationToken ct)
    {
        FilamentTypeDto? dto = await _repo.GetByIdAsync(id, ct);
        if (dto is null)
        {
            throw new KeyNotFoundException("Filament type not found");
        }

        await _repo.DeleteFilamentTypeAsync(id, ct);
        await _repo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Saves complete filament presets configuration, replacing all existing types.
    /// </summary>
    /// <param name="presets">Filament presets DTO with material types and temperatures</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <remarks>
    /// Deletes all existing filament types and creates new ones from presets.
    /// This is a destructive operation - use with caution.
    /// </remarks>
    public async Task SaveFilamentPresetsAsync(FilamentPresetsDto presets, CancellationToken ct)
    {
        if (presets?.Presets == null)
        {
            throw new ArgumentException("Presets are required", nameof(presets));
        }

        foreach (KeyValuePair<string, TempTargets> kvp in presets.Presets)
        {
            string name = kvp.Key.Trim();
            TempTargets tempTargets = kvp.Value;

            // use repository to load or create
            FilamentType? filamentType = await _repo.GetByNameAsync(name, ct);
            if (filamentType == null)
            {
                filamentType = new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    DefaultHotendTemp = tempTargets.Hotend,
                    DefaultBedTemp = tempTargets.Bed,
                    DefaultPricePerKg = GetDefaultPricePerKg(name),
                    DefaultDensity = GetDefaultDensity(name),
                    CreatedAt = DateTime.UtcNow
                };
                await _repo.AddFilamentTypeAsync(filamentType, ct);
            }
            else
            {
                filamentType.DefaultHotendTemp = tempTargets.Hotend;
                filamentType.DefaultBedTemp = tempTargets.Bed;
                await _repo.UpdateFilamentTypeAsync(filamentType, ct);
            }
        }

        await _repo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Imports filament types from Spoolman inventory management system.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Import result with counts of created, updated, skipped, and failed materials</returns>
    /// <remarks>
    /// Import process:
    /// 1. Fetches materials from configured Spoolman instance
    /// 2. Matches materials by name (case-insensitive)
    /// 3. Creates new filament types for unmapped materials
    /// 4. Uses material-specific temperature defaults (PLA: 210°C hotend / 60°C bed, etc.)
    /// 5. Skips materials that already exist with matching name
    /// All operations performed in single transaction; partial failures tracked in result.
    /// </remarks>
    public async Task<SpoolmanFilamentImportResult> ImportFromSpoolmanAsync(CancellationToken ct)
    {
        if (!_startupStatus.IsReady)
        {
            throw new InvalidOperationException("System is still initializing");
        }

        if (_spoolmanService.GetConfig() is not SpoolmanConfigDto config || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            throw new InvalidOperationException("Spoolman is not configured");
        }

        IReadOnlyList<SpoolmanMaterialDto> materials = await _spoolmanService.ListMaterialsAsync(ct);

        HashSet<string> uniqueMaterials = new(StringComparer.OrdinalIgnoreCase);
        foreach (SpoolmanMaterialDto material in materials)
        {
            if (!string.IsNullOrWhiteSpace(material.Name))
            {
                _ = uniqueMaterials.Add(material.Name.Trim());
            }
        }

        List<string> existingTypes = (await _repo.GetFilamentTypesAsync(ct)).Select(f => f.Name).ToList();

        HashSet<string> existingTypesSet = new(existingTypes, StringComparer.OrdinalIgnoreCase);

        int importedCount = 0;
        int skippedCount = 0;
        List<string> importedNames = new();

        foreach (string materialName in uniqueMaterials.OrderBy(m => m))
        {
            if (existingTypesSet.Contains(materialName))
            {
                skippedCount++;
                continue;
            }

            FilamentType newFilamentType = new()
            {
                Id = Guid.NewGuid(),
                Name = materialName,
                DefaultHotendTemp = GetDefaultHotendTemp(materialName),
                DefaultBedTemp = GetDefaultBedTemp(materialName),
                DefaultPricePerKg = GetDefaultPricePerKg(materialName),
                DefaultDensity = GetDefaultDensity(materialName),
                CreatedAt = DateTime.UtcNow
            };

            await _repo.AddFilamentTypeAsync(newFilamentType, ct);
            importedNames.Add(materialName);
            importedCount++;
        }

        await _repo.SaveChangesAsync(ct);

        return new SpoolmanFilamentImportResult(
            ImportedCount: importedCount,
            SkippedCount: skippedCount,
            TotalSpoolmanMaterials: uniqueMaterials.Count,
            ImportedNames: importedNames.ToArray());
    }

    /// <inheritdoc/>
    public async Task<byte[]> ExportToCsvAsync(CancellationToken ct)
    {
        IReadOnlyList<FilamentTypeDto> filaments = await _repo.GetFilamentTypesAsync(ct);

        StringBuilder sb = new();
        sb.AppendLine("Id,Name,HotendTemp,BedTemp,IsAbrasive,NeedsEnclosure,DefaultPricePerKg,DefaultDensity");

        foreach (FilamentTypeDto f in filaments.OrderBy(f => f.Name))
        {
            sb.Append(CsvEscape(f.Id.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(f.Name));
            sb.Append(',');
            sb.Append(f.DefaultTemperatures?.Hotend?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            sb.Append(',');
            sb.Append(f.DefaultTemperatures?.Bed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            sb.Append(',');
            sb.Append(f.IsAbrasive ? "true" : "false");
            sb.Append(',');
            sb.Append(f.NeedsEnclosure ? "true" : "false");
            sb.Append(',');
            sb.Append(f.DefaultPricePerKg?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            sb.Append(',');
            sb.AppendLine(f.DefaultDensity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <inheritdoc/>
    public async Task<FilamentCsvImportResult> ImportFromCsvAsync(Stream csvStream, CancellationToken ct)
    {
        using StreamReader reader = new(csvStream, Encoding.UTF8);
        string? headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return new FilamentCsvImportResult(0, 0, 0, 0, ["CSV file is empty or missing header row"]);
        }

        // Parse header to get column indices
        string[] headers = ParseCsvLine(headerLine);
        Dictionary<string, int> headerMap = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
        {
            headerMap[headers[i].Trim()] = i;
        }

        // Require at least Name column
        if (!headerMap.ContainsKey("Name"))
        {
            return new FilamentCsvImportResult(0, 0, 0, 0, ["CSV must contain a 'Name' column"]);
        }

        // Load existing filament types for upsert matching
        IReadOnlyList<FilamentTypeDto> existing = await _repo.GetFilamentTypesAsync(ct);
        Dictionary<Guid, FilamentTypeDto> byId = existing.ToDictionary(f => f.Id);
        Dictionary<string, FilamentTypeDto> byName = existing.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);

        int created = 0;
        int updated = 0;
        int errorCount = 0;
        int totalRows = 0;
        List<string> errors = new();
        string? line;

        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            totalRows++;

            try
            {
                string[] values = ParseCsvLine(line);
                string name = GetCsvValue(values, headerMap, "Name").Trim();

                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add($"Row {totalRows}: Name is required");
                    errorCount++;
                    continue;
                }

                string idStr = GetCsvValue(values, headerMap, "Id");
                double? hotend = ParseDoubleOrNull(GetCsvValue(values, headerMap, "HotendTemp"));
                double? bed = ParseDoubleOrNull(GetCsvValue(values, headerMap, "BedTemp"));
                bool isAbrasive = ParseBool(GetCsvValue(values, headerMap, "IsAbrasive"));
                bool needsEnclosure = ParseBool(GetCsvValue(values, headerMap, "NeedsEnclosure"));
                double? pricePerKg = ParseDoubleOrNull(GetCsvValue(values, headerMap, "DefaultPricePerKg"));
                double? density = ParseDoubleOrNull(GetCsvValue(values, headerMap, "DefaultDensity"));

                // Upsert: match by Id first, then by Name
                FilamentTypeDto? match = null;
                if (Guid.TryParse(idStr, out Guid parsedId) && byId.TryGetValue(parsedId, out FilamentTypeDto? idMatch))
                {
                    match = idMatch;
                }
                else if (byName.TryGetValue(name, out FilamentTypeDto? nameMatch))
                {
                    match = nameMatch;
                }

                if (match != null)
                {
                    // Update existing
                    FilamentType? entity = await _repo.GetEntityByIdAsync(match.Id, ct);
                    if (entity != null)
                    {
                        entity.Name = name;
                        entity.DefaultHotendTemp = hotend ?? entity.DefaultHotendTemp;
                        entity.DefaultBedTemp = bed ?? entity.DefaultBedTemp;
                        entity.IsAbrasive = isAbrasive;
                        entity.NeedsEnclosure = needsEnclosure;
                        entity.DefaultPricePerKg = pricePerKg ?? entity.DefaultPricePerKg;
                        entity.DefaultDensity = density ?? entity.DefaultDensity;
                        await _repo.UpdateFilamentTypeAsync(entity, ct);
                        updated++;
                    }
                }
                else
                {
                    // Create new
                    FilamentType newFt = new()
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        DefaultHotendTemp = hotend ?? GetDefaultHotendTemp(name),
                        DefaultBedTemp = bed ?? GetDefaultBedTemp(name),
                        IsAbrasive = isAbrasive,
                        NeedsEnclosure = needsEnclosure,
                        DefaultPricePerKg = pricePerKg ?? GetDefaultPricePerKg(name),
                        DefaultDensity = density ?? GetDefaultDensity(name),
                        CreatedAt = DateTime.UtcNow
                    };
                    await _repo.AddFilamentTypeAsync(newFt, ct);

                    // Add to lookup for subsequent duplicate detection within same import
                    FilamentTypeDto newDto = new(newFt.Id, newFt.Name, new TempTargets(newFt.DefaultHotendTemp, newFt.DefaultBedTemp), newFt.IsAbrasive, newFt.NeedsEnclosure, newFt.DefaultPricePerKg, newFt.DefaultDensity);
                    byName[newFt.Name] = newDto;
                    created++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Row {totalRows}: {ex.Message}");
                errorCount++;
            }
        }

        await _repo.SaveChangesAsync(ct);

        return new FilamentCsvImportResult(created, updated, errorCount, totalRows, errors.ToArray());
    }

    /// <inheritdoc/>
    public async Task<SpoolmanDbImportResult> ImportFromSpoolmanDbAsync(
        SpoolmanDbImportRequest request,
        IReadOnlyList<SpoolmanDbFilamentEntry> allFilaments,
        CancellationToken ct)
    {
        if (request?.FilamentIds == null || request.FilamentIds.Length == 0)
        {
            return new SpoolmanDbImportResult(0, 0, 0, []);
        }

        // Index requested IDs
        HashSet<string> requestedIds = new(request.FilamentIds, StringComparer.OrdinalIgnoreCase);
        List<SpoolmanDbFilamentEntry> selected = allFilaments.Where(f => requestedIds.Contains(f.Id)).ToList();

        // Load existing Spoolman filaments to detect duplicates by external_id
        IReadOnlyList<SpoolmanFilamentDto> existingFilaments = await _spoolmanService.ListFilamentsAsync(ct);
        Dictionary<string, SpoolmanFilamentDto> byExternalId = existingFilaments
            .Where(f => !string.IsNullOrWhiteSpace(f.ExternalId))
            .ToDictionary(f => f.ExternalId!, f => f, StringComparer.OrdinalIgnoreCase);

        // Secondary lookup: match by (name, material, vendor) as fallback.
        // This prevents duplicates when external_id isn't returned by Spoolman
        // or when a filament was manually created before importing from SpoolmanDB.
        static string MakeCompositeKey(string? name, string? material, string? vendor) =>
            $"{name?.Trim()}|{material?.Trim()}|{vendor?.Trim()}".ToUpperInvariant();

        Dictionary<string, SpoolmanFilamentDto> byComposite = new(StringComparer.OrdinalIgnoreCase);
        foreach (SpoolmanFilamentDto f in existingFilaments)
        {
            string key = MakeCompositeKey(f.Name, f.Material, f.Vendor);
            byComposite.TryAdd(key, f);
        }

        // Load existing Spoolman vendors and build lookup by name (first-wins for duplicates)
        IReadOnlyList<SpoolmanVendorDto> existingVendors = await _spoolmanService.ListVendorsAsync(ct);
        Dictionary<string, SpoolmanVendorDto> vendorByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (SpoolmanVendorDto v in existingVendors)
        {
            vendorByName.TryAdd(v.Name, v);
        }

        int created = 0;
        int updated = 0;
        int errorCount = 0;
        List<string> errors = new();

        foreach (SpoolmanDbFilamentEntry entry in selected)
        {
            try
            {
                // Resolve or create vendor in Spoolman
                int? vendorId = null;
                if (!string.IsNullOrWhiteSpace(entry.Manufacturer))
                {
                    if (vendorByName.TryGetValue(entry.Manufacturer, out SpoolmanVendorDto? existingVendor))
                    {
                        vendorId = existingVendor.Id;
                    }
                    else
                    {
                        SpoolmanVendorDto newVendor = await _spoolmanService.CreateVendorAsync(entry.Manufacturer, null, ct);
                        vendorByName[entry.Manufacturer] = newVendor;
                        vendorId = newVendor.Id;
                    }
                }

                // Normalize color hex (remove # prefix if present)
                // Fall back to first color in color_hexes array for multi-color filaments
                string? colorHex = entry.ColorHex?.TrimStart('#');
                if (colorHex == null && entry.ColorHexes is { Length: > 0 })
                {
                    colorHex = entry.ColorHexes[0].TrimStart('#');
                }

                // Resolve temperatures: prefer single temp, fall back to average of range
                int? extruderTemp = entry.ExtruderTemp;
                int? bedTemp = entry.BedTemp;
                List<string> rangeNotes = new();

                if (!extruderTemp.HasValue && entry.ExtruderTempRange is { Length: 2 })
                {
                    extruderTemp = (int)(Math.Ceiling((entry.ExtruderTempRange[0] + entry.ExtruderTempRange[1]) / 2.0 / 5.0) * 5);
                    rangeNotes.Add($"Extruder range: {entry.ExtruderTempRange[0]}-{entry.ExtruderTempRange[1]}°C");
                }

                if (!bedTemp.HasValue && entry.BedTempRange is { Length: 2 })
                {
                    bedTemp = (int)(Math.Ceiling((entry.BedTempRange[0] + entry.BedTempRange[1]) / 2.0 / 5.0) * 5);
                    rangeNotes.Add($"Bed range: {entry.BedTempRange[0]}-{entry.BedTempRange[1]}°C");
                }

                // If we still have range data alongside single temps, note them too
                if (extruderTemp.HasValue && entry.ExtruderTemp.HasValue && entry.ExtruderTempRange is { Length: 2 })
                {
                    rangeNotes.Add($"Extruder range: {entry.ExtruderTempRange[0]}-{entry.ExtruderTempRange[1]}°C");
                }

                if (bedTemp.HasValue && entry.BedTemp.HasValue && entry.BedTempRange is { Length: 2 })
                {
                    rangeNotes.Add($"Bed range: {entry.BedTempRange[0]}-{entry.BedTempRange[1]}°C");
                }

                string? comment = rangeNotes.Count > 0
                    ? $"SpoolmanDB temp ranges: {string.Join("; ", rangeNotes)}"
                    : null;

                SpoolmanCreateFilamentRequest filamentRequest = new()
                {
                    Name = entry.Name,
                    VendorId = vendorId,
                    Material = entry.Material,
                    Density = entry.Density ?? 1.24d,
                    Diameter = entry.Diameter ?? 1.75d,
                    Weight = entry.Weight,
                    SpoolWeight = entry.SpoolWeight,
                    SettingsExtruderTemp = extruderTemp,
                    SettingsBedTemp = bedTemp,
                    ColorHex = colorHex,
                    ExternalId = entry.Id,
                    Comment = comment
                };

                // Check if filament with this external_id already exists → update
                if (byExternalId.TryGetValue(entry.Id, out SpoolmanFilamentDto? existingMatch))
                {
                    await _spoolmanService.UpdateFilamentInSpoolmanAsync(existingMatch.Id, filamentRequest, ct);
                    updated++;
                }
                else if (byComposite.TryGetValue(MakeCompositeKey(entry.Name, entry.Material, entry.Manufacturer), out SpoolmanFilamentDto? compositeMatch))
                {
                    // Secondary: match by (name, material, vendor) for filaments created before SpoolmanDB import
                    await _spoolmanService.UpdateFilamentInSpoolmanAsync(compositeMatch.Id, filamentRequest, ct);

                    // Move to external_id lookup for future imports
                    byExternalId[entry.Id] = compositeMatch;
                    byComposite.Remove(MakeCompositeKey(entry.Name, entry.Material, entry.Manufacturer));
                    updated++;
                }
                else
                {
                    SpoolmanFilamentDto newFilament = await _spoolmanService.CreateFilamentInSpoolmanAsync(filamentRequest, ct);
                    byExternalId[entry.Id] = newFilament;
                    created++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Filament '{entry.Id}': {ex.Message}");
                errorCount++;
            }
        }

        return new SpoolmanDbImportResult(created, updated, errorCount, errors.ToArray());
    }

    /// <summary>
    /// Imports selected entries from the Open Filament Database into Spoolman.
    /// Maps OFD data to Spoolman filament requests with vendor resolution and deduplication.
    /// </summary>
    public async Task<OfdImportResult> ImportFromOpenFilamentDbAsync(
        IReadOnlyList<OfdFlattenedEntry> entries, CancellationToken ct)
    {
        if (entries is null or { Count: 0 })
        {
            return new OfdImportResult(0, 0, 0, []);
        }

        // Load existing filaments for dedup
        IReadOnlyList<SpoolmanFilamentDto> existingFilaments = await _spoolmanService.ListFilamentsAsync(ct);
        Dictionary<string, SpoolmanFilamentDto> byExternalId = existingFilaments
            .Where(f => !string.IsNullOrWhiteSpace(f.ExternalId))
            .ToDictionary(f => f.ExternalId!, f => f, StringComparer.OrdinalIgnoreCase);

        static string MakeCompositeKey(string? name, string? material, string? vendor) =>
            $"{name?.Trim()}|{material?.Trim()}|{vendor?.Trim()}".ToUpperInvariant();

        Dictionary<string, SpoolmanFilamentDto> byComposite = new(StringComparer.OrdinalIgnoreCase);
        foreach (SpoolmanFilamentDto f in existingFilaments)
        {
            string key = MakeCompositeKey(f.Name, f.Material, f.Vendor);
            byComposite.TryAdd(key, f);
        }

        // Load existing vendors
        IReadOnlyList<SpoolmanVendorDto> existingVendors = await _spoolmanService.ListVendorsAsync(ct);
        Dictionary<string, SpoolmanVendorDto> vendorByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (SpoolmanVendorDto v in existingVendors)
        {
            vendorByName.TryAdd(v.Name, v);
        }

        int created = 0;
        int updated = 0;
        int errorCount = 0;
        List<string> errors = [];

        foreach (OfdFlattenedEntry entry in entries)
        {
            try
            {
                // Resolve or create vendor
                int? vendorId = null;
                if (!string.IsNullOrWhiteSpace(entry.BrandName))
                {
                    if (vendorByName.TryGetValue(entry.BrandName, out SpoolmanVendorDto? existing))
                    {
                        vendorId = existing.Id;
                    }
                    else
                    {
                        SpoolmanVendorDto newVendor = await _spoolmanService.CreateVendorAsync(entry.BrandName, null, ct);
                        vendorByName[entry.BrandName] = newVendor;
                        vendorId = newVendor.Id;
                    }
                }

                // Build display name: "FilamentName - ColorName (Weight)"
                string displayName = entry.FilamentName;
                if (!string.IsNullOrWhiteSpace(entry.ColorName))
                {
                    displayName = $"{entry.FilamentName} - {entry.ColorName}";
                }

                if (entry.Weight > 0)
                {
                    displayName += $" ({entry.Weight}g)";
                }

                // Resolve temperatures from min/max ranges
                int? extruderTemp = null;
                int? bedTemp = null;
                List<string> rangeNotes = [];

                if (entry.MinPrintTemp.HasValue && entry.MaxPrintTemp.HasValue)
                {
                    extruderTemp = (int)(Math.Ceiling((entry.MinPrintTemp.Value + entry.MaxPrintTemp.Value) / 2.0 / 5.0) * 5);
                    rangeNotes.Add($"Nozzle: {entry.MinPrintTemp}-{entry.MaxPrintTemp}°C");
                }

                if (entry.MinBedTemp.HasValue && entry.MaxBedTemp.HasValue)
                {
                    bedTemp = (int)(Math.Ceiling((entry.MinBedTemp.Value + entry.MaxBedTemp.Value) / 2.0 / 5.0) * 5);
                    rangeNotes.Add($"Bed: {entry.MinBedTemp}-{entry.MaxBedTemp}°C");
                }

                string? comment = rangeNotes.Count > 0
                    ? $"OFD temp ranges: {string.Join("; ", rangeNotes)}"
                    : null;

                string? colorHex = entry.ColorHex?.TrimStart('#');

                SpoolmanCreateFilamentRequest filamentRequest = new()
                {
                    Name = displayName,
                    VendorId = vendorId,
                    Material = entry.Material,
                    Density = entry.Density ?? 1.24d,
                    Diameter = entry.Diameter,
                    Weight = entry.Weight,
                    SettingsExtruderTemp = extruderTemp,
                    SettingsBedTemp = bedTemp,
                    ColorHex = colorHex,
                    ExternalId = entry.EntryId,
                    Comment = comment
                };

                // Dedup: check by ExternalId first, then composite key
                if (byExternalId.TryGetValue(entry.EntryId, out SpoolmanFilamentDto? match))
                {
                    await _spoolmanService.UpdateFilamentInSpoolmanAsync(match.Id, filamentRequest, ct);
                    updated++;
                }
                else if (byComposite.TryGetValue(MakeCompositeKey(displayName, entry.Material, entry.BrandName), out SpoolmanFilamentDto? compositeMatch))
                {
                    await _spoolmanService.UpdateFilamentInSpoolmanAsync(compositeMatch.Id, filamentRequest, ct);
                    byExternalId[entry.EntryId] = compositeMatch;
                    updated++;
                }
                else
                {
                    SpoolmanFilamentDto newFilament = await _spoolmanService.CreateFilamentInSpoolmanAsync(filamentRequest, ct);
                    byExternalId[entry.EntryId] = newFilament;
                    created++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"'{entry.BrandName} {entry.FilamentName} {entry.ColorName}': {ex.Message}");
                errorCount++;
            }
        }

        return new OfdImportResult(created, updated, errorCount, errors.ToArray());
    }

    #region Helper Methods

    /// <summary>
    /// Gets default hotend temperature for a material type.
    /// </summary>
    /// <param name="material">Material name (e.g., "PLA", "ABS", "PETG")</param>
    /// <returns>Default hotend temperature in Celsius</returns>
    /// <remarks>
    /// Temperature defaults:
    /// - PLA: 210°C
    /// - ABS: 240°C
    /// - PETG: 235°C
    /// - TPU/TPE: 220°C
    /// - Nylon: 250°C
    /// - ASA: 240°C
    /// - Default: 200°C (for unknown materials)
    /// </remarks>
    private static int GetDefaultHotendTemp(string material)
    {
        if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
        {
            return 205;
        }

        if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
        {
            return 230;
        }

        if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase))
        {
            return 240;
        }

        if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
        {
            return 245;
        }

        if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
        {
            return 235;
        }

        if (material.Contains("PC", StringComparison.OrdinalIgnoreCase) || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
        {
            return 260;
        }

        if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
        {
            return 220;
        }

        if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
        {
            return 210;
        }

        if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase))
        {
            return 250;
        }

        return material.Contains("CARBON", StringComparison.OrdinalIgnoreCase) ? 260 : 210;
    }

    /// <summary>
    /// Gets default bed temperature for a material type.
    /// </summary>
    /// <param name="material">Material name (e.g., "PLA", "ABS", "PETG")</param>
    /// <returns>Default bed temperature in Celsius</returns>
    /// <remarks>
    /// Temperature defaults:
    /// - PLA: 60°C
    /// - ABS: 100°C
    /// - PETG: 80°C
    /// - TPU/TPE: 50°C
    /// - Nylon: 80°C
    /// - ASA: 100°C
    /// - Default: 60°C (for unknown materials)
    /// </remarks>
    private static int GetDefaultBedTemp(string material)
    {
        if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase))
        {
            return 85;
        }

        if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (material.Contains("PC", StringComparison.OrdinalIgnoreCase) || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
        {
            return 110;
        }

        if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
        {
            return 65;
        }

        if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        return material.Contains("CARBON", StringComparison.OrdinalIgnoreCase) ? 100 : 70;
    }

    /// <summary>
    /// Gets default price per kilogram (USD) for a material type.
    /// Market-averaged across mainstream vendors (Jayo, Sunlu, Eryone, Ambrosia, Polymaker).
    /// </summary>
    private static double? GetDefaultPricePerKg(string material)
    {
        // CF composites (most specific first — PA6/PA12 variants before generic PA)
        if (material.Contains("PA6-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PA6CF", StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        if (material.Contains("PA12-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PA12CF", StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        if (material.Contains("PA-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PACF", StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        if (material.Contains("PC-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PCCF", StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        if (material.Contains("ABS-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("ABSCF", StringComparison.OrdinalIgnoreCase))
        {
            return 28;
        }

        if (material.Contains("ASA-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("ASACF", StringComparison.OrdinalIgnoreCase))
        {
            return 28;
        }

        if (material.Contains("PETG-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PETGCF", StringComparison.OrdinalIgnoreCase) ||
            material.Contains("PET-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PETCF", StringComparison.OrdinalIgnoreCase))
        {
            return 28;
        }

        if (material.Contains("PLA-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PLACF", StringComparison.OrdinalIgnoreCase) ||
            (material.Contains("PLA", StringComparison.OrdinalIgnoreCase) && material.Contains("CARBON", StringComparison.OrdinalIgnoreCase)))
        {
            return 28;
        }

        // GF composites
        if (material.Contains("PA6-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("PA6GF", StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        if (material.Contains("PA-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("PAGF", StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        if (material.Contains("PP-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("PPGF", StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        if (material.Contains("ABS-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("ABSGF", StringComparison.OrdinalIgnoreCase))
        {
            return 28;
        }

        if (material.Contains("ASA-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("ASAGF", StringComparison.OrdinalIgnoreCase))
        {
            return 28;
        }

        // PLA variants
        if (material.Contains("SILK", StringComparison.OrdinalIgnoreCase) && material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
        {
            return 18;
        }

        if (material.Contains("MATTE", StringComparison.OrdinalIgnoreCase) && material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
        {
            return 18;
        }

        if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
        {
            return 22;
        }

        if (material.Contains("PLA+", StringComparison.OrdinalIgnoreCase) || material.Contains("PLA PRO", StringComparison.OrdinalIgnoreCase))
        {
            return 18;
        }

        if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
        {
            return 16;
        }

        // Engineering materials (multi-char codes before short codes)
        if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
        {
            return 24;
        }

        if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase) || material.Contains("PET", StringComparison.OrdinalIgnoreCase))
        {
            return 18;
        }

        if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
        {
            return 22;
        }

        if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
        {
            return 16;
        }

        if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
        {
            return 24;
        }

        if (material.Contains("PVA", StringComparison.OrdinalIgnoreCase))
        {
            return 45;
        }

        if (material.Contains("HIPS", StringComparison.OrdinalIgnoreCase))
        {
            return 18;
        }

        // Short/ambiguous codes — check specific variants first, then word-boundary match
        if (material.Contains("PA12", StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        if (material.Contains("PA6", StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        if (MatchesMaterialWord(material, "PC") || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase) || MatchesMaterialWord(material, "PA"))
        {
            return 30;
        }

        if (MatchesMaterialWord(material, "PP") || material.Contains("POLYPROPYLENE", StringComparison.OrdinalIgnoreCase))
        {
            return 26;
        }

        if (material.Contains("CARBON", StringComparison.OrdinalIgnoreCase))
        {
            return 28;
        }

        return null;
    }

    /// <summary>
    /// Gets default density (g/cm³) for a material type.
    /// </summary>
    private static double? GetDefaultDensity(string material)
    {
        // CF composites (most specific first — PA6/PA12 variants before generic PA)
        if (material.Contains("PA6-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PA6CF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.15;
        }

        if (material.Contains("PA12-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PA12CF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.12;
        }

        if (material.Contains("PA-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PACF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.15;
        }

        if (material.Contains("PC-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PCCF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.22;
        }

        if (material.Contains("ABS-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("ABSCF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.10;
        }

        if (material.Contains("ASA-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("ASACF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.12;
        }

        if (material.Contains("PETG-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PETGCF", StringComparison.OrdinalIgnoreCase) ||
            material.Contains("PET-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PETCF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.30;
        }

        if (material.Contains("PLA-CF", StringComparison.OrdinalIgnoreCase) || material.Contains("PLACF", StringComparison.OrdinalIgnoreCase) ||
            (material.Contains("PLA", StringComparison.OrdinalIgnoreCase) && material.Contains("CARBON", StringComparison.OrdinalIgnoreCase)))
        {
            return 1.29;
        }

        // GF composites
        if (material.Contains("PA6-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("PA6GF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.20;
        }

        if (material.Contains("PA-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("PAGF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.20;
        }

        if (material.Contains("PP-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("PPGF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.05;
        }

        if (material.Contains("ABS-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("ABSGF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.15;
        }

        if (material.Contains("ASA-GF", StringComparison.OrdinalIgnoreCase) || material.Contains("ASAGF", StringComparison.OrdinalIgnoreCase))
        {
            return 1.18;
        }

        // Base materials
        if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
        {
            return 1.15;
        }

        if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
        {
            return 1.24;
        }

        if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
        {
            return 1.23;
        }

        if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase) || material.Contains("PET", StringComparison.OrdinalIgnoreCase))
        {
            return 1.27;
        }

        if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
        {
            return 1.07;
        }

        if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
        {
            return 1.04;
        }

        if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
        {
            return 1.21;
        }

        if (material.Contains("PVA", StringComparison.OrdinalIgnoreCase))
        {
            return 1.23;
        }

        if (material.Contains("HIPS", StringComparison.OrdinalIgnoreCase))
        {
            return 1.04;
        }

        // Short/ambiguous codes — check specific variants first, then word-boundary match
        if (material.Contains("PA12", StringComparison.OrdinalIgnoreCase))
        {
            return 1.02;
        }

        if (material.Contains("PA6", StringComparison.OrdinalIgnoreCase))
        {
            return 1.14;
        }

        if (MatchesMaterialWord(material, "PC") || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
        {
            return 1.20;
        }

        if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase) || MatchesMaterialWord(material, "PA"))
        {
            return 1.14;
        }

        if (MatchesMaterialWord(material, "PP") || material.Contains("POLYPROPYLENE", StringComparison.OrdinalIgnoreCase))
        {
            return 0.90;
        }

        return null;
    }

    /// <summary>
    /// Checks if a material name contains a short code (e.g. "PC", "PA", "PP") as a distinct word,
    /// preventing false positives like "SPACE" matching "PA" or "COPPER" matching "PP".
    /// </summary>
    private static bool MatchesMaterialWord(string material, string code)
    {
        int idx = material.IndexOf(code, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            bool leftBoundary = idx == 0 || !char.IsLetter(material[idx - 1]);
            bool rightBoundary = idx + code.Length >= material.Length || !char.IsLetter(material[idx + code.Length]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            idx = material.IndexOf(code, idx + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Escapes a CSV field value, wrapping in quotes if necessary.</summary>
    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    /// <summary>Parses a CSV line respecting quoted fields.</summary>
    private static string[] ParseCsvLine(string line)
    {
        List<string> fields = new();
        bool inQuotes = false;
        StringBuilder current = new();

        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++; // skip escaped quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }

            i++;
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    /// <summary>Gets a CSV value by column name, returns empty string if not found.</summary>
    private static string GetCsvValue(string[] values, Dictionary<string, int> headerMap, string column)
    {
        return headerMap.TryGetValue(column, out int idx) && idx < values.Length
            ? values[idx].Trim()
            : string.Empty;
    }

    /// <summary>Parses a double from string, returns null if empty or invalid.</summary>
    private static double? ParseDoubleOrNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null
            : double.TryParse(value, CultureInfo.InvariantCulture, out double result) ? result : null;
    }

    /// <summary>Parses a boolean from string (handles true/false, yes/no, 1/0).</summary>
    private static bool ParseBool(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value == "1");
    }

    #endregion

    #region External Material Sync

    /// <summary>
    /// Syncs external materials from Spoolman's SpoolmanDB endpoint as local filament types.
    /// Uses upsert logic: creates new filament types for unknown materials, updates temperatures for existing ones.
    /// </summary>
    public async Task<SpoolmanDbImportResult> SyncExternalMaterialsAsync(IReadOnlyList<SpoolmanDbMaterialEntry> materials, CancellationToken ct)
    {
        int created = 0;
        int updated = 0;
        int errorCount = 0;
        List<string> errors = [];

        foreach (SpoolmanDbMaterialEntry mat in materials)
        {
            if (string.IsNullOrWhiteSpace(mat.Material))
            {
                errorCount++;
                errors.Add("Skipped entry with empty material name");
                continue;
            }

            string name = mat.Material.Trim();

            try
            {
                int hotend = mat.ExtruderTemp ?? 200;
                int bed = mat.BedTemp ?? 60;

                FilamentType? existing = await _repo.GetByNameAsync(name, ct);
                if (existing == null)
                {
                    FilamentType filamentType = new()
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        DefaultHotendTemp = hotend,
                        DefaultBedTemp = bed,
                        CreatedAt = DateTime.UtcNow,
                    };
                    await _repo.AddFilamentTypeAsync(filamentType, ct);
                    created++;
                }
                else
                {
                    existing.DefaultHotendTemp = hotend;
                    existing.DefaultBedTemp = bed;
                    await _repo.UpdateFilamentTypeAsync(existing, ct);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errorCount++;
                errors.Add($"{name}: {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new SpoolmanDbImportResult(created, updated, errorCount, [.. errors]);
    }

    #endregion
}

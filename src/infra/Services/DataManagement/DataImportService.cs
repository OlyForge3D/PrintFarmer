using System.Diagnostics;
using System.Globalization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Infrastructure.Services.Printers;
using Farm.Modules.Abstractions.Normalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.DataManagement;

/// <summary>
/// Service for importing database data from JSON format for backup/restore functionality
/// </summary>
public class DataImportService : IDataImportService
{
    private readonly AppDbContext _context;
    private readonly ILogger<DataImportService> _logger;
    private readonly Farm.Infrastructure.Services.Security.ISensitiveDataProtector _sensitiveDataProtector;
    private readonly IPrintersRepository _printersRepository;

    public DataImportService(
        AppDbContext context,
        ILogger<DataImportService> logger,
        Farm.Infrastructure.Services.Security.ISensitiveDataProtector sensitiveDataProtector,
        IPrintersRepository printersRepository)
    {
        _context = context;
        _logger = logger;
        _sensitiveDataProtector = sensitiveDataProtector;
        _printersRepository = printersRepository;
    }

    private string? ProtectIfNeeded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return _sensitiveDataProtector.Protect(value);
    }

    public async Task<ImportResponseDto> ImportCatalogAsync(CatalogExportDto catalog, ImportMode mode = ImportMode.Merge, CancellationToken ct = default)
    {
        _logger.LogInformation("[DataImport] Starting catalog import in mode: {Mode}", mode);
        Stopwatch sw = Stopwatch.StartNew();

        ImportResponseDto response = new() { Success = true };
        ImportStatistics stats = new();

        try
        {
            // If Replace mode, delete all existing catalog data first
            if (mode == ImportMode.Replace)
            {
                await DeleteAllCatalogDataAsync(ct);
                response.Warnings.Add("All existing catalog data has been deleted as per Replace mode");
            }

            // Import in correct order to maintain referential integrity
            stats.ManufacturersImported = await ImportManufacturersAsync(catalog.Manufacturers, mode, response.Errors, ct);
            stats.FilamentTypesImported = await ImportFilamentTypesAsync(catalog.FilamentTypes, mode, response.Errors, ct);
            stats.PrinterModelsImported = await ImportPrinterModelsAsync(catalog.PrinterModels, mode, response.Errors, ct);
            stats.HotendsImported = await ImportHotendsAsync(catalog.Hotends, mode, response.Errors, ct);
            stats.ExtrudersImported = await ImportExtrudersAsync(catalog.Extruders, mode, response.Errors, ct);
            stats.ToolheadsImported = await ImportToolheadsAsync(catalog.Toolheads, mode, response.Errors, ct);
            stats.NozzlesImported = await ImportNozzlesAsync(catalog.Nozzles, mode, response.Errors, ct);

            stats.TotalItemsImported = stats.ManufacturersImported + stats.FilamentTypesImported +
                                       stats.PrinterModelsImported + stats.HotendsImported +
                                       stats.ExtrudersImported + stats.ToolheadsImported + stats.NozzlesImported;

            sw.Stop();
            stats.Duration = sw.Elapsed;
            response.Statistics = stats;

            _logger.LogInformation("[DataImport] Catalog import complete: {StatsTotalItemsImported} items imported in {SwElapsedMilliseconds}ms", stats.TotalItemsImported, sw.ElapsedMilliseconds);

            if (response.Errors.Count > 0)
            {
                response.Success = false;
                _logger.LogWarning("[DataImport] Import completed with {Count} errors", response.Errors.Count);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            stats.Duration = sw.Elapsed;
            response.Statistics = stats;
            response.Success = false;
            response.Errors.Add($"Critical error during import: {ex.Message}");
            _logger.LogError(ex, "[DataImport] Critical error during catalog import: {Message}", ex.Message);
            _context.ChangeTracker.Clear();
        }

        return response;
    }

    public async Task<ImportResponseDto> ImportFullBackupAsync(FullBackupExportDto backup, ImportMode mode = ImportMode.Merge, CancellationToken ct = default)
    {
        _logger.LogInformation("[DataImport] Starting full backup import in mode: {Mode}", mode);
        Stopwatch sw = Stopwatch.StartNew();

        ImportResponseDto response = new() { Success = true };
        ImportStatistics stats = new();
        IDbContextTransaction? replaceTransaction = null;

        try
        {
            if (mode == ImportMode.Replace)
            {
                replaceTransaction = _context.Database.IsRelational()
                    ? await _context.Database.BeginTransactionAsync(ct)
                    : null;

                await DeleteAllPrintersAsync(ct);
                await DeleteAllLocationsAsync(ct);
                response.Warnings.Add("All existing printers and locations have been deleted as per Replace mode");
            }

            // First import catalog
            ImportResponseDto catalogResult = await ImportCatalogAsync(backup.Catalog, mode, ct);

            // Merge catalog statistics
            stats.ManufacturersImported = catalogResult.Statistics.ManufacturersImported;
            stats.FilamentTypesImported = catalogResult.Statistics.FilamentTypesImported;
            stats.PrinterModelsImported = catalogResult.Statistics.PrinterModelsImported;
            stats.HotendsImported = catalogResult.Statistics.HotendsImported;
            stats.ExtrudersImported = catalogResult.Statistics.ExtrudersImported;
            stats.ToolheadsImported = catalogResult.Statistics.ToolheadsImported;
            stats.NozzlesImported = catalogResult.Statistics.NozzlesImported;

            // Copy catalog errors and warnings
            response.Errors.AddRange(catalogResult.Errors);
            response.Warnings.AddRange(catalogResult.Warnings);

            if (!catalogResult.Success && mode == ImportMode.Replace)
            {
                response.Success = false;
            }
            else
            {
                // Import locations and printers
                stats.LocationsImported = await ImportLocationsAsync(backup.Locations, mode, response.Errors, ct);
                stats.PrintersImported = await ImportPrintersAsync(backup.Printers, mode, response.Errors, ct);
            }

            stats.TotalItemsImported = stats.ManufacturersImported + stats.FilamentTypesImported +
                                       stats.PrinterModelsImported + stats.HotendsImported +
                                       stats.ExtrudersImported + stats.ToolheadsImported +
                                       stats.NozzlesImported + stats.LocationsImported + stats.PrintersImported;

            sw.Stop();
            stats.Duration = sw.Elapsed;
            response.Statistics = stats;

            _logger.LogInformation("[DataImport] Full backup import complete: {StatsTotalItemsImported} items imported in {SwElapsedMilliseconds}ms", stats.TotalItemsImported, sw.ElapsedMilliseconds);

            if (response.Errors.Count > 0)
            {
                response.Success = false;
                _logger.LogWarning("[DataImport] Import completed with {Count} errors", response.Errors.Count);
            }

            if (replaceTransaction is not null)
            {
                if (response.Success)
                {
                    await replaceTransaction.CommitAsync(ct);
                }
                else
                {
                    await replaceTransaction.RollbackAsync(CancellationToken.None);
                    _context.ChangeTracker.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            if (replaceTransaction is not null)
            {
                try
                {
                    await replaceTransaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(
                        rollbackException,
                        "[DataImport] Failed to roll back full backup Replace transaction");
                }
            }

            _context.ChangeTracker.Clear();
            sw.Stop();
            stats.Duration = sw.Elapsed;
            response.Statistics = stats;
            response.Success = false;
            response.Errors.Add($"Critical error during import: {ex.Message}");
            _logger.LogError(ex, "[DataImport] Critical error during full backup import: {Message}", ex.Message);
        }
        finally
        {
            if (replaceTransaction is not null)
            {
                await replaceTransaction.DisposeAsync();
            }
        }

        return response;
    }

    // Private helper methods for importing specific entity types
    private async Task<int> ImportManufacturersAsync(List<ManufacturerExportDto> manufacturers, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (ManufacturerExportDto dto in manufacturers)
        {
            try
            {
                string normalized = CatalogNameNormalizer.NormalizeManufacturer(dto.Name);
                Manufacturer? existing = await _context.Manufacturers
                    .FirstOrDefaultAsync(m => m.Name == normalized, ct);

                if (existing == null)
                {
                    _context.Manufacturers.Add(new Manufacturer
                    {
                        Id = Guid.NewGuid(),
                        Name = normalized,
                        Url = dto.Url,
                        Description = dto.Description,
                        IsActive = dto.IsActive
                    });
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    // Update existing in replace mode
                    existing.Url = dto.Url;
                    existing.Description = dto.Description;
                    existing.IsActive = dto.IsActive;
                    imported++;
                }

                // In merge mode, skip existing
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import manufacturer '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing manufacturer: {Name}", LogSanitizer.Sanitize(dto.Name));
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    private async Task<int> ImportFilamentTypesAsync(List<FilamentTypeExportDto> filamentTypes, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (FilamentTypeExportDto dto in filamentTypes)
        {
            try
            {
                FilamentType? existing = await _context.FilamentTypes
                    .FirstOrDefaultAsync(f => f.Name == dto.Name, ct);

                if (existing == null)
                {
                    _context.FilamentTypes.Add(new FilamentType
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        DefaultHotendTemp = dto.DefaultHotendTemp,
                        DefaultBedTemp = dto.DefaultBedTemp,
                        IsAbrasive = dto.IsAbrasive,
                        NeedsEnclosure = dto.NeedsEnclosure,
                        DefaultPricePerKg = dto.DefaultPricePerKg,
                        DefaultDensity = dto.DefaultDensity
                    });
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    // Update existing in replace mode
                    existing.DefaultHotendTemp = dto.DefaultHotendTemp;
                    existing.DefaultBedTemp = dto.DefaultBedTemp;
                    existing.IsAbrasive = dto.IsAbrasive;
                    existing.NeedsEnclosure = dto.NeedsEnclosure;
                    existing.DefaultPricePerKg = dto.DefaultPricePerKg;
                    existing.DefaultDensity = dto.DefaultDensity;
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import filament type '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing filament type: {Name}", LogSanitizer.Sanitize(dto.Name));
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    private async Task<int> ImportPrinterModelsAsync(List<PrinterModelExportDto> printerModels, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (PrinterModelExportDto dto in printerModels)
        {
            try
            {
                // Find manufacturer
                string normalizedManufacturer = CatalogNameNormalizer.NormalizeManufacturer(dto.ManufacturerName);
                Manufacturer? manufacturer = await _context.Manufacturers
                    .FirstOrDefaultAsync(m => m.Name == normalizedManufacturer, ct);

                if (manufacturer == null)
                {
                    errors.Add($"Manufacturer '{dto.ManufacturerName}' not found for printer model '{dto.Name}'");
                    continue;
                }

                PrinterModel? existing = await _context.PrinterModels
                    .Include(pm => pm.SupportedFilamentTypes)
                    .FirstOrDefaultAsync(pm => pm.Name == dto.Name && pm.ManufacturerId == manufacturer.Id, ct);

                if (existing == null)
                {
                    PrinterModel newModel = new()
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        ManufacturerId = manufacturer.Id,
                        MotionType = dto.MotionType,
                        MaxX = dto.MaxX,
                        MaxY = dto.MaxY,
                        MaxZ = dto.MaxZ,
                        DefaultBackend = dto.DefaultBackend,
                        HasHeatedBed = dto.HasHeatedBed,
                        HasEnclosure = dto.HasEnclosure,
                        MultiMaterial = dto.MultiMaterial,
                        SupportsAutoLeveling = dto.SupportsAutoLeveling,
                        MaxBedTemp = dto.MaxBedTemp,
                        MaxPrintSpeed = dto.MaxPrintSpeed,
                        IsActive = dto.IsActive
                    };
                    _context.PrinterModels.Add(newModel);

                    // Add supported filament types using EF Core skip navigation
                    foreach (string filamentName in dto.SupportedFilamentTypes)
                    {
                        FilamentType? filamentType = await _context.FilamentTypes
                            .FirstOrDefaultAsync(f => f.Name == filamentName, ct);
                        if (filamentType != null)
                        {
                            newModel.SupportedFilamentTypes.Add(filamentType);
                        }
                    }

                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    // Update existing in replace mode
                    existing.MotionType = dto.MotionType;
                    existing.MaxX = dto.MaxX;
                    existing.MaxY = dto.MaxY;
                    existing.MaxZ = dto.MaxZ;
                    existing.DefaultBackend = dto.DefaultBackend;
                    existing.HasHeatedBed = dto.HasHeatedBed;
                    existing.HasEnclosure = dto.HasEnclosure;
                    existing.MultiMaterial = dto.MultiMaterial;
                    existing.SupportsAutoLeveling = dto.SupportsAutoLeveling;
                    existing.MaxBedTemp = dto.MaxBedTemp;
                    existing.MaxPrintSpeed = dto.MaxPrintSpeed;
                    existing.IsActive = dto.IsActive;

                    // Update supported filament types using EF Core skip navigation
                    existing.SupportedFilamentTypes.Clear();
                    foreach (string filamentName in dto.SupportedFilamentTypes)
                    {
                        FilamentType? filamentType = await _context.FilamentTypes
                            .FirstOrDefaultAsync(f => f.Name == filamentName, ct);
                        if (filamentType != null)
                        {
                            existing.SupportedFilamentTypes.Add(filamentType);
                        }
                    }

                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import printer model '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing printer model: {Name}", LogSanitizer.Sanitize(dto.Name));
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    private async Task<int> ImportHotendsAsync(List<HotendModelExportDto> hotends, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (HotendModelExportDto dto in hotends)
        {
            try
            {
                string normalizedManufacturer = CatalogNameNormalizer.NormalizeManufacturer(dto.ManufacturerName);
                Manufacturer? manufacturer = await _context.Manufacturers
                    .FirstOrDefaultAsync(m => m.Name == normalizedManufacturer, ct);

                if (manufacturer == null)
                {
                    errors.Add($"Manufacturer '{dto.ManufacturerName}' not found for hotend '{dto.Name}'");
                    continue;
                }

                HotendModelDefinition? existing = await _context.HotendModelDefinitions
                    .FirstOrDefaultAsync(h => h.Name == dto.Name && h.ManufacturerId == manufacturer.Id, ct);

                if (existing == null)
                {
                    _context.HotendModelDefinitions.Add(new HotendModelDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        ManufacturerId = manufacturer.Id,
                        MaxTemp = dto.MaxTemp,
                        IsHighFlow = dto.IsHighFlow,
                        MaxFlowRate = dto.MaxFlowRate,
                        Description = dto.Description,
                        Url = dto.Url
                    });
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    existing.MaxTemp = dto.MaxTemp;
                    existing.IsHighFlow = dto.IsHighFlow;
                    existing.MaxFlowRate = dto.MaxFlowRate;
                    existing.Description = dto.Description;
                    existing.Url = dto.Url;
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import hotend '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing hotend: {Name}", LogSanitizer.Sanitize(dto.Name));
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    private async Task<int> ImportExtrudersAsync(List<ExtruderModelExportDto> extruders, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (ExtruderModelExportDto dto in extruders)
        {
            try
            {
                string normalizedManufacturer = CatalogNameNormalizer.NormalizeManufacturer(dto.ManufacturerName);
                Manufacturer? manufacturer = await _context.Manufacturers
                    .FirstOrDefaultAsync(m => m.Name == normalizedManufacturer, ct);

                if (manufacturer == null)
                {
                    errors.Add($"Manufacturer '{dto.ManufacturerName}' not found for extruder '{dto.Name}'");
                    continue;
                }

                ExtruderModelDefinition? existing = await _context.ExtruderModelDefinitions
                    .FirstOrDefaultAsync(e => e.Name == dto.Name && e.ManufacturerId == manufacturer.Id, ct);

                if (existing == null)
                {
                    _context.ExtruderModelDefinitions.Add(new ExtruderModelDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        ManufacturerId = manufacturer.Id,
                        GearRatio = dto.GearRatio,
                        IsDirectDrive = dto.IsDirectDrive,
                        Description = dto.Description
                    });
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    existing.GearRatio = dto.GearRatio;
                    existing.IsDirectDrive = dto.IsDirectDrive;
                    existing.Description = dto.Description;
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import extruder '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing extruder: {Name}", LogSanitizer.Sanitize(dto.Name));
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    private async Task<int> ImportToolheadsAsync(List<ToolheadModelExportDto> toolheads, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (ToolheadModelExportDto dto in toolheads)
        {
            try
            {
                string normalizedManufacturer = CatalogNameNormalizer.NormalizeManufacturer(dto.ManufacturerName);
                Manufacturer? manufacturer = await _context.Manufacturers
                    .FirstOrDefaultAsync(m => m.Name == normalizedManufacturer, ct);

                if (manufacturer == null)
                {
                    errors.Add($"Manufacturer '{dto.ManufacturerName}' not found for toolhead '{dto.Name}'");
                    continue;
                }

                ToolheadModelDefinition? existing = await _context.ToolheadModelDefinitions
                    .FirstOrDefaultAsync(t => t.Name == dto.Name && t.ManufacturerId == manufacturer.Id, ct);

                if (existing == null)
                {
                    _context.ToolheadModelDefinitions.Add(new ToolheadModelDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        ManufacturerId = manufacturer.Id,
                        Description = dto.Description
                    });
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    existing.Description = dto.Description;
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import toolhead '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing toolhead: {Name}", LogSanitizer.Sanitize(dto.Name));
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    private async Task<int> ImportNozzlesAsync(List<NozzleModelExportDto> nozzles, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (NozzleModelExportDto dto in nozzles)
        {
            try
            {
                string normalizedManufacturer = CatalogNameNormalizer.NormalizeManufacturer(dto.ManufacturerName);
                Manufacturer? manufacturer = await _context.Manufacturers
                    .FirstOrDefaultAsync(m => m.Name == normalizedManufacturer, ct);

                if (manufacturer == null)
                {
                    errors.Add($"Manufacturer '{dto.ManufacturerName}' not found for nozzle '{dto.Name}'");
                    continue;
                }

                NozzleModelDefinition? existing = await _context.NozzleModelDefinitions
                    .FirstOrDefaultAsync(n => n.Name == dto.Name && n.ManufacturerId == manufacturer.Id, ct);

                // NozzleType is resolved directly against the NozzleMaterial catalog by name — an
                // open string set, not the closed legacy enum (epic #1823 / issue #1826). A null
                // or blank value means the backup pre-dates this field and restores as "Brass";
                // any other value that doesn't match a catalog row rejects the row rather than
                // guessing, exactly like the enum-backed fields below.
                string nozzleTypeName = string.IsNullOrWhiteSpace(dto.NozzleType) ? "Brass" : dto.NozzleType.Trim();

                NozzleMaterial? nozzleMaterial = await _context.NozzleMaterials
                    .FirstOrDefaultAsync(m => m.Name == nozzleTypeName, ct);
                if (nozzleMaterial is null)
                {
                    errors.Add(
                        $"Failed to import nozzle '{LogSanitizer.Sanitize(dto.Name)}': invalid nozzleType '{LogSanitizer.Sanitize(nozzleTypeName)}' (no matching NozzleMaterial catalog entry)");
                    continue;
                }

                // A present-but-unparseable value is corruption, not a legacy backup, and must
                // not fall back: a misspelled "NotHardened" on an abrasion-resistant material
                // would resolve through Auto back to hardened, silently re-admitting the nozzle
                // to abrasive dispatch. Reject the row instead so the operator is told.
                if (!TryParseExportedEnum(
                        dto.HardnessOverride, NozzleHardnessOverride.Auto, out NozzleHardnessOverride hardnessOverride))
                {
                    errors.Add(
                        $"Failed to import nozzle '{LogSanitizer.Sanitize(dto.Name)}': unrecognized hardnessOverride '{LogSanitizer.Sanitize(dto.HardnessOverride)}'");
                    continue;
                }

                // NozzleInterface is now name-based on export, given the same versioned/name-based
                // treatment as NozzleType/HardnessOverride (epic #1823 / issue #1826). Older
                // backups wrote this as a raw ordinal number; the export DTO's
                // NozzleInterfaceExportJsonConverter already normalizes that to a name string on
                // deserialization from JSON, so this parse only needs to handle names here.
                if (!TryParseExportedEnum(
                        dto.NozzleInterface, NozzleInterfaceType.V6, out NozzleInterfaceType nozzleInterface))
                {
                    errors.Add(
                        $"Failed to import nozzle '{LogSanitizer.Sanitize(dto.Name)}': unrecognized nozzleInterface '{LogSanitizer.Sanitize(dto.NozzleInterface)}'");
                    continue;
                }

                if (existing == null)
                {
                    _context.NozzleModelDefinitions.Add(new NozzleModelDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        ManufacturerId = manufacturer.Id,
                        Diameter = dto.Diameter,
                        MaxTemp = dto.MaxTemp,
                        NozzleMaterialId = nozzleMaterial.Id,
                        HardnessOverride = hardnessOverride,
                        NozzleInterface = nozzleInterface,
                        Description = dto.Description
                    });
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    existing.Diameter = dto.Diameter;
                    existing.MaxTemp = dto.MaxTemp;
                    existing.NozzleMaterialId = nozzleMaterial.Id;
                    existing.HardnessOverride = hardnessOverride;
                    existing.NozzleInterface = nozzleInterface;
                    existing.Description = dto.Description;
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import nozzle '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing nozzle: {Name}", LogSanitizer.Sanitize(dto.Name));
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    /// <summary>
    /// Parses an enum exported by name, distinguishing an absent field from a corrupt one.
    /// <para>
    /// A null or empty value means the backup pre-dates the field, which is benign and yields
    /// <paramref name="fallback"/>. A present-but-unparseable value means corruption or a newer
    /// schema; that returns <c>false</c> so the caller can reject the row rather than silently
    /// substituting a default, which for hardness could be the unsafe direction.
    /// </para>
    /// </summary>
    /// <typeparam name="TEnum">The enum type to parse into.</typeparam>
    /// <param name="rawValue">Exported enum name, or null for a pre-field backup.</param>
    /// <param name="fallback">Value to use when the field is absent.</param>
    /// <param name="value">The parsed value, or <paramref name="fallback"/> when absent.</param>
    /// <returns><c>false</c> only when a non-empty value could not be parsed.</returns>
    private static bool TryParseExportedEnum<TEnum>(string? rawValue, TEnum fallback, out TEnum value)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            value = fallback;
            return true;
        }

        // Reject numeric input outright. Enum.TryParse happily maps "5" onto a defined
        // member, which would quietly reintroduce the ordinal coupling that exporting by
        // name exists to avoid — Enum.IsDefined only rejects *undefined* ordinals.
        if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            value = fallback;
            return false;
        }

        if (Enum.TryParse(rawValue, true, out TEnum parsed) && Enum.IsDefined(parsed))
        {
            value = parsed;
            return true;
        }

        value = fallback;
        return false;
    }

    private async Task<int> ImportLocationsAsync(List<LocationExportDto> locations, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (LocationExportDto dto in locations)
        {
            try
            {
                if (dto.Name.Contains('/', StringComparison.Ordinal))
                {
                    // A '/' in Name would be indistinguishable from a path separator in the
                    // materialized Path, letting this location be misidentified as a descendant
                    // of an unrelated location during subtree Path-prefix matching. Reject it
                    // here the same way LocationService.CreateLocationAsync/UpdateLocationAsync
                    // do for API-created locations.
                    errors.Add($"Failed to import location '{dto.Name}': location name cannot contain '/'.");
                    continue;
                }

                Location? existing = await _context.Locations
                    .FirstOrDefaultAsync(l => l.Name == dto.Name, ct);

                if (existing == null)
                {
                    // Imports never carry hierarchy information (LocationExportDto has no
                    // ParentId), so every imported location is a top-level node. Compute Path
                    // the same way LocationService.CreateLocationAsync does for a root location
                    // instead of leaving the Location.Path/Depth defaults ("/", 0-as-unset)
                    // unpopulated, which would make every subtree query treat this location as
                    // an ancestor of every other active location in the table.
                    _context.Locations.Add(new Location
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        Description = dto.Description,
                        Path = $"/{dto.Name}",
                        Depth = 0
                    });
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    existing.Description = dto.Description;
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import location '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing location: {Name}", LogSanitizer.Sanitize(dto.Name));
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    private async Task<int> ImportPrintersAsync(List<PrinterExportDto> printers, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (PrinterExportDto dto in printers)
        {
            try
            {
                // Find model and location
                PrinterModel? model = null;
                if (!string.IsNullOrEmpty(dto.ModelName))
                {
                    model = await _context.PrinterModels
                        .FirstOrDefaultAsync(pm => pm.Name == dto.ModelName, ct);
                }

                Location? location = null;
                if (!string.IsNullOrEmpty(dto.LocationName))
                {
                    location = await _context.Locations
                        .FirstOrDefaultAsync(l => l.Name == dto.LocationName, ct);
                }

                Printer? existing = await _context.Printers
                    .Include(printer => printer.Toolheads)
                    .FirstOrDefaultAsync(p => p.Name == dto.Name, ct);

                string? apiKey = dto.ApiKey;
                string? username = dto.Username;
                string? password = dto.Password;

                // Backward compatibility: some legacy exports stored PrusaLink password in ApiKey.
                if (dto.Backend == (int)PrinterBackend.PrusaLink && string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(apiKey))
                {
                    password = apiKey;
                    apiKey = null;
                }

                // Default username for PrusaLink digest auth if password is present.
                if (dto.Backend == (int)PrinterBackend.PrusaLink && !string.IsNullOrWhiteSpace(password) && string.IsNullOrWhiteSpace(username))
                {
                    username = "maker";
                }

                if (existing == null)
                {
                    Guid manufacturerId = model?.ManufacturerId ?? Guid.Empty;
                    Guid modelId = model?.Id ?? Guid.Empty;

                    var importedPrinter = new Printer
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        ServerUrl = dto.ServerUrl,
                        OriginalServerUrl = dto.OriginalServerUrl,
                        BackendPort = dto.BackendPort,
                        FrontendPort = dto.FrontendPort,
                        ManufacturerId = manufacturerId,
                        ModelId = modelId,
                        LocationId = location?.Id,
                        Backend = dto.Backend,
                        IsAvailable = false, // Always set to unavailable on import for safety
                        ApiKey = ProtectIfNeeded(apiKey),
                        Username = username,
                        Password = ProtectIfNeeded(password)
                    };
                    _ = PerToolAttributionCapability.Refresh(importedPrinter);
                    _context.Printers.Add(importedPrinter);
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    existing.ServerUrl = dto.ServerUrl;
                    existing.OriginalServerUrl = dto.OriginalServerUrl;
                    existing.BackendPort = dto.BackendPort;
                    existing.FrontendPort = dto.FrontendPort;
                    if (model != null)
                    {
                        existing.ManufacturerId = model.ManufacturerId;
                        existing.ModelId = model.Id;
                    }

                    existing.LocationId = location?.Id;
                    existing.Backend = dto.Backend;
                    existing.IsAvailable = false; // Always set to unavailable on import for safety
                    existing.ApiKey = ProtectIfNeeded(apiKey);
                    existing.Username = username;
                    existing.Password = ProtectIfNeeded(password);
                    _ = PerToolAttributionCapability.Refresh(existing);
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import printer '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing printer: {Name}", LogSanitizer.Sanitize(dto.Name));
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    // Delete methods for Replace mode
    private async Task DeleteAllCatalogDataAsync(CancellationToken ct)
    {
        _logger.LogWarning("[DataImport] Deleting all catalog data (Replace mode)");

        List<GcodeFile> modelGcodeFiles = await _context.GcodeFiles
            .Where(file => file.PrinterModelId != null)
            .ToListAsync(ct);
        modelGcodeFiles.ForEach(file => file.PrinterModelId = null);

        List<PrintJobStatistics> modelStatistics = await _context.PrintJobStatistics
            .Where(statistics => statistics.PrinterModelId != null)
            .ToListAsync(ct);
        modelStatistics.ForEach(statistics => statistics.PrinterModelId = null);

        // Delete in reverse order of dependencies
        // Note: PrinterModel-FilamentType relationship is handled via EF Core skip navigation (implicit join table)
        // Clearing the collections and deleting PrinterModels will cascade-delete the join entries
        _context.PrinterModelToolheads.RemoveRange(await _context.PrinterModelToolheads.ToListAsync(ct));
        _context.NozzleModelDefinitions.RemoveRange(await _context.NozzleModelDefinitions.ToListAsync(ct));
        _context.ToolheadModelDefinitions.RemoveRange(await _context.ToolheadModelDefinitions.ToListAsync(ct));
        _context.ExtruderModelDefinitions.RemoveRange(await _context.ExtruderModelDefinitions.ToListAsync(ct));
        _context.HotendModelDefinitions.RemoveRange(await _context.HotendModelDefinitions.ToListAsync(ct));
        _context.PrinterModels.RemoveRange(await _context.PrinterModels.ToListAsync(ct));
        _context.FilamentTypes.RemoveRange(await _context.FilamentTypes.ToListAsync(ct));
        _context.Manufacturers.RemoveRange(await _context.Manufacturers.ToListAsync(ct));

        await _context.SaveChangesAsync(ct);
    }

    private async Task DeleteAllLocationsAsync(CancellationToken ct)
    {
        _logger.LogWarning("[DataImport] Deleting all locations (Replace mode)");
        _context.Locations.RemoveRange(await _context.Locations.ToListAsync(ct));
        await _context.SaveChangesAsync(ct);
    }

    private async Task DeleteAllPrintersAsync(CancellationToken ct)
    {
        _logger.LogWarning("[DataImport] Deleting all printers (Replace mode)");

        // F1 + F4 — route printer deletion through the authoritative
        // IPrintersRepository.RemoveAsync path rather than raw RemoveRange. The repository
        // path runs every compensating cleanup that the Dallas cascade adjudication for
        // #953 now requires (schedules Restrict FK, direct PartOutputMappings Restrict FK,
        // Queue references, harvest ops, PrintJobs) and wraps each per-printer removal in
        // its own owned transaction. To make the whole Replace-mode batch atomic and to
        // let each repo call ride on our transaction instead of opening its own, we open
        // ONE outer transaction here on relational providers. The repository detects the
        // outer transaction (via `_db.Database.CurrentTransaction`) and skips owning one.
        // Non-relational (in-memory) providers return a null handle from
        // BeginTransactionAsync via the same IsRelational() guard used elsewhere; in that
        // case there is no explicit transaction and each RemoveAsync runs in the
        // provider's implicit-per-SaveChanges scope.
        IDbContextTransaction? ownedTransaction = _context.Database.IsRelational()
            && _context.Database.CurrentTransaction is null
                ? await _context.Database.BeginTransactionAsync(ct)
                : null;
        try
        {
            // Snapshot the printer IDs first — RemoveAsync mutates the DbSet and would
            // invalidate a live enumeration.
            List<Guid> printerIds = await _context.Printers
                .AsNoTracking()
                .Select(p => p.Id)
                .ToListAsync(ct);

            foreach (Guid printerId in printerIds)
            {
                await _printersRepository.RemoveAsync(new Printer { Id = printerId }, ct);
            }

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(ct);
            }
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                try
                {
                    await ownedTransaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // Rollback best-effort; original exception propagates.
                }
            }

            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }
}

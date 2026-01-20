using System.Diagnostics;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Models.Admin;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for importing database data from JSON format for backup/restore functionality
/// </summary>
public class DataImportService : IDataImportService
{
    private readonly AppDbContext _context;
    private readonly IUnifiedLoggingService _logger;

    public DataImportService(
        AppDbContext context,
        IUnifiedLoggingService logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ImportResponseDto> ImportCatalogAsync(CatalogExportDto catalog, ImportMode mode = ImportMode.Merge, CancellationToken ct = default)
    {
        _logger.LogInformation($"[DataImport] Starting catalog import in mode: {mode}");
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

            _logger.LogInformation($"[DataImport] Catalog import complete: {stats.TotalItemsImported} items imported in {sw.ElapsedMilliseconds}ms");

            if (response.Errors.Count > 0)
            {
                response.Success = false;
                _logger.LogWarning($"[DataImport] Import completed with {response.Errors.Count} errors");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            stats.Duration = sw.Elapsed;
            response.Statistics = stats;
            response.Success = false;
            response.Errors.Add($"Critical error during import: {ex.Message}");
            _logger.LogError(ex, $"[DataImport] Critical error during catalog import: {ex.Message}");
        }

        return response;
    }

    public async Task<ImportResponseDto> ImportFullBackupAsync(FullBackupExportDto backup, ImportMode mode = ImportMode.Merge, CancellationToken ct = default)
    {
        _logger.LogInformation($"[DataImport] Starting full backup import in mode: {mode}");
        Stopwatch sw = Stopwatch.StartNew();

        ImportResponseDto response = new() { Success = true };
        ImportStatistics stats = new();

        try
        {
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

            // If Replace mode, delete all existing locations and printers first
            if (mode == ImportMode.Replace)
            {
                await DeleteAllPrintersAsync(ct);
                await DeleteAllLocationsAsync(ct);
                response.Warnings.Add("All existing printers and locations have been deleted as per Replace mode");
            }

            // Import locations and printers
            stats.LocationsImported = await ImportLocationsAsync(backup.Locations, mode, response.Errors, ct);
            stats.PrintersImported = await ImportPrintersAsync(backup.Printers, mode, response.Errors, ct);

            stats.TotalItemsImported = stats.ManufacturersImported + stats.FilamentTypesImported +
                                       stats.PrinterModelsImported + stats.HotendsImported +
                                       stats.ExtrudersImported + stats.ToolheadsImported +
                                       stats.NozzlesImported + stats.LocationsImported + stats.PrintersImported;

            sw.Stop();
            stats.Duration = sw.Elapsed;
            response.Statistics = stats;

            _logger.LogInformation($"[DataImport] Full backup import complete: {stats.TotalItemsImported} items imported in {sw.ElapsedMilliseconds}ms");

            if (response.Errors.Count > 0)
            {
                response.Success = false;
                _logger.LogWarning($"[DataImport] Import completed with {response.Errors.Count} errors");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            stats.Duration = sw.Elapsed;
            response.Statistics = stats;
            response.Success = false;
            response.Errors.Add($"Critical error during import: {ex.Message}");
            _logger.LogError(ex, "[DataImport] Critical error during full backup import: {Message}", ex.Message);
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
                _logger.LogError(ex, "[DataImport] Error importing manufacturer: {Name}", dto.Name);
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
                        NeedsEnclosure = dto.NeedsEnclosure
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
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import filament type '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing filament type: {Name}", dto.Name);
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
                        NumberOfExtruders = dto.NumberOfExtruders,
                        SupportsAutoLeveling = dto.SupportsAutoLeveling,
                        MaxBedTemp = dto.MaxBedTemp,
                        MaxPrintSpeed = dto.MaxPrintSpeed,
                        IsActive = dto.IsActive
                    };
                    _context.PrinterModels.Add(newModel);
                    await _context.SaveChangesAsync(ct); // Save to get the Id

                    // Add supported filament types
                    foreach (string filamentName in dto.SupportedFilamentTypes)
                    {
                        FilamentType? filamentType = await _context.FilamentTypes
                            .FirstOrDefaultAsync(f => f.Name == filamentName, ct);
                        if (filamentType != null)
                        {
                            _context.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType
                            {
                                PrinterModelId = newModel.Id,
                                FilamentTypeId = filamentType.Id
                            });
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
                    existing.NumberOfExtruders = dto.NumberOfExtruders;
                    existing.SupportsAutoLeveling = dto.SupportsAutoLeveling;
                    existing.MaxBedTemp = dto.MaxBedTemp;
                    existing.MaxPrintSpeed = dto.MaxPrintSpeed;
                    existing.IsActive = dto.IsActive;

                    // Update supported filament types
                    _context.PrinterModelFilamentTypes.RemoveRange(existing.SupportedFilamentTypes);
                    foreach (string filamentName in dto.SupportedFilamentTypes)
                    {
                        FilamentType? filamentType = await _context.FilamentTypes
                            .FirstOrDefaultAsync(f => f.Name == filamentName, ct);
                        if (filamentType != null)
                        {
                            _context.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType
                            {
                                PrinterModelId = existing.Id,
                                FilamentTypeId = filamentType.Id
                            });
                        }
                    }

                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import printer model '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing printer model: {Name}", dto.Name);
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
                        Description = dto.Description,
                        Url = dto.Url
                    });
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    existing.MaxTemp = dto.MaxTemp;
                    existing.IsHighFlow = dto.IsHighFlow;
                    existing.Description = dto.Description;
                    existing.Url = dto.Url;
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import hotend '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing hotend: {Name}", dto.Name);
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
                _logger.LogError(ex, "[DataImport] Error importing extruder: {Name}", dto.Name);
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
                _logger.LogError(ex, "[DataImport] Error importing toolhead: {Name}", dto.Name);
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

                if (existing == null)
                {
                    _context.NozzleModelDefinitions.Add(new NozzleModelDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        ManufacturerId = manufacturer.Id,
                        Diameter = dto.Diameter,
                        MaxTemp = dto.MaxTemp,
                        NozzleInterface = (NozzleInterfaceType)dto.NozzleInterface,
                        Description = dto.Description
                    });
                    imported++;
                }
                else if (mode == ImportMode.Replace)
                {
                    existing.Diameter = dto.Diameter;
                    existing.MaxTemp = dto.MaxTemp;
                    existing.NozzleInterface = (NozzleInterfaceType)dto.NozzleInterface;
                    existing.Description = dto.Description;
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import nozzle '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing nozzle: {Name}", dto.Name);
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    private async Task<int> ImportLocationsAsync(List<LocationExportDto> locations, ImportMode mode, List<string> errors, CancellationToken ct)
    {
        int imported = 0;

        foreach (LocationExportDto dto in locations)
        {
            try
            {
                Location? existing = await _context.Locations
                    .FirstOrDefaultAsync(l => l.Name == dto.Name, ct);

                if (existing == null)
                {
                    _context.Locations.Add(new Location
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
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
                errors.Add($"Failed to import location '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing location: {Name}", dto.Name);
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
                    .FirstOrDefaultAsync(p => p.Name == dto.Name, ct);

                if (existing == null)
                {
                    Guid manufacturerId = model?.ManufacturerId ?? Guid.Empty;
                    Guid modelId = model?.Id ?? Guid.Empty;

                    _context.Printers.Add(new Printer
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
                        IsAvailable = false // Always set to unavailable on import for safety
                    });
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
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import printer '{dto.Name}': {ex.Message}");
                _logger.LogError(ex, "[DataImport] Error importing printer: {Name}", dto.Name);
            }
        }

        await _context.SaveChangesAsync(ct);
        return imported;
    }

    // Delete methods for Replace mode
    private async Task DeleteAllCatalogDataAsync(CancellationToken ct)
    {
        _logger.LogWarning("[DataImport] Deleting all catalog data (Replace mode)");

        // Delete in reverse order of dependencies
        _context.PrinterModelFilamentTypes.RemoveRange(await _context.PrinterModelFilamentTypes.ToListAsync(ct));
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
        _context.Printers.RemoveRange(await _context.Printers.ToListAsync(ct));
        await _context.SaveChangesAsync(ct);
    }
}

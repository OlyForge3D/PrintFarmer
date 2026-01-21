using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Models.SeedData;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for seeding database from YAML configuration files
/// </summary>
public class DataSeedService : IDataSeedService
{
    private readonly AppDbContext _context;
    private readonly IYamlSeedDataReader _yamlReader;
    private readonly IUnifiedLoggingService _logger;

    public DataSeedService(
        AppDbContext context,
        IYamlSeedDataReader yamlReader,
        IUnifiedLoggingService logger)
    {
        _context = context;
        _yamlReader = yamlReader;
        _logger = logger;
    }

    public async Task SeedAllAsync()
    {
        _logger.LogInformation("[SeedData] Starting seed data load from YAML files");

        await SeedManufacturersAsync();
        await SeedFilamentTypesAsync();
        await SeedPrinterModelsAsync();
        await SeedComponentModelsAsync();
        await SeedMaintenanceSchedulesAsync();

        _logger.LogInformation("[SeedData] Completed seed data load from YAML files");
    }

    public async Task SeedManufacturersAsync()
    {
        try
        {
            List<ManufacturerSeedDto> manufacturersData = await _yamlReader.ReadManufacturersAsync();

            _logger.LogInformation($"[SeedData] Seeding {manufacturersData.Count} manufacturers from YAML");

            foreach (ManufacturerSeedDto dto in manufacturersData)
            {
                string normalized = CatalogNameNormalizer.NormalizeManufacturer(dto.Name);

                Manufacturer? existing = await _context.Manufacturers
                    .FirstOrDefaultAsync(m => m.Name == normalized);

                if (existing == null)
                {
                    _context.Manufacturers.Add(new Manufacturer
                    {
                        Id = Guid.NewGuid(),
                        Name = normalized
                    });
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Manufacturers seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding manufacturers: {Message}", ex.Message);
            throw;
        }
    }

    public async Task SeedFilamentTypesAsync()
    {
        try
        {
            List<FilamentTypeSeedDto> filamentsData = await _yamlReader.ReadFilamentTypesAsync();

            _logger.LogInformation($"[SeedData] Seeding {filamentsData.Count} filament types from YAML");

            foreach (FilamentTypeSeedDto dto in filamentsData)
            {
                FilamentType? existing = await _context.FilamentTypes
                    .FirstOrDefaultAsync(f => f.Name == dto.Name);

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
                }
                else
                {
                    // Update existing filament types with new properties if they weren't set
                    existing.IsAbrasive = dto.IsAbrasive;
                    existing.NeedsEnclosure = dto.NeedsEnclosure;
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Filament types seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding filament types: {Message}", ex.Message);
            throw;
        }
    }

    public async Task SeedPrinterModelsAsync()
    {
        try
        {
            List<PrinterModelSeedDto> modelsData = await _yamlReader.ReadPrinterModelsAsync();

            if (modelsData.Count == 0)
            {
                _logger.LogInformation("[SeedData] No printer models found in YAML, skipping");
                return;
            }

            _logger.LogInformation($"[SeedData] Seeding {modelsData.Count} printer models from YAML");

            // Build manufacturer lookup
            Dictionary<string, Guid> manufacturers = await _context.Manufacturers
                .ToDictionaryAsync(m => m.Name, m => m.Id, StringComparer.OrdinalIgnoreCase);

            foreach (PrinterModelSeedDto dto in modelsData)
            {
                if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
                {
                    _logger.LogWarning(
                        "[SeedData] Manufacturer '{Manufacturer}' not found for model '{Model}', skipping",
                        dto.Manufacturer, dto.Name);
                    continue;
                }

                bool exists = await _context.PrinterModels
                    .AnyAsync(pm => pm.ManufacturerId == manufacturerId && pm.Name == dto.Name);

                if (!exists)
                {
                    var printerModel = new PrinterModel
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        ManufacturerId = manufacturerId,
                        MaxX = dto.BuildVolume?.X ?? 200,
                        MaxY = dto.BuildVolume?.Y ?? 200,
                        MaxZ = dto.BuildVolume?.Z ?? 200,
                        HasHeatedBed = dto.HasHeatedBed,
                        HasEnclosure = dto.HasEnclosure,
                        SupportsAutoLeveling = dto.SupportsAutoLeveling,
                        MultiMaterial = dto.MultiMaterial,
                        NumberOfExtruders = dto.NumberOfExtruders,
                        MaxBedTemp = dto.MaxBedTemp,
                        MaxPrintSpeed = dto.MaxPrintSpeed
                    };

                    // Parse backend and motion type if provided
                    if (!string.IsNullOrEmpty(dto.DefaultBackend) &&
                        Enum.TryParse<PrinterBackend>(dto.DefaultBackend, out PrinterBackend backend))
                    {
                        printerModel.DefaultBackend = (int)backend;
                    }

                    if (!string.IsNullOrEmpty(dto.MotionType) &&
                        Enum.TryParse<MotionType>(dto.MotionType, out MotionType motionType))
                    {
                        printerModel.MotionType = (int)motionType;
                    }

                    _context.PrinterModels.Add(printerModel);
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Printer models seeded successfully");

            // Seed aliases and filament type associations
            await SeedPrinterModelAliasesAsync(modelsData);
            await SeedModelFilamentTypesAsync(modelsData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding printer models: {Message}", ex.Message);
            throw;
        }
    }

    public async Task SeedComponentModelsAsync()
    {
        try
        {
            _logger.LogInformation("[SeedData] Seeding component models from YAML");

            // Build manufacturer lookup
            Dictionary<string, Guid> manufacturers = await _context.Manufacturers
                .ToDictionaryAsync(m => m.Name, m => m.Id, StringComparer.OrdinalIgnoreCase);

            // Seed hotends
            await SeedHotendsAsync(manufacturers);

            // Seed extruders
            await SeedExtrudersAsync(manufacturers);

            // Seed toolheads
            await SeedToolheadsAsync(manufacturers);

            // Seed nozzles
            await SeedNozzlesAsync(manufacturers);

            _logger.LogInformation("[SeedData] Component models seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding component models: {Message}", ex.Message);
            throw;
        }
    }

    public async Task ReloadSeedDataAsync()
    {
        _logger.LogInformation("[SeedData] Reloading seed data from YAML files");
        await SeedAllAsync();
    }

    private async Task SeedHotendsAsync(Dictionary<string, Guid> manufacturers)
    {
        List<HotendModelSeedDto> hotends = await _yamlReader.ReadHotendsAsync();

        if (hotends.Count == 0)
        {
            _logger.LogInformation("[SeedData] No hotends found in YAML, skipping");
            return;
        }

        _logger.LogInformation($"[SeedData] Seeding {hotends.Count} hotend models");

        foreach (HotendModelSeedDto dto in hotends)
        {
            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for hotend '{Name}', skipping",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            bool exists = await _context.HotendModelDefinitions
                .AnyAsync(h => h.Name == dto.Name && h.ManufacturerId == manufacturerId);

            if (!exists)
            {
                _context.HotendModelDefinitions.Add(new HotendModelDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name,
                    ManufacturerId = manufacturerId,
                    MaxTemp = dto.MaxTemp,
                    IsHighFlow = dto.IsHighFlow,
                    Description = dto.Description,
                    Url = dto.Url
                });
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task SeedExtrudersAsync(Dictionary<string, Guid> manufacturers)
    {
        List<ExtruderModelSeedDto> extruders = await _yamlReader.ReadExtrudersAsync();

        if (extruders.Count == 0)
        {
            _logger.LogInformation("[SeedData] No extruders found in YAML, skipping");
            return;
        }

        _logger.LogInformation($"[SeedData] Seeding {extruders.Count} extruder models");

        foreach (ExtruderModelSeedDto dto in extruders)
        {
            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for extruder '{Name}', skipping",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            bool exists = await _context.ExtruderModelDefinitions
                .AnyAsync(e => e.Name == dto.Name && e.ManufacturerId == manufacturerId);

            if (!exists)
            {
                _context.ExtruderModelDefinitions.Add(new ExtruderModelDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name,
                    ManufacturerId = manufacturerId,
                    GearRatio = dto.GearRatio,
                    IsDirectDrive = dto.IsDirectDrive,
                    Description = dto.Description,
                    Url = dto.Url
                });
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task SeedToolheadsAsync(Dictionary<string, Guid> manufacturers)
    {
        List<ToolheadModelSeedDto> toolheads = await _yamlReader.ReadToolheadsAsync();

        if (toolheads.Count == 0)
        {
            _logger.LogInformation("[SeedData] No toolheads found in YAML, skipping");
            return;
        }

        _logger.LogInformation($"[SeedData] Seeding {toolheads.Count} toolhead models");

        foreach (ToolheadModelSeedDto dto in toolheads)
        {
            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for toolhead '{Name}', skipping",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            bool exists = await _context.ToolheadModelDefinitions
                .AnyAsync(t => t.Name == dto.Name && t.ManufacturerId == manufacturerId);

            if (!exists)
            {
                _context.ToolheadModelDefinitions.Add(new ToolheadModelDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name,
                    ManufacturerId = manufacturerId,
                    Description = dto.Description,
                    Url = dto.Url
                });
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task SeedNozzlesAsync(Dictionary<string, Guid> manufacturers)
    {
        List<NozzleModelSeedDto> nozzles = await _yamlReader.ReadNozzlesAsync();

        if (nozzles.Count == 0)
        {
            _logger.LogInformation("[SeedData] No nozzles found in YAML, skipping");
            return;
        }

        _logger.LogInformation($"[SeedData] Seeding {nozzles.Count} nozzle models");

        foreach (NozzleModelSeedDto dto in nozzles)
        {
            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for nozzle '{Name}', skipping",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            bool exists = await _context.NozzleModelDefinitions
                .AnyAsync(n => n.Name == dto.Name && n.ManufacturerId == manufacturerId);

            if (!exists)
            {
                // Parse nozzle type
                NozzleType nozzleType = NozzleType.Brass;
                if (!string.IsNullOrEmpty(dto.NozzleType) &&
                    Enum.TryParse<NozzleType>(dto.NozzleType.Replace(" ", string.Empty), out NozzleType parsedType))
                {
                    nozzleType = parsedType;
                }

                _context.NozzleModelDefinitions.Add(new NozzleModelDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name,
                    ManufacturerId = manufacturerId,
                    MaxTemp = dto.MaxTemp,
                    NozzleType = nozzleType,
                    Description = dto.Description,
                    Url = dto.Url
                });
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task SeedPrinterModelAliasesAsync(List<PrinterModelSeedDto> modelsData)
    {
        foreach (PrinterModelSeedDto dto in modelsData)
        {
            if (dto.Aliases == null || dto.Aliases.Count == 0)
            {
                continue;
            }

            PrinterModel? model = await _context.PrinterModels
                .FirstOrDefaultAsync(pm => pm.Name == dto.Name);

            if (model == null)
            {
                continue;
            }

            foreach (SlicerAliasDto alias in dto.Aliases)
            {
                bool aliasExists = await _context.PrinterModelAliases
                    .AnyAsync(a => a.PrinterModelId == model.Id &&
                        a.SlicerModelName == alias.SlicerModelName &&
                        a.SlicerType == alias.SlicerType);

                if (!aliasExists)
                {
                    _context.PrinterModelAliases.Add(new PrinterModelAlias
                    {
                        Id = Guid.NewGuid(),
                        PrinterModelId = model.Id,
                        SlicerModelName = alias.SlicerModelName,
                        SlicerType = alias.SlicerType,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task SeedModelFilamentTypesAsync(List<PrinterModelSeedDto> modelsData)
    {
        // Build filament type lookup
        Dictionary<string, Guid> filamentTypes = await _context.FilamentTypes
            .ToDictionaryAsync(ft => ft.Name, ft => ft.Id, StringComparer.OrdinalIgnoreCase);

        foreach (PrinterModelSeedDto dto in modelsData)
        {
            if (dto.SupportedMaterials == null || dto.SupportedMaterials.Count == 0)
            {
                continue;
            }

            PrinterModel? model = await _context.PrinterModels
                .FirstOrDefaultAsync(pm => pm.Name == dto.Name);

            if (model == null)
            {
                continue;
            }

            // Ensure the model's SupportedFilamentTypes collection is loaded
            await _context.Entry(model).Collection(m => m.SupportedFilamentTypes).LoadAsync();

            foreach (string material in dto.SupportedMaterials)
            {
                if (filamentTypes.TryGetValue(material, out Guid filamentTypeId))
                {
                    // Check if filament type is already associated using skip navigation
                    bool exists = model.SupportedFilamentTypes.Any(ft => ft.Id == filamentTypeId);

                    if (!exists)
                    {
                        // Load the FilamentType entity and add to the collection
                        FilamentType? filamentType = await _context.FilamentTypes.FindAsync(filamentTypeId);
                        if (filamentType != null)
                        {
                            model.SupportedFilamentTypes.Add(filamentType);
                        }
                    }
                }
            }
        }

        await _context.SaveChangesAsync();
    }

    public async Task SeedMaintenanceSchedulesAsync()
    {
        try
        {
            List<MaintenanceScheduleSeedDto> schedulesData = await _yamlReader.ReadMaintenanceSchedulesAsync();

            if (schedulesData.Count == 0)
            {
                _logger.LogInformation("[SeedData] No maintenance schedules found in YAML, skipping");
                return;
            }

            _logger.LogInformation($"[SeedData] Seeding {schedulesData.Count} maintenance schedules from YAML");

            // Build manufacturer lookup
            Dictionary<string, Guid> manufacturers = await _context.Manufacturers
                .AsNoTracking()
                .ToDictionaryAsync(m => m.Name, m => m.Id);

            // Build printer model lookup
            Dictionary<string, Guid> printerModels = await _context.PrinterModels
                .AsNoTracking()
                .ToDictionaryAsync(pm => pm.Name, pm => pm.Id);

            foreach (MaintenanceScheduleSeedDto dto in schedulesData)
            {
                // Resolve optional FK references
                Guid? printerId = null;
                Guid? printerModelId = null;
                Guid? manufacturerId = null;

                if (!string.IsNullOrEmpty(dto.PrinterModel) && printerModels.TryGetValue(dto.PrinterModel, out Guid modelId))
                {
                    printerModelId = modelId;
                }

                if (!string.IsNullOrEmpty(dto.Manufacturer) && manufacturers.TryGetValue(dto.Manufacturer, out Guid mfgId))
                {
                    manufacturerId = mfgId;
                }

                // Check if schedule already exists (match on TaskName + optional FKs)
                bool exists = await _context.MaintenanceSchedules
                    .AnyAsync(ms =>
                        ms.TaskName == dto.TaskName &&
                        ms.PrinterId == printerId &&
                        ms.PrinterModelId == printerModelId &&
                        ms.ManufacturerId == manufacturerId);

                if (!exists)
                {
                    _context.MaintenanceSchedules.Add(new MaintenanceSchedule
                    {
                        Id = Guid.NewGuid(),
                        TaskName = dto.TaskName,
                        Description = dto.Description,
                        Component = dto.Component,
                        IntervalHours = dto.IntervalHours,
                        IntervalDays = dto.IntervalDays,
                        EstimatedDurationMinutes = dto.EstimatedDurationMinutes,
                        IsActive = dto.IsActive,
                        PrinterId = printerId,
                        PrinterModelId = printerModelId,
                        ManufacturerId = manufacturerId,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Maintenance schedules seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding maintenance schedules: {Message}", ex.Message);
            throw;
        }
    }
}

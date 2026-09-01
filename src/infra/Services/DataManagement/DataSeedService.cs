using System.Globalization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Modules.Abstractions.Normalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.DataManagement;

/// <summary>
/// Service for seeding database from YAML configuration files
/// </summary>
public class DataSeedService : IDataSeedService
{
    private readonly AppDbContext _context;
    private readonly IYamlSeedDataReader _yamlReader;
    private readonly ILogger<DataSeedService> _logger;

    public DataSeedService(
        AppDbContext context,
        IYamlSeedDataReader yamlReader,
        ILogger<DataSeedService> logger)
    {
        _context = context;
        _yamlReader = yamlReader;
        _logger = logger;
    }

    /// <summary>
    /// Case-insensitive, manufacturer-scoped key comparer for the (ManufacturerId, Name) preload
    /// dictionaries used throughout this class. The preload dictionaries replace a server-side
    /// <c>==</c> comparison (translated to SQL, collation-dependent) with a client-side .NET
    /// comparison. SQLite and PostgreSQL are case-sensitive by default, but SQL Server's default
    /// collation is case-insensitive, so comparing ordinally here would make a YAML casing-only
    /// edit miss a row on SQL Server that the old per-row query would have matched. Matching
    /// case-insensitively everywhere is the safe, permissive choice: it can only turn a miss into
    /// a hit (update instead of a duplicate insert attempt), never the reverse.
    /// </summary>
    private sealed class ManufacturerScopedNameComparer : IEqualityComparer<(Guid ManufacturerId, string Name)>
    {
        public static readonly ManufacturerScopedNameComparer Instance = new();

        public bool Equals((Guid ManufacturerId, string Name) x, (Guid ManufacturerId, string Name) y) =>
            x.ManufacturerId == y.ManufacturerId &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid ManufacturerId, string Name) obj) =>
            HashCode.Combine(obj.ManufacturerId, obj.Name.ToUpperInvariant());
    }

    /// <summary>
    /// Case-insensitive (Name, Category) key comparer for maintenance component lookups. See
    /// <see cref="ManufacturerScopedNameComparer"/> for why case-insensitivity matters here.
    /// </summary>
    private sealed class NameCategoryComparer : IEqualityComparer<(string Name, string Category)>
    {
        public static readonly NameCategoryComparer Instance = new();

        public bool Equals((string Name, string Category) x, (string Name, string Category) y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Category, y.Category, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, string Category) obj) =>
            HashCode.Combine(obj.Name.ToUpperInvariant(), obj.Category.ToUpperInvariant());
    }

    /// <summary>
    /// Builds a preload dictionary from an already-materialized list using first-wins insertion
    /// (<see cref="Dictionary{TKey,TValue}.TryAdd"/>) instead of <c>ToDictionaryAsync</c>. Some
    /// catalog entities (maintenance tasks/components/plans, and component definitions such as
    /// hotends/extruders/toolheads/nozzles) have no unique database constraint on the key used
    /// here, so legitimate duplicate-keyed rows can exist; <c>ToDictionaryAsync</c> throws
    /// <see cref="ArgumentException"/> on the first duplicate, whereas the original per-row
    /// <c>FirstOrDefaultAsync</c> silently tolerated duplicates by returning the first match. This
    /// preserves that tolerant behavior while still issuing a single query.
    /// </summary>
    private static Dictionary<TKey, TValue> BuildFirstWinsDictionary<TKey, TValue>(
        IEnumerable<TValue> source, Func<TValue, TKey> keySelector, IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        Dictionary<TKey, TValue> dictionary = new(comparer);
        foreach (TValue item in source)
        {
            dictionary.TryAdd(keySelector(item), item);
        }

        return dictionary;
    }

    public async Task SeedAllAsync()
    {
        _logger.LogInformation("[SeedData] Starting seed data load from YAML files");

        await SeedManufacturersAsync();
        await SeedFilamentTypesAsync();
        await SeedBedTypesAsync();
        await SeedNozzleMaterialsAsync();  // Must come before component models so nozzle seeding can resolve materials
        await SeedComponentModelsAsync();  // Must come before printer models so toolhead defaults exist
        await SeedPrinterModelsAsync();
        await SeedMaintenanceTasksAsync();
        await SeedMaintenanceComponentsAsync();
        await SeedMaintenancePlansAsync();

        _logger.LogInformation("[SeedData] Completed seed data load from YAML files");
    }

    public async Task SeedManufacturersAsync()
    {
        try
        {
            List<ManufacturerSeedDto> manufacturersData = await _yamlReader.ReadManufacturersAsync();

            _logger.LogInformation("[SeedData] Seeding {ManufacturersDataCount} manufacturers from YAML", manufacturersData.Count);

            // Preload all existing manufacturers once instead of issuing one existence query
            // per row (#2328). Matches case-insensitively (the unique index is on a lowercased
            // shadow column) so behavior is identical across SQLite/PostgreSQL/SQL Server, and
            // the dictionary is updated after each Add so a duplicate name later in the same
            // YAML file resolves against the row just created rather than inserting twice.
            Dictionary<string, Manufacturer> existingByName = BuildFirstWinsDictionary(
                await _context.Manufacturers.ToListAsync(), m => m.Name, StringComparer.OrdinalIgnoreCase);

            foreach (ManufacturerSeedDto dto in manufacturersData)
            {
                string normalized = CatalogNameNormalizer.NormalizeManufacturer(dto.Name);

                existingByName.TryGetValue(normalized, out Manufacturer? existing);

                if (existing == null)
                {
                    var manufacturer = new Manufacturer
                    {
                        Id = Guid.NewGuid(),
                        Name = normalized
                    };
                    _context.Manufacturers.Add(manufacturer);
                    existingByName[normalized] = manufacturer;
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

            _logger.LogInformation("[SeedData] Seeding {FilamentsDataCount} filament types from YAML", filamentsData.Count);

            // Preload all existing filament types once instead of one existence query per row.
            // Matches case-insensitively (unique index is on a lowercased shadow column) and the
            // dictionary is updated after each Add for intra-loop duplicate protection.
            Dictionary<string, FilamentType> existingByName = BuildFirstWinsDictionary(
                await _context.FilamentTypes.ToListAsync(), f => f.Name, StringComparer.OrdinalIgnoreCase);

            foreach (FilamentTypeSeedDto dto in filamentsData)
            {
                existingByName.TryGetValue(dto.Name, out FilamentType? existing);

                if (existing == null)
                {
                    var filamentType = new FilamentType
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        DefaultHotendTemp = dto.DefaultHotendTemp,
                        DefaultBedTemp = dto.DefaultBedTemp,
                        IsAbrasive = dto.IsAbrasive,
                        NeedsEnclosure = dto.NeedsEnclosure,
                        DefaultPricePerKg = dto.DefaultPricePerKg,
                        DefaultDensity = dto.DefaultDensity
                    };
                    _context.FilamentTypes.Add(filamentType);
                    existingByName[dto.Name] = filamentType;
                }
                else
                {
                    // Upsert: update existing filament type properties from seed data
                    existing.DefaultHotendTemp = dto.DefaultHotendTemp;
                    existing.DefaultBedTemp = dto.DefaultBedTemp;
                    existing.IsAbrasive = dto.IsAbrasive;
                    existing.NeedsEnclosure = dto.NeedsEnclosure;
                    existing.DefaultPricePerKg = dto.DefaultPricePerKg ?? existing.DefaultPricePerKg;
                    existing.DefaultDensity = dto.DefaultDensity ?? existing.DefaultDensity;
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

    /// <summary>
    /// Seeds default bed surface types. These are system types that cannot be deleted by users.
    /// </summary>
    public async Task SeedBedTypesAsync()
    {
        try
        {
            var defaultBedTypes = new (string Name, string Description, string Color)[]
            {
                ("PEI Smooth", "Smooth PEI (polyetherimide) sheet — excellent adhesion for PLA, PETG", "#4CAF50"),
                ("PEI Textured", "Textured PEI powder-coated sheet — easy release, matte finish", "#8BC34A"),
                ("Glass", "Borosilicate glass bed — flat surface, good for PLA with adhesive", "#2196F3"),
                ("BuildTak", "BuildTak adhesive sheet — strong adhesion, good for ABS", "#FF9800"),
                ("Spring Steel", "Flexible spring steel sheet — magnetic, easy print removal", "#9E9E9E"),
                ("Garolite", "Garolite (G10/FR4) sheet — ideal for nylon and high-temp materials", "#795548"),
            };

            _logger.LogInformation("[SeedData] Seeding {Count} default bed types", defaultBedTypes.Length);

            // Preload existing names once instead of one existence query per row.
            HashSet<string> existingNames = new(
                await _context.BedTypes.Select(b => b.Name).ToListAsync(),
                StringComparer.Ordinal);

            foreach ((string name, string description, string color) in defaultBedTypes)
            {
                if (existingNames.Add(name))
                {
                    _context.BedTypes.Add(new BedType
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        Description = description,
                        Color = color,
                        IsSystem = true,
                        CreatedDate = DateTimeOffset.UtcNow,
                        UpdatedDate = DateTimeOffset.UtcNow,
                    });
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Bed types seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding bed types: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Seeds the built-in NozzleMaterial catalog rows, one per legacy NozzleType enum member.
    /// These are also seeded by the AddNozzleMaterialCatalog data migration for deployed
    /// databases; this method covers local dev/test setups that use
    /// <c>DatabaseFacade.EnsureCreated</c> instead of applying migrations, where the migration's
    /// data-seed SQL never runs. Values and fixed IDs are kept in sync with the migration so both
    /// paths produce identical rows.
    /// </summary>
    public async Task SeedNozzleMaterialsAsync()
    {
        try
        {
            var builtInMaterials = new (Guid Id, string Name, bool IsHardened, int DefaultMaxTemp, string Description)[]
            {
                (Guid.Parse("9f5a1c1e-0001-4a1a-8c1a-000000000001"), nameof(NozzleType.Brass), false, 260, "Standard brass nozzle - not abrasion resistant"),
                (Guid.Parse("9f5a1c1e-0001-4a1a-8c1a-000000000002"), nameof(NozzleType.HardenedSteel), true, 300, "Hardened steel nozzle - abrasion resistant"),
                (Guid.Parse("9f5a1c1e-0001-4a1a-8c1a-000000000003"), nameof(NozzleType.StainlessSteel), false, 300, "Stainless steel nozzle - food safe, not abrasion resistant"),
                (Guid.Parse("9f5a1c1e-0001-4a1a-8c1a-000000000004"), nameof(NozzleType.TungstenCarbide), true, 500, "Tungsten carbide nozzle - highly abrasion resistant"),
                (Guid.Parse("9f5a1c1e-0001-4a1a-8c1a-000000000005"), nameof(NozzleType.Abrasive), true, 500, "Generic abrasion-resistant nozzle material"),
                (Guid.Parse("9f5a1c1e-0001-4a1a-8c1a-000000000006"), nameof(NozzleType.Diamond), true, 500, "Diamond-tipped nozzle - extreme abrasion resistance combined with high thermal conductivity"),
                (Guid.Parse("9f5a1c1e-0001-4a1a-8c1a-000000000007"), nameof(NozzleType.Ruby), true, 300, "Ruby-tipped nozzle in a brass body - abrasion resistant while retaining good thermal conductivity"),
                (Guid.Parse("9f5a1c1e-0001-4a1a-8c1a-000000000008"), nameof(NozzleType.PlatedCopper), false, 300, "Plated copper nozzle - excellent thermal conductivity for high-flow printing, not abrasion resistant"),
                (Guid.Parse("9f5a1c1e-0001-4a1a-8c1a-000000000009"), nameof(NozzleType.ToolSteel), true, 500, "Tool steel nozzle - abrasion resistant with high temperature tolerance"),
            };

            _logger.LogInformation("[SeedData] Seeding {Count} built-in nozzle materials", builtInMaterials.Length);

            // Preload existing names once instead of one existence query per row.
            HashSet<string> existingNames = new(
                await _context.NozzleMaterials.Select(m => m.Name).ToListAsync(),
                StringComparer.Ordinal);

            foreach ((Guid id, string name, bool isHardened, int defaultMaxTemp, string description) in builtInMaterials)
            {
                if (existingNames.Add(name))
                {
                    _context.NozzleMaterials.Add(new NozzleMaterial
                    {
                        Id = id,
                        Name = name,
                        IsHardened = isHardened,
                        DefaultMaxTemp = defaultMaxTemp,
                        IsBuiltIn = true,
                        Description = description,
                    });
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Nozzle materials seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding nozzle materials: {Message}", ex.Message);
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

            _logger.LogInformation("[SeedData] Seeding {ModelsDataCount} printer models from YAML", modelsData.Count);

            // Build manufacturer lookup
            Dictionary<string, Guid> manufacturers = await _context.Manufacturers
                .ToDictionaryAsync(m => m.Name, m => m.Id, StringComparer.OrdinalIgnoreCase);

            // Preload all existing printer models keyed by (ManufacturerId, Name) once instead
            // of issuing one existence query per model row — this loop is the primary source of
            // the ~400 sequential per-row queries measured in #2328 (~98 printer models seeded
            // across four separate per-model lookups: this method, aliases, filament types, and
            // toolheads). Matches case-insensitively (unique index is on a lowercased shadow
            // column, and SQL Server's default collation is case-insensitive even without it) and
            // the dictionary is updated after each Add for intra-loop duplicate protection.
            Dictionary<(Guid ManufacturerId, string Name), PrinterModel> existingModels = BuildFirstWinsDictionary(
                await _context.PrinterModels.ToListAsync(),
                pm => (pm.ManufacturerId, pm.Name),
                ManufacturerScopedNameComparer.Instance);

            foreach (PrinterModelSeedDto dto in modelsData)
            {
                if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
                {
                    _logger.LogWarning(
                        "[SeedData] Manufacturer '{Manufacturer}' not found for model '{Model}', skipping",
                        dto.Manufacturer, dto.Name);
                    continue;
                }

                existingModels.TryGetValue((manufacturerId, dto.Name), out PrinterModel? existing);

                if (existing == null)
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
                        HasCarbonFilter = dto.HasCarbonFilter,
                        HasHepaFilter = dto.HasHepaFilter,
                        HasBowdenTube = dto.HasBowdenTube,
                        HasPtfeLiner = dto.HasPtfeLiner,
                        HasLinearRails = dto.HasLinearRails,
                        HasLeadScrews = dto.HasLeadScrews,
                        HasToolchanger = dto.HasToolchanger,
                        HasFilamentCutter = dto.HasFilamentCutter,
                        HasHeatedChamber = dto.HasHeatedChamber,
                        SupportsAutoLeveling = dto.SupportsAutoLeveling,
                        MultiMaterial = dto.MultiMaterial,
                        MaxBedTemp = dto.MaxBedTemp,
                        MaxPrintSpeed = dto.MaxPrintSpeed,
                        DefaultWattage = dto.DefaultWattage,
                        DefaultHourlyRate = dto.DefaultHourlyRate
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
                    existingModels[(manufacturerId, dto.Name)] = printerModel;
                }
                else
                {
                    // Upsert: update existing model with seed data values
                    existing.MaxX = dto.BuildVolume?.X ?? existing.MaxX;
                    existing.MaxY = dto.BuildVolume?.Y ?? existing.MaxY;
                    existing.MaxZ = dto.BuildVolume?.Z ?? existing.MaxZ;
                    existing.HasHeatedBed = dto.HasHeatedBed;
                    existing.HasEnclosure = dto.HasEnclosure;
                    existing.HasCarbonFilter = dto.HasCarbonFilter;
                    existing.HasHepaFilter = dto.HasHepaFilter;
                    existing.HasBowdenTube = dto.HasBowdenTube;
                    existing.HasPtfeLiner = dto.HasPtfeLiner;
                    existing.HasLinearRails = dto.HasLinearRails;
                    existing.HasLeadScrews = dto.HasLeadScrews;
                    existing.HasToolchanger = dto.HasToolchanger;
                    existing.HasFilamentCutter = dto.HasFilamentCutter;
                    existing.HasHeatedChamber = dto.HasHeatedChamber;
                    existing.SupportsAutoLeveling = dto.SupportsAutoLeveling;
                    existing.MultiMaterial = dto.MultiMaterial;
                    existing.MaxBedTemp = dto.MaxBedTemp ?? existing.MaxBedTemp;
                    existing.MaxPrintSpeed = dto.MaxPrintSpeed ?? existing.MaxPrintSpeed;
                    existing.DefaultWattage = dto.DefaultWattage ?? existing.DefaultWattage;
                    existing.DefaultHourlyRate = dto.DefaultHourlyRate ?? existing.DefaultHourlyRate;

                    if (!string.IsNullOrEmpty(dto.DefaultBackend) &&
                        Enum.TryParse<PrinterBackend>(dto.DefaultBackend, out PrinterBackend updatedBackend))
                    {
                        existing.DefaultBackend = (int)updatedBackend;
                    }

                    if (!string.IsNullOrEmpty(dto.MotionType) &&
                        Enum.TryParse<MotionType>(dto.MotionType, out MotionType updatedMotionType))
                    {
                        existing.MotionType = (int)updatedMotionType;
                    }
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Printer models seeded successfully");

            // Seed aliases and filament type associations
            await SeedPrinterModelAliasesAsync(modelsData, manufacturers);
            await SeedModelFilamentTypesAsync(modelsData, manufacturers);

            // Seed printer model toolheads (components are already seeded before printer models)
            await SeedPrinterModelToolheadsAsync(modelsData, manufacturers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding printer models: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Seeds printer model toolheads from already-loaded YAML data.
    /// </summary>
    private async Task SeedPrinterModelToolheadsAsync(
        List<PrinterModelSeedDto> modelsData,
        Dictionary<string, Guid> manufacturers)
    {
        try
        {
            int toolheadCount = modelsData.Count(m => m.Toolheads?.Count > 0);

            if (toolheadCount == 0)
            {
                _logger.LogInformation("[SeedData] No printer model toolheads found in YAML, skipping");
                return;
            }

            _logger.LogInformation("[SeedData] Seeding toolheads for {ToolheadCount} printer model(s) from YAML", toolheadCount);

            // Build lookups for component resolution using composite key (name:manufacturer)
            // This allows different manufacturers to have components with the same name
            // Also build name-only lookup for backward compatibility (first match wins)
            Dictionary<string, Guid> hotendsByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (var h in await _context.HotendModelDefinitions.ToListAsync())
            {
                hotendsByName.TryAdd(h.Name, h.Id);
            }

            Dictionary<string, Guid> extrudersByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (var e in await _context.ExtruderModelDefinitions.ToListAsync())
            {
                extrudersByName.TryAdd(e.Name, e.Id);
            }

            Dictionary<string, Guid> nozzlesByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (var n in await _context.NozzleModelDefinitions.ToListAsync())
            {
                nozzlesByName.TryAdd(n.Name, n.Id);
            }

            // Build toolhead model lookup with composite key
            Dictionary<string, ToolheadModelDefinition> toolheadsByKey = await _context.ToolheadModelDefinitions
                .Include(t => t.Manufacturer)
                .ToDictionaryAsync(
                    t => $"{t.Name}:{t.Manufacturer?.Name ?? "Unknown"}",
                    t => t,
                    StringComparer.OrdinalIgnoreCase);

            Dictionary<string, ToolheadModelDefinition> toolheadsByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (var t in await _context.ToolheadModelDefinitions.ToListAsync())
            {
                toolheadsByName.TryAdd(t.Name, t);
            }

            int seededCount = 0;

            // Preload printer models (with their toolheads) keyed by (ManufacturerId, Name) once
            // instead of one query per model row. Case-insensitive for cross-provider parity;
            // see ManufacturerScopedNameComparer.
            Dictionary<(Guid ManufacturerId, string Name), PrinterModel> printerModelsByKey = BuildFirstWinsDictionary(
                await _context.PrinterModels.Include(pm => pm.Toolheads).ToListAsync(),
                pm => (pm.ManufacturerId, pm.Name),
                ManufacturerScopedNameComparer.Instance);

            foreach (PrinterModelSeedDto dto in modelsData.Where(m => m.Toolheads?.Count > 0))
            {
                if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
                {
                    continue;
                }

                // Find the printer model
                printerModelsByKey.TryGetValue((manufacturerId, dto.Name), out PrinterModel? printerModel);

                if (printerModel == null)
                {
                    _logger.LogWarning("[SeedData] Printer model '{Name}' not found for toolhead seeding", dto.Name);
                    continue;
                }

                // Skip if already has toolheads
                if (printerModel.Toolheads?.Any() == true)
                {
                    continue;
                }

                int index = 0;
                foreach (ToolheadAssignmentDto toolheadDto in dto.Toolheads!)
                {
                    var printerModelToolhead = new PrinterModelToolhead
                    {
                        Id = Guid.NewGuid(),
                        PrinterModelId = printerModel.Id,
                        Name = toolheadDto.Name ?? $"Toolhead {index}",
                        Index = index,
                        IsPrimary = index == 0
                    };

                    // Resolve toolhead model and get its defaults
                    ToolheadModelDefinition? toolheadModel = null;
                    if (!string.IsNullOrEmpty(toolheadDto.Toolhead))
                    {
                        // Try composite key first (Name:Manufacturer), fall back to name-only
                        string compositeKey = $"{toolheadDto.Toolhead}:{dto.Manufacturer}";
                        if (!toolheadsByKey.TryGetValue(compositeKey, out toolheadModel))
                        {
                            toolheadsByName.TryGetValue(toolheadDto.Toolhead, out toolheadModel);
                        }

                        if (toolheadModel != null)
                        {
                            printerModelToolhead.ToolheadModelDefId = toolheadModel.Id;
                        }
                    }

                    // Resolve hotend model - use explicit value or fall back to toolhead default
                    if (!string.IsNullOrEmpty(toolheadDto.Hotend) &&
                        hotendsByName.TryGetValue(toolheadDto.Hotend, out Guid hotendId))
                    {
                        printerModelToolhead.HotendModelId = hotendId;
                    }
                    else if (toolheadModel?.DefaultHotendId != null)
                    {
                        printerModelToolhead.HotendModelId = toolheadModel.DefaultHotendId;
                    }

                    // Resolve extruder model - use explicit value or fall back to toolhead default
                    if (!string.IsNullOrEmpty(toolheadDto.Extruder) &&
                        extrudersByName.TryGetValue(toolheadDto.Extruder, out Guid extruderId))
                    {
                        printerModelToolhead.ExtruderModelId = extruderId;
                    }
                    else if (toolheadModel?.DefaultExtruderId != null)
                    {
                        printerModelToolhead.ExtruderModelId = toolheadModel.DefaultExtruderId;
                    }

                    // Resolve nozzle model - use explicit value or fall back to toolhead default
                    if (!string.IsNullOrEmpty(toolheadDto.Nozzle) &&
                        nozzlesByName.TryGetValue(toolheadDto.Nozzle, out Guid nozzleId))
                    {
                        printerModelToolhead.NozzleModelId = nozzleId;
                    }
                    else if (toolheadModel?.DefaultNozzleId != null)
                    {
                        printerModelToolhead.NozzleModelId = toolheadModel.DefaultNozzleId;
                    }

                    _context.PrinterModelToolheads.Add(printerModelToolhead);
                    index++;
                }

                seededCount++;
            }

            if (seededCount > 0)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("[SeedData] Seeded toolheads for {SeededCount} printer model(s)", seededCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding printer model toolheads: {Message}", ex.Message);
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

            // Seed toolheads (creates records without default components)
            await SeedToolheadsAsync(manufacturers);

            // Seed nozzles
            await SeedNozzlesAsync(manufacturers);

            // Resolve toolhead default components (must run after all components are seeded)
            await ResolveToolheadDefaultComponentsFromYamlAsync(manufacturers);

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

        _logger.LogInformation("[SeedData] Seeding {HotendsCount} hotend models", hotends.Count);

        // Preload existing hotends keyed by (ManufacturerId, Name) once instead of one
        // existence query per row. Built via first-wins TryAdd (not ToDictionaryAsync) because
        // this entity has no unique DB constraint on the key, so a legitimate duplicate would
        // otherwise throw ArgumentException instead of being tolerated like the old per-row
        // FirstOrDefaultAsync. Case-insensitive for cross-provider parity.
        Dictionary<(Guid ManufacturerId, string Name), HotendModelDefinition> existingByKey = BuildFirstWinsDictionary(
            await _context.HotendModelDefinitions.ToListAsync(),
            h => (h.ManufacturerId, h.Name),
            ManufacturerScopedNameComparer.Instance);

        foreach (HotendModelSeedDto dto in hotends)
        {
            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for hotend '{Name}', skipping",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            existingByKey.TryGetValue((manufacturerId, dto.Name), out HotendModelDefinition? existing);

            if (existing == null)
            {
                var hotend = new HotendModelDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name,
                    ManufacturerId = manufacturerId,
                    MaxTemp = dto.MaxTemp,
                    IsHighFlow = dto.IsHighFlow,
                    MaxFlowRate = dto.MaxFlowRate,
                    Description = dto.Description,
                    Url = dto.Url
                };
                _context.HotendModelDefinitions.Add(hotend);
                existingByKey[(manufacturerId, dto.Name)] = hotend;
            }
            else
            {
                existing.MaxTemp = dto.MaxTemp;
                existing.IsHighFlow = dto.IsHighFlow;
                existing.MaxFlowRate = dto.MaxFlowRate;
                existing.Description = dto.Description;
                existing.Url = dto.Url;
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

        _logger.LogInformation("[SeedData] Seeding {ExtrudersCount} extruder models", extruders.Count);

        // Preload existing extruders keyed by (ManufacturerId, Name) once instead of one
        // existence query per row. Built via first-wins TryAdd (not ToDictionaryAsync); see
        // SeedHotendsAsync for why. Case-insensitive for cross-provider parity.
        Dictionary<(Guid ManufacturerId, string Name), ExtruderModelDefinition> existingByKey = BuildFirstWinsDictionary(
            await _context.ExtruderModelDefinitions.ToListAsync(),
            e => (e.ManufacturerId, e.Name),
            ManufacturerScopedNameComparer.Instance);

        foreach (ExtruderModelSeedDto dto in extruders)
        {
            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for extruder '{Name}', skipping",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            existingByKey.TryGetValue((manufacturerId, dto.Name), out ExtruderModelDefinition? existing);

            if (existing == null)
            {
                var extruder = new ExtruderModelDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name,
                    ManufacturerId = manufacturerId,
                    GearRatio = dto.GearRatio,
                    IsDirectDrive = dto.IsDirectDrive,
                    Description = dto.Description,
                    Url = dto.Url
                };
                _context.ExtruderModelDefinitions.Add(extruder);
                existingByKey[(manufacturerId, dto.Name)] = extruder;
            }
            else
            {
                existing.GearRatio = dto.GearRatio;
                existing.IsDirectDrive = dto.IsDirectDrive;
                existing.Description = dto.Description;
                existing.Url = dto.Url;
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

        _logger.LogInformation("[SeedData] Seeding {ToolheadsCount} toolhead models", toolheads.Count);

        // Preload existing toolheads keyed by (ManufacturerId, Name) once instead of one
        // existence query per row. Built via first-wins TryAdd (not ToDictionaryAsync); see
        // SeedHotendsAsync for why. Case-insensitive for cross-provider parity.
        Dictionary<(Guid ManufacturerId, string Name), ToolheadModelDefinition> existingByKey = BuildFirstWinsDictionary(
            await _context.ToolheadModelDefinitions.ToListAsync(),
            t => (t.ManufacturerId, t.Name),
            ManufacturerScopedNameComparer.Instance);

        foreach (ToolheadModelSeedDto dto in toolheads)
        {
            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for toolhead '{Name}', skipping",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            existingByKey.TryGetValue((manufacturerId, dto.Name), out ToolheadModelDefinition? existing);

            if (existing == null)
            {
                var toolhead = new ToolheadModelDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name,
                    ManufacturerId = manufacturerId,
                    Description = dto.Description,
                    Url = dto.Url
                };
                _context.ToolheadModelDefinitions.Add(toolhead);
                existingByKey[(manufacturerId, dto.Name)] = toolhead;
            }
            else
            {
                existing.Description = dto.Description;
                existing.Url = dto.Url;
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task ResolveToolheadDefaultComponentsFromYamlAsync(Dictionary<string, Guid> manufacturers)
    {
        List<ToolheadModelSeedDto> toolheads = await _yamlReader.ReadToolheadsAsync();

        if (toolheads.Count == 0)
        {
            return;
        }

        _logger.LogInformation("[SeedData] Resolving toolhead default components from YAML");

        // Build component lookups using composite key (name:manufacturer)
        // This allows different manufacturers to have components with the same name
        Dictionary<string, Guid> hotendsByKey = await _context.HotendModelDefinitions
            .Include(h => h.Manufacturer)
            .ToDictionaryAsync(
                h => $"{h.Name}:{h.Manufacturer?.Name ?? "Unknown"}",
                h => h.Id,
                StringComparer.OrdinalIgnoreCase);

        // Also build name-only lookup for backward compatibility (first match wins)
        Dictionary<string, Guid> hotendsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var h in await _context.HotendModelDefinitions.ToListAsync())
        {
            hotendsByName.TryAdd(h.Name, h.Id);
        }

        Dictionary<string, Guid> extrudersByKey = await _context.ExtruderModelDefinitions
            .Include(e => e.Manufacturer)
            .ToDictionaryAsync(
                e => $"{e.Name}:{e.Manufacturer?.Name ?? "Unknown"}",
                e => e.Id,
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, Guid> extrudersByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var e in await _context.ExtruderModelDefinitions.ToListAsync())
        {
            extrudersByName.TryAdd(e.Name, e.Id);
        }

        Dictionary<string, Guid> nozzlesByKey = await _context.NozzleModelDefinitions
            .Include(n => n.Manufacturer)
            .ToDictionaryAsync(
                n => $"{n.Name}:{n.Manufacturer?.Name ?? "Unknown"}",
                n => n.Id,
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, Guid> nozzlesByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var n in await _context.NozzleModelDefinitions.ToListAsync())
        {
            nozzlesByName.TryAdd(n.Name, n.Id);
        }

        int updatedCount = 0;

        // Preload toolhead definitions keyed by (ManufacturerId, Name) once instead of one
        // existence query per row. Built via first-wins TryAdd (not ToDictionaryAsync); see
        // SeedHotendsAsync for why. Case-insensitive for cross-provider parity.
        Dictionary<(Guid ManufacturerId, string Name), ToolheadModelDefinition> toolheadsByManufacturerAndName =
            BuildFirstWinsDictionary(
                await _context.ToolheadModelDefinitions.ToListAsync(),
                t => (t.ManufacturerId, t.Name),
                ManufacturerScopedNameComparer.Instance);

        foreach (ToolheadModelSeedDto dto in toolheads)
        {
            // Skip if no defaults specified
            if (string.IsNullOrEmpty(dto.DefaultHotend) &&
                string.IsNullOrEmpty(dto.DefaultExtruder) &&
                string.IsNullOrEmpty(dto.DefaultNozzle))
            {
                continue;
            }

            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                continue;
            }

            // Find the toolhead definition
            toolheadsByManufacturerAndName.TryGetValue((manufacturerId, dto.Name), out ToolheadModelDefinition? toolhead);

            if (toolhead == null)
            {
                _logger.LogWarning(
                    "[SeedData] Toolhead '{Name}' not found for resolving default components",
                    dto.Name);
                continue;
            }

            bool needsUpdate = false;

            // Resolve default hotend - try toolhead's manufacturer first, then name-only
            if (!string.IsNullOrEmpty(dto.DefaultHotend))
            {
                string compositeKey = $"{dto.DefaultHotend}:{dto.Manufacturer}";
                if (!hotendsByKey.TryGetValue(compositeKey, out Guid hotendId))
                {
                    hotendsByName.TryGetValue(dto.DefaultHotend, out hotendId);
                }

                if (hotendId != Guid.Empty)
                {
                    if (toolhead.DefaultHotendId != hotendId)
                    {
                        toolhead.DefaultHotendId = hotendId;
                        needsUpdate = true;
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[SeedData] Default hotend '{Hotend}' not found for toolhead '{Toolhead}'",
                        dto.DefaultHotend, dto.Name);
                }
            }

            // Resolve default extruder - try toolhead's manufacturer first, then name-only
            if (!string.IsNullOrEmpty(dto.DefaultExtruder))
            {
                string compositeKey = $"{dto.DefaultExtruder}:{dto.Manufacturer}";
                if (!extrudersByKey.TryGetValue(compositeKey, out Guid extruderId))
                {
                    extrudersByName.TryGetValue(dto.DefaultExtruder, out extruderId);
                }

                if (extruderId != Guid.Empty)
                {
                    if (toolhead.DefaultExtruderId != extruderId)
                    {
                        toolhead.DefaultExtruderId = extruderId;
                        needsUpdate = true;
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[SeedData] Default extruder '{Extruder}' not found for toolhead '{Toolhead}'",
                        dto.DefaultExtruder, dto.Name);
                }
            }

            // Resolve default nozzle - try toolhead's manufacturer first, then name-only
            if (!string.IsNullOrEmpty(dto.DefaultNozzle))
            {
                string compositeKey = $"{dto.DefaultNozzle}:{dto.Manufacturer}";
                if (!nozzlesByKey.TryGetValue(compositeKey, out Guid nozzleId))
                {
                    nozzlesByName.TryGetValue(dto.DefaultNozzle, out nozzleId);
                }

                if (nozzleId != Guid.Empty)
                {
                    if (toolhead.DefaultNozzleId != nozzleId)
                    {
                        toolhead.DefaultNozzleId = nozzleId;
                        needsUpdate = true;
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[SeedData] Default nozzle '{Nozzle}' not found for toolhead '{Toolhead}'",
                        dto.DefaultNozzle, dto.Name);
                }
            }

            if (needsUpdate)
            {
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Resolved default components for {UpdatedCount} toolhead(s)", updatedCount);
        }
    }

    private async Task SeedNozzlesAsync(Dictionary<string, Guid> manufacturers)
    {
        List<NozzleModelSeedDto> nozzles = await _yamlReader.ReadNozzlesAsync();

        if (nozzles.Count == 0)
        {
            _logger.LogInformation("[SeedData] No nozzles found in YAML, skipping");
            return;
        }

        _logger.LogInformation("[SeedData] Seeding {NozzlesCount} nozzle models", nozzles.Count);

        // Built-in materials are seeded by the #1824 data migration with names matching the
        // legacy NozzleType enum member names, so this is a direct name lookup.
        Dictionary<string, Guid> materialsByName = await _context.NozzleMaterials
            .ToDictionaryAsync(m => m.Name, m => m.Id, StringComparer.OrdinalIgnoreCase);

        if (!materialsByName.TryGetValue(nameof(NozzleType.Brass), out Guid defaultMaterialId))
        {
            _logger.LogWarning("[SeedData] Built-in nozzle material '{Material}' not found, nozzle seeding may be incomplete", nameof(NozzleType.Brass));
        }

        // Preload existing nozzles keyed by (ManufacturerId, Name) once instead of one
        // existence query per row. Built via first-wins TryAdd (not ToDictionaryAsync); see
        // SeedHotendsAsync for why. Case-insensitive for cross-provider parity.
        Dictionary<(Guid ManufacturerId, string Name), NozzleModelDefinition> existingByKey = BuildFirstWinsDictionary(
            await _context.NozzleModelDefinitions.ToListAsync(),
            n => (n.ManufacturerId, n.Name),
            ManufacturerScopedNameComparer.Instance);

        foreach (NozzleModelSeedDto dto in nozzles)
        {
            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for nozzle '{Name}', skipping",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            NozzleType nozzleType = ParseSeedEnum(dto.NozzleType, NozzleType.Brass, nameof(dto.NozzleType), dto.Name);
            NozzleHardnessOverride hardnessOverride = ParseSeedEnum(
                dto.HardnessOverride, NozzleHardnessOverride.Auto, nameof(dto.HardnessOverride), dto.Name);
            NozzleInterfaceType nozzleInterface = ParseSeedEnum(
                dto.NozzleInterface, NozzleInterfaceType.V6, nameof(dto.NozzleInterface), dto.Name);

            // Resolve the parsed material enum to its NozzleMaterial catalog row by name. Built-in
            // materials are seeded with names matching the enum member names (see #1824's data
            // migration), so this is a direct name lookup once the enum itself has been safely parsed.
            if (!materialsByName.TryGetValue(nozzleType.ToString(), out Guid nozzleMaterialId))
            {
                nozzleMaterialId = defaultMaterialId;
                _logger.LogWarning(
                    "[SeedData] Nozzle material '{NozzleType}' not found for nozzle '{Name}', defaulting to Brass",
                    nozzleType, dto.Name);
            }

            existingByKey.TryGetValue((manufacturerId, dto.Name), out NozzleModelDefinition? existing);

            if (existing == null)
            {
                var nozzle = new NozzleModelDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name,
                    ManufacturerId = manufacturerId,
                    Diameter = dto.Diameter,
                    MaxTemp = dto.MaxTemp,
                    NozzleMaterialId = nozzleMaterialId,
                    HardnessOverride = hardnessOverride,
                    NozzleInterface = nozzleInterface,
                    Description = dto.Description,
                    Url = dto.Url
                };
                _context.NozzleModelDefinitions.Add(nozzle);
                existingByKey[(manufacturerId, dto.Name)] = nozzle;
            }
            else
            {
                existing.Diameter = dto.Diameter;
                existing.MaxTemp = dto.MaxTemp;
                existing.NozzleMaterialId = nozzleMaterialId;
                existing.HardnessOverride = hardnessOverride;
                existing.NozzleInterface = nozzleInterface;
                existing.Description = dto.Description;
                existing.Url = dto.Url;
            }
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Parses an enum-valued seed field, warning rather than silently falling back when the
    /// value is unrecognized. Silent fallback is unsafe here: nozzle hardness gates whether
    /// abrasive filament may be dispatched, so a typo must not quietly re-enable a nozzle the
    /// operator excluded.
    /// </summary>
    /// <typeparam name="TEnum">The enum type to parse into.</typeparam>
    /// <param name="rawValue">Raw YAML value; null or empty yields <paramref name="fallback"/> without warning.</param>
    /// <param name="fallback">Value to use when the field is absent or unparseable.</param>
    /// <param name="fieldName">Field name, for the warning message.</param>
    /// <param name="nozzleName">Owning nozzle name, for the warning message.</param>
    /// <returns>The parsed value, or <paramref name="fallback"/>.</returns>
    private TEnum ParseSeedEnum<TEnum>(string? rawValue, TEnum fallback, string fieldName, string nozzleName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return fallback;
        }

        // Normalize once, then guard and parse the SAME string. Checking the raw value while
        // parsing the space-stripped one leaves a hole: "+ 5" fails the numeric guard (the
        // sign is detached from its digits) but then parses as ordinal 5.
        string normalized = rawValue.Replace(" ", string.Empty);

        // Reject numeric input outright. Enum.TryParse happily maps "5" onto a defined
        // member, so seed YAML could otherwise pin a material by ordinal and silently
        // change meaning if the enum is ever renumbered. Enum.IsDefined below only rejects
        // *undefined* ordinals, which is not the same guarantee.
        bool isNumeric = long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

        if (!isNumeric &&
            Enum.TryParse(normalized, true, out TEnum parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        _logger.LogWarning(
            "[SeedData] Unrecognized {Field} '{Value}' for nozzle '{Name}', using {Fallback}",
            fieldName, LogSanitizer.Sanitize(rawValue), LogSanitizer.Sanitize(nozzleName), fallback);
        return fallback;
    }

    private async Task SeedPrinterModelAliasesAsync(
        List<PrinterModelSeedDto> modelsData,
        Dictionary<string, Guid> manufacturers)
    {
        // Preload printer models together with their existing aliases in a single query,
        // keyed by (ManufacturerId, Name) — matching the unique index and the lookup used
        // when the models themselves are created/updated — instead of one per-model lookup
        // plus one per-alias existence query. This loop was a major contributor to the ~400
        // sequential seed queries in #2328. A name-only lookup would silently collapse models
        // that share a Name across different manufacturers onto whichever row happens to come
        // back first, so aliases for the "losing" manufacturer's model would never be seeded.
        // Case-insensitive for cross-provider parity; see ManufacturerScopedNameComparer.
        Dictionary<(Guid ManufacturerId, string Name), PrinterModel> modelsByKey = BuildFirstWinsDictionary(
            await _context.PrinterModels.Include(pm => pm.Aliases).ToListAsync(),
            pm => (pm.ManufacturerId, pm.Name),
            ManufacturerScopedNameComparer.Instance);

        foreach (PrinterModelSeedDto dto in modelsData)
        {
            if (dto.Aliases == null || dto.Aliases.Count == 0)
            {
                continue;
            }

            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for model '{Model}', skipping aliases",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            if (!modelsByKey.TryGetValue((manufacturerId, dto.Name), out PrinterModel? model))
            {
                continue;
            }

            foreach (SlicerAliasDto alias in dto.Aliases)
            {
                // Compare on the normalized columns (not the raw SlicerModelName/SlicerType) so
                // seeding doesn't attempt to insert a case/whitespace-variant duplicate that the
                // unique index on the normalized columns would reject (#2080).
                string normalizedSeedName = PrinterModelAlias.NormalizeLookupValue(alias.SlicerModelName);
                string normalizedSeedType = PrinterModelAlias.NormalizeLookupValue(alias.SlicerType);
                bool aliasExists = model.Aliases.Any(a =>
                    a.SlicerModelNameNormalized == normalizedSeedName &&
                    a.SlicerTypeNormalized == normalizedSeedType);

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

    private async Task SeedModelFilamentTypesAsync(
        List<PrinterModelSeedDto> modelsData,
        Dictionary<string, Guid> manufacturers)
    {
        // Build filament type lookup (full entities, so associations can be added without an
        // extra FindAsync round trip per association).
        Dictionary<string, FilamentType> filamentTypesByName = await _context.FilamentTypes
            .ToDictionaryAsync(ft => ft.Name, StringComparer.OrdinalIgnoreCase);

        // Preload printer models together with their existing filament-type associations in a
        // single query, keyed by (ManufacturerId, Name) — matching the unique index and the
        // lookup used when the models themselves are created/updated — instead of one per-model
        // lookup plus an explicit Collection(...).LoadAsync() round trip per model. This loop was
        // a major contributor to the ~400 sequential seed queries in #2328. A name-only lookup
        // would silently collapse models that share a Name across different manufacturers onto
        // whichever row happens to come back first, so the "losing" manufacturer's model would
        // never get its filament-type associations.
        Dictionary<(Guid ManufacturerId, string Name), PrinterModel> modelsByKey = BuildFirstWinsDictionary(
            await _context.PrinterModels.Include(pm => pm.SupportedFilamentTypes).ToListAsync(),
            pm => (pm.ManufacturerId, pm.Name),
            ManufacturerScopedNameComparer.Instance);

        foreach (PrinterModelSeedDto dto in modelsData)
        {
            if (dto.SupportedMaterials == null || dto.SupportedMaterials.Count == 0)
            {
                continue;
            }

            if (!manufacturers.TryGetValue(dto.Manufacturer, out Guid manufacturerId))
            {
                _logger.LogWarning(
                    "[SeedData] Manufacturer '{Manufacturer}' not found for model '{Model}', skipping filament types",
                    dto.Manufacturer, dto.Name);
                continue;
            }

            if (!modelsByKey.TryGetValue((manufacturerId, dto.Name), out PrinterModel? model))
            {
                continue;
            }

            foreach (string material in dto.SupportedMaterials)
            {
                if (filamentTypesByName.TryGetValue(material, out FilamentType? filamentType))
                {
                    // Check if filament type is already associated using skip navigation
                    bool exists = model.SupportedFilamentTypes.Any(ft => ft.Id == filamentType.Id);

                    if (!exists)
                    {
                        model.SupportedFilamentTypes.Add(filamentType);
                    }
                }
            }
        }

        await _context.SaveChangesAsync();
    }

    public async Task SeedMaintenanceTasksAsync()
    {
        try
        {
            List<MaintenanceTaskSeedDto> tasksData = await _yamlReader.ReadMaintenanceTasksAsync();

            if (tasksData.Count == 0)
            {
                _logger.LogInformation("[SeedData] No maintenance tasks found in YAML, skipping");
                return;
            }

            _logger.LogInformation("[SeedData] Seeding {TaskCount} maintenance tasks from YAML", tasksData.Count);

            // Preload all existing maintenance tasks once instead of one existence query per row.
            // Built via first-wins TryAdd (not ToDictionaryAsync): MaintenanceTaskName has no
            // unique DB constraint (tasks are user-creatable), so two legitimately-named-the-same
            // rows can exist; ToDictionaryAsync would throw ArgumentException on the second one,
            // whereas the old per-row FirstOrDefaultAsync silently tolerated it. Case-insensitive
            // for cross-provider parity and to match the other call site over this same table
            // (SeedMaintenancePlansAsync), which previously used OrdinalIgnoreCase inconsistently.
            Dictionary<string, MaintenanceTask> existingByName = BuildFirstWinsDictionary(
                await _context.MaintenanceTasks.ToListAsync(), t => t.TaskName, StringComparer.OrdinalIgnoreCase);

            foreach (MaintenanceTaskSeedDto dto in tasksData)
            {
                existingByName.TryGetValue(dto.TaskName, out MaintenanceTask? existing);

                if (existing == null)
                {
                    var task = new MaintenanceTask
                    {
                        Id = Guid.NewGuid(),
                        TaskName = dto.TaskName,
                        Description = dto.Description,
                        Category = dto.Category,
                        IntervalHours = dto.IntervalHours,
                        IntervalDays = dto.IntervalDays,
                        EstimatedDurationMinutes = dto.EstimatedDurationMinutes,
                        Priority = dto.Priority,
                        IsActive = dto.IsActive,
                        IsDefault = true,
                        RequiresEnclosure = dto.RequiresEnclosure,
                        RequiresCarbonFilter = dto.RequiresCarbonFilter,
                        RequiresHepaFilter = dto.RequiresHepaFilter,
                        RequiresBowdenTube = dto.RequiresBowdenTube,
                        RequiresPtfeLiner = dto.RequiresPtfeLiner,
                        RequiresLinearRails = dto.RequiresLinearRails,
                        RequiresLeadScrews = dto.RequiresLeadScrews,
                        RequiresToolchanger = dto.RequiresToolchanger,
                        RequiresFilamentCutter = dto.RequiresFilamentCutter,
                        RequiresHeatedChamber = dto.RequiresHeatedChamber,
                        RequiresHeatedBed = dto.RequiresHeatedBed,
                        RequiresMultiMaterial = dto.RequiresMultiMaterial,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _context.MaintenanceTasks.Add(task);
                    existingByName[dto.TaskName] = task;
                }
                else
                {
                    existing.Description = dto.Description;
                    existing.Category = dto.Category;
                    existing.IntervalHours = dto.IntervalHours;
                    existing.IntervalDays = dto.IntervalDays;
                    existing.EstimatedDurationMinutes = dto.EstimatedDurationMinutes;
                    existing.Priority = dto.Priority;
                    existing.IsActive = dto.IsActive;
                    existing.RequiresEnclosure = dto.RequiresEnclosure;
                    existing.RequiresCarbonFilter = dto.RequiresCarbonFilter;
                    existing.RequiresHepaFilter = dto.RequiresHepaFilter;
                    existing.RequiresBowdenTube = dto.RequiresBowdenTube;
                    existing.RequiresPtfeLiner = dto.RequiresPtfeLiner;
                    existing.RequiresLinearRails = dto.RequiresLinearRails;
                    existing.RequiresLeadScrews = dto.RequiresLeadScrews;
                    existing.RequiresToolchanger = dto.RequiresToolchanger;
                    existing.RequiresFilamentCutter = dto.RequiresFilamentCutter;
                    existing.RequiresHeatedChamber = dto.RequiresHeatedChamber;
                    existing.RequiresHeatedBed = dto.RequiresHeatedBed;
                    existing.RequiresMultiMaterial = dto.RequiresMultiMaterial;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Maintenance tasks seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding maintenance tasks: {Message}", ex.Message);
            throw;
        }
    }

    public async Task SeedMaintenanceComponentsAsync()
    {
        try
        {
            List<MaintenanceComponentSeedDto> componentsData = await _yamlReader.ReadMaintenanceComponentsAsync();

            if (componentsData.Count == 0)
            {
                _logger.LogInformation("[SeedData] No maintenance components found in YAML, skipping");
                return;
            }

            _logger.LogInformation("[SeedData] Seeding {ComponentCount} maintenance components from YAML", componentsData.Count);

            // Preload all existing maintenance components keyed by (Name, Category) once
            // instead of one existence query per row. Built via first-wins TryAdd (not
            // ToDictionaryAsync): this key has no unique DB constraint (components are
            // user-creatable), so a legitimate duplicate would otherwise throw ArgumentException
            // instead of being tolerated like the old per-row FirstOrDefaultAsync.
            // Case-insensitive for cross-provider parity.
            Dictionary<(string Name, string Category), MaintenanceComponent> existingByKey = BuildFirstWinsDictionary(
                await _context.MaintenanceComponents.ToListAsync(), c => (c.Name, c.Category), NameCategoryComparer.Instance);

            foreach (MaintenanceComponentSeedDto dto in componentsData)
            {
                existingByKey.TryGetValue((dto.Name, dto.Category), out MaintenanceComponent? existing);

                if (existing == null)
                {
                    var component = new MaintenanceComponent
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        Category = dto.Category,
                        Description = dto.Description,
                        Sku = dto.Sku,
                        UnitCost = dto.UnitCost,
                        Supplier = dto.Supplier,
                        Url = dto.Url,
                        InStock = 0,
                        MinimumStock = dto.RecommendedMinimumStock,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _context.MaintenanceComponents.Add(component);
                    existingByKey[(dto.Name, dto.Category)] = component;
                }
                else
                {
                    existing.Description = dto.Description;
                    existing.Sku = dto.Sku;
                    existing.UnitCost = dto.UnitCost;
                    existing.Supplier = dto.Supplier;
                    existing.Url = dto.Url;
                    existing.MinimumStock = dto.RecommendedMinimumStock;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Maintenance components seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding maintenance components: {Message}", ex.Message);
            throw;
        }
    }

    public async Task SeedMaintenancePlansAsync()
    {
        try
        {
            List<MaintenancePlanSeedDto> plansData = await _yamlReader.ReadMaintenancePlansAsync();

            if (plansData.Count == 0)
            {
                _logger.LogInformation("[SeedData] No maintenance plans found in YAML, skipping");
                return;
            }

            _logger.LogInformation("[SeedData] Seeding {PlanCount} maintenance plans from YAML", plansData.Count);

            // Pre-load all tasks by name for efficient lookup. Built via first-wins TryAdd (not
            // ToDictionaryAsync): TaskName has no unique DB constraint, so a legitimate duplicate
            // would otherwise throw ArgumentException instead of resolving to the first match,
            // same as the old per-row lookup.
            Dictionary<string, MaintenanceTask> tasksByName = BuildFirstWinsDictionary(
                await _context.MaintenanceTasks.ToListAsync(), t => t.TaskName, StringComparer.OrdinalIgnoreCase);

            // Preload all existing plans (with their tasks) once instead of one existence
            // query per row. Built via first-wins TryAdd (not ToDictionaryAsync): plan Name has
            // no unique DB constraint (plans are user-creatable per printer/model), so a
            // legitimate duplicate would otherwise throw ArgumentException instead of being
            // tolerated like the old per-row FirstOrDefaultAsync. Case-insensitive for
            // cross-provider parity.
            Dictionary<string, MaintenancePlan> existingByName = BuildFirstWinsDictionary(
                await _context.MaintenancePlans.Include(p => p.PlanTasks).ToListAsync(),
                p => p.Name,
                StringComparer.OrdinalIgnoreCase);

            foreach (MaintenancePlanSeedDto dto in plansData)
            {
                existingByName.TryGetValue(dto.Name, out MaintenancePlan? existing);

                if (existing == null)
                {
                    var plan = new MaintenancePlan
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        Description = dto.Description,
                        IsActive = dto.IsActive,
                        IsDefault = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };

                    // Resolve task names to PlanTask join entities
                    int sortOrder = 0;
                    foreach (string taskName in dto.Tasks)
                    {
                        if (tasksByName.TryGetValue(taskName, out MaintenanceTask? task))
                        {
                            plan.PlanTasks.Add(new PlanTask
                            {
                                Id = Guid.NewGuid(),
                                MaintenancePlanId = plan.Id,
                                MaintenanceTaskId = task.Id,
                                SortOrder = sortOrder++,
                            });
                        }
                        else
                        {
                            _logger.LogWarning("[SeedData] Plan '{PlanName}' references unknown task '{TaskName}', skipping", dto.Name, taskName);
                        }
                    }

                    _context.MaintenancePlans.Add(plan);
                    existingByName[dto.Name] = plan;
                }
                else
                {
                    existing.Description = dto.Description;
                    existing.IsActive = dto.IsActive;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[SeedData] Maintenance plans seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error seeding maintenance plans: {Message}", ex.Message);
            throw;
        }
    }
}

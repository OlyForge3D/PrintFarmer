#pragma warning disable SA1402 // File may only contain a single type (or custom namespace warning)
#pragma warning disable CS0136 // Suppress variable shadowing error in this file

using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

public class DatabaseSeeder(AppDbContext context, IUnifiedLoggingService logger) : IDatabaseSeeder
{
    private readonly AppDbContext _context = context;
    private readonly IUnifiedLoggingService _logger = logger;

    public async Task SeedCatalogDataAsync()
    {
        try
        {
            // Desired manufacturers to ensure exist (display names)
            // NOTE: "VoronDesign" was previously used; renamed to the friendlier "Voron" for UI display.
            // We include a one-time rename below for existing databases.
            string[] manufacturerNames = new[]
            {
                "Unknown",  // Default for unidentified manufacturers - must be first to ensure it gets a consistent ID
                "Elegoo",
                "Eryone",
                "FlashForge",
                "Phrozen",
                "PrintersForAnts",
                "Prusa",
                "Sovol",
                "RatRig",
                "Voron",
            };


            Dictionary<string, Manufacturer> manufacturers = new(StringComparer.OrdinalIgnoreCase);
            foreach (string? name in manufacturerNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string normalized = CatalogNameNormalizer.NormalizeManufacturer(name);
                Manufacturer? existing = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == normalized);
                if (existing == null)
                {
                    existing = new Manufacturer { Id = Guid.NewGuid(), Name = normalized };
                    _context.Manufacturers.Add(existing);
                    await _context.SaveChangesAsync();
                }
                manufacturers[normalized] = existing;
            }

            // Models to ensure exist with comprehensive capability data
            // (Name, ManufacturerName, MaxX/MaxY/MaxZ, DefaultBackend, MotionType, NozzleDiam, HasBed, HasEnclosure, MultiMat, Extruders, AutoLevel, MinHot, MaxHot, MinBed, MaxBed, Materials, MaxSpeed)
            (string Name, string Mfg, double X, double Y, double Z, int? DefaultBackend, MotionType? MotionType,
             double? NozzleDiameter, bool HasBed, bool HasEnclosure, bool MultiMaterial, int Extruders, bool AutoLevel,
             int? MinHotend, int? MaxHotend, int? MinBed, int? MaxBed, string Materials, int? MaxSpeed)[] modelSeeds = new[]
            {
                ("Unknown Model", "Unknown", 200.0, 200.0, 200.0, (int?)0, (MotionType?)MotionType.Unknown, (double?)0.4, true, false, false, 1, false, (int?)0, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS", (int?)100),
                
                // FlashForge models
                ("AD5X", "FlashForge", 220.0, 220.0, 220.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                
                // Sovol models
                ("SV08", "Sovol", 350.0, 350.0, 350.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("SV08 Max", "Sovol", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Zero", "Sovol", 150.0, 150.0, 150.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS", (int?)150),
                
                // Eryone models
                ("Thinker X400", "Eryone", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                
                // Elegoo models  
                ("Centauri", "Elegoo", 256.0, 256.0, 256.0, (int?)2, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Centauri Carbon", "Elegoo", 256.0, 256.0, 256.0, (int?)2, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                
                // PrintersForAnts models
                ("SaladFork 120", "PrintersForAnts", 120.0, 120.0, 120.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("SaladFork 180", "PrintersForAnts", 180.0, 180.0, 180.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 120", "PrintersForAnts", 120.0, 120.0, 120.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 160", "PrintersForAnts", 160.0, 160.0, 165.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 180", "PrintersForAnts", 180.0, 180.0, 165.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                
                // Voron models
                ("Voron v0", "Voron", 120.0, 120.0, 120.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("v2.4 250", "Voron", 250.0, 250.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("v2.4 300", "Voron", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("v2.4 350", "Voron", 350.0, 350.0, 350.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Switchwire", "Voron", 250.0, 210.0, 240.0, (int?)0, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Trident 250", "Voron", 250.0, 250.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Trident 300", "Voron", 300.0, 300.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Trident 300 Cube", "Voron", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Trident 350", "Voron", 350.0, 350.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                
                // RatRig models
                ("vCore3.1 200", "RatRig", 200.0, 200.0, 200.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.1 300", "RatRig", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.1 400", "RatRig", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.1 500", "RatRig", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.2 200", "RatRig", 200.0, 200.0, 200.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.2 300", "RatRig", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.2 400", "RatRig", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.2 500", "RatRig", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore4 300", "RatRig", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 400", "RatRig", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 500", "RatRig", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 300 Hybrid", "RatRig", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 400 Hybrid", "RatRig", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 500 Hybrid", "RatRig", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 300 IDEX", "RatRig", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 400 IDEX", "RatRig", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 500 IDEX", "RatRig", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                
                // Phrozen models
                ("Arco", "Phrozen", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                
                // Prusa models
                ("Original Prusa Mini+", "Prusa", 180.0, 180.0, 180.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS,ASA,PC", (int?)180),
                ("Original Prusa i3 MK3S+", "Prusa", 250.0, 210.0, 210.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                ("Original Prusa MK4S", "Prusa", 250.0, 210.0, 220.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
                ("Original Prusa Core One", "Prusa", 250.0, 220.0, 270.0, (int?)1, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)250),
                ("Original Prusa XL", "Prusa", 250.0, 220.0, 270.0, (int?)1, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, true, 5, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
            };

            foreach ((string modelName, string mfg, double x, double y, double z, int? defaultBackend, MotionType? motionType,
                      double? nozzleDiameter, bool hasBed, bool hasEnclosure, bool multiMaterial, int extruders, bool autoLevel,
                      int? minHotend, int? maxHotend, int? minBed, int? maxBed, string _, int? maxSpeed) in modelSeeds)
            {

                if (!manufacturers.TryGetValue(mfg, out Manufacturer? m))
                {
                    // Skip if manufacturer wasn't ensured above for any reason
                    continue;
                }

                bool exists = await _context.Models.AnyAsync(pm => pm.ManufacturerId == m.Id && pm.Name == modelName);
                if (!exists)
                {
                    _context.Models.Add(new PrinterModel
                    {
                        Id = Guid.NewGuid(),
                        Name = modelName,
                        ManufacturerId = m.Id,
                        MaxX = x,
                        MaxY = y,
                        MaxZ = z,
                        DefaultBackend = defaultBackend,
                        MotionType = (int?)motionType,
                        DefaultNozzleDiameter = nozzleDiameter,
                        HasHeatedBed = hasBed,
                        HasEnclosure = hasEnclosure,
                        MultiMaterial = multiMaterial,
                        NumberOfExtruders = extruders,
                        SupportsAutoLeveling = autoLevel,
                        MinHotendTemp = minHotend,
                        MaxHotendTemp = maxHotend,
                        MinBedTemp = minBed,
                        MaxBedTemp = maxBed,
                        MaxPrintSpeed = maxSpeed
                    });
                }
            }
            await _context.SaveChangesAsync();

            // Now create the filament type relationships
            await SeedModelFilamentTypesAsync(modelSeeds);
        }
        catch (Exception ex)
        {
            // Log the exception but don't throw to prevent application startup failure
            _logger.LogError(ex, "Catalog seeding error");
            throw; // Re-throw if you want startup to fail on seeding errors
        }
    }

    private async Task SeedModelFilamentTypesAsync((string Name, string Mfg, double X, double Y, double Z,
             int? DefaultBackend, MotionType? MotionType, double? NozzleDiameter, bool HasBed, bool HasEnclosure,
             bool MultiMaterial, int Extruders, bool AutoLevel, int? MinHotend, int? MaxHotend,
             int? MinBed, int? MaxBed, string Materials, int? MaxSpeed)[] modelSeeds)
    {
        try
        {
            // Get all filament types once
            var filamentTypes = await _context.FilamentTypes
                .ToDictionaryAsync(ft => ft.Name.ToUpperInvariant(), ft => ft);

            // Process each model's supported materials
#pragma warning disable IDE0008 // Use explicit type
            foreach (var (
                modelName,
                manufacturerName,
                volumeX,
                volumeY,
                volumeZ,
                backendId,
                modelMotionType,
                modelNozzleDiameter,
                hasHeatedBed,
                hasPrinterEnclosure,
                supportsMultiMaterial,
                extruderCount,
                supportsAutoLeveling,
                minHotendTemp,
                maxHotendTemp,
                minBedTemp,
                maxBedTemp,
                supportedMaterials,
                maxPrintSpeed
            ) in modelSeeds)
            {
                // Find the model we just created
                var model = await _context.Models
                    .FirstOrDefaultAsync(m => m.Name == modelName &&
                                           m.Manufacturer != null &&
                                           m.Manufacturer.Name == manufacturerName);

                if (model != null && !string.IsNullOrEmpty(supportedMaterials))
                {
                    // Parse the comma-separated materials list
                    var materialNames = supportedMaterials.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(material => material.Trim().ToUpperInvariant());

                    foreach (var materialName in materialNames)
                    {
                        if (filamentTypes.TryGetValue(materialName, out var filamentType))
                        {
                            // Check if relationship already exists
                            bool exists = await _context.PrinterModelFilamentTypes
                                .AnyAsync(pmft => pmft.PrinterModelId == model.Id &&
                                                pmft.FilamentTypeId == filamentType.Id);

                            if (!exists)
                            {
                                _context.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType
                                {
                                    PrinterModelId = model.Id,
                                    FilamentTypeId = filamentType.Id
                                });
                            }
                        }
                    }
                }
            }
#pragma warning restore IDE0008 // Use explicit type

            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Filament type seeding error");
            // Don't throw - this is not critical for application startup
        }
    }

    public async Task SeedSpoolmanConfigAsync()
    {
        try
        {
            // Check if SpoolmanConfig already exists
            SpoolmanConfig? existingConfig = await _context.SpoolmanConfigs.FirstOrDefaultAsync();
            if (existingConfig == null)
            {
                _context.SpoolmanConfigs.Add(new SpoolmanConfig
                {
                    // Remove explicit Id assignment - let SQL Server auto-generate it
                    BaseUrl = "http://spoolman.local:7912"
                });
                await _context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spoolman config seeding error");
        }
    }

    public async Task SeedFilamentTypesAsync()
    {
        // Default filament types to ensure exist
        (string Name, int HotendTemp, int BedTemp)[] filamentTypes = new (string Name, int HotendTemp, int BedTemp)[]
        {
            ("PLA", 205, 60),
            ("ABS", 230, 100),
            ("PETG", 240, 85),
            ("ASA", 245, 100),
            ("PC", 260, 110),
            ("PCTG", 235, 80),
            ("TPU", 220, 60),
            ("Wood", 210, 65)
        };

        foreach ((string? name, int hotendTemp, int bedTemp) in filamentTypes)
        {
            FilamentType? existing = await _context.FilamentTypes.FirstOrDefaultAsync(f => f.Name == name);
            if (existing == null)
            {
                FilamentType filamentType = new()
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    DefaultHotendTemp = hotendTemp,
                    DefaultBedTemp = bedTemp
                };
                _context.FilamentTypes.Add(filamentType);
            }
        }

        await _context.SaveChangesAsync();
    }

    public async Task SeedAllAsync()
    {
        await SeedSpoolmanConfigAsync();
        await SeedFilamentTypesAsync();  // Must come before SeedCatalogDataAsync
        await SeedCatalogDataAsync();    // This creates printer model/filament type relationships
    }

    /// <summary>
    /// Gets the "Unknown" manufacturer, which should always exist after seeding
    /// </summary>
    public async Task<Manufacturer> GetUnknownManufacturerAsync()
    {
        Manufacturer? unknown = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Unknown");
        return unknown ?? throw new InvalidOperationException("Unknown manufacturer not found. Ensure SeedCatalogDataAsync() has been called.");
    }

    /// <summary>
    /// Gets the "Unknown Model" from the "Unknown" manufacturer, which should always exist after seeding
    /// </summary>
    public async Task<PrinterModel> GetUnknownModelAsync()
    {
        Manufacturer unknownMfg = await GetUnknownManufacturerAsync();
        PrinterModel? unknownModel = await _context.Models.FirstOrDefaultAsync(m =>
            m.ManufacturerId == unknownMfg.Id && m.Name == "Unknown Model");
        return unknownModel ?? throw new InvalidOperationException("Unknown model not found. Ensure SeedCatalogDataAsync() has been called.");
    }
}

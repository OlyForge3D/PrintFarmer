#pragma warning disable SA1402 // File may only contain a single type (or custom namespace warning)
#pragma warning disable CS0136 // Suppress variable shadowing error in this file

using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Services.Interfaces;
using Farm.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

public class DatabaseSeeder(AppDbContext context, IUnifiedLoggingService logger) : IDatabaseSeeder
{
    private readonly AppDbContext _context = context;
    private readonly IUnifiedLoggingService _logger = logger;

    public async Task SeedAllAsync()
    {
        await SeedAuthenticationDataAsync();
        await SeedCatalogDataAsync();    // This creates printer model/filament type relationships
        await SeedFilamentTypesAsync();  // Must come before SeedCatalogDataAsync
    }

    private async Task SeedCatalogDataAsync()
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
                    _ = _context.Manufacturers.Add(existing);
                    _ = await _context.SaveChangesAsync();
                }
                manufacturers[normalized] = existing;
            }

            // Models to ensure exist with comprehensive capability data
            // (Name, ManufacturerName, MaxX/MaxY/MaxZ, DefaultBackend, MotionType, NozzleDiam, HasBed, HasEnclosure, MultiMat, Extruders, AutoLevel, MinHot, MaxHot, MinBed, MaxBed, Materials, MaxSpeed)
            (string Name, string Mfg, double X, double Y, double Z, int? DefaultBackend, MotionType? MotionType,
             double? NozzleDiameter, bool HasBed, bool HasEnclosure, bool MultiMaterial, int Extruders, bool AutoLevel,
             int? MinHotend, int? MaxHotend, int? MinBed, int? MaxBed, string Materials, int? MaxSpeed)[] modelSeeds = new[]
            {
                ("Unknown Model", "Unknown", 200.0, 200.0, 200.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.Unknown, (double?)0.4, true, false, false, 1, false, (int?)0, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS", (int?)100),
                
                // FlashForge models
                ("AD5X", "FlashForge", 220.0, 220.0, 220.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                
                // Sovol models
                ("SV08", "Sovol", 350.0, 350.0, 350.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("SV08 Max", "Sovol", 500.0, 500.0, 500.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Zero", "Sovol", 150.0, 150.0, 150.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS", (int?)150),
                
                // Eryone models
                ("Thinker X400", "Eryone", 400.0, 400.0, 400.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                
                // Elegoo models  
                ("Centauri", "Elegoo", 256.0, 256.0, 256.0, (int?)PrinterBackend.SDCP, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Centauri Carbon", "Elegoo", 256.0, 256.0, 256.0, (int?)PrinterBackend.SDCP, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                
                // PrintersForAnts models
                ("SaladFork 120", "PrintersForAnts", 120.0, 120.0, 120.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("SaladFork 180", "PrintersForAnts", 180.0, 180.0, 180.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 120", "PrintersForAnts", 120.0, 120.0, 120.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 160", "PrintersForAnts", 160.0, 160.0, 165.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 180", "PrintersForAnts", 180.0, 180.0, 165.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                
                // Voron models
                ("Voron v0", "Voron", 120.0, 120.0, 120.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("v2.4 250", "Voron", 250.0, 250.0, 250.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("v2.4 300", "Voron", 300.0, 300.0, 300.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("v2.4 350", "Voron", 350.0, 350.0, 350.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Switchwire", "Voron", 250.0, 210.0, 240.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Trident 250", "Voron", 250.0, 250.0, 250.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Trident 300", "Voron", 300.0, 300.0, 250.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Trident 300 Cube", "Voron", 300.0, 300.0, 300.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Trident 350", "Voron", 350.0, 350.0, 250.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                
                // RatRig models
                ("vCore3.1 200", "RatRig", 200.0, 200.0, 200.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.1 300", "RatRig", 300.0, 300.0, 300.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.1 400", "RatRig", 400.0, 400.0, 400.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.1 500", "RatRig", 500.0, 500.0, 500.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.2 200", "RatRig", 200.0, 200.0, 200.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.2 300", "RatRig", 300.0, 300.0, 300.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.2 400", "RatRig", 400.0, 400.0, 400.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore3.2 500", "RatRig", 500.0, 500.0, 500.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("vCore4 300", "RatRig", 300.0, 300.0, 300.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 400", "RatRig", 400.0, 400.0, 400.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 500", "RatRig", 500.0, 500.0, 500.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 300 Hybrid", "RatRig", 300.0, 300.0, 300.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 400 Hybrid", "RatRig", 400.0, 400.0, 400.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 500 Hybrid", "RatRig", 500.0, 500.0, 500.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 300 IDEX", "RatRig", 300.0, 300.0, 300.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 400 IDEX", "RatRig", 400.0, 400.0, 400.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("vCore4 500 IDEX", "RatRig", 500.0, 500.0, 500.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                
                // Phrozen models
                ("Arco", "Phrozen", 300.0, 300.0, 300.0, (int?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                
                // Prusa models
                ("Original Prusa Mini+", "Prusa", 180.0, 180.0, 180.0, (int?)PrinterBackend.PrusaLink, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS,ASA,PC", (int?)180),
                ("Original Prusa i3 MK3S+", "Prusa", 250.0, 210.0, 210.0, (int?)PrinterBackend.PrusaLink, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                ("Original Prusa MK4S", "Prusa", 250.0, 210.0, 220.0, (int?)PrinterBackend.PrusaLink, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
                ("Original Prusa Core One", "Prusa", 250.0, 220.0, 270.0, (int?)PrinterBackend.PrusaLink, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)250),
                ("Original Prusa XL", "Prusa", 250.0, 220.0, 270.0, (int?)PrinterBackend.PrusaLink, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, true, 5, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
            };

            foreach ((string modelName, string mfg, double x, double y, double z, int? defaultBackend, MotionType? motionType,
                      double? nozzleDiameter, bool hasBed, bool hasEnclosure, bool multiMaterial, int extruders, bool autoLevel,
                      int? minHotend, int? maxHotend, int? minBed, int? maxBed, _, int? maxSpeed) in modelSeeds)
            {

                if (!manufacturers.TryGetValue(mfg, out Manufacturer? m))
                {
                    // Skip if manufacturer wasn't ensured above for any reason
                    continue;
                }

                bool exists = await _context.Models.AnyAsync(pm => pm.ManufacturerId == m.Id && pm.Name == modelName);
                if (!exists)
                {
                    _ = _context.Models.Add(new PrinterModel
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
            _ = await _context.SaveChangesAsync();

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
            // Get all filament types once (case-insensitive keys)
            Dictionary<string, FilamentType> filamentTypes = await _context.FilamentTypes
                .ToDictionaryAsync(ft => ft.Name, ft => ft, StringComparer.OrdinalIgnoreCase);

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
                        .Select(material => material.Trim());

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
                                _ = _context.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType
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

            _ = await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Filament type seeding error");
            // Don't throw - this is not critical for application startup
        }
    }

    private async Task SeedFilamentTypesAsync()
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
                _ = _context.FilamentTypes.Add(filamentType);
            }
        }

        _ = await _context.SaveChangesAsync();
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

    private async Task SeedAuthenticationDataAsync()
    {
        ArgumentNullException.ThrowIfNull(_context);

        try
        {
            // Try to query the Actions table to see if it exists
            _ = await _context.Actions.AnyAsync();
        }
        catch (Exception)
        {
            // If authentication tables don't exist yet, skip seeding
            // This can happen during initial database setup or testing
            return;
        }

        // Seed Actions first
        await SeedActionsAsync();

        // Seed Resources
        await SeedResourcesAsync();

        // Seed Roles
        await SeedRolesAsync();

        // Seed Role Permissions
        await SeedRolePermissionsAsync();

        _ = await _context.SaveChangesAsync();
    }

    private async Task SeedActionsAsync()
    {
        var actions = new[]
        {
            new { Name = "create", DisplayName = "Create", Description = "Create new resources" },
            new { Name = "read", DisplayName = "Read", Description = "View and read resources" },
            new { Name = "update", DisplayName = "Update", Description = "Modify existing resources" },
            new { Name = "delete", DisplayName = "Delete", Description = "Remove resources" },
            new { Name = "execute", DisplayName = "Execute", Description = "Execute operations on resources" },
            new { Name = "admin", DisplayName = "Administer", Description = "Full administrative control" }
        };

        foreach (var action in actions)
        {
            if (!await _context.Actions.AnyAsync(a => a.Name == action.Name))
            {
                _ = _context.Actions.Add(new Farm.Infrastructure.Domain.Action
                {
                    Id = Guid.NewGuid(),
                    Name = action.Name,
                    DisplayName = action.DisplayName,
                    Description = action.Description,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
    }

    private async Task SeedResourcesAsync()
    {
        var resources = new[]
        {
            new { Name = "printers", DisplayName = "Printers", ResourceType = "printer", Description = "3D printer management" },
            new { Name = "gcode_harvest", DisplayName = "G-code Harvest", ResourceType = "harvest", Description = "G-code file harvesting operations" },
            new { Name = "gcode_library", DisplayName = "G-code Library", ResourceType = "library", Description = "G-code file library management" },
            new { Name = "job_queue", DisplayName = "Print Job Queue", ResourceType = "queue", Description = "Print job queue management" },
            new { Name = "slicer_engines", DisplayName = "Slicer Engines", ResourceType = "slicer", Description = "Slicer integration and management" },
            new { Name = "users", DisplayName = "Users", ResourceType = "system", Description = "User account management" },
            new { Name = "roles", DisplayName = "Roles", ResourceType = "system", Description = "Role and permission management" },
            new { Name = "system_settings", DisplayName = "System Settings", ResourceType = "system", Description = "Application configuration and settings" },
            new { Name = "spoolman", DisplayName = "Spoolman Integration", ResourceType = "integration", Description = "Spoolman filament management integration" },
            new { Name = "network_discovery", DisplayName = "Network Discovery", ResourceType = "system", Description = "Printer network discovery and management" }
        };

        foreach (var resource in resources)
        {
            if (!await _context.Resources.AnyAsync(r => r.Name == resource.Name))
            {
                _ = _context.Resources.Add(new Resource
                {
                    Id = Guid.NewGuid(),
                    Name = resource.Name,
                    DisplayName = resource.DisplayName,
                    Description = resource.Description,
                    ResourceType = resource.ResourceType,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
    }

    private async Task SeedRolesAsync()
    {
        var roles = new[]
        {
            new { Name = "farm_admin", DisplayName = "Farm Administrator", Description = "Full access to all farm resources and user management", IsSystemRole = true },
            new { Name = "farm_user", DisplayName = "Farm User", Description = "Standard user access to printers and print operations", IsSystemRole = true }
        };

        foreach (var role in roles)
        {
            if (!await _context.Roles.AnyAsync(r => r.Name == role.Name))
            {
                _ = _context.Roles.Add(new Role
                {
                    Id = Guid.NewGuid(),
                    Name = role.Name,
                    DisplayName = role.DisplayName,
                    Description = role.Description,
                    IsSystemRole = role.IsSystemRole,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
    }

    private async Task SeedRolePermissionsAsync()
    {
        // Ensure all roles, resources, and actions are saved first
        _ = await _context.SaveChangesAsync();

        // Get the admin role - admins get all permissions
        Role? adminRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
        if (adminRole != null)
        {
            List<Resource> allResources = await _context.Resources.ToListAsync();
            Farm.Infrastructure.Domain.Action? adminAction = await _context.Actions.FirstOrDefaultAsync(a => a.Name == "admin");

            if (adminAction != null)
            {
                foreach (Resource resource in allResources)
                {
                    if (!await _context.RolePermissions.AnyAsync(rp =>
                        rp.RoleId == adminRole.Id && rp.ResourceId == resource.Id && rp.ActionId == adminAction.Id))
                    {
                        _ = _context.RolePermissions.Add(new RolePermission
                        {
                            Id = Guid.NewGuid(),
                            RoleId = adminRole.Id,
                            ResourceId = resource.Id,
                            ActionId = adminAction.Id,
                            Granted = true,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        // Get the user role - users get read access to most resources
        Role? userRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "farm_user");
        if (userRole != null)
        {
            (string, string)[] userPermissions = new[]
            {
                ("printers", "read"),
                ("printers", "execute"), // Can control printers
                ("gcode_library", "read"),
                ("gcode_library", "create"), // Can upload files
                ("job_queue", "read"),
                ("job_queue", "create"), // Can create print jobs
                ("spoolman", "read")
            };

            foreach ((string? resourceName, string? actionName) in userPermissions)
            {
                Resource? resource = await _context.Resources.FirstOrDefaultAsync(r => r.Name == resourceName);
                Farm.Infrastructure.Domain.Action? action = await _context.Actions.FirstOrDefaultAsync(a => a.Name == actionName);

                if (resource != null && action != null)
                {
                    if (!await _context.RolePermissions.AnyAsync(rp =>
                        rp.RoleId == userRole.Id && rp.ResourceId == resource.Id && rp.ActionId == action.Id))
                    {
                        _ = _context.RolePermissions.Add(new RolePermission
                        {
                            Id = Guid.NewGuid(),
                            RoleId = userRole.Id,
                            ResourceId = resource.Id,
                            ActionId = action.Id,
                            Granted = true,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }
        }
    }
}

using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

public class DatabaseSeeder : IDatabaseSeeder
{
    private readonly AppDbContext _context;

    public DatabaseSeeder(AppDbContext context)
    {
        _context = context;
    }

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
                "Prusa",
                "Elegoo",
                "Eryone",
                "FlashForge",
                "Sovol",
                "RatRig",
                "Voron",
                "PrintersForAnts"
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

            // Models to ensure exist (Name, ManufacturerName, MaxX/MaxY/MaxZ, DefaultBackend, PrinterType)
            (string Name, string Mfg, double X, double Y, double Z, int? DefaultBackend, PrinterType? Type)[] modelSeeds = new (string Name, string Mfg, double X, double Y, double Z, int? DefaultBackend, PrinterType? Type)[]
            {
                ("Unknown Model", "Unknown", 200, 200, 200, 0, PrinterType.Unknown), // Default for unidentified models
                ("AD5X", "FlashForge", 220, 220, 220, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("SV08", "Sovol", 350, 350, 350, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("SV08 Max", "Sovol", 500, 500, 500, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Zero", "Sovol", 150, 150, 150, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Thinker X400", "Eryone", 400, 400, 400, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Centauri", "Elegoo", 256, 256, 256, 2, PrinterType.CoreXY), // SDCP
                ("Centauri Carbon", "Elegoo", 256, 256, 256, 2, PrinterType.CoreXY), // SDCP
                ("SaladFork 120", "PrintersForAnts", 120, 120, 120, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("SaladFork 180", "PrintersForAnts", 180, 180, 180, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Micron 120", "PrintersForAnts", 120, 120, 120, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Micron 160", "PrintersForAnts", 160, 160, 165, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Micron 180", "PrintersForAnts", 180, 180, 165, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Voron v0", "Voron", 120, 120, 120, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("v2.4 250", "Voron", 250, 250, 250, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("v2.4 300", "Voron", 300, 300, 300, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("v2.4 350", "Voron", 350, 350, 350, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Switchwire", "Voron", 250, 210, 240, 0, PrinterType.Cartesian), // Moonraker (Klipper)
                ("Trident 250", "Voron", 250, 250, 250, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Trident 300", "Voron", 300, 300, 250, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Trident 300 Cube", "Voron", 300, 300, 300, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Trident 350", "Voron", 350, 350, 250, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore3.1 200", "RatRig", 200, 200, 200, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore3.1 300", "RatRig", 300, 300, 300, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore3.1 400", "RatRig", 400, 400, 400, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore3.1 500", "RatRig", 500, 500, 500, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore3.2 200", "RatRig", 200, 200, 200, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore3.2 300", "RatRig", 300, 300, 300, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore3.2 400", "RatRig", 400, 400, 400, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore3.2 500", "RatRig", 500, 500, 500, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore4 300", "RatRig", 300, 300, 300, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore4 400", "RatRig", 400, 400, 400, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore4 500", "RatRig", 500, 500, 500, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore4 300 Hybrid", "RatRig", 300, 300, 300, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore4 400 Hybrid", "RatRig", 400, 400, 400, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore4 500 Hybrid", "RatRig", 500, 500, 500, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore4 300 IDEX", "RatRig", 300, 300, 300, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore4 400 IDEX", "RatRig", 400, 400, 400, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("vCore4 500 IDEX", "RatRig", 500, 500, 500, 0, PrinterType.CoreXY), // Moonraker (Klipper)
                ("Original Prusa Mini+", "Prusa", 180, 180, 180, 1, PrinterType.Cartesian), // PrusaLink
                ("Original Prusa i3 MK3S+", "Prusa", 250, 210, 210, 1, PrinterType.Cartesian), // PrusaLink
                ("Original Prusa MK4S", "Prusa", 250, 210, 220, 1, PrinterType.Cartesian), // PrusaLink
                ("Original Prusa Core One", "Prusa", 250, 220, 270, 1, PrinterType.CoreXY), // PrusaLink
                ("Original Prusa XL", "Prusa", 250, 220, 270, 1, PrinterType.CoreXY), // PrusaLink
            };

            foreach ((string? name, string? mfg, double x, double y, double z, int? defaultBackend, PrinterType? type) in modelSeeds)
            {
                if (!manufacturers.TryGetValue(mfg, out Manufacturer? m))
                {
                    // Skip if manufacturer wasn't ensured above for any reason
                    continue;
                }

                bool exists = await _context.Models.AnyAsync(pm => pm.ManufacturerId == m.Id && pm.Name == name);
                if (!exists)
                {
                    _context.Models.Add(new PrinterModel
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        ManufacturerId = m.Id,
                        MaxX = x,
                        MaxY = y,
                        MaxZ = z,
                        DefaultBackend = defaultBackend
                    });
                }
            }
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Log the exception but don't throw to prevent application startup failure
            Console.WriteLine($"Catalog seeding error: {ex.Message}");
            throw; // Re-throw if you want startup to fail on seeding errors
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
            Console.WriteLine($"Spoolman config seeding error: {ex.Message}");
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
        await SeedCatalogDataAsync();
        await SeedFilamentTypesAsync();
    }

    /// <summary>
    /// Gets the "Unknown" manufacturer, which should always exist after seeding
    /// </summary>
    public async Task<Manufacturer> GetUnknownManufacturerAsync()
    {
        Manufacturer? unknown = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Unknown");
        if (unknown == null)
        {
            throw new InvalidOperationException("Unknown manufacturer not found. Ensure SeedCatalogDataAsync() has been called.");
        }
        return unknown;
    }

    /// <summary>
    /// Gets the "Unknown Model" from the "Unknown" manufacturer, which should always exist after seeding
    /// </summary>
    public async Task<PrinterModel> GetUnknownModelAsync()
    {
        Manufacturer unknownMfg = await GetUnknownManufacturerAsync();
        PrinterModel? unknownModel = await _context.Models.FirstOrDefaultAsync(m =>
            m.ManufacturerId == unknownMfg.Id && m.Name == "Unknown Model");
        if (unknownModel == null)
        {
            throw new InvalidOperationException("Unknown Model not found. Ensure SeedCatalogDataAsync() has been called.");
        }
        return unknownModel;
    }
}

using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Farm.Web.Api.Services.Interfaces;

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
            // Desired manufacturers to ensure exist
            var manufacturerNames = new[]
            {
                "Prusa",
                "Elegoo",
                "Eryone",
                "FlashForge",
                "Sovol",
                "RatRig",
                "VoronDesign",
                "PrintersForAnts"
            };

            var manufacturers = new Dictionary<string, Manufacturer>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in manufacturerNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var existing = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == name);
                if (existing == null)
                {
                    existing = new Manufacturer { Id = Guid.NewGuid(), Name = name };
                    _context.Manufacturers.Add(existing);
                    await _context.SaveChangesAsync();
                }
                manufacturers[name] = existing;
            }

            // Models to ensure exist (Name, ManufacturerName, MaxX/MaxY/MaxZ, DefaultBackend)
            var modelSeeds = new (string Name, string Mfg, double X, double Y, double Z, int? DefaultBackend)[]
            {
                ("AD5X", "FlashForge", 220, 220, 220, 0), // Moonraker (Klipper)
                ("SV08", "Sovol", 350, 350, 350, 0), // Moonraker (Klipper)
                ("SV08 Max", "Sovol", 500, 500, 500, 0), // Moonraker (Klipper)
                ("Zero", "Sovol", 150, 150, 150, 0), // Moonraker (Klipper)
                ("Thinker X400", "Eryone", 400, 400, 400, 0), // Moonraker (Klipper)
                ("Centauri Carbon", "Elegoo", 256, 256, 256, 2), // SDCP
                ("Micron 120", "PrintersForAnts", 120, 120, 120, 0), // Moonraker (Klipper)
                ("Micron 180", "PrintersForAnts", 180, 180, 165, 0), // Moonraker (Klipper)
                ("Voron Trident 250", "VoronDesign", 250, 250, 250, 0), // Moonraker (Klipper)
                ("Voron Trident 300", "VoronDesign", 300, 300, 250, 0), // Moonraker (Klipper)
                ("Voron Trident 300 Cube", "VoronDesign", 300, 300, 300, 0), // Moonraker (Klipper)
                ("Voron Trident 350", "VoronDesign", 350, 350, 250, 0), // Moonraker (Klipper)
                ("Voron v0", "VoronDesign", 120, 120, 120, 0), // Moonraker (Klipper)
                ("Voron v2.4 300", "VoronDesign", 300, 300, 300, 0), // Moonraker (Klipper)
                ("Voron v2.4 350", "VoronDesign", 350, 350, 350, 0), // Moonraker (Klipper)
                ("vCore4 400", "RatRig", 400, 400, 400, 0), // Moonraker (Klipper)
                ("vCore4 500", "RatRig", 500, 500, 500, 0), // Moonraker (Klipper)
                ("Original Prusa Mini+", "Prusa", 180, 180, 180, 1), // PrusaLink
                ("Original Prusa MK4S", "Prusa", 250, 210, 220, 1), // PrusaLink
                ("Original Prusa Core One", "Prusa", 250, 220, 270, 1), // PrusaLink
                ("Original Prusa i3 MK3S+", "Prusa", 250, 210, 210, 1) // PrusaLink
            };

            foreach (var (name, mfg, x, y, z, defaultBackend) in modelSeeds)
            {
                if (!manufacturers.TryGetValue(mfg, out var m))
                {
                    // Skip if manufacturer wasn't ensured above for any reason
                    continue;
                }

                var exists = await _context.Models.AnyAsync(pm => pm.ManufacturerId == m.Id && pm.Name == name);
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
            var existingConfig = await _context.SpoolmanConfigs.FirstOrDefaultAsync();
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

    public async Task SeedAllAsync()
    {
        await SeedSpoolmanConfigAsync();
        await SeedCatalogDataAsync();
    }
}

using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

/// <summary>
/// Handles database initialization with retry logic for resilient startup
/// </summary>

public class DatabaseInitializer(AppDbContext context, IUnifiedLoggingService logger) : IDatabaseInitializer
{
    private readonly AppDbContext _context = context;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Initialize database with retry logic for container startup scenarios
    /// </summary>
    public virtual async Task InitializeAsync(string dbProvider, int maxRetries = 10, int delaySeconds = 5)
    {
        _logger.LogInformation($"[DB] Starting database initialization for provider: {dbProvider}");

        int retryCount = 0;

        while (retryCount < maxRetries)
        {
            try
            {
                // Test database connectivity first
                _ = await _context.Database.CanConnectAsync();
                _logger.LogInformation("[DB] Database connection established successfully");

                // For MVP development, use EnsureCreated instead of migrations.
                // This approach automatically handles schema changes during development.
                try
                {
                    _ = await _context.Database.EnsureCreatedAsync();
                    _logger.LogInformation("[DB] Database schema ensured successfully (EnsureCreated)");

                    // Lightweight self-healing for SQLite when schema was created before introducing shadow columns
                    // Ensure case-insensitive shadow columns (NameLowered) & indexes exist for Manufacturers / PrinterModels.
                    if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            await EnsureCaseInsensitiveColumnsAsync();
                        }
                        catch (Exception colEx)
                        {
                            _logger.LogWarning(colEx, $"[DB] Non-fatal: automatic shadow column/index verification failed: {colEx.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"[DB] EnsureCreated failed: {ex.Message}. Attempting manual schema initialization for SQLite.");
                    // Fallback: very early containers (or volume permission issues) sometimes cause EnsureCreated to throw
                    // For SQLite only, attempt a minimal manual schema verification/creation of the Users table presence heuristic.
                    try
                    {
                        if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                        {
                            // Issue a pragma to force open / create file, then check a sentinel table.
                            _ = await _context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                            // If no tables exist, this query will fail; wrap & create a tiny bootstrap table then re-run seed later.
                            // We won't create full schema manually (that belongs to EF model); just let a second EnsureCreated attempt run.
                            _ = await _context.Database.EnsureCreatedAsync();
                            _logger.LogInformation("[DB] Fallback EnsureCreated second attempt succeeded");

                            if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    await EnsureCaseInsensitiveColumnsAsync();
                                }
                                catch (Exception colEx)
                                {
                                    _logger.LogWarning(colEx, $"[DB] Non-fatal (fallback path): automatic shadow column/index verification failed: {colEx.Message}");
                                }
                            }
                        }
                        else
                        {
                            throw; // Non-SQLite providers should just retry via outer loop
                        }
                    }
                    catch (Exception inner)
                    {
                        _logger.LogError(inner, $"[DB] Manual fallback schema initialization failed. Will retry (attempt {retryCount + 1})");
                        throw; // Bubble to retry loop
                    }
                }

                // Seed all data (authentication, catalog, filament types)
                // Some providers (or test SQLite setups) may require a brief moment after EnsureCreated
                // before all connections observe the new schema. Retry a few times for core table existence
                // before attempting the full seed to avoid "no such table" errors.
                const int seedMaxAttempts = 3;
                int seedAttempt = 0;
                while (true)
                {
                    try
                    {
                        await SeedAllAsync();
                        break;
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException sqlEx) when (sqlEx.Message?.Contains("no such table", StringComparison.OrdinalIgnoreCase) == true && seedAttempt < seedMaxAttempts)
                    {
                        seedAttempt++;
                        _logger.LogWarning(sqlEx, $"[DB] Seed attempt {seedAttempt}/{seedMaxAttempts} failed due to missing table (SQLite); retrying in 2s...");
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                    catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P01" && seedAttempt < seedMaxAttempts)
                    {
                        // PostgreSQL error 42P01 = relation does not exist (table/view not found)
                        seedAttempt++;
                        _logger.LogWarning(pgEx, $"[DB] Seed attempt {seedAttempt}/{seedMaxAttempts} failed due to missing relation (PostgreSQL); retrying in 2s...");
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                }
                _logger.LogInformation("[DB] Database initialization completed successfully");
                return; // Success - exit retry loop
            }
            catch (Exception ex)
            {
                retryCount++;
                if (retryCount < maxRetries)
                {
                    _logger.LogWarning(ex,
                        $"[DB] Database initialization attempt {retryCount}/{maxRetries} failed: {ex.Message}. Retrying in {delaySeconds} seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
                else
                {
                    _logger.LogError(ex,
                        $"[DB] Database initialization failed after {maxRetries} attempts. Last error: {ex.Message}");
                    throw new InvalidOperationException(
                        $"Failed to initialize database after {maxRetries} attempts. " +
                        $"This usually indicates the database server is not ready or connection settings are incorrect. " +
                        $"Last error: {ex.Message}", ex);
                }
            }
        }
    }

    // === BEGIN: Seeding logic merged from DatabaseSeeder ===
    public virtual async Task SeedAllAsync()
    {
        await SeedFilamentTypesAsync();  // Must come before SeedCatalogDataAsync
        await SeedCatalogDataAsync();    // This creates printer model/filament type relationships
        await SeedAuthenticationDataAsync();
    }

    private async Task SeedCatalogDataAsync()
    {
        try
        {
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
                string normalized = Farm.Web.Api.Infrastructure.Normalization.CatalogNameNormalizer.NormalizeManufacturer(name);
                Manufacturer? existing = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == normalized);
                if (existing == null)
                {
                    existing = new Manufacturer { Id = Guid.NewGuid(), Name = normalized };
                    _ = _context.Manufacturers.Add(existing);
                    _ = await _context.SaveChangesAsync();
                }
                manufacturers[normalized] = existing;
            }


            (string Name, string Mfg, double X, double Y, double Z, int? DefaultBackend, MotionType? MotionType,
             double? NozzleDiameter, bool HasBed, bool HasEnclosure, bool MultiMaterial, int Extruders, bool AutoLevel,
             int? MinHotend, int? MaxHotend, int? MinBed, int? MaxBed, string Materials, int? MaxSpeed)[] modelSeeds = new[]
            {
                ("Unknown Model", "Unknown", 200.0, 200.0, 200.0, (int?)0, (MotionType?)MotionType.Unknown, (double?)0.4, true, false, false, 1, false, (int?)0, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS", (int?)100),
                ("AD5X", "FlashForge", 220.0, 220.0, 220.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                ("SV08", "Sovol", 350.0, 350.0, 350.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("SV08 Max", "Sovol", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Zero", "Sovol", 150.0, 150.0, 150.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS", (int?)150),
                ("Thinker X400", "Eryone", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                ("Centauri", "Elegoo", 256.0, 256.0, 256.0, (int?)2, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Centauri Carbon", "Elegoo", 256.0, 256.0, 256.0, (int?)2, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("SaladFork 120", "PrintersForAnts", 120.0, 120.0, 120.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("SaladFork 180", "PrintersForAnts", 180.0, 180.0, 180.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 120", "PrintersForAnts", 120.0, 120.0, 120.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 160", "PrintersForAnts", 160.0, 160.0, 165.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 180", "PrintersForAnts", 180.0, 180.0, 165.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Voron v0", "Voron", 120.0, 120.0, 120.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("v2.4 250", "Voron", 250.0, 250.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("v2.4 300", "Voron", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("v2.4 350", "Voron", 350.0, 350.0, 350.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Switchwire", "Voron", 250.0, 210.0, 240.0, (int?)0, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Trident 250", "Voron", 250.0, 250.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Trident 300", "Voron", 300.0, 300.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Trident 300 Cube", "Voron", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Trident 350", "Voron", 350.0, 350.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
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
                ("Arco", "Phrozen", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                ("Original Prusa Mini+", "Prusa", 180.0, 180.0, 180.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS,ASA,PC", (int?)180),
                ("Original Prusa i3 MK3S+", "Prusa", 250.0, 210.0, 210.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                ("Original Prusa MK4S", "Prusa", 250.0, 210.0, 220.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
                ("Original Prusa Core One", "Prusa", 250.0, 220.0, 270.0, (int?)1, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)250),
                ("Original Prusa XL", "Prusa", 250.0, 220.0, 270.0, (int?)1, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, true, 5, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
            };

            foreach ((string modelName, string mfg, double x, double y, double z, int? defaultBackend, MotionType? motionType,
                      double? nozzleDiameter, bool hasBed, bool hasEnclosure, bool multiMaterial, int extruders, bool autoLevel,
                      int? minHotend, int? maxHotend, int? minBed, int? maxBed, _, int? maxSpeed) in modelSeeds)
            {
                if (!manufacturers.TryGetValue(mfg, out Manufacturer? m))
                {
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
            await SeedModelFilamentTypesAsync(modelSeeds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Catalog seeding error");
            throw;
        }
    }

    private async Task SeedModelFilamentTypesAsync((string Name, string Mfg, double X, double Y, double Z,
             int? DefaultBackend, MotionType? MotionType, double? NozzleDiameter, bool HasBed, bool HasEnclosure,
             bool MultiMaterial, int Extruders, bool AutoLevel, int? MinHotend, int? MaxHotend,
             int? MinBed, int? MaxBed, string Materials, int? MaxSpeed)[] modelSeeds)
    {
        try
        {
            // Build a case-insensitive dictionary for filament lookups to avoid culture-sensitive ToUpperInvariant allocations
            Dictionary<string, FilamentType> filamentTypes = (await _context.FilamentTypes.ToListAsync())
                .ToDictionary(ft => ft.Name, ft => ft, StringComparer.OrdinalIgnoreCase);
            foreach ((
                string modelName,
                string manufacturerName,
                _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, string supportedMaterials, _) in modelSeeds)
            {
                PrinterModel? model = await _context.Models
                    .FirstOrDefaultAsync(m => m.Name == modelName &&
                                           m.Manufacturer != null &&
                                           m.Manufacturer.Name == manufacturerName);
                if (model != null && !string.IsNullOrEmpty(supportedMaterials))
                {
                    IEnumerable<string> materialNames = supportedMaterials.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(material => material.Trim());
                    foreach (string? materialName in materialNames)
                    {
                        if (filamentTypes.TryGetValue(materialName, out FilamentType? filamentType))
                        {
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
            try
            {
                _ = await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _logger.LogWarning(ex, "Ignored unique constraint violation while seeding model-filament relationships; another process probably inserted the same records.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Filament type seeding error");
        }
    }

    private async Task SeedFilamentTypesAsync()
    {
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
        try
        {
            _ = await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _logger.LogWarning(ex, "Ignored unique constraint violation while seeding filament types; another process probably inserted the same records.");
        }
    }

    public async Task<Manufacturer> GetUnknownManufacturerAsync()
    {
        Manufacturer? unknown = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Unknown");
        return unknown ?? throw new InvalidOperationException("Unknown manufacturer not found. Ensure SeedCatalogDataAsync() has been called.");
    }

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
            _ = await _context.Actions.AnyAsync();
        }
        catch (Exception)
        {
            return;
        }
        await SeedActionsAsync();
        await SeedResourcesAsync();
        await SeedRolesAsync();
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

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        if (ex == null)
        {
            return false;
        }
        // Walk inner exceptions to find DB-specific messages
        Exception? e = ex;
        while (e != null)
        {
            string msg = e.Message ?? string.Empty;
            // SQLite
            if (msg.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) || msg.Contains("constraint failed", StringComparison.OrdinalIgnoreCase) || msg.Contains("unique index", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // Postgres
            if (msg.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase) || msg.Contains("unique_violation", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // SQL Server
            if (msg.Contains("violation of unique", StringComparison.OrdinalIgnoreCase) || msg.Contains("unique constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            e = e.InnerException;
        }
        return false;
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
        _ = await _context.SaveChangesAsync();
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
        Role? userRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "farm_user");
        if (userRole != null)
        {
            (string, string)[] userPermissions = new[]
            {
                ("printers", "read"),
                ("printers", "execute"),
                ("gcode_library", "read"),
                ("gcode_library", "create"),
                ("job_queue", "read"),
                ("job_queue", "create"),
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
    // === END: Seeding logic merged from DatabaseSeeder ===

    /// <summary>
    /// Validate database connection without initializing
    /// </summary>
    public async Task<bool> ValidateConnectionAsync()
    {
        try
        {
            return await _context.Database.CanConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"[DB] Database connection validation failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// For SQLite + EnsureCreated dev workflow: if the database file predates new shadow columns
    /// (NameLowered) we add them and their unique indexes safely. This avoids forcing devs to delete
    /// the whole DB when only these columns were added for case-insensitive uniqueness.
    /// </summary>
    private async Task EnsureCaseInsensitiveColumnsAsync()
    {
        // Only run for SQLite provider
        if (!_context.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            return;
        }

        DbConnection conn = _context.Database.GetDbConnection();
        await conn.OpenAsync();
        using DbTransaction tx = await conn.BeginTransactionAsync();
        try
        {
            async Task<bool> ColumnExistsAsync(string table, string column)
            {
                using DbCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE lower(name)=lower(@col) LIMIT 1";
                DbParameter p = cmd.CreateParameter();
                p.ParameterName = "@col";
                p.Value = column;
                _ = cmd.Parameters.Add(p);
                object? result = await cmd.ExecuteScalarAsync();
                return result != null;
            }

            async Task<bool> TableExistsAsync(string table)
            {
                using DbCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name = @tbl LIMIT 1";
                DbParameter p = cmd.CreateParameter();
                p.ParameterName = "@tbl";
                p.Value = table;
                _ = cmd.Parameters.Add(p);
                object? r = await cmd.ExecuteScalarAsync();
                return r != null;
            }

            async Task EnsureColumnAsync(string table, string column)
            {
                if (!await ColumnExistsAsync(table, column))
                {
                    using DbCommand alter = conn.CreateCommand();
                    alter.Transaction = tx;
                    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT NOT NULL DEFAULT ''";
                    _ = await alter.ExecuteNonQueryAsync();
                    _logger.LogInformation($"[DB] Added missing column {table}.{column}");
                }
            }

            async Task<bool> HasDuplicatesAsync(string table)
            {
                using DbCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = table == "Manufacturers"
                    ? "SELECT 1 FROM (SELECT lower(Name) AS L, COUNT(*) c FROM Manufacturers GROUP BY lower(Name) HAVING c>1) LIMIT 1"
                    : "SELECT 1 FROM (SELECT ManufacturerId, lower(Name) AS L, COUNT(*) c FROM PrinterModels GROUP BY ManufacturerId, lower(Name) HAVING c>1) LIMIT 1";
                object? r = await cmd.ExecuteScalarAsync();
                return r != null;
            }

            async Task BackfillAsync(string table)
            {
                using DbCommand upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = $"UPDATE {table} SET NameLowered = lower(Name) WHERE NameLowered = '' OR NameLowered IS NULL";
                int rows = await upd.ExecuteNonQueryAsync();
                if (rows >= 0)
                {
                    _logger.LogDebug($"[DB] Backfilled {rows} rows for {table}.NameLowered");
                }
            }

            async Task EnsureIndexAsync(string sql, string description)
            {
                using DbCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                try
                {
                    _ = await cmd.ExecuteNonQueryAsync();
                    _logger.LogInformation($"[DB] Ensured index: {description}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"[DB] Failed to ensure index {description}: {ex.Message}");
                }
            }

            // Manufacturers
            if (await TableExistsAsync("Manufacturers"))
            {
                await EnsureColumnAsync("Manufacturers", "NameLowered");
                await BackfillAsync("Manufacturers");
                if (await HasDuplicatesAsync("Manufacturers"))
                {
                    _logger.LogWarning("[DB] Duplicate manufacturer names (case-insensitive) detected; skipping unique index creation on Manufacturers.NameLowered. Resolve duplicates and restart to enforce uniqueness.");
                }
                else
                {
                    await EnsureIndexAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_Manufacturers_NameLowered ON Manufacturers (NameLowered)", "IX_Manufacturers_NameLowered");
                }
            }
            else
            {
                _logger.LogInformation("[DB] Skipping Manufacturers shadow column/index creation because Manufacturers table does not exist yet.");
            }

            // PrinterModels
            if (await TableExistsAsync("PrinterModels"))
            {
                await EnsureColumnAsync("PrinterModels", "NameLowered");
                await BackfillAsync("PrinterModels");
                if (await HasDuplicatesAsync("PrinterModels"))
                {
                    _logger.LogWarning("[DB] Duplicate printer model names (case-insensitive within manufacturer) detected; skipping unique composite index creation. Resolve duplicates and restart to enforce uniqueness.");
                }
                else
                {
                    await EnsureIndexAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_PrinterModels_ManufacturerId_NameLowered ON PrinterModels (ManufacturerId, NameLowered)", "IX_PrinterModels_ManufacturerId_NameLowered");
                }
            }
            else
            {
                _logger.LogInformation("[DB] Skipping PrinterModels shadow column/index creation because PrinterModels table does not exist yet.");
            }

            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("Failed to ensure shadow columns for case-insensitive uniqueness", ex);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}

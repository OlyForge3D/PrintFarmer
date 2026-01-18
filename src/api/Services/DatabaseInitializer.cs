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
        await SeedComponentModelsAsync(); // Seed hotend, extruder, toolhead, nozzle models
        await SeedAuthenticationDataAsync();
        await SeedRootFoldersAsync();    // Seed root "/" folders for gcode and models to prevent race conditions
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
                "Flashforge",  // Note: OrcaSlicer manifest uses "Flashforge" not "FlashForge"
                "Phrozen",
                "PrintersForAnts",  // Community derivative of Voron
                "Prusa",
                "Qidi",  // Note: OrcaSlicer manifest uses "Qidi" not "QIDI"
                "Sovol",
                "Ratrig",  // Note: OrcaSlicer manifest uses "Ratrig" not "RatRig", but model names use "RatRig V-Core"
                "Voron",
                // Bambu Lab (popular ecosystem for hotend adaptations)
                "Bambu Lab",
                // Component manufacturers (hotends, extruders, nozzles, toolheads)
                "Phaetus",           // Dragon, Rapido, etc.
                "Slice Engineering", // Mosquito, Copperhead, Mako
                "E3D",               // V6, Revo, etc.
                "Bondtech",          // BMG, LGX, LGX Lite
                "TriangleLabs",      // CHC clones, nozzles
                "West3D",            // Undertaker nozzles
                "BIQU",              // Panda Revo, H2
                "LDO",               // Motors, kits
                "Orbiter",           // Orbiter extruders
                "Microswiss",        // All-metal hotends
                "DropEffect",        // NextG hotends
                "Mellow",            // NF-Zone, CNC parts
                "Fysetc",            // Budget boards and parts
                "Community"          // For OpenSource community contributors
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
                // Flashforge (note: manifest uses "Flashforge" not "FlashForge")
                ("Flashforge AD5X", "Flashforge", 220.0, 220.0, 220.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                // Sovol (manifest names: "Sovol SV08", "Sovol SV08 MAX", "Sovol Zero")
                ("Sovol SV08", "Sovol", 350.0, 350.0, 350.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Sovol SV08 MAX", "Sovol", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Sovol Zero", "Sovol", 150.0, 150.0, 150.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS", (int?)150),
                // Eryone (manifest: "Eryone" -> "Thinker X400")
                ("Thinker X400", "Eryone", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                // Elegoo (manifest: Neptune 4 models and Centauri models)
                ("Elegoo Neptune 4", "Elegoo", 256.0, 256.0, 256.0, (int?)2, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Elegoo Neptune 4 Max", "Elegoo", 256.0, 256.0, 256.0, (int?)2, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Elegoo Centauri", "Elegoo", 256.0, 256.0, 256.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Elegoo Centauri Carbon", "Elegoo", 256.0, 256.0, 256.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                // PrintersForAnts (community derivative of Voron - smaller build volumes)
                ("SaladFork 120", "PrintersForAnts", 120.0, 120.0, 120.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("SaladFork 180", "PrintersForAnts", 180.0, 180.0, 165.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 120", "PrintersForAnts", 120.0, 120.0, 120.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 180", "PrintersForAnts", 180.0, 180.0, 165.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                // Voron (manifest: "Voron 0.1", "Voron 2.4 250", etc. - NOT "v2.4" or "Voron v0")
                ("Voron 0.1", "Voron", 120.0, 120.0, 120.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron 2.4 250", "Voron", 250.0, 250.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron 2.4 300", "Voron", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron 2.4 350", "Voron", 350.0, 350.0, 350.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron Switchwire 250", "Voron", 250.0, 210.0, 240.0, (int?)0, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Voron Trident 250", "Voron", 250.0, 250.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron Trident 300", "Voron", 300.0, 300.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron Trident 350", "Voron", 350.0, 350.0, 250.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                // Ratrig (note: manifest uses "Ratrig" not "RatRig", models use "RatRig V-Core 3 200" etc.)
                ("RatRig V-Core 3 200", "Ratrig", 200.0, 200.0, 200.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("RatRig V-Core 3 300", "Ratrig", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("RatRig V-Core 3 400", "Ratrig", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("RatRig V-Core 3 500", "Ratrig", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("RatRig V-Core 4 300", "Ratrig", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 400", "Ratrig", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 500", "Ratrig", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 HYBRID 300", "Ratrig", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 HYBRID 400", "Ratrig", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 HYBRID 500", "Ratrig", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 IDEX 300", "Ratrig", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 IDEX 400", "Ratrig", 400.0, 400.0, 400.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 IDEX 500", "Ratrig", 500.0, 500.0, 500.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                // Phrozen (manifest: "Phrozen" -> "Phrozen Arco")
                ("Phrozen Arco", "Phrozen", 300.0, 300.0, 300.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                // Qidi (manifest: "Qidi" -> "Qidi Q1 Pro", "Qidi X-Plus 4", etc.)
                ("QIDI X-Plus 4", "Qidi", 305.0, 305.0, 280.0, (int?)0, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)370, (int?)0, (int?)110, "PLA,PETG,ABS,ASA,PC,TPU,Nylon", (int?)600),
                // Prusa (manifest: "Prusa MINI", "Prusa MK3S", "Prusa MK4S", "Prusa CORE One", "Prusa XL" - NO "Original Prusa")
                ("Prusa MINI", "Prusa", 180.0, 180.0, 180.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS,ASA,PC", (int?)180),
                ("Prusa MK3S", "Prusa", 250.0, 210.0, 210.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                ("Prusa MK3.5", "Prusa", 250.0, 210.0, 210.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
                ("Prusa MK4", "Prusa", 250.0, 210.0, 220.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
                ("Prusa MK4S", "Prusa", 250.0, 210.0, 220.0, (int?)1, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
                ("Prusa CORE One", "Prusa", 250.0, 220.0, 270.0, (int?)1, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)250),
                ("Prusa CORE One L", "Prusa", 300.0, 300.0, 300.0, (int?)1, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)250),
                ("Prusa XL", "Prusa", 250.0, 220.0, 270.0, (int?)1, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, true, 5, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
            };

            foreach ((string modelName, string mfg, double x, double y, double z, int? defaultBackend, MotionType? motionType,
                      double? nozzleDiameter, bool hasBed, bool hasEnclosure, bool multiMaterial, int extruders, bool autoLevel,
                      int? minHotend, int? maxHotend, int? minBed, int? maxBed, _, int? maxSpeed) in modelSeeds)
            {
                if (!manufacturers.TryGetValue(mfg, out Manufacturer? m))
                {
                    continue;
                }

                bool exists = await _context.PrinterModels.AnyAsync(pm => pm.ManufacturerId == m.Id && pm.Name == modelName);
                if (!exists)
                {
                    _ = _context.PrinterModels.Add(new PrinterModel
                    {
                        Id = Guid.NewGuid(),
                        Name = modelName,
                        ManufacturerId = m.Id,
                        MaxX = x,
                        MaxY = y,
                        MaxZ = z,
                        DefaultBackend = defaultBackend,
                        MotionType = (int?)motionType,
                        HasHeatedBed = hasBed,
                        HasEnclosure = hasEnclosure,
                        MultiMaterial = multiMaterial,
                        NumberOfExtruders = extruders,
                        SupportsAutoLeveling = autoLevel,
                        MaxBedTemp = maxBed,
                        MaxPrintSpeed = maxSpeed
                    });
                }
            }

            _ = await _context.SaveChangesAsync();
            await SeedPrinterModelAliasesAsync();
            await SeedModelFilamentTypesAsync(modelSeeds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Catalog seeding error");
            throw;
        }
    }

    private async Task SeedPrinterModelAliasesAsync()
    {
        try
        {
            // Map OrcaSlicer model names to our canonical model names
            // These are the names used by OrcaSlicer when exporting gcode
            var orcaSlicerNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Prusa models
                { "Prusa MINI", "Prusa MINI" },
                { "Prusa MK3S", "Prusa MK3S" },
                { "Prusa MK3.5", "Prusa MK3.5" },
                { "Prusa MK4", "Prusa MK4" },
                { "Prusa MK4S", "Prusa MK4S" },
                { "Prusa CORE One", "Prusa CORE One" },
                { "Prusa XL", "Prusa XL" },
                
                // Voron models
                { "Voron 0.1", "Voron 0.1" },
                { "Voron 2.4 250", "Voron 2.4 250" },
                { "Voron 2.4 300", "Voron 2.4 300" },
                { "Voron 2.4 350", "Voron 2.4 350" },
                { "Voron Switchwire 250", "Voron Switchwire 250" },
                { "Voron Trident 250", "Voron Trident 250" },
                { "Voron Trident 300", "Voron Trident 300" },
                { "Voron Trident 350", "Voron Trident 350" },
                
                // RatRig models
                { "RatRig V-Core 3 200", "RatRig V-Core 3 200" },
                { "RatRig V-Core 3 300", "RatRig V-Core 3 300" },
                { "RatRig V-Core 3 400", "RatRig V-Core 3 400" },
                { "RatRig V-Core 3 500", "RatRig V-Core 3 500" },
                { "RatRig V-Core 4 300", "RatRig V-Core 4 300" },
                { "RatRig V-Core 4 400", "RatRig V-Core 4 400" },
                { "RatRig V-Core 4 500", "RatRig V-Core 4 500" },
                { "RatRig V-Core 4 HYBRID 300", "RatRig V-Core 4 HYBRID 300" },
                { "RatRig V-Core 4 HYBRID 400", "RatRig V-Core 4 HYBRID 400" },
                { "RatRig V-Core 4 HYBRID 500", "RatRig V-Core 4 HYBRID 500" },
                { "RatRig V-Core 4 IDEX 300", "RatRig V-Core 4 IDEX 300" },
                { "RatRig V-Core 4 IDEX 400", "RatRig V-Core 4 IDEX 400" },
                { "RatRig V-Core 4 IDEX 500", "RatRig V-Core 4 IDEX 500" },
                
                // Sovol models
                { "Sovol SV08", "Sovol SV08" },
                { "Sovol SV08 MAX", "Sovol SV08 MAX" },
                { "Sovol Zero", "Sovol Zero" },
                
                // Other manufacturers
                { "Flashforge AD5X", "Flashforge AD5X" },
                { "Phrozen Arco", "Phrozen Arco" },
                { "Thinker X400", "Thinker X400" },
                { "Elegoo Neptune 4", "Elegoo Neptune 4" },
                { "Elegoo Neptune 4 Max", "Elegoo Neptune 4 Max" },
                { "Elegoo Centauri", "Elegoo Centauri" },
                { "Elegoo Centauri Carbon", "Elegoo Centauri Carbon" },
                { "SaladFork 120", "SaladFork 120" },
                { "SaladFork 180", "SaladFork 180" },
                { "Micron 120", "Micron 120" },
                { "Micron 180", "Micron 180" },
            };

            // Map PrusaSlicer model names to our canonical model names
            // These are the names used by PrusaSlicer when exporting gcode
            var prusaSlicerNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Prusa models - PrusaSlicer uses abbreviated names
                { "MINIIS", "Prusa MINI" },
                { "MK3S", "Prusa MK3S" },
                { "MK3.5", "Prusa MK3.5" },
                { "MK4IS", "Prusa MK4" },
                { "MK4S", "Prusa MK4S" },
                { "COREONE", "Prusa CORE" },
                { "COREONEL", "Prusa CORE One L" },
                { "XLIS", "Prusa XL" },
                
                // Voron models - PrusaSlicer abbreviations
                { "Voron_v0_120", "Voron 0.1" },
                
                // RatRig models
                { "VC3_200", "RatRig V-Core 3 200" },
                { "VC3_300", "RatRig V-Core 3 300" },
                { "VC3_400", "RatRig V-Core 3 400" },
                { "VC3_500 COREXY", "RatRig V-Core 3 500" },
                { "VC4_300 COREXY", "RatRig V-Core 4 300" },
                { "VC4_400 COREXY", "RatRig V-Core 4 400" },
                { "VC4_500 COREXY", "RatRig V-Core 4 500" },
                { "VC4_300 HYBRID", "RatRig V-Core 4 HYBRID 300" },
                { "VC4_400 HYBRID", "RatRig V-Core 4 HYBRID 400" },
                { "VC4_500 HYBRID", "RatRig V-Core 4 HYBRID 500" },
                { "VC4_300 IDEX", "RatRig V-Core 4 IDEX 300" },
                { "VC4_400 IDEX", "RatRig V-Core 4 IDEX 400" },
                { "VC4_500 IDEX", "RatRig V-Core 4 IDEX 500" },
            };

            // Seed OrcaSlicer aliases
            foreach (var (slicerName, canonicalName) in orcaSlicerNames)
            {
                // Find the canonical PrinterModel
                var model = await _context.PrinterModels
                    .FirstOrDefaultAsync(pm => pm.Name == canonicalName);

                if (model == null)
                {
                    _logger.LogWarning($"[DB] Skipping OrcaSlicer alias '{slicerName}' -> '{canonicalName}': canonical model not found");
                    continue;
                }

                // Check if alias already exists
                bool aliasExists = await _context.PrinterModelAliases
                    .AnyAsync(a => a.PrinterModelId == model.Id &&
                        a.SlicerModelName == slicerName &&
                        a.SlicerType == "OrcaSlicer");

                if (!aliasExists)
                {
                    _context.PrinterModelAliases.Add(new PrinterModelAlias
                    {
                        Id = Guid.NewGuid(),
                        PrinterModelId = model.Id,
                        SlicerModelName = slicerName,
                        SlicerType = "OrcaSlicer",
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            // Seed PrusaSlicer aliases
            foreach (var (slicerName, canonicalName) in prusaSlicerNames)
            {
                // Find the canonical PrinterModel
                var model = await _context.PrinterModels
                    .FirstOrDefaultAsync(pm => pm.Name == canonicalName);

                if (model == null)
                {
                    _logger.LogWarning($"[DB] Skipping PrusaSlicer alias '{slicerName}' -> '{canonicalName}': canonical model not found");
                    continue;
                }

                // Check if alias already exists
                bool aliasExists = await _context.PrinterModelAliases
                    .AnyAsync(a => a.PrinterModelId == model.Id &&
                        a.SlicerModelName == slicerName &&
                        a.SlicerType == "PrusaSlicer");

                if (!aliasExists)
                {
                    _context.PrinterModelAliases.Add(new PrinterModelAlias
                    {
                        Id = Guid.NewGuid(),
                        PrinterModelId = model.Id,
                        SlicerModelName = slicerName,
                        SlicerType = "PrusaSlicer",
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[DB] Printer model aliases seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printer model alias seeding error");
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
                PrinterModel? model = await _context.PrinterModels
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
        PrinterModel? unknownModel = await _context.PrinterModels.FirstOrDefaultAsync(m =>
            m.ManufacturerId == unknownMfg.Id && m.Name == "Unknown Model");
        return unknownModel ?? throw new InvalidOperationException("Unknown model not found. Ensure SeedCatalogDataAsync() has been called.");
    }

    /// <summary>
    /// Seeds component model definitions (hotends, extruders, toolheads, nozzles) with manufacturer references.
    /// These are extensible tables that allow adding new components without code changes.
    /// </summary>
    private async Task SeedComponentModelsAsync()
    {
        try
        {
            // Build manufacturer lookup
            Dictionary<string, Guid> mfgLookup = await _context.Manufacturers
                .AsNoTracking()
                .ToDictionaryAsync(m => m.Name, m => m.Id, StringComparer.OrdinalIgnoreCase);

            // ===== HOTEND MODELS =====
            var hotendSeeds = new (string Name, string Mfg, int MaxTemp, bool IsHighFlow, string Desc, string? Url)[]
            {
                // Stock option - for unmodified/default hotends
                ("Stock", "Unknown", 280, false, "Original hotend that came with the printer", null),
                
                // Bambu Lab
                ("A1 Hotend", "Bambu Lab", 300, true, "Popular hotend from Bambu A1, commonly adapted to other printers", "https://bambulab.com/en/a1"),
                
                // Phaetus
                ("Dragon Standard Flow", "Phaetus", 500, false, "Popular all-metal hotend with great heat break", "https://www.phaetus.com/products/dragon-hotend-standard-flow"),
                ("Dragon High Flow", "Phaetus", 500, true, "High flow variant for faster prints", "https://www.phaetus.com/products/dragon-hotend-high-flow"),
                ("Dragon ACE", "Phaetus", 500, false, "Dragon with integrated accelerometer", "https://www.phaetus.com/products/dragon-ace"),
                ("Rapido", "Phaetus", 350, false, "Compact volcano-style hotend", "https://www.phaetus.com/products/rapido-hotend"),
                ("Rapido HF", "Phaetus", 350, true, "High flow Rapido variant", "https://www.phaetus.com/products/rapido-hotend"),
                ("Rapido 2", "Phaetus", 350, true, "Updated Rapido with improved heater", "https://www.phaetus.com/products/rapido-2-hotend"),
                ("Rapido 2 Plus", "Phaetus", 350, true, "Large format high flow hotend", "https://www.phaetus.com/products/rapido-2-plus"),
                
                // Slice Engineering
                ("Mosquito", "Slice Engineering", 500, false, "Premium all-metal hotend", "https://www.sliceengineering.com/products/mosquito-hotend"),
                ("Mosquito Magnum", "Slice Engineering", 500, true, "High flow Mosquito", "https://www.sliceengineering.com/products/mosquito-magnum-hotend"),
                ("Mosquito Magnum+", "Slice Engineering", 500, true, "Enhanced Magnum with better cooling", "https://www.sliceengineering.com/products/mosquito-magnum-plus-hotend"),
                ("Copperhead", "Slice Engineering", 450, false, "Bi-metal heat break design", "https://www.sliceengineering.com/products/copperhead-heat-break"),
                ("Mako", "Slice Engineering", 500, true, "Compact high flow hotend", "https://www.sliceengineering.com/products/mako-hotend"),
                
                // E3D
                ("V6", "E3D", 285, false, "Classic all-metal hotend", "https://e3d-online.com/products/v6-all-metal-hotend"),
                ("Revo Six", "E3D", 300, false, "Quick-swap nozzle system", "https://e3d-online.com/products/revo-six"),
                ("Revo Voron", "E3D", 300, false, "Revo optimized for Voron", "https://e3d-online.com/products/revo-voron"),
                ("Revo Micro", "E3D", 250, false, "Compact Revo for small printers", "https://e3d-online.com/products/revo-micro"),
                
                // TriangleLabs
                ("CHC Pro", "TriangleLabs", 500, true, "High quality ceramic heater core", "https://www.aliexpress.com/item/1005004566533274.html"),
                
                // Microswiss
                ("All Metal Hotend", "Microswiss", 300, false, "Direct replacement for Creality", "https://store.micro-swiss.com/collections/all-metal-hotend"),
                ("FlowTech", "Microswiss", 300, true, "High flow design", "https://store.micro-swiss.com/products/flowtech-hotend"),
                
                // DropEffect
                ("NextG", "DropEffect", 500, true, "Ultra high flow hotend", "https://www.dropeffect.com/products/nextg-hotend"),
                ("XG", "DropEffect", 500, true, "Extra large format", "https://www.dropeffect.com/products/xg-hotend"),
                
                // BIQU
                ("H2", "BIQU", 300, false, "Integrated extruder and hotend", "https://biqu.equipment/products/biqu-h2-extruder"),
                ("H2 V2S", "BIQU", 300, false, "Updated H2 design", "https://biqu.equipment/products/biqu-h2-v2s-extruder"),
                ("Panda Revo", "BIQU", 300, false, "Revo-compatible hotend", "https://biqu.equipment/products/panda-revo-hotend"),
            };

            foreach (var (name, mfg, maxTemp, isHighFlow, desc, url) in hotendSeeds)
            {
                if (!mfgLookup.TryGetValue(mfg, out Guid mfgId))
                {
                    _logger.LogWarning("[DB] Skipping hotend '{Name}': manufacturer '{Mfg}' not found", name, mfg);
                    continue;
                }

                bool exists = await _context.HotendModelDefinitions.AnyAsync(h => h.Name == name && h.ManufacturerId == mfgId);
                if (!exists)
                {
                    _context.HotendModelDefinitions.Add(new HotendModelDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        ManufacturerId = mfgId,
                        MaxTemp = maxTemp,
                        IsHighFlow = isHighFlow,
                        Description = desc,
                        Url = url
                    });
                }
            }

            // ===== EXTRUDER MODELS =====
            var extruderSeeds = new (string Name, string Mfg, string GearRatio, bool IsDirectDrive, string Desc, string? Url)[]
            {
                // Stock option - for unmodified/default extruders
                ("Stock", "Unknown", "N/A", false, "Original extruder that came with the printer", null),
                
                // Bondtech
                ("BMG", "Bondtech", "3:1", true, "Dual-drive extruder, very popular", "https://www.bondtech.se/product/bmg-extruder/"),
                ("LGX", "Bondtech", "3.5:1", true, "Large gears for better grip", "https://www.bondtech.se/product/lgx-large-gears-extruder/"),
                ("LGX Lite", "Bondtech", "3.5:1", true, "Lighter weight LGX", "https://www.bondtech.se/product/lgx-lite-large-gears-extruder/"),
                ("CW2", "Bondtech", "3:1", true, "Clockwork 2 compatible", "https://www.bondtech.se/product/cw2-extruder/"),
                
                // Orbiter
                ("Orbiter 1.5", "Orbiter", "7.5:1", true, "Lightweight planetary gearbox", "https://www.orbiterprojects.com/orbiter-v1-5/"),
                ("Orbiter 2.0", "Orbiter", "7.5:1", true, "Improved filament path", "https://www.orbiterprojects.com/orbiter-v2-0/"),
                ("Orbiter 2.5", "Orbiter", "7.5:1", true, "Latest revision", "https://www.orbiterprojects.com/orbiter-v2-5/"),
                
                // E3D
                ("Titan", "E3D", "3:1", false, "Compact geared extruder", "https://e3d-online.com/products/titan-extruder"),
                ("Hemera", "E3D", "3:1", true, "Integrated hotend/extruder", "https://e3d-online.com/products/e3d-hemera"),
                ("Hemera XS", "E3D", "3:1", true, "Compact Hemera", "https://e3d-online.com/products/e3d-hemera-xs"),
                
                // TriangleLabs
                ("BMG Clone", "TriangleLabs", "3:1", true, "BMG compatible", "https://www.aliexpress.com/item/32917029058.html"),
                ("Orbiter Clone", "TriangleLabs", "7.5:1", true, "Orbiter compatible", "https://www.aliexpress.com/item/1005003292442498.html"),
                
                // LDO
                ("Galileo", "LDO", "7.5:1", true, "Compact planetary extruder", "https://docs.ldomotors.com/en/voron/toolhead-pcbs/galileo"),
                ("Galileo 2", "LDO", "9:1", true, "Higher gear ratio Galileo", "https://docs.ldomotors.com/en/voron/toolhead-pcbs/galileo2"),
                
                // BIQU
                ("H2 Extruder", "BIQU", "7:1", true, "Integrated with H2 hotend", "https://biqu.equipment/products/biqu-h2-extruder"),
                
                // Voron
                ("Clockwork 1", "Voron", "3:1", true, "First generation Voron extruder", "https://github.com/VoronDesign/Voron-Afterburner"),
                ("Clockwork 2", "Voron", "3:1", true, "Improved Voron extruder with better grip", "https://github.com/VoronDesign/Voron-Stealthburner"),
            };

            foreach (var (name, mfg, gearRatio, isDirectDrive, desc, url) in extruderSeeds)
            {
                if (!mfgLookup.TryGetValue(mfg, out Guid mfgId))
                {
                    _logger.LogWarning("[DB] Skipping extruder '{Name}': manufacturer '{Mfg}' not found", name, mfg);
                    continue;
                }

                bool exists = await _context.ExtruderModelDefinitions.AnyAsync(e => e.Name == name && e.ManufacturerId == mfgId);
                if (!exists)
                {
                    _context.ExtruderModelDefinitions.Add(new ExtruderModelDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        ManufacturerId = mfgId,
                        GearRatio = gearRatio,
                        IsDirectDrive = isDirectDrive,
                        Description = desc,
                        Url = url
                    });
                }
            }

            // ===== TOOLHEAD MODELS =====
            // Note: Many toolheads are community designs without a specific manufacturer
            var toolheadSeeds = new (string Name, string? Mfg, string Desc, string? Url)[]
            {
                // Voron official/community
                ("StealthBurner", "Voron", "Enclosed direct drive toolhead for Voron", "https://github.com/VoronDesign/Voron-Stealthburner"),
                ("MiniStealthBurner", "Voron", "Compact StealthBurner for V0", "https://github.com/VoronDesign/Voron-0"),
                
                // Community designs (use Unknown manufacturer)
                ("DragonBurner", "Community", "Popular community toolhead design", "https://github.com/chirpy2605/voron/tree/main/V0/Dragon_Burner"),
                ("Xol", "Community", "High performance community toolhead", "https://github.com/Armchair-Heavy-Industries/Xol-Toolhead"),
                ("Archetype", "Community", "Modern community toolhead", "https://github.com/Armchair-Heavy-Industries/Archetype"),
                ("Jabberwocky", "Community", "Community toolhead for V0", "https://github.com/Diyshift/Jabberwocky"),
                ("AntHead", "Community", "Compact community design", "https://github.com/PrintersForAnts/AntHead"),
                ("MiniAB", "Community", "Afterburner-based mini toolhead", "https://github.com/PrintersForAnts/Mini-AfterSherpa"),
                
                // RatRig
                ("EVA", "Ratrig", "Universal toolhead system", "https://github.com/EVA-3D/eva-main"),
                ("EVA 3", "Ratrig", "Latest EVA revision", "https://github.com/EVA-3D/eva-main"),
                
                // E3D
                ("Hemera Toolhead", "E3D", "Official Hemera mounting", "https://e3d-online.com/products/e3d-hemera"),
                ("Revo Toolhead", "E3D", "Quick-swap capable toolhead", "https://e3d-online.com/products/revo-voron"),
            };

            foreach (var (name, mfg, desc, url) in toolheadSeeds)
            {
                // Manufacturer is required - should always resolve
                if (string.IsNullOrEmpty(mfg) || !mfgLookup.TryGetValue(mfg, out Guid mfgId))
                {
                    _logger.LogWarning("Toolhead {Name} has invalid manufacturer {Mfg}, using Unknown", name, mfg);
                    mfgId = mfgLookup["Unknown"];
                }

                bool exists = await _context.ToolheadModelDefinitions.AnyAsync(t => t.Name == name && t.ManufacturerId == mfgId);

                if (!exists)
                {
                    _context.ToolheadModelDefinitions.Add(new ToolheadModelDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        ManufacturerId = mfgId,
                        Description = desc,
                        Url = url
                    });
                }
            }

            // ===== NOZZLE MODELS =====
            var nozzleSeeds = new (string Name, string Mfg, int MaxTemp, bool IsHardened, string Desc, string? Url)[]
            {
                // Slice Engineering
                ("Vanadium", "Slice Engineering", 500, true, "Extreme wear resistance", "https://www.sliceengineering.com/products/vanadium-nozzle"),
                ("BridgeMaster", "Slice Engineering", 300, false, "Optimized for bridging", "https://www.sliceengineering.com/products/bridgemaster-nozzle"),
                ("GammaMaster", "Slice Engineering", 300, false, "Precision nozzle", "https://www.sliceengineering.com/products/gammamaster-nozzle"),
                
                // West3D
                ("Undertaker", "West3D", 500, true, "High flow hardened nozzle", "https://west3d.com/products/undertaker-nozzle"),
                ("Undertaker Volcano", "West3D", 500, true, "Volcano-style Undertaker", "https://west3d.com/products/undertaker-volcano-nozzle"),
                
                // E3D
                ("V6 Brass", "E3D", 300, false, "Standard brass nozzle", "https://e3d-online.com/products/v6-brass-nozzle"),
                ("V6 Hardened Steel", "E3D", 500, true, "Abrasion resistant", "https://e3d-online.com/products/v6-hardened-steel-nozzle"),
                ("NozzleX", "E3D", 500, true, "WS2 coated nozzle", "https://e3d-online.com/products/nozzle-x"),
                ("Revo Nozzle", "E3D", 300, false, "Quick-swap nozzle", "https://e3d-online.com/products/revo-nozzle"),
                
                // TriangleLabs
                ("ZS Nozzle", "TriangleLabs", 500, true, "Hardened steel", "https://www.aliexpress.com/item/1005001347220543.html"),
                ("CHC Nozzle", "TriangleLabs", 500, false, "For CHC hotends", "https://www.aliexpress.com/item/1005004566533274.html"),
                
                // Phaetus
                ("PS Nozzle", "Phaetus", 300, false, "Plated steel", "https://www.phaetus.com/products/ps-nozzle"),
                ("Tungsten Carbide", "Phaetus", 500, true, "Maximum wear resistance", "https://www.phaetus.com/products/tungsten-carbide-nozzle"),
                
                // Bondtech
                ("CHT Nozzle", "Bondtech", 300, false, "Clone-hotend technology high flow nozzle", "https://www.bondtech.se/product/bondtech-cht-nozzle/"),
                ("CHT Coated", "Bondtech", 500, true, "CHT with hardened coating for abrasives", "https://www.bondtech.se/product/bondtech-cht-coated-nozzle/"),
            };

            foreach (var (name, mfg, maxTemp, isHardened, desc, url) in nozzleSeeds)
            {
                if (!mfgLookup.TryGetValue(mfg, out Guid mfgId))
                {
                    _logger.LogWarning("[DB] Skipping nozzle '{Name}': manufacturer '{Mfg}' not found", name, mfg);
                    continue;
                }

                bool exists = await _context.NozzleModelDefinitions.AnyAsync(n => n.Name == name && n.ManufacturerId == mfgId);
                if (!exists)
                {
                    _context.NozzleModelDefinitions.Add(new NozzleModelDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        ManufacturerId = mfgId,
                        MaxTemp = maxTemp,
                        IsHardened = isHardened,
                        Description = desc,
                        Url = url
                    });
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("[DB] Component models seeded successfully (hotends, extruders, toolheads, nozzles)");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogWarning(ex, "Ignored unique constraint violation while seeding component models; another process probably inserted the same records.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] Error seeding component models");
            throw;
        }
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
    /// Seeds root folders ("/") for gcode and models folder types.
    /// This prevents race conditions when multiple concurrent uploads try to create the root folder simultaneously.
    /// </summary>
    private async Task SeedRootFoldersAsync()
    {
        try
        {
            // Ensure root "/" folder exists for "gcode" folder type
            string folderPath = "/";
            string[] folderTypes = new[] { "gcode", "models" };

            foreach (string folderType in folderTypes)
            {
                bool rootExists = await _context.Folders.AnyAsync(f => f.Path == folderPath && f.FolderType == folderType);
                if (!rootExists)
                {
                    _context.Folders.Add(new FolderNode
                    {
                        Id = Guid.NewGuid(),
                        Path = folderPath,
                        FolderType = folderType,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            try
            {
                _ = await _context.SaveChangesAsync();
                _logger.LogInformation("[DB] Root folders ('/') seeded successfully for gcode and models types");
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Ignore duplicate key errors - root folder already exists (possibly created by another instance)
                _logger.LogDebug("[DB] Root folders already exist (duplicate key handled gracefully)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] Failed to seed root folders");
            throw;
        }
    }

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

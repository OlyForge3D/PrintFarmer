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
public class DatabaseInitializer(AppDbContext context, IUnifiedLoggingService logger, IDataSeedService dataSeedService) : IDatabaseInitializer
{
    private readonly AppDbContext _context = context;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IDataSeedService _dataSeedService = dataSeedService;

    /// <summary>
    /// Initialize database with retry logic for container startup scenarios
    /// </summary>
    /// <param name="dbProvider">The database provider name (e.g., "Sqlite", "SqlServer", "Postgres").</param>
    /// <param name="maxRetries">Maximum number of retry attempts for database connection.</param>
    /// <param name="delaySeconds">Delay in seconds between retry attempts.</param>
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
                    _logger.LogWarning(
                        ex,
                        $"[DB] Database initialization attempt {retryCount}/{maxRetries} failed: {ex.Message}. Retrying in {delaySeconds} seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
                else
                {
                    _logger.LogError(
                        ex,
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

        // Seed these from existing methods
        await SeedAuthenticationDataAsync();
        await SeedRootFoldersAsync();

        // Try loading from YAML files first (new approach)
        // If YAML files don't exist or fail to load, fall back to hardcoded seed data
        try
        {
            _logger.LogInformation("[DB] Attempting to seed from YAML files");

            await _dataSeedService.SeedFilamentTypesAsync();
            await _dataSeedService.SeedManufacturersAsync();
            await _dataSeedService.SeedComponentModelsAsync();  // Must come before printer models so toolhead defaults exist
            await _dataSeedService.SeedPrinterModelsAsync();    // Now includes toolhead seeding

            // Note: Toolhead default components are now resolved within SeedComponentModelsAsync
            // via ResolveToolheadDefaultComponentsFromYamlAsync which reads defaults from toolheads.yaml

            // Fallback: fill in any printer models not covered by YAML with hardcoded mappings
            await SeedPrinterModelToolheadsAsync();

            _logger.LogInformation("[DB] Successfully seeded from YAML files");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DB] Failed to seed from YAML files, falling back to hardcoded seed data: {Message}", ex.Message);
        }

        // Fallback to original hardcoded seeding
        // await SeedFilamentTypesAsync();  // Must come before SeedCatalogDataAsync
        // await SeedCatalogDataAsync();    // This creates printer model/filament type relationships
        // await SeedComponentModelsAsync(); // Seed hotend, extruder, toolhead, nozzle models
        // await SeedPrinterModelToolheadsAsync(); // Link printer models to OEM hotends/extruders
        // await SeedAuthenticationDataAsync();
        // await SeedRootFoldersAsync();    // Seed root "/" folders for gcode and models to prevent race conditions
    }

    private async Task SeedCatalogDataAsync()
    {
        try
        {
#pragma warning disable SA1025 // Code should not contain multiple whitespace in a row
            string[] manufacturerNames = new[]
            {
                "Unknown",  // Default for unidentified manufacturers - must be first to ensure it gets a consistent ID
                "Generic",  // For generic/unbranded components (e.g., Generic Brass Nozzle)
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
                "Creality",
                "Anycubic",

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
#pragma warning restore SA1025 // Code should not contain multiple whitespace in a row

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

            (string Name, string Mfg, double X, double Y, double Z, PrinterBackend? DefaultBackend, MotionType? MotionType,
             double? NozzleDiameter, bool HasBed, bool HasEnclosure, bool MultiMaterial, int Extruders, bool AutoLevel,
             int? MinHotend, int? MaxHotend, int? MinBed, int? MaxBed, string Materials, int? MaxSpeed)[] modelSeeds = new[]
            {
                ("Unknown Model", "Unknown", 200.0, 200.0, 200.0, (PrinterBackend?)null, (MotionType?)MotionType.Unknown, (double?)0.4, true, false, false, 1, false, (int?)0, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS", (int?)100),

                // Flashforge (note: manifest uses "Flashforge" not "FlashForge") - uses Moonraker/Klipper
                ("Flashforge AD5X", "Flashforge", 220.0, 220.0, 220.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),

                // Sovol (manifest names: "Sovol SV08", "Sovol SV08 MAX", "Sovol Zero") - uses Moonraker/Klipper
                ("Sovol SV08", "Sovol", 350.0, 350.0, 350.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Sovol SV08 MAX", "Sovol", 500.0, 500.0, 500.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Sovol Zero", "Sovol", 150.0, 150.0, 150.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS", (int?)150),

                // Eryone (manifest: "Eryone" -> "Thinker X400") - uses Moonraker/Klipper
                ("Thinker X400", "Eryone", 400.0, 400.0, 400.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),

                // Elegoo (manifest: Neptune 4 models and Centauri models) - uses Moonraker/Klipper
                ("Elegoo Centauri", "Elegoo", 256.0, 256.0, 256.0, (PrinterBackend?)PrinterBackend.SDCP, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Elegoo Centauri Carbon", "Elegoo", 256.0, 256.0, 256.0, (PrinterBackend?)PrinterBackend.SDCP, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),

                // PrintersForAnts (community derivative of Voron - smaller build volumes) - uses Moonraker/Klipper
                ("SaladFork 120", "PrintersForAnts", 120.0, 120.0, 120.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("SaladFork 180", "PrintersForAnts", 180.0, 180.0, 165.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 120", "PrintersForAnts", 120.0, 120.0, 120.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),
                ("Micron 180", "PrintersForAnts", 180.0, 180.0, 165.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA", (int?)200),

                // Voron (manifest: "Voron 0.1", "Voron 2.4 250", etc. - NOT "v2.4" or "Voron v0") - uses Moonraker/Klipper
                ("Voron 0.1", "Voron", 120.0, 120.0, 120.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron 2.4 250", "Voron", 250.0, 250.0, 250.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron 2.4 300", "Voron", 300.0, 300.0, 300.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron 2.4 350", "Voron", 350.0, 350.0, 350.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron Switchwire 250", "Voron", 250.0, 210.0, 240.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("Voron Trident 250", "Voron", 250.0, 250.0, 250.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron Trident 300", "Voron", 300.0, 300.0, 250.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("Voron Trident 350", "Voron", 350.0, 350.0, 250.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),

                // Ratrig (note: manifest uses "Ratrig" not "RatRig", models use "RatRig V-Core 3 200" etc.) - uses Moonraker/Klipper
                ("RatRig V-Core 3 200", "Ratrig", 200.0, 200.0, 200.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("RatRig V-Core 3 300", "Ratrig", 300.0, 300.0, 300.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("RatRig V-Core 3 400", "Ratrig", 400.0, 400.0, 400.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("RatRig V-Core 3 500", "Ratrig", 500.0, 500.0, 500.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)250),
                ("RatRig V-Core 4 300", "Ratrig", 300.0, 300.0, 300.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 400", "Ratrig", 400.0, 400.0, 400.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 500", "Ratrig", 500.0, 500.0, 500.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 HYBRID 300", "Ratrig", 300.0, 300.0, 300.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 HYBRID 400", "Ratrig", 400.0, 400.0, 400.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 HYBRID 500", "Ratrig", 500.0, 500.0, 500.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 IDEX 300", "Ratrig", 300.0, 300.0, 300.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 IDEX 400", "Ratrig", 400.0, 400.0, 400.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),
                ("RatRig V-Core 4 IDEX 500", "Ratrig", 500.0, 500.0, 500.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, true, 2, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)300),

                // Phrozen (manifest: "Phrozen" -> "Phrozen Arco") - uses Moonraker/Klipper
                ("Phrozen Arco", "Phrozen", 300.0, 300.0, 300.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, false, false, 1, true, (int?)180, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),

                // Qidi (manifest: "Qidi" -> "Qidi Q1 Pro", "Qidi X-Plus 4", etc.) - uses Moonraker/Klipper
                ("QIDI X-Plus 4", "Qidi", 305.0, 305.0, 280.0, (PrinterBackend?)PrinterBackend.Moonraker, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)180, (int?)370, (int?)0, (int?)110, "PLA,PETG,ABS,ASA,PC,TPU,Nylon", (int?)600),

                // Prusa (manifest: "Prusa MINI", "Prusa MK3S", "Prusa MK4S", "Prusa CORE One", "Prusa XL" - NO "Original Prusa")
                ("Prusa MINI", "Prusa", 180.0, 180.0, 180.0, (PrinterBackend?)PrinterBackend.PrusaLink, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)280, (int?)0, (int?)100, "PLA,PETG,ABS,ASA,PC", (int?)180),
                ("Prusa MK3S", "Prusa", 250.0, 210.0, 210.0, (PrinterBackend?)PrinterBackend.PrusaLink, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC", (int?)200),
                ("Prusa MK3.5", "Prusa", 250.0, 210.0, 210.0, (PrinterBackend?)PrinterBackend.PrusaLink, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
                ("Prusa MK4", "Prusa", 250.0, 210.0, 220.0, (PrinterBackend?)PrinterBackend.PrusaLink, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
                ("Prusa MK4S", "Prusa", 250.0, 210.0, 220.0, (PrinterBackend?)PrinterBackend.PrusaLink, (MotionType?)MotionType.Cartesian, (double?)0.4, true, false, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
                ("Prusa CORE One", "Prusa", 250.0, 220.0, 270.0, (PrinterBackend?)PrinterBackend.PrusaLink, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)250),
                ("Prusa CORE One L", "Prusa", 300.0, 300.0, 300.0, (PrinterBackend?)PrinterBackend.PrusaLink, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, false, 1, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)250),
                ("Prusa XL", "Prusa", 250.0, 220.0, 270.0, (PrinterBackend?)PrinterBackend.PrusaLink, (MotionType?)MotionType.CoreXY, (double?)0.4, true, true, true, 5, true, (int?)170, (int?)300, (int?)0, (int?)120, "PLA,PETG,ABS,ASA,PC,TPU", (int?)200),
            };

            foreach ((string modelName, string mfg, double x, double y, double z, PrinterBackend? defaultBackend, MotionType? motionType,
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
                        DefaultBackend = defaultBackend.HasValue ? (int?)defaultBackend.Value : null,
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
            foreach ((string? slicerName, string? canonicalName) in orcaSlicerNames)
            {
                // Find the canonical PrinterModel
                PrinterModel? model = await _context.PrinterModels
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
            foreach ((string? slicerName, string? canonicalName) in prusaSlicerNames)
            {
                // Find the canonical PrinterModel
                PrinterModel? model = await _context.PrinterModels
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
             PrinterBackend? DefaultBackend, MotionType? MotionType, double? NozzleDiameter, bool HasBed, bool HasEnclosure,
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
                    .Include(m => m.SupportedFilamentTypes)
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
                            // Skip navigation - just add to the collection
                            if (!model.SupportedFilamentTypes.Contains(filamentType))
                            {
                                model.SupportedFilamentTypes.Add(filamentType);
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
        // (Name, HotendTemp, BedTemp, IsAbrasive, NeedsEnclosure)
        (string Name, int HotendTemp, int BedTemp, bool IsAbrasive, bool NeedsEnclosure)[] filamentTypes = new[]
        {
            // Standard filaments
            ("PLA", 205, 60, false, false),
            ("PLA+", 210, 60, false, false),
            ("PETG", 240, 85, false, false),
            ("ABS", 245, 100, false, true),
            ("ASA", 250, 100, false, true),
            ("TPU", 220, 60, false, false),
            ("TPU-95A", 230, 60, false, false),
            ("TPU-85A", 215, 50, false, false),
            ("FLEX", 225, 60, false, false),

            // Engineering filaments
            ("PC", 270, 110, false, true),
            ("PCTG", 235, 80, false, false),
            ("PET", 235, 80, false, false),
            ("PP", 230, 85, false, true),
            ("PHAT", 240, 90, false, false),
            ("Nylon", 260, 70, false, true),
            ("PA6", 260, 80, false, true),
            ("PA12", 250, 75, false, true),

            // Carbon fiber composites (abrasive)
            ("PLA-CF", 220, 60, true, false),
            ("PETG-CF", 255, 85, true, false),
            ("PET-CF", 255, 85, true, false),
            ("ABS-CF", 260, 100, true, true),
            ("ASA-CF", 260, 100, true, true),
            ("PA6-CF", 280, 90, true, true),
            ("PA12-CF", 270, 85, true, true),
            ("PC-CF", 280, 110, true, true),

            // Glass fiber composites (abrasive)
            ("ABS-GF", 255, 100, true, true),
            ("ASA-GF", 255, 100, true, true),
            ("PA6-GF", 275, 90, true, true),
            ("PP-GF", 240, 85, true, true),

            // Specialty filaments
            ("Wood", 210, 65, false, false),
            ("Glow-in-the-Dark", 210, 60, true, false),  // Contains phosphorescent particles (abrasive)
            ("Silk PLA", 210, 60, false, false),
            ("Matte PLA", 205, 60, false, false),
        };
        foreach ((string name, int hotendTemp, int bedTemp, bool isAbrasive, bool needsEnclosure) in filamentTypes)
        {
            FilamentType? existing = await _context.FilamentTypes.FirstOrDefaultAsync(f => f.Name == name);
            if (existing == null)
            {
                FilamentType filamentType = new()
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    DefaultHotendTemp = hotendTemp,
                    DefaultBedTemp = bedTemp,
                    IsAbrasive = isAbrasive,
                    NeedsEnclosure = needsEnclosure
                };
                _ = _context.FilamentTypes.Add(filamentType);
            }
            else
            {
                // Update existing filament types with new properties if they weren't set
                existing.IsAbrasive = isAbrasive;
                existing.NeedsEnclosure = needsEnclosure;
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
                // Bambu Lab - Model-specific hotends
                ("X1 Series Hotend", "Bambu Lab", 300, true, "Stock hotend for Bambu Lab X1, X1C, X1E series with ceramic heater", "https://bambulab.com/en/x1"),
                ("P1 Series Hotend", "Bambu Lab", 300, true, "Stock hotend for Bambu Lab P1P, P1S series", "https://bambulab.com/en/p1"),
                ("A1 Hotend", "Bambu Lab", 300, true, "Stock hotend for Bambu Lab A1 series, commonly adapted to other printers", "https://bambulab.com/en/a1"),
                ("A1 Mini Hotend", "Bambu Lab", 300, false, "Compact hotend for Bambu Lab A1 Mini", "https://bambulab.com/en/a1-mini"),

                // Prusa - Model-specific hotends
                ("MK3S Hotend", "Prusa", 300, false, "V6-style hotend for MK3S/MK3S+ with PTFE-lined heatbreak", "https://www.prusa3d.com/product/original-prusa-i3-mk3s-3d-printer-3/"),
                ("Mini Hotend", "Prusa", 280, false, "Compact hotend for Prusa Mini/Mini+", "https://www.prusa3d.com/product/original-prusa-mini-semi-assembled-3d-printer/"),
                ("Nextruder Hotend", "Prusa", 300, true, "Integrated hotend in Nextruder toolhead for MK3.9S, MK4, MK4S", "https://www.prusa3d.com/product/original-prusa-mk4-3d-printer/"),
                ("CORE One Hotend", "Prusa", 300, true, "High-flow hotend for Prusa CORE One, based on Nextruder", "https://www.prusa3d.com/product/prusa-core-one/"),
                ("XL Hotend", "Prusa", 300, true, "Toolchanger-compatible hotend for Prusa XL", "https://www.prusa3d.com/product/original-prusa-xl/"),

                // Creality - Model-specific hotends (for Klipper-converted machines)
                ("Sprite Hotend", "Creality", 300, false, "All-metal hotend in Sprite Pro extruder for Ender-3 S1, K1", "https://www.creality.com"),
                ("K1 Hotend", "Creality", 300, true, "High-speed ceramic heater hotend for K1 series", "https://www.creality.com"),
                ("K1 Max Hotend", "Creality", 300, true, "Large format high-speed hotend for K1 Max", "https://www.creality.com"),

                // Qidi - Model-specific hotends
                ("X-Plus 4 Hotend", "Qidi", 350, true, "High-temp ceramic heater hotend for X-Plus 4", "https://www.qidi3d.com"),
                ("Q1 Pro Hotend", "Qidi", 350, true, "High-temp hotend for Q1 Pro", "https://www.qidi3d.com"),

                // Elegoo - Model-specific hotends
                ("Centauri Hotend", "Elegoo", 300, true, "Stock hotend for Elegoo Centauri series", "https://www.elegoo.com"),

                // Sovol - Model-specific hotends
                ("SV08 Hotend", "Sovol", 300, true, "Stock hotend for Sovol SV08", "https://sovol3d.com"),

                // Phaetus - Dragon series
                ("Dragon Standard Flow", "Phaetus", 500, false, "Popular all-metal hotend with precision bi-metal heat break", "https://www.phaetus.com/products/dragon-hotend-standard-flow"),
                ("Dragon High Flow", "Phaetus", 500, true, "High flow variant with larger melt zone for faster prints", "https://www.phaetus.com/products/dragon-hotend-high-flow"),
                ("Dragon ACE", "Phaetus", 500, false, "Dragon with integrated ADXL345 accelerometer for input shaping", "https://www.phaetus.com/products/dragon-ace"),
                ("Dragon UHF", "Phaetus", 500, true, "Ultra high flow Dragon for maximum extrusion rates", "https://www.phaetus.com/products/dragon-uhf"),

                // Phaetus - Dragonfly series (compact bi-metal)
                ("Dragonfly BMO", "Phaetus", 500, false, "Compact bi-metal hotend, V6-compatible mounting", "https://www.phaetus.com/products/dragonfly-bmo"),
                ("Dragonfly BMS", "Phaetus", 500, false, "Bi-metal hotend with shorter profile for tight spaces", "https://www.phaetus.com/products/dragonfly-bms"),
                ("Dragonfly HIC", "Phaetus", 500, false, "High-performance integrated cooling variant", "https://www.phaetus.com/products/dragonfly-hic"),

                // Phaetus - Rapido series (high flow volcano-style with ceramic heater)
                ("Rapido", "Phaetus", 450, false, "Compact volcano-style hotend with 115W ceramic heater", "https://www.phaetus.com/products/rapido-hotend"),
                ("Rapido HF", "Phaetus", 450, true, "High flow Rapido variant for faster printing", "https://www.phaetus.com/products/rapido-hotend"),
                ("Rapido 2", "Phaetus", 450, true, "Updated Rapido with improved heater and flow", "https://www.phaetus.com/products/rapido-2-hotend"),
                ("Rapido 2 Plus", "Phaetus", 450, true, "Large format high flow hotend for big printers", "https://www.phaetus.com/products/rapido-2-plus"),

                // Slice Engineering
                ("Mosquito", "Slice Engineering", 500, false, "Premium all-metal hotend", "https://www.sliceengineering.com/products/mosquito-hotend"),
                ("Mosquito Magnum", "Slice Engineering", 500, true, "High flow Mosquito", "https://www.sliceengineering.com/products/mosquito-magnum-hotend"),
                ("Mosquito Magnum+", "Slice Engineering", 500, true, "Enhanced Magnum with better cooling", "https://www.sliceengineering.com/products/mosquito-magnum-plus-hotend"),
                ("Copperhead", "Slice Engineering", 500, false, "Bi-metal heat break with copper block", "https://www.sliceengineering.com/products/copperhead-heat-break"),
                ("Mako", "Slice Engineering", 500, true, "Compact high flow hotend", "https://www.sliceengineering.com/products/mako-hotend"),

                // E3D - Classic V6 line
                ("V6", "E3D", 285, false, "Classic all-metal hotend, industry standard", "https://e3d-online.com/products/v6-all-metal-hotend"),
                ("V6 Volcano", "E3D", 300, true, "High flow V6 variant with extended melt zone", "https://e3d-online.com/products/volcano-hotend"),
                ("V6 SuperVolcano", "E3D", 300, true, "Maximum flow for large format printing", "https://e3d-online.com/products/supervolcano-hotend"),

                // E3D - Revo ecosystem (quick-swap nozzle system)
                ("Revo Six", "E3D", 300, false, "Standard Revo with quick-swap nozzles, 24V heater", "https://e3d-online.com/products/revo-six"),
                ("Revo Voron", "E3D", 300, false, "Revo optimized for Voron with shorter melt zone", "https://e3d-online.com/products/revo-voron"),
                ("Revo Micro", "E3D", 250, false, "Compact lightweight Revo for small printers and bowden setups", "https://e3d-online.com/products/revo-micro"),
                ("Revo CR", "E3D", 300, false, "Drop-in Revo replacement for Creality printers", "https://e3d-online.com/products/revo-cr"),
                ("Revo Hemera", "E3D", 300, false, "Integrated direct drive extruder with Revo hotend", "https://e3d-online.com/products/revo-hemera"),
                ("Revo Hemera XS", "E3D", 300, false, "Compact version of Revo Hemera for tight spaces", "https://e3d-online.com/products/revo-hemera-xs"),

                // E3D - Hemera (non-Revo, direct drive ecosystem)
                ("Hemera", "E3D", 500, false, "Direct drive extruder with all-metal V6 hotend", "https://e3d-online.com/products/hemera"),
                ("Hemera XS", "E3D", 500, false, "Compact direct drive for confined spaces", "https://e3d-online.com/products/hemera-xs"),

                // TriangleLabs - Ceramic heater core series
                ("CHC Pro", "TriangleLabs", 500, true, "High quality ceramic heater core hotend", "https://www.aliexpress.com/item/1005004566533274.html"),
                ("CHC Pro HF", "TriangleLabs", 500, true, "High flow ceramic heater core variant", "https://www.aliexpress.com/item/1005004566533274.html"),

                // TriangleLabs - TD6 series (compact hotends)
                ("TD6S", "TriangleLabs", 500, false, "Compact all-metal hotend with bi-metal heat break", "https://www.aliexpress.com/item/1005005159949561.html"),
                ("TD6S HF", "TriangleLabs", 500, true, "High flow TD6S variant", "https://www.aliexpress.com/item/1005005159949561.html"),

                // TriangleLabs - TZ-V6 series (bi-metal V6-compatible)
                ("TZ-V6 2.0", "TriangleLabs", 300, false, "V6-compatible hotend with titanium alloy bi-metal heatbreak, 33mm³/s max flow", "https://www.aliexpress.com/item/1005003582802382.html"),

                // TriangleLabs - Dragon clones
                ("Dragon SF Clone", "TriangleLabs", 500, false, "Dragon Standard Flow compatible hotend", "https://www.aliexpress.com/item/1005001892537591.html"),
                ("Dragon HF Clone", "TriangleLabs", 500, true, "Dragon High Flow compatible hotend", "https://www.aliexpress.com/item/1005001892537591.html"),

                // TriangleLabs - Rapido clones
                ("Rapido Clone", "TriangleLabs", 450, true, "Rapido-compatible high flow hotend", "https://www.aliexpress.com/item/1005003738382019.html"),
                ("Rapido UHF Clone", "TriangleLabs", 450, true, "Ultra high flow Rapido-compatible hotend", "https://www.aliexpress.com/item/1005003738382019.html"),

                // Microswiss
                ("All Metal Hotend", "Microswiss", 285, false, "Direct replacement for Creality/Ender printers", "https://store.micro-swiss.com/collections/all-metal-hotend"),
                ("FlowTech", "Microswiss", 300, true, "High flow bi-metal design", "https://store.micro-swiss.com/products/flowtech-hotend"),

                // DropEffect
                ("NextG", "DropEffect", 500, true, "Ultra high flow hotend", "https://www.dropeffect.com/products/nextg-hotend"),
                ("XG", "DropEffect", 500, true, "Extra large format", "https://www.dropeffect.com/products/xg-hotend"),

                // BIQU
                ("H2", "BIQU", 300, false, "Integrated extruder and hotend", "https://biqu.equipment/products/biqu-h2-extruder"),
                ("H2 V2S", "BIQU", 300, false, "Updated H2 design", "https://biqu.equipment/products/biqu-h2-v2s-extruder"),
                ("Panda Revo", "BIQU", 300, false, "Revo-compatible hotend", "https://biqu.equipment/products/panda-revo-hotend"),
            };

            foreach ((string? name, string? mfg, int maxTemp, bool isHighFlow, string? desc, string? url) in hotendSeeds)
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
                // Prusa - Specific extruder models
                ("MK3S Extruder", "Prusa", "3:1", true, "Stock extruder for MK3S/MK3S+, compatible with V6-style hotends", "https://www.prusa3d.com/product/original-prusa-i3-mk3s-3d-printer-3/"),
                ("Mini Extruder", "Prusa", "3:1", false, "Bowden extruder for Prusa Mini/Mini+, mounted on frame", "https://www.prusa3d.com/product/original-prusa-mini-semi-assembled-3d-printer/"),
                ("Nextruder Extruder", "Prusa", "4:1", true, "Planetary gearbox extruder in Nextruder toolhead for MK3.9S, MK4, MK4S, CORE One", "https://www.prusa3d.com/product/original-prusa-mk4-3d-printer/"),

                // Bambu Lab - Model-specific extruders
                ("X1 Series Extruder", "Bambu Lab", "3.5:1", true, "Stock direct drive extruder for X1, X1C, X1E", "https://bambulab.com/en/x1"),
                ("P1 Series Extruder", "Bambu Lab", "3.5:1", true, "Stock direct drive extruder for P1P, P1S", "https://bambulab.com/en/p1"),
                ("A1 Extruder", "Bambu Lab", "3.5:1", true, "Stock direct drive extruder for A1", "https://bambulab.com/en/a1"),
                ("A1 Mini Extruder", "Bambu Lab", "3.5:1", true, "Compact direct drive extruder for A1 Mini", "https://bambulab.com/en/a1-mini"),

                // Creality - Model-specific extruders
                ("Sprite Pro", "Creality", "3.5:1", true, "Dual-gear direct drive extruder with all-metal hotend", "https://www.creality.com"),
                ("K1 Extruder", "Creality", "3.5:1", true, "High-speed direct drive extruder for K1 series", "https://www.creality.com"),

                // Qidi - Model-specific extruders
                ("X-Plus 4 Extruder", "Qidi", "3.5:1", true, "High-speed direct drive extruder for X-Plus 4", "https://www.qidi3d.com"),

                // Elegoo - Model-specific extruders
                ("Centauri Extruder", "Elegoo", "3.5:1", true, "Stock extruder for Elegoo Centauri series", "https://www.elegoo.com"),

                // Sovol - Model-specific extruders
                ("SV08 Extruder", "Sovol", "3.5:1", true, "Stock extruder for Sovol SV08", "https://sovol3d.com"),

                // Bondtech - Dual drive extruders
                ("BMG", "Bondtech", "3:1", true, "Dual-drive extruder, industry standard for reliable extrusion", "https://www.bondtech.se/product/bmg-extruder/"),
                ("BMG-M", "Bondtech", "3:1", true, "BMG mirrored version for multi-material setups", "https://www.bondtech.se/product/bmg-m-extruder/"),

                // Bondtech - LGX series (large gears, ~5.18:1 effective ratio)
                ("LGX", "Bondtech", "5.18:1", true, "Large gears extruder with excellent grip on flexible filaments", "https://www.bondtech.se/product/lgx-large-gears-extruder/"),
                ("LGX Lite", "Bondtech", "5.18:1", true, "Lightweight LGX with reduced mass for faster acceleration", "https://www.bondtech.se/product/lgx-lite-large-gears-extruder/"),
                ("LGX Lite ACE", "Bondtech", "5.18:1", true, "LGX Lite with integrated ADXL345 accelerometer", "https://www.bondtech.se/product/lgx-lite-ace/"),
                ("LGX Shortcut", "Bondtech", "5.18:1", true, "Compact LGX variant for tight spaces, often paired with Mosquito hotend", "https://www.bondtech.se/product/lgx-shortcut/"),

                // Bondtech - DDX (Direct Drive X) integrated systems
                ("DDX v3", "Bondtech", "3:1", true, "Direct Drive X system, replaces stock Creality extruder", "https://www.bondtech.se/product/ddx-v3/"),
                ("DDX-PH", "Bondtech", "3:1", true, "DDX for Prusa MK3/MK3S with Phaetus Dragon compatibility", "https://www.bondtech.se/product/ddx-ph/"),

                // Bondtech - Prusa Mini upgrade
                ("IFS Extruder for Prusa Mini", "Bondtech", "3:1", true, "Direct drive upgrade for Prusa Mini/Mini+, replaces stock bowden setup", "https://www.bondtech.se/product/ifs-extruder-for-prusa-mini/"),

                // Bondtech - Voron compatible
                ("CW2", "Bondtech", "3:1", true, "Clockwork 2 compatible extruder for Voron StealthBurner", "https://www.bondtech.se/product/cw2-extruder/"),

                // Orbiter
                ("Orbiter 1.5", "Orbiter", "7.5:1", true, "Lightweight planetary gearbox", "https://www.orbiterprojects.com/orbiter-v1-5/"),
                ("Orbiter 2.0", "Orbiter", "7.5:1", true, "Improved filament path", "https://www.orbiterprojects.com/orbiter-v2-0/"),
                ("Orbiter 2.5", "Orbiter", "7.5:1", true, "Latest revision", "https://www.orbiterprojects.com/orbiter-v2-5/"),

                // E3D
                ("Titan", "E3D", "3:1", false, "Compact geared extruder", "https://e3d-online.com/products/titan-extruder"),
                ("Hemera", "E3D", "3:1", true, "Integrated hotend/extruder", "https://e3d-online.com/products/e3d-hemera"),
                ("Hemera XS", "E3D", "3:1", true, "Compact Hemera", "https://e3d-online.com/products/e3d-hemera-xs"),

                // TriangleLabs - Clone extruders (budget-friendly alternatives)
                ("BMG Clone", "TriangleLabs", "3:1", true, "BMG-compatible dual drive extruder", "https://www.aliexpress.com/item/32917029058.html"),
                ("BMG-M Clone", "TriangleLabs", "3:1", true, "Mirrored BMG clone for multi-material", "https://www.aliexpress.com/item/32917029058.html"),
                ("Orbiter Clone", "TriangleLabs", "7.5:1", true, "Orbiter-compatible planetary extruder", "https://www.aliexpress.com/item/1005003292442498.html"),
                ("LGX Lite Clone", "TriangleLabs", "5.18:1", true, "LGX Lite compatible extruder", "https://www.aliexpress.com/item/1005004123456789.html"),
                ("Sherpa Mini Clone", "TriangleLabs", "5:1", true, "Sherpa Mini compatible compact extruder", "https://www.aliexpress.com/item/1005003987654321.html"),

                // LDO
                ("Galileo", "LDO", "7.5:1", true, "Compact planetary extruder", "https://docs.ldomotors.com/en/voron/toolhead-pcbs/galileo"),
                ("Galileo 2", "LDO", "9:1", true, "Higher gear ratio Galileo", "https://docs.ldomotors.com/en/voron/toolhead-pcbs/galileo2"),

                // BIQU
                ("H2 Extruder", "BIQU", "7:1", true, "Integrated with H2 hotend", "https://biqu.equipment/products/biqu-h2-extruder"),

                // Voron
                ("Clockwork 1", "Voron", "3:1", true, "First generation Voron extruder", "https://github.com/VoronDesign/Voron-Afterburner"),
                ("Clockwork 2", "Voron", "3:1", true, "Improved Voron extruder with better grip", "https://github.com/VoronDesign/Voron-Stealthburner"),
            };

            foreach ((string? name, string? mfg, string? gearRatio, bool isDirectDrive, string? desc, string? url) in extruderSeeds)
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
                // Prusa - Model-specific toolheads
                ("MK3S Toolhead", "Prusa", "Stock toolhead for Prusa MK3S/MK3S+ with E3D V6-style hotend", "https://www.prusa3d.com/product/original-prusa-i3-mk3s-3d-printer-3/"),
                ("Mini Toolhead", "Prusa", "Stock bowden toolhead for Prusa Mini/Mini+", "https://www.prusa3d.com/product/original-prusa-mini-semi-assembled-3d-printer/"),
                ("Nextruder", "Prusa", "Integrated direct drive toolhead for MK3.9S, MK4, MK4S, CORE One", "https://www.prusa3d.com/product/original-prusa-mk4-3d-printer/"),
                ("XL Toolhead", "Prusa", "Toolchanger-compatible toolhead for Prusa XL", "https://www.prusa3d.com/product/original-prusa-xl/"),

                // Bambu Lab - Model-specific toolheads
                ("X1 Series Toolhead", "Bambu Lab", "Stock toolhead for Bambu Lab X1, X1C, X1E series", "https://bambulab.com/en/x1"),
                ("P1 Series Toolhead", "Bambu Lab", "Stock toolhead for Bambu Lab P1P, P1S series", "https://bambulab.com/en/p1"),
                ("A1 Toolhead", "Bambu Lab", "Stock toolhead for Bambu Lab A1 series", "https://bambulab.com/en/a1"),
                ("A1 Mini Toolhead", "Bambu Lab", "Compact toolhead for Bambu Lab A1 Mini", "https://bambulab.com/en/a1-mini"),

                // Creality - Model-specific toolheads
                ("Sprite Pro Toolhead", "Creality", "Sprite Pro toolhead for Ender-3 S1 and derivatives", "https://www.creality.com"),
                ("K1 Toolhead", "Creality", "High-speed toolhead for K1 series", "https://www.creality.com"),

                // Qidi - Model-specific toolheads
                ("X-Plus 4 Toolhead", "Qidi", "High-speed toolhead for Qidi X-Plus 4", "https://www.qidi3d.com"),

                // Elegoo - Model-specific toolheads
                ("Centauri Toolhead", "Elegoo", "Stock toolhead for Elegoo Centauri series", "https://www.elegoo.com"),

                // Sovol - Model-specific toolheads
                ("SV08 Toolhead", "Sovol", "Stock toolhead for Sovol SV08", "https://sovol3d.com"),

                // Voron official/community
                ("StealthBurner", "Voron", "Enclosed direct drive toolhead for Voron", "https://github.com/VoronDesign/Voron-Stealthburner"),
                ("Mini StealthBurner", "Voron", "Compact StealthBurner for V0", "https://github.com/VoronDesign/Voron-0"),

                // Community designs (PrintersForAnts community)
                ("DragonBurner", "PrintersForAnts", "Popular community toolhead design", "https://github.com/chirpy2605/voron/tree/main/V0/Dragon_Burner"),
                ("Xol", "Community", "High performance community toolhead", "https://github.com/Armchair-Heavy-Industries/Xol-Toolhead"),
                ("Archetype", "Community", "Modern community toolhead", "https://github.com/Armchair-Heavy-Industries/Archetype"),
                ("Jabberwocky", "Community", "Community toolhead for V0", "https://github.com/Diyshift/Jabberwocky"),
                ("AntHead", "PrintersForAnts", "Compact design for Micron and other mini printers", "https://github.com/PrintersForAnts/AntHead"),
                ("MiniAB", "PrintersForAnts", "Afterburner-based mini toolhead", "https://github.com/PrintersForAnts/Mini-AfterSherpa"),

                // RatRig
                ("EVA", "Ratrig", "Universal toolhead system", "https://github.com/EVA-3D/eva-main"),
                ("EVA 3", "Ratrig", "Latest EVA revision", "https://github.com/EVA-3D/eva-main"),

                // E3D
                ("Hemera Toolhead", "E3D", "Official Hemera mounting", "https://e3d-online.com/products/e3d-hemera"),
                ("Revo Toolhead", "E3D", "Quick-swap capable toolhead", "https://e3d-online.com/products/revo-voron"),
            };

            foreach ((string? name, string? mfg, string? desc, string? url) in toolheadSeeds)
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
            // Format: (Name, Mfg, MaxTemp, NozzleType, NozzleInterface, Description, Url)
            var nozzleSeeds = new (string Name, string Mfg, int MaxTemp, NozzleType NozzleType, NozzleInterfaceType Interface, string Desc, string? Url)[]
            {
                // Generic nozzles for V6 interface (standard E3D-style thread)
                ("Brass Nozzle", "Generic", 300, NozzleType.Brass, NozzleInterfaceType.V6, "Standard brass nozzle, not suitable for abrasive filaments", null),
                ("Hardened Steel Nozzle", "Generic", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Generic hardened steel nozzle for abrasive filaments", null),
                ("Stainless Steel Nozzle", "Generic", 300, NozzleType.StainlessSteel, NozzleInterfaceType.V6, "Generic stainless steel nozzle", null),

                // Generic nozzles for Volcano interface (extended melt zone)
                ("Volcano Brass Nozzle", "Generic", 300, NozzleType.Brass, NozzleInterfaceType.Volcano, "Generic Volcano-length brass nozzle for high flow", null),
                ("Volcano Hardened Steel Nozzle", "Generic", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Volcano, "Generic Volcano-length hardened steel nozzle", null),

                // Slice Engineering (V6 interface)
                ("Vanadium", "Slice Engineering", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Extreme wear resistance", "https://www.sliceengineering.com/products/vanadium-nozzle"),
                ("BridgeMaster", "Slice Engineering", 300, NozzleType.Brass, NozzleInterfaceType.V6, "Optimized for bridging", "https://www.sliceengineering.com/products/bridgemaster-nozzle"),
                ("GammaMaster", "Slice Engineering", 300, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Precision nozzle", "https://www.sliceengineering.com/products/gammamaster-nozzle"),

                // West3D
                ("Undertaker", "West3D", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "High flow hardened nozzle", "https://west3d.com/products/undertaker-nozzle"),
                ("Undertaker Volcano", "West3D", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Volcano, "Volcano-style Undertaker", "https://west3d.com/products/undertaker-volcano-nozzle"),

                // E3D - V6/Volcano threaded nozzles
                ("V6 Brass", "E3D", 300, NozzleType.Brass, NozzleInterfaceType.V6, "Standard brass nozzle for V6 hotends, not for abrasives", "https://e3d-online.com/products/v6-brass-nozzle"),
                ("V6 Hardened Steel", "E3D", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Abrasion resistant nozzle for V6 hotends", "https://e3d-online.com/products/v6-hardened-steel-nozzle"),
                ("V6 Plated Copper", "E3D", 500, NozzleType.Brass, NozzleInterfaceType.V6, "Nickel-plated copper for excellent thermal conductivity", "https://e3d-online.com/products/v6-plated-copper-nozzle"),
                ("Volcano Brass", "E3D", 300, NozzleType.Brass, NozzleInterfaceType.Volcano, "Extended melt zone brass nozzle for high flow", "https://e3d-online.com/products/volcano-nozzle"),
                ("Volcano Hardened Steel", "E3D", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Volcano, "Volcano nozzle for abrasive filaments", "https://e3d-online.com/products/volcano-hardened-steel-nozzle"),
                ("NozzleX", "E3D", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "WS2 coated hardened steel, excellent release and wear resistance", "https://e3d-online.com/products/nozzle-x"),
                ("NozzleX Volcano", "E3D", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Volcano, "NozzleX coating on Volcano-length nozzle", "https://e3d-online.com/products/nozzle-x-volcano"),

                // E3D - Revo quick-swap nozzles (RapidChange system)
                ("Revo Brass", "E3D", 300, NozzleType.Brass, NozzleInterfaceType.Revo, "Quick-swap brass nozzle for Revo hotends", "https://e3d-online.com/products/revo-nozzle"),
                ("Revo High Flow", "E3D", 300, NozzleType.Brass, NozzleInterfaceType.Revo, "Revo nozzle with extended melt zone for faster printing", "https://e3d-online.com/products/revo-high-flow-nozzle"),
                ("Revo ObXidian", "E3D", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Revo, "Hardened Revo nozzle for abrasive materials like CF and GF", "https://e3d-online.com/products/revo-obxidian-nozzle"),
                ("Revo Micro", "E3D", 250, NozzleType.Brass, NozzleInterfaceType.Revo, "Compact Revo nozzle for Revo Micro hotend", "https://e3d-online.com/products/revo-micro-nozzle"),

                // TriangleLabs - Hardened nozzles
                ("ZS Nozzle", "TriangleLabs", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Hardened steel V6-compatible nozzle", "https://www.aliexpress.com/item/1005001347220543.html"),
                ("ZS Volcano", "TriangleLabs", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Volcano, "Hardened steel Volcano-length nozzle", "https://www.aliexpress.com/item/1005001347220543.html"),
                ("Ruby Nozzle", "TriangleLabs", 500, NozzleType.Abrasive, NozzleInterfaceType.V6, "Ruby-tipped nozzle for extreme abrasion resistance", "https://www.aliexpress.com/item/1005001543676594.html"),
                ("Tungsten Carbide", "TriangleLabs", 500, NozzleType.TungstenCarbide, NozzleInterfaceType.V6, "Budget tungsten carbide nozzle", "https://www.aliexpress.com/item/1005003456789012.html"),

                // TriangleLabs - CHC nozzles (CHC uses V6 thread)
                ("CHC Nozzle", "TriangleLabs", 500, NozzleType.Brass, NozzleInterfaceType.V6, "For CHC ceramic heater hotends", "https://www.aliexpress.com/item/1005004566533274.html"),
                ("CHC Hardened", "TriangleLabs", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Hardened CHC nozzle for abrasives", "https://www.aliexpress.com/item/1005004566533274.html"),

                // TriangleLabs - Standard nozzles
                ("V6 Brass", "TriangleLabs", 300, NozzleType.Brass, NozzleInterfaceType.V6, "Budget brass V6-compatible nozzle", "https://www.aliexpress.com/item/32851848033.html"),
                ("V6 Hardened Steel", "TriangleLabs", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Budget hardened steel V6 nozzle", "https://www.aliexpress.com/item/32851848033.html"),
                ("Volcano Brass", "TriangleLabs", 300, NozzleType.Brass, NozzleInterfaceType.Volcano, "Budget brass Volcano nozzle", "https://www.aliexpress.com/item/32851848033.html"),
                ("Volcano Hardened Steel", "TriangleLabs", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Volcano, "Budget hardened Volcano nozzle", "https://www.aliexpress.com/item/32851848033.html"),

                // Phaetus - Dragon/V6 compatible nozzles
                ("PS Nozzle", "Phaetus", 300, NozzleType.StainlessSteel, NozzleInterfaceType.V6, "Plated steel nozzle, non-stick surface", "https://www.phaetus.com/products/ps-nozzle"),
                ("Hardened Steel", "Phaetus", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Hardened steel for abrasive filaments", "https://www.phaetus.com/products/hardened-steel-nozzle"),
                ("Tungsten Carbide", "Phaetus", 500, NozzleType.TungstenCarbide, NozzleInterfaceType.V6, "Maximum wear resistance for highly abrasive materials", "https://www.phaetus.com/products/tungsten-carbide-nozzle"),
                ("Brass Nozzle", "Phaetus", 300, NozzleType.Brass, NozzleInterfaceType.V6, "Standard brass nozzle for Dragon/V6 hotends", "https://www.phaetus.com/products/brass-nozzle"),

                // Phaetus - Rapido-specific nozzles (Rapido uses V6 thread)
                ("Rapido Brass", "Phaetus", 300, NozzleType.Brass, NozzleInterfaceType.V6, "Brass nozzle for Rapido hotends", "https://www.phaetus.com/products/rapido-nozzle"),
                ("Rapido Hardened Steel", "Phaetus", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Hardened steel for Rapido with abrasives", "https://www.phaetus.com/products/rapido-hardened-nozzle"),
                ("Rapido Tungsten Carbide", "Phaetus", 500, NozzleType.TungstenCarbide, NozzleInterfaceType.V6, "Maximum wear resistance Rapido nozzle", "https://www.phaetus.com/products/rapido-tc-nozzle"),

                // Bondtech - CHT (Clone Hotend Technology) high flow nozzles
                ("CHT Brass", "Bondtech", 300, NozzleType.Brass, NozzleInterfaceType.V6, "High flow brass nozzle with multi-channel design", "https://www.bondtech.se/product/bondtech-cht-nozzle/"),
                ("CHT Coated", "Bondtech", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "CHT with hardened coating for abrasive filaments", "https://www.bondtech.se/product/bondtech-cht-coated-nozzle/"),
                ("CHT BiMetal", "Bondtech", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Copper core with hardened steel tip for best of both worlds", "https://www.bondtech.se/product/bondtech-cht-bimetal-nozzle/"),
                ("CHT Volcano", "Bondtech", 300, NozzleType.Brass, NozzleInterfaceType.Volcano, "CHT design in Volcano length for maximum flow", "https://www.bondtech.se/product/bondtech-cht-volcano-nozzle/"),
                ("CHT Volcano Coated", "Bondtech", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Volcano, "Coated CHT Volcano for abrasives", "https://www.bondtech.se/product/bondtech-cht-volcano-coated-nozzle/"),

                // Bambu Lab - Model-specific nozzles (proprietary quick-swap system)
                ("Bambu Brass Nozzle", "Bambu Lab", 300, NozzleType.Brass, NozzleInterfaceType.Proprietary, "Standard brass nozzle for Bambu Lab printers, quick-swap compatible", "https://bambulab.com"),
                ("Bambu Hardened Steel Nozzle", "Bambu Lab", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Proprietary, "Hardened steel nozzle for abrasive filaments on Bambu printers", "https://bambulab.com"),
                ("Bambu Stainless Steel Nozzle", "Bambu Lab", 300, NozzleType.StainlessSteel, NozzleInterfaceType.Proprietary, "Stainless steel nozzle for corrosion resistance", "https://bambulab.com"),

                // Prusa - Model-specific nozzles
                ("Prusa V6 Brass Nozzle", "Prusa", 300, NozzleType.Brass, NozzleInterfaceType.V6, "Stock brass nozzle for MK3S and earlier models", "https://www.prusa3d.com"),
                ("Prusa Hardened Steel Nozzle", "Prusa", 500, NozzleType.HardenedSteel, NozzleInterfaceType.V6, "Hardened steel nozzle for MK3S/MK4 with abrasives", "https://www.prusa3d.com"),
                ("Nextruder Brass Nozzle", "Prusa", 300, NozzleType.Brass, NozzleInterfaceType.Proprietary, "Quick-swap brass nozzle for Nextruder toolhead", "https://www.prusa3d.com"),
                ("Nextruder Hardened Steel Nozzle", "Prusa", 500, NozzleType.HardenedSteel, NozzleInterfaceType.Proprietary, "Quick-swap hardened steel for Nextruder with abrasives", "https://www.prusa3d.com"),
                ("Nextruder High Flow Nozzle", "Prusa", 300, NozzleType.Brass, NozzleInterfaceType.Proprietary, "High flow nozzle for Nextruder, extended melt zone", "https://www.prusa3d.com"),
            };

            foreach ((string name, string mfg, int maxTemp, NozzleType nozzleType, NozzleInterfaceType nozzleInterface, string desc, string? url) in nozzleSeeds)
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
                        NozzleType = nozzleType,
                        NozzleInterface = nozzleInterface,
                        Description = desc,
                        Url = url
                    });
                }
            }

            await _context.SaveChangesAsync();

            // ===== TOOLHEAD DEFAULT COMPONENT MAPPINGS =====
            // This uses NAME-BASED mapping so it can be externalized to JSON/YAML files.
            // Format: (ToolheadName, ManufacturerName, DefaultHotendName, DefaultExtruderName, DefaultNozzleName)
            // Use null to indicate no default for that component category.
            var toolheadDefaultMappings = new (string ToolheadName, string ManufacturerName, string? DefaultHotend, string? DefaultExtruder, string? DefaultNozzle)[]
            {
                // Prusa toolheads
                ("MK3S Toolhead", "Prusa", "MK3S Hotend", "MK3S Extruder", "Prusa V6 Brass Nozzle"),
                ("Mini Toolhead", "Prusa", "Mini Hotend", "Mini Extruder", "Prusa V6 Brass Nozzle"),
                ("Nextruder", "Prusa", "Nextruder Hotend", "Nextruder Extruder", "Nextruder Brass Nozzle"),
                ("XL Toolhead", "Prusa", "XL Hotend", "Nextruder Extruder", "Nextruder Brass Nozzle"),

                // Bambu Lab toolheads (proprietary system)
                ("X1 Series Toolhead", "Bambu Lab", "X1 Hotend", "X1 Extruder", "Bambu Brass Nozzle"),
                ("P1 Series Toolhead", "Bambu Lab", "P1 Hotend", "P1 Extruder", "Bambu Brass Nozzle"),
                ("A1 Toolhead", "Bambu Lab", "A1 Hotend", "A1 Extruder", "Bambu Brass Nozzle"),
                ("A1 Mini Toolhead", "Bambu Lab", "A1 Mini Hotend", "A1 Mini Extruder", "Bambu Brass Nozzle"),

                // Creality toolheads
                ("Sprite Pro Toolhead", "Creality", "Sprite Pro Hotend", "Sprite Pro Extruder", "Brass Nozzle"),
                ("K1 Toolhead", "Creality", "K1 Hotend", "K1 Extruder", "Brass Nozzle"),

                // Elegoo toolheads - Centauri Carbon uses hardened steel by default
                ("Centauri Toolhead", "Elegoo", "Centauri Hotend", "Centauri Extruder", "Hardened Steel Nozzle"),

                // Sovol toolheads
                ("SV08 Toolhead", "Sovol", "SV08 Hotend", "SV08 Extruder", "Brass Nozzle"),

                // Qidi toolheads
                ("X-Plus 4 Toolhead", "Qidi", "X-Plus 4 Hotend", "X-Plus 4 Extruder", "Brass Nozzle"),

                // Voron/Community toolheads - V6-compatible, default to Generic Brass
                ("StealthBurner", "Voron", null, null, "Brass Nozzle"),
                ("Mini StealthBurner", "Voron", null, null, "Brass Nozzle"),
                ("DragonBurner", "PrintersForAnts", null, null, "Brass Nozzle"),
                ("Xol", "Community", null, null, "Brass Nozzle"),
                ("Archetype", "Community", null, null, "Brass Nozzle"),

                // E3D toolheads
                ("Hemera Toolhead", "E3D", "Hemera", "Hemera Extruder", "V6 Brass"),
                ("Revo Toolhead", "E3D", "Revo Six", null, "Revo Brass"),

                // Ratrig EVA
                ("EVA", "Ratrig", null, null, "Brass Nozzle"),
                ("EVA 3", "Ratrig", null, null, "Brass Nozzle"),
            };

            // Resolve names to IDs and update toolheads
            await ResolveToolheadDefaultComponentsAsync(toolheadDefaultMappings, mfgLookup);

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

    /// <summary>
    /// Seeds default PrinterModelToolhead entries for each printer model, linking them to model-specific
    /// hotends, extruders, toolheads, and nozzles. Uses a data-driven mapping to assign the correct
    /// components for each printer model, falling back to OEM components when specific ones aren't defined.
    /// </summary>
    private async Task SeedPrinterModelToolheadsAsync()
    {
        try
        {
            // Get all printer models that don't have toolhead templates yet
            List<PrinterModel> modelsWithoutToolheads = await _context.PrinterModels
                .Include(pm => pm.Toolheads)
                .Include(pm => pm.Manufacturer)
                .Where(pm => !pm.Toolheads.Any())
                .ToListAsync();

            if (modelsWithoutToolheads.Count == 0)
            {
                _logger.LogInformation("[DB] All printer models already have toolhead templates");
                return;
            }

            // ===== PRINTER MODEL → COMPONENT MAPPING =====
            // Maps printer model name patterns to specific component names
            // Format: (ModelPattern, ToolheadName, HotendName, ExtruderName, NozzleName)
            // Use null to fall back to OEM component for that category
            var printerComponentMappings = new (string ModelPattern, string? Toolhead, string? Hotend, string? Extruder, string? Nozzle)[]
            {
                // ===== PRUSA =====
                ("Prusa MK3S", "MK3S Toolhead", "MK3S Hotend", "MK3S Extruder", "Prusa V6 Brass Nozzle"),
                ("Prusa MK3.5", "Nextruder", "Nextruder Hotend", "Nextruder Extruder", "Nextruder Brass Nozzle"),
                ("Prusa MK4", "Nextruder", "Nextruder Hotend", "Nextruder Extruder", "Nextruder Brass Nozzle"),
                ("Prusa MK4S", "Nextruder", "Nextruder Hotend", "Nextruder Extruder", "Nextruder Brass Nozzle"),
                ("Prusa MINI", "Mini Toolhead", "Mini Hotend", "Mini Extruder", "Prusa V6 Brass Nozzle"),
                ("Prusa CORE One", "Nextruder", "CORE One Hotend", "Nextruder Extruder", "Nextruder Brass Nozzle"),
                ("Prusa XL", "XL Toolhead", "XL Hotend", "Nextruder Extruder", "Nextruder Brass Nozzle"),

                // ===== BAMBU LAB =====
                // X1 Series (X1, X1C, X1E)
                ("Bambu X1", "X1 Series Toolhead", "X1 Series Hotend", "X1 Series Extruder", "Bambu Brass Nozzle"),
                ("Bambu Lab X1", "X1 Series Toolhead", "X1 Series Hotend", "X1 Series Extruder", "Bambu Brass Nozzle"),

                // P1 Series (P1P, P1S)
                ("Bambu P1", "P1 Series Toolhead", "P1 Series Hotend", "P1 Series Extruder", "Bambu Brass Nozzle"),
                ("Bambu Lab P1", "P1 Series Toolhead", "P1 Series Hotend", "P1 Series Extruder", "Bambu Brass Nozzle"),

                // A1 Series
                ("Bambu A1 Mini", "A1 Mini Toolhead", "A1 Mini Hotend", "A1 Mini Extruder", "Bambu Brass Nozzle"),
                ("Bambu Lab A1 Mini", "A1 Mini Toolhead", "A1 Mini Hotend", "A1 Mini Extruder", "Bambu Brass Nozzle"),
                ("Bambu A1", "A1 Toolhead", "A1 Hotend", "A1 Extruder", "Bambu Brass Nozzle"),
                ("Bambu Lab A1", "A1 Toolhead", "A1 Hotend", "A1 Extruder", "Bambu Brass Nozzle"),

                // ===== CREALITY =====
                ("Creality K1 Max", "K1 Toolhead", "K1 Max Hotend", "K1 Extruder", null),
                ("Creality K1", "K1 Toolhead", "K1 Hotend", "K1 Extruder", null),
                ("Creality Ender-3 S1", "Sprite Pro Toolhead", "Sprite Hotend", "Sprite Pro", null),

                // ===== QIDI =====
                ("QIDI X-Plus 4", "X-Plus 4 Toolhead", "X-Plus 4 Hotend", "X-Plus 4 Extruder", null),
                ("Qidi Q1 Pro", null, "Q1 Pro Hotend", null, null),

                // ===== ELEGOO =====
                ("Elegoo Centauri", "Centauri Toolhead", "Centauri Hotend", "Centauri Extruder", null),

                // ===== SOVOL =====
                ("Sovol SV08", "SV08 Toolhead", "SV08 Hotend", "SV08 Extruder", null),

                // ===== VORON =====
                // V0 series uses Mini StealthBurner
                ("Voron 0", "Mini StealthBurner", "Dragon Standard Flow", "Clockwork 2", null),

                // V2.4 series uses StealthBurner
                ("Voron 2.4", "StealthBurner", "Dragon Standard Flow", "Clockwork 2", null),

                // Trident uses StealthBurner
                ("Voron Trident", "StealthBurner", "Dragon Standard Flow", "Clockwork 2", null),

                // Switchwire uses StealthBurner
                ("Voron Switchwire", "StealthBurner", "Dragon Standard Flow", "Clockwork 2", null),

                // ===== PRINTERS FOR ANTS =====
                ("SaladFork", "DragonBurner", "Dragon Standard Flow", "LGX Lite", null),
                ("Micron", "AntHead", "Dragon Standard Flow", "LGX Lite", null),

                // ===== RATRIG =====
                ("RatRig V-Core 3", "EVA 3", "Dragon Standard Flow", "BMG", null),
                ("RatRig V-Core 4", "EVA 3", "Rapido", "LGX Lite", null),
            };

            // Build component lookups by name (across all manufacturers)
            // Load data first with Include(), then group in memory to handle duplicates
            List<HotendModelDefinition> allHotends = await _context.HotendModelDefinitions
                .Include(h => h.Manufacturer)
                .ToListAsync();
            Dictionary<string, Guid> hotendLookup = allHotends
                .GroupBy(h => $"{h.Manufacturer?.Name ?? "Unknown"}|{h.Name}")
                .ToDictionary(g => g.Key, g => g.First().Id);

            List<ExtruderModelDefinition> allExtruders = await _context.ExtruderModelDefinitions
                .Include(e => e.Manufacturer)
                .ToListAsync();
            Dictionary<string, Guid> extruderLookup = allExtruders
                .GroupBy(e => $"{e.Manufacturer?.Name ?? "Unknown"}|{e.Name}")
                .ToDictionary(g => g.Key, g => g.First().Id);

            List<ToolheadModelDefinition> allToolheads = await _context.ToolheadModelDefinitions
                .Include(t => t.Manufacturer)
                .ToListAsync();
            Dictionary<string, Guid> toolheadLookup = allToolheads
                .GroupBy(t => $"{t.Manufacturer?.Name ?? "Unknown"}|{t.Name}")
                .ToDictionary(g => g.Key, g => g.First().Id);

            List<NozzleModelDefinition> allNozzles = await _context.NozzleModelDefinitions
                .Include(n => n.Manufacturer)
                .ToListAsync();
            Dictionary<string, Guid> nozzleLookup = allNozzles
                .GroupBy(n => $"{n.Manufacturer?.Name ?? "Unknown"}|{n.Name}")
                .ToDictionary(g => g.Key, g => g.First().Id);

            // Also build simple name lookups for easier matching (first match wins)
            Dictionary<string, Guid> hotendByName = await _context.HotendModelDefinitions
                .GroupBy(h => h.Name)
                .Select(g => g.First())
                .ToDictionaryAsync(h => h.Name, h => h.Id);
            Dictionary<string, Guid> extruderByName = await _context.ExtruderModelDefinitions
                .GroupBy(e => e.Name)
                .Select(g => g.First())
                .ToDictionaryAsync(e => e.Name, e => e.Id);
            Dictionary<string, Guid> toolheadByName = await _context.ToolheadModelDefinitions
                .GroupBy(t => t.Name)
                .Select(g => g.First())
                .ToDictionaryAsync(t => t.Name, t => t.Id);
            Dictionary<string, Guid> nozzleByName = await _context.NozzleModelDefinitions
                .GroupBy(n => n.Name)
                .Select(g => g.First())
                .ToDictionaryAsync(n => n.Name, n => n.Id);

            // Get "Unknown" manufacturer ID for fallback
            Guid unknownMfgId = await _context.Manufacturers
                .Where(m => m.Name == "Unknown")
                .Select(m => m.Id)
                .FirstOrDefaultAsync();

            int seededCount = 0;

            // Track newly created model-specific components to avoid re-querying
            Dictionary<string, Guid> createdHotends = [];
            Dictionary<string, Guid> createdExtruders = [];
            Dictionary<string, Guid> createdToolheads = [];
            Dictionary<string, Guid> createdNozzles = [];

            foreach (PrinterModel model in modelsWithoutToolheads)
            {
                int numExtruders = model.NumberOfExtruders;
                string modelName = model.Name;
                string mfgName = model.Manufacturer?.Name ?? "Unknown";
                Guid mfgId = model.ManufacturerId;

                // Find the best matching component mapping for this printer model
                (string? toolheadName, string? hotendName, string? extruderName, string? nozzleName) = (null, null, null, null);
                foreach ((string pattern, string? th, string? hot, string? ext, string? noz) in printerComponentMappings)
                {
                    if (modelName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        toolheadName = th;
                        hotendName = hot;
                        extruderName = ext;
                        nozzleName = noz;
                        break;
                    }
                }

#pragma warning disable S1871 // Two branches in a conditional structure should not have exactly the same implementation

                // Resolve component IDs: Try manufacturer-specific first, then by name only
                Guid? hotendId = null;
                if (hotendName != null)
                {
                    if (hotendLookup.TryGetValue($"{mfgName}|{hotendName}", out Guid id))
                    {
                        hotendId = id;
                    }
                    else if (hotendByName.TryGetValue(hotendName, out id))
                    {
                        hotendId = id;
                    }
                }

                Guid? extruderId = null;
                if (extruderName != null)
                {
                    if (extruderLookup.TryGetValue($"{mfgName}|{extruderName}", out Guid id))
                    {
                        extruderId = id;
                    }
                    else if (extruderByName.TryGetValue(extruderName, out id))
                    {
                        extruderId = id;
                    }
                }

                Guid? toolheadId = null;
                if (toolheadName != null)
                {
                    if (toolheadLookup.TryGetValue($"{mfgName}|{toolheadName}", out Guid id))
                    {
                        toolheadId = id;
                    }
                    else if (toolheadByName.TryGetValue(toolheadName, out id))
                    {
                        toolheadId = id;
                    }
                }

                Guid? nozzleId = null;
                if (nozzleName != null)
                {
                    if (nozzleLookup.TryGetValue($"{mfgName}|{nozzleName}", out Guid id))
                    {
                        nozzleId = id;
                    }
                    else if (nozzleByName.TryGetValue(nozzleName, out id))
                    {
                        nozzleId = id;
                    }
                }

#pragma warning restore S1871 // Two branches in a conditional structure should not have exactly the same implementation

                // Fall back to model-named components if specific ones not found
                // Create "{ModelName} Hotend/Extruder/Toolhead" instead of generic OEM components
                if (hotendId == null)
                {
                    string modelHotendName = $"{modelName} Hotend";
                    if (createdHotends.TryGetValue(modelHotendName, out Guid existingId))
                    {
                        hotendId = existingId;
                    }
                    else if (hotendByName.TryGetValue(modelHotendName, out existingId))
                    {
                        hotendId = existingId;
                        createdHotends[modelHotendName] = existingId;
                    }
                    else
                    {
                        // Create model-specific hotend
                        HotendModelDefinition newHotend = new()
                        {
                            Id = Guid.NewGuid(),
                            Name = modelHotendName,
                            ManufacturerId = mfgId,
                            MaxTemp = 300,
                            IsHighFlow = false,
                            Description = $"Stock hotend for {modelName}"
                        };
                        _context.HotendModelDefinitions.Add(newHotend);
                        hotendId = newHotend.Id;
                        createdHotends[modelHotendName] = newHotend.Id;
                        hotendByName[modelHotendName] = newHotend.Id;
                    }
                }

                if (extruderId == null)
                {
                    string modelExtruderName = $"{modelName} Extruder";
                    if (createdExtruders.TryGetValue(modelExtruderName, out Guid existingId))
                    {
                        extruderId = existingId;
                    }
                    else if (extruderByName.TryGetValue(modelExtruderName, out existingId))
                    {
                        extruderId = existingId;
                        createdExtruders[modelExtruderName] = existingId;
                    }
                    else
                    {
                        // Create model-specific extruder
                        ExtruderModelDefinition newExtruder = new()
                        {
                            Id = Guid.NewGuid(),
                            Name = modelExtruderName,
                            ManufacturerId = mfgId,
                            GearRatio = "3:1",
                            IsDirectDrive = true,
                            Description = $"Stock extruder for {modelName}"
                        };
                        _context.ExtruderModelDefinitions.Add(newExtruder);
                        extruderId = newExtruder.Id;
                        createdExtruders[modelExtruderName] = newExtruder.Id;
                        extruderByName[modelExtruderName] = newExtruder.Id;
                    }
                }

                if (toolheadId == null)
                {
                    string modelToolheadName = $"{modelName} Toolhead";
                    if (createdToolheads.TryGetValue(modelToolheadName, out Guid existingId))
                    {
                        toolheadId = existingId;
                    }
                    else if (toolheadByName.TryGetValue(modelToolheadName, out existingId))
                    {
                        toolheadId = existingId;
                        createdToolheads[modelToolheadName] = existingId;
                    }
                    else
                    {
                        // Create model-specific toolhead
                        ToolheadModelDefinition newToolhead = new()
                        {
                            Id = Guid.NewGuid(),
                            Name = modelToolheadName,
                            ManufacturerId = mfgId,
                            Description = $"Stock toolhead assembly for {modelName}"
                        };
                        _context.ToolheadModelDefinitions.Add(newToolhead);
                        toolheadId = newToolhead.Id;
                        createdToolheads[modelToolheadName] = newToolhead.Id;
                        toolheadByName[modelToolheadName] = newToolhead.Id;
                    }
                }

                // Create model-specific nozzle if none found
                if (nozzleId == null)
                {
                    string modelNozzleName = $"{modelName} Nozzle";
                    if (createdNozzles.TryGetValue(modelNozzleName, out Guid existingId))
                    {
                        nozzleId = existingId;
                    }
                    else if (nozzleByName.TryGetValue(modelNozzleName, out existingId))
                    {
                        nozzleId = existingId;
                        createdNozzles[modelNozzleName] = existingId;
                    }
                    else
                    {
                        // Create model-specific nozzle
                        NozzleModelDefinition newNozzle = new()
                        {
                            Id = Guid.NewGuid(),
                            Name = modelNozzleName,
                            ManufacturerId = mfgId,
                            MaxTemp = 300,
                            NozzleType = NozzleType.Brass,  // Default to brass for stock nozzles
                            Description = $"Stock nozzle for {modelName}"
                        };
                        _context.NozzleModelDefinitions.Add(newNozzle);
                        nozzleId = newNozzle.Id;
                        createdNozzles[modelNozzleName] = newNozzle.Id;
                        nozzleByName[modelNozzleName] = newNozzle.Id;
                    }
                }

                // Create toolhead templates for each extruder
                for (int i = 0; i < numExtruders; i++)
                {
                    PrinterModelToolhead toolhead = new()
                    {
                        Id = Guid.NewGuid(),
                        PrinterModelId = model.Id,
                        Name = numExtruders == 1 ? "Primary" : $"Extruder {i + 1}",
                        Index = i,
                        IsPrimary = i == 0,
                        MaxHotendTemp = model.MaxBedTemp,  // Use bed temp as conservative proxy; user can override
                        MaxFlowRate = null,    // Will be populated when user specifies
                        HotendModelId = hotendId,
                        ExtruderModelId = extruderId,
                        ToolheadModelDefId = toolheadId,
                        NozzleModelId = nozzleId  // Nozzle diameter is derived from the nozzle model
                    };

                    _context.PrinterModelToolheads.Add(toolhead);
                    seededCount++;
                }
            }

            if (seededCount > 0)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("[DB] Seeded {SeededCount} PrinterModelToolhead templates for {ModelCount} printer models with model-specific components", seededCount.ToString(), modelsWithoutToolheads.Count.ToString());
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogWarning(ex, "Ignored unique constraint violation while seeding printer model toolheads; another process probably inserted the same records.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] Error seeding printer model toolheads");
            throw;
        }
    }

    private async Task SeedAuthenticationDataAsync()
    {
        ArgumentNullException.ThrowIfNull(_context);
        try
        {
            _ = await _context.UserActions.AnyAsync();
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
            if (!await _context.UserActions.AnyAsync(a => a.Name == action.Name))
            {
                _ = _context.UserActions.Add(new UserAction
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
            UserAction? adminAction = await _context.UserActions.FirstOrDefaultAsync(a => a.Name == "admin");
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
                UserAction? action = await _context.UserActions.FirstOrDefaultAsync(a => a.Name == actionName);
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

    /// <summary>
    /// Resolves name-based toolhead default component mappings to database IDs and updates toolhead records.
    /// This design allows the mapping data to be externalized to JSON/YAML files in the future.
    /// </summary>
    /// <param name="mappings">Array of (ToolheadName, ManufacturerName, DefaultHotend, DefaultExtruder, DefaultNozzle) tuples</param>
    /// <param name="mfgLookup">Manufacturer name to ID lookup dictionary</param>
    private async Task ResolveToolheadDefaultComponentsAsync(
        (string ToolheadName, string ManufacturerName, string? DefaultHotend, string? DefaultExtruder, string? DefaultNozzle)[] mappings,
        Dictionary<string, Guid> mfgLookup)
    {
        if (mappings.Length == 0)
        {
            return;
        }

        // Build (ManufacturerId, Name) to ID lookup dictionaries for all component types
        // Using composite key allows same name from different manufacturers (e.g., "Brass Nozzle" from Generic vs Phaetus)
        Dictionary<(Guid MfgId, string Name), Guid> hotendLookup = await _context.HotendModelDefinitions
            .AsNoTracking()
            .ToDictionaryAsync(h => (h.ManufacturerId, h.Name), h => h.Id);

        Dictionary<(Guid MfgId, string Name), Guid> extruderLookup = await _context.ExtruderModelDefinitions
            .AsNoTracking()
            .ToDictionaryAsync(e => (e.ManufacturerId, e.Name), e => e.Id);

        Dictionary<(Guid MfgId, string Name), Guid> nozzleLookup = await _context.NozzleModelDefinitions
            .AsNoTracking()
            .ToDictionaryAsync(n => (n.ManufacturerId, n.Name), n => n.Id);

        int updated = 0;
        foreach ((string toolheadName, string mfgName, string? hotendName, string? extruderName, string? nozzleName) in mappings)
        {
            if (!mfgLookup.TryGetValue(mfgName, out Guid mfgId))
            {
                _logger.LogWarning("[DB] Skipping toolhead default mapping for '{Toolhead}': manufacturer '{Mfg}' not found", toolheadName, mfgName);
                continue;
            }

            // Find the toolhead by name and manufacturer
            ToolheadModelDefinition? toolhead = await _context.ToolheadModelDefinitions
                .FirstOrDefaultAsync(t => t.Name == toolheadName && t.ManufacturerId == mfgId);

            if (toolhead == null)
            {
                _logger.LogDebug("[DB] Toolhead '{Toolhead}' by '{Mfg}' not found, skipping default component mapping", toolheadName, mfgName);
                continue;
            }

            // Skip if already has defaults set (don't overwrite existing data)
            if (toolhead.DefaultHotendId != null || toolhead.DefaultExtruderId != null || toolhead.DefaultNozzleId != null)
            {
                continue;
            }

            // Resolve component names to IDs using composite key (ManufacturerId, Name)
            Guid? hotendId = hotendName != null && hotendLookup.TryGetValue((mfgId, hotendName), out Guid hid) ? hid : null;
            Guid? extruderId = extruderName != null && extruderLookup.TryGetValue((mfgId, extruderName), out Guid eid) ? eid : null;
            Guid? nozzleId = nozzleName != null && nozzleLookup.TryGetValue((mfgId, nozzleName), out Guid nid) ? nid : null;

            // Log warnings for unresolved references
            if (hotendName != null && hotendId == null)
            {
                _logger.LogWarning("[DB] Default hotend '{Hotend}' for toolhead '{Toolhead}' not found", hotendName, toolheadName);
            }

            if (extruderName != null && extruderId == null)
            {
                _logger.LogWarning("[DB] Default extruder '{Extruder}' for toolhead '{Toolhead}' not found", extruderName, toolheadName);
            }

            if (nozzleName != null && nozzleId == null)
            {
                _logger.LogWarning("[DB] Default nozzle '{Nozzle}' for toolhead '{Toolhead}' not found", nozzleName, toolheadName);
            }

            // Update toolhead with resolved IDs (even if some are null)
            if (hotendId != null || extruderId != null || nozzleId != null)
            {
                toolhead.DefaultHotendId = hotendId;
                toolhead.DefaultExtruderId = extruderId;
                toolhead.DefaultNozzleId = nozzleId;
                updated++;
            }
        }

        if (updated > 0)
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation("[DB] Updated {Updated} toolheads with default component associations", updated.ToString());
        }
    }
}

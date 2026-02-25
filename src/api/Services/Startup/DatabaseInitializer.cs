using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Web.Api.Infrastructure.Normalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Startup;

/// <summary>
/// Handles database initialization with retry logic for resilient startup
/// </summary>
public class DatabaseInitializer(AppDbContext context, ILogger<DatabaseInitializer> logger, IDataSeedService dataSeedService) : IDatabaseInitializer
{
    private readonly AppDbContext _context = context;
    private readonly ILogger<DatabaseInitializer> _logger = logger;
    private readonly IDataSeedService _dataSeedService = dataSeedService;

    /// <summary>
    /// Initialize database with retry logic for container startup scenarios
    /// </summary>
    /// <param name="dbProvider">The database provider name (e.g., "Sqlite", "SqlServer", "Postgres").</param>
    /// <param name="maxRetries">Maximum number of retry attempts for database connection.</param>
    /// <param name="delaySeconds">Delay in seconds between retry attempts.</param>
    public virtual async Task InitializeAsync(string dbProvider, int maxRetries = 10, int delaySeconds = 5)
    {
        _logger.LogInformation("[DB] Starting database initialization for provider: {DbProvider}", dbProvider);

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
                            _logger.LogWarning(colEx, "[DB] Non-fatal: automatic shadow column/index verification failed: {ColExMessage}", colEx.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DB] EnsureCreated failed: {Message}. Attempting manual schema initialization for SQLite.", ex.Message);

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
                                    _logger.LogWarning(colEx, "[DB] Non-fatal (fallback path): automatic shadow column/index verification failed: {ColExMessage}", colEx.Message);
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
                        _logger.LogError(inner, "[DB] Manual fallback schema initialization failed. Will retry (attempt {Value0})", retryCount + 1);
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
                        _logger.LogWarning(sqlEx, "[DB] Seed attempt {SeedAttempt}/{SeedMaxAttempts} failed due to missing table (SQLite); retrying in 2s...", seedAttempt, seedMaxAttempts);
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                    catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P01" && seedAttempt < seedMaxAttempts)
                    {
                        // PostgreSQL error 42P01 = relation does not exist (table/view not found)
                        seedAttempt++;
                        _logger.LogWarning(pgEx, "[DB] Seed attempt {SeedAttempt}/{SeedMaxAttempts} failed due to missing relation (PostgreSQL); retrying in 2s...", seedAttempt, seedMaxAttempts);
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
                        "[DB] Database initialization attempt {RetryCount}/{MaxRetries} failed: {Message}. Retrying in {DelaySeconds} seconds...", retryCount, maxRetries, ex.Message, delaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
                else
                {
                    _logger.LogError(
                        ex,
                        "[DB] Database initialization failed after {MaxRetries} attempts. Last error: {Message}", maxRetries, ex.Message);
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
            await _dataSeedService.SeedMaintenanceSchedulesAsync();

            // Note: Toolhead default components are now resolved within SeedComponentModelsAsync
            // via ResolveToolheadDefaultComponentsFromYamlAsync which reads defaults from toolheads.yaml

            // Fallback: fill in any printer models not covered by YAML with hardcoded mappings
            // await SeedPrinterModelToolheadsAsync();
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
                bool rootExists = await _context.Set<FolderNode>().AnyAsync(f => f.Path == folderPath && f.FolderType == folderType);
                if (!rootExists)
                {
                    _context.Set<FolderNode>().Add(new FolderNode
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
            _logger.LogWarning(ex, "[DB] Database connection validation failed: {Message}", ex.Message);
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
                    _logger.LogInformation("[DB] Added missing column {Table}.{Column}", table, column);
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
                    _logger.LogDebug("[DB] Backfilled {Rows} rows for {Table}.NameLowered", rows, table);
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
                    _logger.LogInformation("[DB] Ensured index: {Description}", description);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DB] Failed to ensure index {Description}: {Message}", description, ex.Message);
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

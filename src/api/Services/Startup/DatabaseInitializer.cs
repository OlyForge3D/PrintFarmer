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

                // Seed all data (authentication, catalog, filament types)
                // Some providers (or test SQLite setups) may require a brief moment after migration
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
            await _dataSeedService.SeedNozzleMaterialsAsync();  // Must come before component models so nozzle seeding can resolve materials
            await _dataSeedService.SeedComponentModelsAsync();  // Must come before printer models so toolhead defaults exist
            await _dataSeedService.SeedPrinterModelsAsync();    // Now includes toolhead seeding
            await _dataSeedService.SeedMaintenanceTasksAsync(); // Seed global maintenance task catalog
            await _dataSeedService.SeedMaintenanceComponentsAsync(); // Seed starter parts inventory with categories
            await _dataSeedService.SeedMaintenancePlansAsync(); // Seed default maintenance plans (must come after tasks)

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
        catch (Microsoft.Data.Sqlite.SqliteException sqlEx) when (sqlEx.Message?.Contains("no such table", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Core tables aren't visible to this connection yet (e.g. a brief window after
            // migration). This is the same transient condition InitializeAsync's outer seed
            // retry loop already tolerates for SeedAllAsync as a whole, so log the reason and
            // propagate rather than silently skipping the entire authentication seed here.
            _logger.LogWarning(sqlEx, "[DB] Authentication seed probe failed: core tables not yet visible (SQLite); propagating so startup retry logic can recover");
            throw;
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P01")
        {
            // PostgreSQL error 42P01 = relation does not exist (table/view not found).
            _logger.LogWarning(pgEx, "[DB] Authentication seed probe failed: core relation not yet visible (PostgreSQL); propagating so startup retry logic can recover");
            throw;
        }
        catch (Exception ex)
        {
            // Anything else (connection drop, permissions error, provider quirk, etc.) is not a
            // known-recoverable missing-table condition. Log the reason at error level and
            // propagate so InitializeAsync's retry/failure handling sees it, instead of skipping
            // the entire authentication seed silently.
            _logger.LogError(ex, "[DB] Authentication seed probe failed unexpectedly; failing rather than silently skipping the authentication seed");
            throw;
        }

        const int maxUniqueConstraintAttempts = 3;
        for (int attempt = 1; attempt <= maxUniqueConstraintAttempts; attempt++)
        {
            try
            {
                await SeedActionsAsync();
                await SeedResourcesAsync();
                await SeedRolesAsync();

                // Persist the principals before creating permissions so a concurrent
                // initializer can lose the insert race and then reload the committed rows.
                _ = await _context.SaveChangesAsync();

                await SeedRolePermissionsAsync();
                _ = await _context.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Caught on every attempt, including the last: falling through to the explicit
                // failure below (rather than letting the final attempt's exception propagate
                // raw) gives a single, clear, domain-specific failure message regardless of
                // which attempt exhausted the retries.
                _logger.LogWarning(ex, "[DB] Authentication seed insert raced with another initializer; retrying from committed rows (attempt {Attempt}/{MaxAttempts})", attempt, maxUniqueConstraintAttempts);
                _context.ChangeTracker.Clear();
            }
        }

        // Every attempt hit a unique-constraint violation. Roles/permissions are not
        // guaranteed to be fully seeded at this point, so fail loudly instead of returning
        // normally: a silent return here would let InitializeAsync report success even though
        // authentication data may be incomplete.
        _logger.LogError(
            "[DB] Authentication data seed failed after {MaxAttempts} attempts due to repeated unique constraint violations; refusing to report a successful seed.",
            maxUniqueConstraintAttempts);
        throw new InvalidOperationException(
            $"[DB] Authentication data seed failed after {maxUniqueConstraintAttempts} attempts due to repeated unique constraint violations; refusing to report a successful seed.");
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
            new { Name = "admin", DisplayName = "Administer", Description = "Full administrative control" },
            new { Name = "generate", DisplayName = "Generate", Description = "Generate resource outputs" },
            new { Name = "publish", DisplayName = "Publish", Description = "Publish resource outputs" },
            new { Name = "write", DisplayName = "Write", Description = "Create or modify queued resources" },
            new { Name = "start", DisplayName = "Start", Description = "Start queued work" },
            new { Name = "cancel", DisplayName = "Cancel", Description = "Cancel queued work" },
            new { Name = "acknowledge-bed-clear", DisplayName = "Acknowledge Bed Clear", Description = "Acknowledge that a job-specific printer bed is clear" },
            new { Name = "reconcile", DisplayName = "Reconcile", Description = "Reconcile uncertain queue state" },
            new { Name = "submit", DisplayName = "Submit", Description = "Submit work for slicing" },
            new { Name = "read-artifact", DisplayName = "Read Artifact", Description = "Read slicing artifact data" },
            new { Name = "promote", DisplayName = "Promote", Description = "Promote slicing artifacts into the G-code library" },
            new { Name = "manage", DisplayName = "Manage", Description = "Manage dispatch configuration" }
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
        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            int? sqliteErrorCode = null;
            int? sqliteExtendedErrorCode = null;
            if (inner is Microsoft.Data.Sqlite.SqliteException sqlite)
            {
                sqliteErrorCode = sqlite.SqliteErrorCode;
                sqliteExtendedErrorCode = sqlite.SqliteExtendedErrorCode;
            }

            string? sqlState = (inner as System.Data.Common.DbException)?.SqlState;
            int? sqlServerErrorNumber = null;
            if (inner.GetType().FullName is "Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException")
            {
                sqlServerErrorNumber = inner.GetType().GetProperty("Number")?.GetValue(inner) as int?;
            }

            int? mySqlErrorNumber = null;
            if (inner.GetType().FullName is "MySqlConnector.MySqlException" or "MySql.Data.MySqlClient.MySqlException")
            {
                mySqlErrorNumber = inner.GetType().GetProperty("Number")?.GetValue(inner) as int?;
            }

            if (MatchesUniqueConstraintViolation(
                sqlState,
                sqlServerErrorNumber,
                mySqlErrorNumber,
                sqliteErrorCode,
                sqliteExtendedErrorCode))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool MatchesUniqueConstraintViolation(
        string? sqlState,
        int? sqlServerErrorNumber,
        int? mySqlErrorNumber,
        int? sqliteErrorCode,
        int? sqliteExtendedErrorCode)
        => sqliteExtendedErrorCode is 1555 or 2067
            || string.Equals(sqlState, "23505", StringComparison.Ordinal)
            || sqlServerErrorNumber is 2601 or 2627
            || mySqlErrorNumber == 1062;

    private async Task SeedResourcesAsync()
    {
        var resources = new[]
        {
            new { Name = "printers", DisplayName = "Printers", ResourceType = "printer", Description = "3D printer management" },
            new { Name = "gcode_harvest", DisplayName = "G-code Harvest", ResourceType = "harvest", Description = "G-code file harvesting operations" },
            new { Name = "gcode_library", DisplayName = "G-code Library", ResourceType = "library", Description = "G-code file library management" },
            new { Name = "job_queue", DisplayName = "Print Job Queue (legacy admin overrides)", ResourceType = "queue", Description = "Administrative print-job retry/approval overrides (RetriesController, PrintApprovalsController). Distinct from \"queue\", which gates the calibration/dispatch queue actions that farm_user can hold; job_queue is farm_admin-only by design." },
            new { Name = "slicer_engines", DisplayName = "Slicer Engines (admin management)", ResourceType = "slicer", Description = "Administrative slicer worker/engine management (Slicer host admin endpoints, SlicerHub). Distinct from \"slicing\", which gates the farm_user-reachable slice-submit/artifact/promote actions; slicer_engines is farm_admin-only by design." },
            new { Name = "users", DisplayName = "Users", ResourceType = "system", Description = "User account management" },
            new { Name = "roles", DisplayName = "Roles", ResourceType = "system", Description = "Role and permission management" },
            new { Name = "system_settings", DisplayName = "System Settings", ResourceType = "system", Description = "Application configuration and settings" },
            new { Name = "spoolman", DisplayName = "Spoolman Integration", ResourceType = "integration", Description = "Spoolman filament management integration" },
            new { Name = "network_discovery", DisplayName = "Network Discovery", ResourceType = "system", Description = "Printer network discovery and management" },
            new { Name = "calibration", DisplayName = "Printer Calibration", ResourceType = "calibration", Description = "Printer calibration projects and generation" },
            new { Name = "queue", DisplayName = "Print Queue Operations", ResourceType = "queue", Description = "Farm-user-reachable queue actions: read/write/start/cancel/acknowledge-bed-clear/reconcile (AutoDispatchController, PrintersController, JobQueueAnalyticsController). Distinct from \"job_queue\", which gates farm_admin-only retry/approval overrides." },
            new { Name = "slicing", DisplayName = "Slicing Submission & Artifacts", ResourceType = "slicer", Description = "Farm-user-reachable slicing actions: submit/read-artifact/promote (CalibrationGenerationController, GcodePromotionsController). Distinct from \"slicer_engines\", which gates farm_admin-only slicer worker/engine management." },
            new { Name = "dispatch-settings", DisplayName = "Dispatch Settings", ResourceType = "system", Description = "Dispatch configuration management" },
            new { Name = "obico", DisplayName = "Obico Integration", ResourceType = "integration", Description = "Obico ML failure-detection server management and connectivity probes" },
            new { Name = "catalog", DisplayName = "Catalog", ResourceType = "catalog", Description = "Manufacturer, machine, and material catalog management" },
            new { Name = "quota", DisplayName = "Quota", ResourceType = "system", Description = "User and group print quota management" },
            new { Name = "filament_type", DisplayName = "Filament Type", ResourceType = "catalog", Description = "Filament type and fallback group management" },
            new { Name = "maintenance", DisplayName = "Maintenance", ResourceType = "maintenance", Description = "Printer maintenance components, plans, schedules, and tasks" },
            new { Name = "material_cluster", DisplayName = "Material Cluster", ResourceType = "catalog", Description = "Material cluster management" },
            new { Name = "parts_inventory", DisplayName = "Parts Inventory", ResourceType = "inventory", Description = "Spare parts inventory management" },
            new { Name = "cameras", DisplayName = "Cameras", ResourceType = "system", Description = "Printer camera management" },
            new { Name = "custom_fields", DisplayName = "Custom Fields", ResourceType = "system", Description = "Custom field definitions management" },
            new { Name = "locations", DisplayName = "Locations", ResourceType = "system", Description = "Printer physical location management" },
            new { Name = "bed_type", DisplayName = "Bed Type", ResourceType = "catalog", Description = "Print bed type management" },
            new { Name = "bins", DisplayName = "Bins", ResourceType = "inventory", Description = "Storage bin management" },
            new { Name = "tags", DisplayName = "Tags", ResourceType = "system", Description = "Entity tag management" },
            new { Name = "nfc_devices", DisplayName = "NFC Devices", ResourceType = "system", Description = "NFC device management" },
            new { Name = "webhooks", DisplayName = "Webhooks", ResourceType = "integration", Description = "Outbound webhook management" },
            new { Name = "power_monitors", DisplayName = "Power Monitors", ResourceType = "integration", Description = "Power monitor device management" },
            new { Name = "home_assistant", DisplayName = "Home Assistant Integration", ResourceType = "integration", Description = "Home Assistant integration management" },
            new { Name = "telegram", DisplayName = "Telegram Integration", ResourceType = "integration", Description = "Telegram notification integration management" },
            new { Name = "monitoring", DisplayName = "Monitoring", ResourceType = "system", Description = "System health and monitoring management" },
            new { Name = "prediction", DisplayName = "Prediction", ResourceType = "system", Description = "Print failure prediction management" },
            new { Name = "background_services", DisplayName = "Background Services", ResourceType = "system", Description = "Background service status and control" },
            new { Name = "diagnostics", DisplayName = "Diagnostics", ResourceType = "system", Description = "Connection and SignalR diagnostics" },
            new { Name = "data_management", DisplayName = "Data Management", ResourceType = "system", Description = "Catalog/full data export, import, and reseed operations" }
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
                ("spoolman", "read"),

                // Issue #1453: #945 added the calibration/queue/slicing resources, their
                // actions, and [RequirePermission] attributes, but never extended this list —
                // leaving permissions enforceable in code but unreachable by any role except via
                // the farm_admin bypass. Reconciled against
                // PrintFarmerPermissions.CalibrationFoundation, the single source of truth for
                // this permission set. Purely additive: existing deployments only gain these
                // grants for farm_user, never lose any.
                //
                // "queue:reconcile" and "dispatch-settings:manage" are deliberately NOT granted
                // here: reviewer feedback (issue #1453 PR discussion) flagged both as farm-wide,
                // unscoped administrative actions with no per-printer/per-group authorization —
                // "queue:reconcile" triggers a farm-wide orphaned-job sync
                // (JobQueueController.SyncOrphanedJobsAsync) and "dispatch-settings:manage" gates
                // the singleton system-wide auto-dispatch configuration
                // (DispatchSettingsController). Both remain farm_admin-only by design and are
                // documented in PermissionGrantPathTests.AdminOnlyAllowlist.
                ("calibration", "create"),
                ("calibration", "read"),
                ("calibration", "update"),
                ("calibration", "delete"),
                ("calibration", "generate"),
                ("calibration", "publish"),
                ("queue", "read"),
                ("queue", "write"),
                ("queue", "start"),
                ("queue", "cancel"),
                ("queue", "acknowledge-bed-clear"),
                ("slicing", "submit"),
                ("slicing", "read-artifact"),
                ("slicing", "promote"),
            };
            foreach ((string? resourceName, string? actionName) in userPermissions)
            {
                Resource? resource = await _context.Resources.FirstOrDefaultAsync(r => r.Name == resourceName);
                UserAction? action = await _context.UserActions.FirstOrDefaultAsync(a => a.Name == actionName);
                if (resource != null && action != null && !await _context.RolePermissions.AnyAsync(rp =>
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
}

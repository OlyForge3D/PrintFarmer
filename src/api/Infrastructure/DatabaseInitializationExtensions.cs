using System.Data.Common;
using System.Linq;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Startup;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Infrastructure;

public static class DatabaseInitializationExtensions
{
    /// <summary>
    /// Initializes and seeds the database during application startup.
    /// Ensures schema exists before any services query the database.
    /// Enforces timeouts to prevent hanging containers during startup.
    /// </summary>
    /// <param name="app">The web application instance.</param>
    /// <param name="logger">The unified logging service for diagnostic output.</param>
    /// <param name="db">The application database context.</param>
    /// <param name="dbInitializer">The database initializer service for seeding data.</param>
    /// <param name="startupStatus">The startup status tracker to mark application readiness.</param>
    public static async Task InitializeDatabaseAsync(
        this WebApplication app,
        ILogger logger,
        AppDbContext db,
        IDatabaseInitializer dbInitializer,
        IStartupStatus startupStatus)
    {
        // Get startup timeout from environment (default: 120 seconds)
        TimeSpan dbStartupTimeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("DB_STARTUP_TIMEOUT"), out int timeoutSec) ? timeoutSec : 120);

        try
        {
            using CancellationTokenSource startupCts = new CancellationTokenSource(dbStartupTimeout);

            // STEP 1: Ensure database schema exists FIRST (before any services query it)
            logger.LogInformation("[Startup] Step 1/3: Creating/verifying database schema (timeout: {Timeout}s)...", dbStartupTimeout.TotalSeconds.ToString());

            try
            {
                // Production: Use migrations for proper schema versioning and updates.
                // Development: Use EnsureCreated for rapid iteration.
                // SQLite (any environment): Always use EnsureCreated because no SQLite
                // migration assembly exists. SQLite is used for lite/RPi deployments
                // and development; schema changes are applied by recreating the DB.
                bool isSqlite = (db.Database.ProviderName ?? string.Empty)
                    .Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

                if (app.Environment.IsDevelopment() || isSqlite)
                {
                    _ = await db.Database.EnsureCreatedAsync(startupCts.Token);
                    logger.LogInformation(
                        "[Startup]   ✓ Schema ensured ({Mode})",
                        isSqlite ? "SQLite — no migration assembly" : "Development mode");
                }
                else
                {
                    // Use migrations (handles baselining EnsureCreated databases without history)
                    await ApplyMigrationsWithFallbackAsync(db, logger, startupCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogError("[Startup] FATAL: Schema operation exceeded timeout ({Timeout}s). API will not start.", dbStartupTimeout.TotalSeconds.ToString());
                throw;
            }

            // STEP 2: Optionally skip the heavy initialization/seeding for test runners
            // that pre-seed the database (see tests SharedSqliteFixture). When
            // TEST_SKIP_STARTUP_DB_INIT=true is set we assume a test fixture has
            // already provisioned schema and seed data and skip this step to avoid
            // races with migrations or provider-specific locking.
            bool skipStartupInit = string.Equals(Environment.GetEnvironmentVariable("TEST_SKIP_STARTUP_DB_INIT"), "true", StringComparison.OrdinalIgnoreCase);
            if (skipStartupInit)
            {
                logger.LogInformation("[Startup] Step 2/3: Skipping database initialization (TEST_SKIP_STARTUP_DB_INIT=true)");
            }
            else
            {
                // Determine DB provider for initialization. Avoid resolving ISettingsService here because
                // its constructor may access DB tables (AppSettings) which don't exist yet. Prefer environment
                // configuration for startup initialization. Tests and containers set DB_PROVIDER env var.
                string provider = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? "sqlite";
                int retryCount = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_COUNT"), out int rc) ? rc : 3;
                int retryDelay = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_DELAY"), out int rd) ? rd : 2;

                logger.LogInformation("[Startup] Step 2/3: Seeding reference data (provider: {Provider})...", provider);

                // Small verification: for SQLite-backed test databases EnsureCreated may
                // return before other connections observe the created schema. Poll
                // sqlite_master briefly to make sure core domain tables exist before
                // invoking the initializer which will run seeding queries.
                try
                {
                    string providerName = db.Database.ProviderName ?? string.Empty;
                    if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        DbConnection conn = db.Database.GetDbConnection();
                        await conn.OpenAsync(startupCts.Token);
                        try
                        {
                            string[] required = new[] { "Manufacturers", "FilamentTypes", "SystemLogs" };
                            int attempts = 0;
                            const int maxAttempts = 10;
                            const int delayMs = 200;
                            bool allPresent = false;
                            while (attempts < maxAttempts)
                            {
                                using DbCommand cmd = conn.CreateCommand();
                                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('Manufacturers','FilamentTypes','SystemLogs')";
                                List<string> found = [];
                                using DbDataReader reader = await cmd.ExecuteReaderAsync();
                                while (await reader.ReadAsync())
                                {
                                    found.Add(reader.GetString(0));
                                }

                                if (Array.TrueForAll(required, r => found.Contains(r)))
                                {
                                    allPresent = true;
                                    break;
                                }

                                attempts++;
                                await Task.Delay(delayMs, startupCts.Token);
                            }

                            if (!allPresent)
                            {
                                logger.LogWarning("[Startup] Core tables did not appear within the short wait window. Seeding will proceed but may retry on missing-table errors.");
                            }
                            else
                            {
                                logger.LogInformation("[Startup]   ✓ Core tables detected before seeding");
                            }
                        }
                        finally
                        {
                            await conn.CloseAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[Startup] Short verification of core tables failed (non-fatal)");
                }

                // STEP 2B: Run initialization and seeding with retry logic
                try
                {
                    await dbInitializer.InitializeAsync(provider, retryCount, retryDelay);
                    logger.LogInformation("[Startup]   ✓ Reference data seeded successfully");
                }
                catch (OperationCanceledException)
                {
                    logger.LogError("[Startup] FATAL: Seeding exceeded timeout ({Timeout}s). API will not start.", dbStartupTimeout.TotalSeconds.ToString());
                    throw;
                }

                // Diagnostic: ensure key domain tables exist before proceeding
                try
                {
                    // Only run SQLite-specific diagnostics when the provider is SQLite. Previously
                    // this block unconditionally executed a SELECT against sqlite_master which
                    // caused errors on other providers (for example Postgres). Use both the
                    // EF provider name and the DB_PROVIDER env var as signals to be robust
                    // in a variety of deployment/test setups.
                    string providerName = db.Database.ProviderName ?? string.Empty;
                    string envProvider = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? string.Empty;
                    if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ||
                        envProvider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        DbConnection conn = db.Database.GetDbConnection();
                        await conn.OpenAsync(startupCts.Token);
                        try
                        {
                            using DbCommand cmd = conn.CreateCommand();
                            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('Manufacturers','FilamentTypes','SystemLogs')";
                            List<string> tables = [];
                            using DbDataReader reader = await cmd.ExecuteReaderAsync();
                            while (await reader.ReadAsync())
                            {
                                tables.Add(reader.GetString(0));
                            }

                            if (!tables.Contains("Manufacturers") || !tables.Contains("FilamentTypes"))
                            {
                                logger.LogWarning("[Startup] Core domain tables not present yet: {Tables}. Will proceed but this may indicate a seeding issue. TablesFound={TablesFound}", string.Join(',', tables), tables.Count);
                            }
                        }
                        finally
                        {
                            await conn.CloseAsync();
                        }
                    }
                }
                catch (Exception diagEx)
                {
                    logger.LogDebug(diagEx, "[Startup] Post-seed diagnostics check for core tables failed (non-fatal)");
                }
            }

            // STEP 3: Initialize application settings from environment variables
            logger.LogInformation("[Startup] Step 3/3: Initializing application settings...");
            try
            {
                // Create a scope to resolve scoped services (SettingsService depends on AppDbContext which is scoped)
                using IServiceScope settingsScope = app.Services.CreateScope();
                ISettingsInitializationService settingsInit = settingsScope.ServiceProvider.GetRequiredService<ISettingsInitializationService>();
                settingsInit.InitializeFromEnvironment<SpoolmanSettings>();
                settingsInit.InitializeFromEnvironment<NetworkDiscoverySettings>();
                logger.LogInformation("[Startup]   ✓ Settings initialized from environment");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Startup]   ⚠ Settings initialization failed (non-fatal)");
            }

            // STEP 4: Mark application as ready
            startupStatus.MarkReady();

            logger.LogInformation("[Startup] ✓ Database initialization complete - application ready to serve requests");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "[Startup] FATAL: Database startup sequence exceeded timeout ({Timeout}s). API will not start.", Environment.GetEnvironmentVariable("DB_STARTUP_TIMEOUT") ?? "120");
            await Console.Error.WriteAsync($"[Startup] FATAL: Database startup timeout. Last error: {ex.Message}\n");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Startup] FATAL: Database initialization failed: {Message}", ex.Message);
            await Console.Error.WriteAsync($"[Startup] FATAL: Database initialization failed: {ex.Message}\n{ex.StackTrace}");
            throw; // Fail fast for container restart
        }
    }

    /// <summary>
    /// Applies migrations with fallback handling for databases created with EnsureCreated().
    /// When a database was initially created with EnsureCreated(), it has no __EFMigrationsHistory
    /// table. Applying migrations will fail because the schema already exists. This method
    /// detects that scenario and either:
    /// 1. Creates the history table and marks existing migrations as applied, OR
    /// 2. Falls back to no-op if the schema already matches the model
    /// </summary>
    private static async Task ApplyMigrationsWithFallbackAsync(
        AppDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        if (pendingMigrations.Count == 0)
        {
            logger.LogInformation("[Startup]   ✓ No pending migrations - schema is up to date");
            return;
        }

        logger.LogInformation(
            "[Startup]   Found {Count} pending migration(s): {Migrations}",
            pendingMigrations.Count.ToString(), string.Join(", ", pendingMigrations));

        // If this database has migration history already, we can safely apply pending migrations normally.
        // The baselining logic below is only meant for databases created with EnsureCreated() (tables exist
        // but no __EFMigrationsHistory table). Previously, we incorrectly skipped migrations when tables
        // existed but InitialCreate wasn't pending (common after some migrations had already been applied).
        bool historyExists = await CheckIfMigrationHistoryTableExistsAsync(db, cancellationToken);
        if (historyExists)
        {
            logger.LogInformation("[Startup]   Applying pending migrations...");
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("[Startup]   ✓ Pending migrations applied");
            return;
        }

        // Check if the database was created with EnsureCreated (tables exist but no migration history)
        bool tablesExist = await CheckIfSchemaTablesExistAsync(db, cancellationToken);

        if (tablesExist)
        {
            // Database has tables but no migration history - this is an EnsureCreated database
            // We need to "baseline" the migrations by marking InitialCreate as applied without running it
            logger.LogWarning("[Startup]   ⚠ Database tables exist without migration history (created with EnsureCreated)");
            logger.LogInformation("[Startup]   Baselining migrations - marking InitialCreate as applied...");

            try
            {
                // Find the InitialCreate migration (should be the first one)
                var allMigrations = db.Database.GetMigrations().ToList();
                var initialMigration = allMigrations.FirstOrDefault(m => m.Contains("InitialCreate", StringComparison.OrdinalIgnoreCase));
                if (initialMigration != null)
                {
                    // Insert a record into __EFMigrationsHistory to mark InitialCreate as applied
                    // This allows future migrations to work correctly
                    string historyTable = "__EFMigrationsHistory";
                    string productVersion = GetEfCoreProductVersion();

                    // Ensure history table exists
                    await EnsureMigrationHistoryTableExistsAsync(db, logger, cancellationToken);

                    // Insert the baseline migration record
                    string insertSql = $"INSERT INTO \"{historyTable}\" (\"MigrationId\", \"ProductVersion\") VALUES ('{initialMigration}', '{productVersion}')";
                    await db.Database.ExecuteSqlRawAsync(insertSql, cancellationToken);

                    logger.LogInformation("[Startup]   ✓ Baselined migration: {Migration}", initialMigration);

                    // Now apply any remaining migrations
                    var remainingMigrations = pendingMigrations.Where(m => m != initialMigration).ToList();
                    if (remainingMigrations.Count > 0)
                    {
                        logger.LogInformation("[Startup]   Applying {Count} remaining migration(s)...", remainingMigrations.Count.ToString());
                        await db.Database.MigrateAsync(cancellationToken);
                        logger.LogInformation("[Startup]   ✓ Remaining migrations applied");
                    }
                    else
                    {
                        logger.LogInformation("[Startup]   ✓ No additional migrations to apply");
                    }
                }
                else
                {
                    // No InitialCreate migration - schema was created manually or differently
                    logger.LogWarning("[Startup]   ⚠ No InitialCreate migration found - applying migrations normally");
                    await db.Database.MigrateAsync(cancellationToken);
                    logger.LogInformation("[Startup]   ✓ Pending migrations applied");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Startup]   Failed to baseline migrations: {Message}", ex.Message);
                throw;
            }
        }
        else
        {
            // Fresh database - apply migrations normally
            logger.LogInformation("[Startup]   Applying migrations to new database...");
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("[Startup]   ✓ Migrations applied (Production mode)");
        }
    }

    private static async Task<bool> CheckIfMigrationHistoryTableExistsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            string providerName = db.Database.ProviderName ?? string.Empty;
            string sql;

            if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                // PostgreSQL
                sql = "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL";
            }
            else if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                // SQL Server
                sql = "SELECT CASE WHEN OBJECT_ID(N'__EFMigrationsHistory', N'U') IS NOT NULL THEN 1 ELSE 0 END";
            }
            else
            {
                // SQLite or other - check sqlite_master
                sql = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory')";
            }

            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await db.Database.OpenConnectionAsync(cancellationToken);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToBoolean(result);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if key schema tables exist (indicating EnsureCreated was used previously)
    /// </summary>
    private static async Task<bool> CheckIfSchemaTablesExistAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            string providerName = db.Database.ProviderName ?? string.Empty;
            string sql;

            if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                // PostgreSQL
                sql = "SELECT EXISTS (SELECT FROM pg_tables WHERE schemaname = 'public' AND tablename = 'Printers')";
            }
            else if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                // SQL Server
                sql = "SELECT CASE WHEN EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Printers') THEN 1 ELSE 0 END";
            }
            else
            {
                // SQLite or other - check sqlite_master
                sql = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type='table' AND name='Printers')";
            }

            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await db.Database.OpenConnectionAsync(cancellationToken);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToBoolean(result);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures the EF Core migration history table exists
    /// </summary>
    private static async Task EnsureMigrationHistoryTableExistsAsync(
        AppDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string providerName = db.Database.ProviderName ?? string.Empty;
        string createTableSql;

        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            createTableSql = @"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" character varying(150) NOT NULL,
                    ""ProductVersion"" character varying(32) NOT NULL,
                    CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                )";
        }
        else if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            createTableSql = @"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='__EFMigrationsHistory' AND xtype='U')
                CREATE TABLE [__EFMigrationsHistory] (
                    [MigrationId] nvarchar(150) NOT NULL,
                    [ProductVersion] nvarchar(32) NOT NULL,
                    CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
                )";
        }
        else
        {
            // SQLite
            createTableSql = @"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                    ""ProductVersion"" TEXT NOT NULL
                )";
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync(createTableSql, cancellationToken);
            logger.LogInformation("[Startup]   ✓ Migration history table ensured");
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Startup]   Migration history table may already exist: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Gets the EF Core product version for migration history records
    /// </summary>
    private static string GetEfCoreProductVersion()
    {
        var efCoreAssembly = typeof(DbContext).Assembly;
        var version = efCoreAssembly.GetName().Version;
        return version?.ToString() ?? "9.0.0";
    }
}

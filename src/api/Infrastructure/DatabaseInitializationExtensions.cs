using System.Linq;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Infrastructure;

public static class DatabaseInitializationExtensions
{
    /// <summary>
    /// Initializes and seeds the database during application startup.
    /// Ensures schema exists before any services query the database.
    /// Enforces timeouts to prevent hanging containers during startup.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app,
        IUnifiedLoggingService logger,
        AppDbContext db,
        Farm.Web.Api.Services.Interfaces.IDatabaseInitializer dbInitializer,
        IStartupStatus startupStatus)
    {
        // Get startup timeout from environment (default: 120 seconds)
        var dbStartupTimeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("DB_STARTUP_TIMEOUT"), out int timeoutSec) ? timeoutSec : 120
        );

        try
        {
            using var startupCts = new CancellationTokenSource(dbStartupTimeout);

            // STEP 1: Ensure database schema exists FIRST (before any services query it)
            logger.LogInformation("[Startup] Step 1/3: Creating/verifying database schema (timeout: {Timeout}s)...", dbStartupTimeout.TotalSeconds.ToString());

            try
            {
                // For local development and testing we prefer EnsureCreated to avoid relying on migrations
                // which may not be embedded in test assemblies. Production scenarios should use migrations.
                if (app.Environment.IsDevelopment() || string.Equals(app.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
                {
                    _ = await db.Database.EnsureCreatedAsync(startupCts.Token);
                    logger.LogInformation("[Startup]   ✓ Schema ensured (EnsureCreated)");
                }
                else
                {
                    await db.Database.MigrateAsync(startupCts.Token);
                    logger.LogInformation("[Startup]   ✓ Migrations applied");
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
                    var providerName = db.Database.ProviderName ?? string.Empty;
                    if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        var conn = db.Database.GetDbConnection();
                        await conn.OpenAsync(startupCts.Token);
                        try
                        {
                            var required = new[] { "Manufacturers", "FilamentTypes", "SystemLogs" };
                            int attempts = 0;
                            const int maxAttempts = 10;
                            const int delayMs = 200;
                            bool allPresent = false;
                            while (attempts < maxAttempts)
                            {
                                using var cmd = conn.CreateCommand();
                                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('Manufacturers','FilamentTypes','SystemLogs')";
                                var found = new List<string>();
                                using var reader = await cmd.ExecuteReaderAsync();
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
                    var providerName = db.Database.ProviderName ?? string.Empty;
                    string envProvider = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? string.Empty;
                    if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ||
                        envProvider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        var conn = db.Database.GetDbConnection();
                        await conn.OpenAsync(startupCts.Token);
                        try
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('Manufacturers','FilamentTypes','SystemLogs')";
                            var tables = new List<string>();
                            using var reader = await cmd.ExecuteReaderAsync();
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
                ISettingsInitializationService settingsInit = app.Services.GetRequiredService<ISettingsInitializationService>();
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
}

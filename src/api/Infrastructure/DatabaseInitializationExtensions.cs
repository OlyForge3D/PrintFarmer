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
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app,
        IUnifiedLoggingService logger,
        AppDbContext db,
        Farm.Web.Api.Services.Interfaces.IDatabaseInitializer dbInitializer,
        IStartupStatus startupStatus)
    {
        try
        {
            // STEP 1: Ensure database schema exists FIRST (before any services query it)
            logger.LogInformation("[Startup] Ensuring database schema exists...");

            // For local development and testing we prefer EnsureCreated to avoid relying on migrations
            // which may not be embedded in test assemblies. Production scenarios should use migrations.
            if (app.Environment.IsDevelopment() || string.Equals(app.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
            {
                _ = await db.Database.EnsureCreatedAsync();
                logger.LogInformation("[Startup] Database schema created (EnsureCreated)");
            }
            else
            {
                await db.Database.MigrateAsync();
                logger.LogInformation("[Startup] Database migrations applied");
            }

            // STEP 2: Optionally skip the heavy initialization/seeding for test runners
            // that pre-seed the database (see tests SharedSqliteFixture). When
            // TEST_SKIP_STARTUP_DB_INIT=true is set we assume a test fixture has
            // already provisioned schema and seed data and skip this step to avoid
            // races with migrations or provider-specific locking.
            bool skipStartupInit = string.Equals(Environment.GetEnvironmentVariable("TEST_SKIP_STARTUP_DB_INIT"), "true", StringComparison.OrdinalIgnoreCase);
            if (skipStartupInit)
            {
                logger.LogInformation("[Startup][TEST] Skipping database initializer/seed because TEST_SKIP_STARTUP_DB_INIT=true");
            }
            else
            {
                // Determine DB provider for initialization. Avoid resolving ISettingsService here because
                // its constructor may access DB tables (AppSettings) which don't exist yet. Prefer environment
                // configuration for startup initialization. Tests and containers set DB_PROVIDER env var.
                string provider = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? "sqlite";
                int retryCount = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_COUNT"), out int rc) ? rc : 3;
                int retryDelay = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_DELAY"), out int rd) ? rd : 2;

                logger.LogInformation($"[Startup] Initializing database provider: {provider}");

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
                        await conn.OpenAsync();
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
                                await Task.Delay(delayMs);
                            }
                            if (!allPresent)
                            {
                                logger.LogWarning("[Startup][DB] Core tables did not appear within the short wait window. Seeding will proceed but may retry on missing-table errors.");
                            }
                            else
                            {
                                logger.LogInformation("[Startup][DB] Core tables detected before seeding.");
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
                    logger.LogDebug(ex, "[Startup][DB] Short verification of core tables failed (non-fatal)");
                }

                // STEP 3: Run initialization and seeding
                await dbInitializer.InitializeAsync(provider, retryCount, retryDelay);
                logger.LogInformation("[Startup] Database initialization complete");

                // Diagnostic: ensure key domain tables exist before running shadow-column checks or seed queries
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
                        await conn.OpenAsync();
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
                                logger.LogWarning("[Startup][DB] Core domain tables not present yet: {Tables}. Will attempt seeding but this may fail. TablesFound={TablesFound}", string.Join(',', tables), tables.Count);
                            }
                            else
                            {
                                logger.LogInformation("[Startup][DB] Core tables detected before seeding.");
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
                    // Non-fatal diagnostic failure - continue to initialization which includes retries
                    logger.LogDebug(diagEx, "[Startup][DB] Diagnostics check for core tables failed (non-fatal)");
                }

                await dbInitializer.SeedAllAsync();
                logger.LogInformation("[Startup] Database seeding complete");
            }

            // STEP 4: Mark application as ready
            startupStatus.MarkReady();

            logger.LogInformation("[Startup] Application ready to serve requests");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Startup][FATAL] Database initialization failed: {Message}", ex.Message);
            await Console.Error.WriteAsync($"[Startup][FATAL] Database initialization failed: {ex.Message}\n{ex.StackTrace}");
            throw; // Fail fast for container restart
        }
    }
}

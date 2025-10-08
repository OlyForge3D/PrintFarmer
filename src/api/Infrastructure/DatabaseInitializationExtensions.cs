using Farm.Infrastructure.Data;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Infrastructure;

public static class DatabaseInitializationExtensions
{
    /// <summary>
    /// Initializes and seeds the database during application startup.
    /// Ensures schema exists before any services query the database.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<IUnifiedLoggingService>();

        try
        {
            // STEP 1: Ensure database schema exists FIRST (before any services query it)
            var db = services.GetRequiredService<AppDbContext>();
            logger.LogInformation("[Startup] Ensuring database schema exists...");

            if (app.Environment.IsDevelopment())
            {
                await db.Database.EnsureCreatedAsync();
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
            var skipStartupInit = string.Equals(Environment.GetEnvironmentVariable("TEST_SKIP_STARTUP_DB_INIT"), "true", StringComparison.OrdinalIgnoreCase);
            if (skipStartupInit)
            {
                logger.LogInformation("[Startup][TEST] Skipping database initializer/seed because TEST_SKIP_STARTUP_DB_INIT=true");
            }
            else
            {
                // STEP 2: Now safe to resolve settings and initializers (they can query DB)
                var dbSettingsService = services.GetRequiredService<SettingsService>();
                var dbSettings = dbSettingsService.Get<DatabaseSettings>();
                var dbInitializer = services.GetRequiredService<DatabaseInitializer>();

                int retryCount = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_COUNT"), out int rc) ? rc : 3;
                int retryDelay = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_DELAY"), out int rd) ? rd : 2;

                logger.LogInformation($"[Startup] Initializing database provider: {dbSettings.Provider}");

                // STEP 3: Run initialization and seeding
                await dbInitializer.InitializeAsync(dbSettings.Provider, retryCount, retryDelay);
                logger.LogInformation("[Startup] Database initialization complete");

                await dbInitializer.SeedAllAsync();
                logger.LogInformation("[Startup] Database seeding complete");
            }

            // STEP 4: Mark application as ready
            var startupStatus = services.GetRequiredService<StartupStatus>();
            startupStatus.MarkReady();

            logger.LogInformation("[Startup] Application ready to serve requests");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Startup][FATAL] Database initialization failed: {Message}", ex.Message);
            await Console.Error.WriteLineAsync($"[Startup][FATAL] Database initialization failed: {ex.Message}\n{ex.StackTrace}");
            throw; // Fail fast for container restart
        }
    }
}

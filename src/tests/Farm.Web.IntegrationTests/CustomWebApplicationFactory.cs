using System;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.IntegrationTests
{
    // Minimal compatibility CustomWebApplicationFactory for integration tests
    // Provides a simple WebApplicationFactory<Program> so tests can run in this environment.
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
    {
        public CustomWebApplicationFactory()
        {
        }

        public static CustomWebApplicationFactory CreateWithIsolatedDatabase(bool useInMemorySqlite = true)
        {
            return new CustomWebApplicationFactory();
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            _ = builder.ConfigureServices(services =>
            {
                // Provide a lightweight test orchestrator so integration tests can run without full worker infrastructure.
                _ = services.AddSingleton<ISlicerOrchestrator, TestSlicerOrchestrator>();
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Configure a shared in-memory SQLite database for integration tests so
            // the application startup code will run EnsureCreated + seeding as it
            // would in a normal host. This keeps integration behavior consistent
            // with the main test factory without pulling in extra complexity.
            string memDbName = $"integ_shared_{Guid.NewGuid():N}";
            string connStr = $"Data Source=file:{memDbName}?mode=memory&cache=shared";

            // Override connection strings and test flags used by Program startup
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", connStr);
            Environment.SetEnvironmentVariable("ConnectionStrings__Sqlite", connStr);
            Environment.SetEnvironmentVariable("TEST_USE_SQLITE_INMEMORY", "true");
            Environment.SetEnvironmentVariable("TEST_DISABLE_BACKGROUND_SERVICES", "true");
            Environment.SetEnvironmentVariable("DISABLE_TELEMETRY", "true");
            // Provide a test JWT key (32+ chars) so authentication middleware can initialize in tests
            Environment.SetEnvironmentVariable("Jwt__Key", "test-integration-key-please-change-0123456789");
            Environment.SetEnvironmentVariable("Jwt__Issuer", "PrintFarmer");
            Environment.SetEnvironmentVariable("Jwt__Audience", "PrintFarmer");
            // Note: Do NOT force-disable the rate limiter here. Some integration tests
            // validate rate limiting behavior and expect it to be active. Tests that
            // need the rate limiter disabled should set the environment variable
            // explicitly in their own setup.

            // Ensure host runs in Testing environment to match other test factories
            _ = builder.UseEnvironment("Testing");

            IHost host = base.CreateHost(builder);

            // Best-effort: run a host-scoped EnsureCreated + DatabaseInitializer.SeedAllAsync
            // so integration tests see the same seed data and schema as other factories.
            try
            {
                using IServiceScope scope = host.Services.CreateScope();
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                _ = db.Database.EnsureCreated();

                DatabaseInitializer? initializer = scope.ServiceProvider.GetService<DatabaseInitializer>();
                if (initializer != null)
                {
                    try
                    {
                        initializer.InitializeAsync("sqlite", 3, 2).GetAwaiter().GetResult();
                        initializer.SeedAllAsync().GetAwaiter().GetResult();
                    }
                    catch { /* best-effort */ }
                }
            }
            catch { /* best-effort */ }

            return host;
        }
    }
}

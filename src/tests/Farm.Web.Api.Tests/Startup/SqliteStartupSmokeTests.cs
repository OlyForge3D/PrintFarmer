using System.Net;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Startup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Regression coverage for issue #1567's empty SQLite image-startup path.
/// </summary>
[Collection(EnvironmentVariableTestCollection.Name)]
public sealed class SqliteStartupSmokeTests
{
    [Fact]
    public async Task EmptySqliteDatabase_InitializesSchemaBeforeHealthzResponds()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"printfarmer-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string databasePath = Path.Combine(tempDirectory, "smoke.db");
        const string skipStartupVariable = "TEST_SKIP_STARTUP_DB_INIT";
        string? originalSkipStartup = Environment.GetEnvironmentVariable(skipStartupVariable);
        Environment.SetEnvironmentVariable(skipStartupVariable, null);

        try
        {
            await using var factory = new FreshSqliteWebApplicationFactory(databasePath);
            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync("/healthz");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            IStartupStatus startupStatus = factory.Services.GetRequiredService<IStartupStatus>();
            startupStatus.IsDatabaseSchemaReady.Should().BeTrue();
            startupStatus.IsReady.Should().BeTrue();

            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = await db.AppSettingsEntities.CountAsync();
            (await db.MutationCounters.AnyAsync()).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(skipStartupVariable, originalSkipStartup);
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private sealed class FreshSqliteWebApplicationFactory(string databasePath) : WebApplicationFactory<Program>
    {
        private readonly string _connectionString = $"Data Source={databasePath}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _ = builder.UseEnvironment("Testing");
            _ = builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = _connectionString,
                    ["DB_PROVIDER"] = "Sqlite",
                    ["DEPLOYMENT_MODE"] = "microservices",
                    ["DISABLE_TELEMETRY"] = "true",
                    ["Jwt:Key"] = "sqlite-startup-smoke-only-key-32-bytes-minimum",
                    ["Slicer:Enabled"] = "false",
                    ["TEST_DISABLE_BACKGROUND_SERVICES"] = "true",
                    ["WorkerAuth:SharedKey"] = "sqlite-startup-smoke-worker-key",
                });
            });
            _ = builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
                services.RemoveAll<IDbContextFactory<AppDbContext>>();

                _ = services.AddDbContext<AppDbContext>(options =>
                    options.UseSqlite(
                        _connectionString,
                        sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")));
                _ = services.AddDbContextFactory<AppDbContext>(options =>
                    options.UseSqlite(
                        _connectionString,
                        sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")));
            });
        }
    }

    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class EnvironmentVariableTestCollection
    {
        public const string Name = "EnvironmentVariableSerial";
    }
}

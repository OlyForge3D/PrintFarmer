using Farm.Infrastructure.Data;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.TestInfrastructure;

public class SharedSqliteFixture : IDisposable
{
    // NOTE FOR MAINTAINERS:
    // This fixture opens a single "keeper" SQLite in-memory connection which keeps the in-memory
    // database alive for the duration of a test collection. Do NOT pass this open SqliteConnection
    // instance directly into EF Core's DbContext registrations for test servers that will run
    // concurrent operations. Passing the open connection to EF causes overlapping-reader errors
    // ("Invalid attempt to call Read when reader is closed") when multiple EF connections/readers
    // are used concurrently.
    //
    // Instead, keep this connection open only to hold the in-memory DB alive and register EF Core
    // using the same connection string (e.g. conn.ConnectionString). That lets EF open and manage
    // its own physical connections while the keeper preserves DB lifetime. See TestWebApplicationFactory
    // for the registration pattern used in tests.

    public SqliteConnection Connection { get; }
    // Expose a global static connection so the test factory can reuse the exact
    // same SqliteConnection instance (not just the connection string). Reusing
    // the object instance guarantees schema visibility for in-memory SQLite.
    public static SqliteConnection? GlobalConnection { get; private set; }

    public SharedSqliteFixture()
    {
        string name = $"shared_fixture_{Guid.NewGuid():N}";
        string connStr = $"Data Source=file:{name}?mode=memory&cache=shared";
        Connection = new SqliteConnection(connStr);
        Connection.Open();

        // Publish the exact opened connection instance for factories to reuse.
        GlobalConnection = Connection;

        // Export connection string so test factories can reuse the same shared in-memory DB
        Environment.SetEnvironmentVariable("TEST_SHARED_SQLITE_CONN", connStr);

        // Build temporary service provider to create schema and run real initializer.
        // IMPORTANT: do NOT register EF to use the already-opened keeper connection
        // instance. Passing the open SqliteConnection into EF causes nested-transaction
        // and reader/closed errors when EF opens its own connections concurrently.
        // Instead register EF using the connection string so EF will open separate
        // physical connections while the keeper connection simply keeps the
        // in-memory DB alive.
        ServiceCollection services = new ServiceCollection();
        // Minimal configuration: register DbContext using the connection string
        services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(connStr));

        // Register DatabaseInitializer and its dependencies similarly to startup
        // We will attempt to reuse the project's service registrations minimally.
        services.AddLogging();
        // Tests run without the full service graph; DatabaseInitializer depends on IUnifiedLoggingService.
        // Provide a simple NoOp implementation so the initializer can be constructed for seeding.
        services.AddSingleton<IUnifiedLoggingService, NoOpUnifiedLoggingService>();
        services.AddScoped<DatabaseInitializer>();

        ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Do NOT call EnsureCreated here. The DatabaseInitializer.InitializeAsync
        // will perform schema provisioning; calling EnsureCreated twice (here and
        // inside the initializer) has led to duplicate-create errors on shared
        // in-memory SQLite in concurrent scenarios. Let the initializer be the
        // single source of truth for schema creation.

        DatabaseInitializer? initializer = scope.ServiceProvider.GetService<DatabaseInitializer>();
        if (initializer != null)
        {
            try
            {
                initializer.InitializeAsync("sqlite", 3, 2).GetAwaiter().GetResult();
                initializer.SeedAllAsync().GetAwaiter().GetResult();
                // Indicate to the application under test that the database has already
                // been provisioned and seeded by the test fixture. This instructs
                // startup to skip the heavy initialization path which would otherwise
                // run again and may race with our pre-seeded in-memory DB.
                Environment.SetEnvironmentVariable("TEST_SKIP_STARTUP_DB_INIT", "true");
            }
            catch
            {
                // best-effort
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Connection.Close();
        }
        catch
        {
        }

        try
        {
            Connection.Dispose();
        }
        catch
        {
        }
    }
}

[CollectionDefinition("SharedSqliteFixtureCollection")]
public class SharedSqliteFixtureCollection : ICollectionFixture<SharedSqliteFixture>
{
}

// Combined collection for Db-heavy tests that also need the shared sqlite fixture.
[CollectionDefinition("DbHeavySerialWithSharedFixture")]
public class DbHeavySerialWithSharedFixtureCollection : ICollectionFixture<SharedSqliteFixture>
{
}

// Minimal no-op implementation of IUnifiedLoggingService used only in tests during
// fixture setup to allow DatabaseInitializer construction without pulling in full telemetry stack.
internal class NoOpUnifiedLoggingService : IUnifiedLoggingService
{
    public void LogCritical(string message, string? correlationId = null, object? metadata = null) { }
    public void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
    public void LogDebug(string message, string? correlationId = null, object? metadata = null) { }
    public void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
    public void LogError(string message, string? correlationId = null, object? metadata = null) { }
    public void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
    public void LogInformation(string message, string? correlationId = null, object? metadata = null) { }
    public void LogWarning(string message, string? correlationId = null, object? metadata = null) { }
    public void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
    public void LogWithContext(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null) { }
}

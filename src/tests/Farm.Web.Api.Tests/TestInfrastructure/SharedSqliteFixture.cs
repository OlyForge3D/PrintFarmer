using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Farm.Web.Api.Services;
using Farm.Infrastructure.Data;

namespace Farm.Web.Api.Tests.TestInfrastructure;

public class SharedSqliteFixture : IDisposable
{
    public SqliteConnection Connection { get; }
    // Expose a global static connection so the test factory can reuse the exact
    // same SqliteConnection instance (not just the connection string). Reusing
    // the object instance guarantees schema visibility for in-memory SQLite.
    public static SqliteConnection? GlobalConnection { get; private set; }

    public SharedSqliteFixture()
    {
        var name = $"shared_fixture_{Guid.NewGuid():N}";
        var connStr = $"Data Source=file:{name}?mode=memory&cache=shared";
        Connection = new SqliteConnection(connStr);
        Connection.Open();

        // Publish the exact opened connection instance for factories to reuse.
        GlobalConnection = Connection;

        // Export connection string so test factories can reuse the same shared in-memory DB
        Environment.SetEnvironmentVariable("TEST_SHARED_SQLITE_CONN", connStr);

        // Build temporary service provider to create schema and run real initializer
        var services = new ServiceCollection();
        // Minimal configuration: register DbContext using the opened connection
        services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(Connection));

        // Register DatabaseInitializer and its dependencies similarly to startup
        // We will attempt to reuse the project's service registrations minimally.
        services.AddLogging();
        services.AddScoped<DatabaseInitializer>();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        var initializer = scope.ServiceProvider.GetService<DatabaseInitializer>();
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

public class SharedSqliteFixtureCollection : ICollectionFixture<SharedSqliteFixture>
{
}

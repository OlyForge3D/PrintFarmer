using System;
using System.Linq;
using Farm.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Infrastructure;

public abstract class DbHeavyTestBase<TEntryPoint> : IClassFixture<WebApplicationFactory<TEntryPoint>> where TEntryPoint : class
{
    protected readonly WebApplicationFactory<TEntryPoint> _factory;
    protected readonly SqliteConnection _connection;
    protected DbHeavyTestBase(WebApplicationFactory<TEntryPoint> factory)
        : this(factory, false, "")
    {
    }

    protected DbHeavyTestBase(WebApplicationFactory<TEntryPoint> factory, bool useFileDb, string dbFilePath)
    {
        if (useFileDb && !string.IsNullOrEmpty(dbFilePath))
        {
            // Delete the file if it exists to ensure a clean DB for each test run
            if (System.IO.File.Exists(dbFilePath))
            {
                System.IO.File.Delete(dbFilePath);
            }
            _connection = new SqliteConnection($"Data Source={dbFilePath};Cache=Shared");
        }
        else
        {
            // Prefer a global shared connection when a collection fixture created one.
            var global = Farm.Web.Api.Tests.TestInfrastructure.SharedSqliteFixture.GlobalConnection;
            if (global != null)
            {
                _connection = global;
            }
            else
            {
                _connection = new SqliteConnection("DataSource=:memory:;Cache=Shared");
            }
        }
        // Ensure connection is open (if it's a global fixture connection it may already be open)
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }

        // Ensure the schema is created on the connection before DI uses it
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (var context = new AppDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove any existing registrations that reference AppDbContext so our
                // test-provided SqliteConnection is used for all DbContext instances.
                var descriptorsToRemove = services.Where(d =>
                    (d.ServiceType != null && d.ServiceType.FullName != null && d.ServiceType.FullName.Contains("AppDbContext")) ||
                    (d.ImplementationType != null && d.ImplementationType.FullName != null && d.ImplementationType.FullName.Contains("AppDbContext")) ||
                    (d.ServiceType == typeof(DbContextOptions<AppDbContext>))
                ).ToList();
                foreach (var d in descriptorsToRemove)
                {
                    services.Remove(d);
                }

                // Register the opened connection instance and re-register AppDbContext
                // to use that exact connection so the in-memory DB schema/seed are visible
                // to the real host's DbContext instances.
                services.AddSingleton(_connection);
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });
        });
    }
}

using System;
using System.Linq;
using Farm.Web.Api.Data;
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
            _connection = new SqliteConnection("DataSource=:memory:;Cache=Shared");
        }
        _connection.Open();

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
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }
                services.AddSingleton(_connection);
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });
        });
    }
}

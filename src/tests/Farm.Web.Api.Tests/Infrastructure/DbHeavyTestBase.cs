using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Farm.Web.Api.Data;

namespace Farm.Web.Api.Tests.Infrastructure;

public abstract class DbHeavyTestBase<TEntryPoint> : IClassFixture<WebApplicationFactory<TEntryPoint>> where TEntryPoint : class
{
    protected readonly WebApplicationFactory<TEntryPoint> _factory;

    protected DbHeavyTestBase(WebApplicationFactory<TEntryPoint> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                services.AddSingleton(connection);
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));

                // Ensure schema is created
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                dbContext.Database.EnsureCreated();
            });
        });
    }
}

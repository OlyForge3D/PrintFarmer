using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Tests;

/// <summary>
/// Issue #3168: <see cref="SlicerDbContext"/> honours a distinct
/// <c>ConnectionStrings:SlicerDatabase</c> for split application/slicer deployments.
/// </summary>
public sealed class SlicerModuleExtensionsSlicerDatabaseTests
{
    private const string AppConnection = "Data Source=app-3168.db";
    private const string SlicerConnection = "Data Source=slicer-3168.db";

    private static IConfiguration Configuration(string? slicerDatabase) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PROVIDER"] = "sqlite",
                ["ConnectionStrings:Default"] = AppConnection,
                ["ConnectionStrings:SlicerDatabase"] = slicerDatabase,
            })
            .Build();

    [Theory]
    [InlineData(null, AppConnection)]
    [InlineData(SlicerConnection, SlicerConnection)]
    public async Task EnsureSlicerDatabaseRegistered_ResolvesContextAndFactoryFromSlicerDatabase(
        string? slicerDatabase,
        string expectedConnection)
    {
        ServiceCollection services = new();
        _ = services.EnsureSlicerDatabaseRegistered(Configuration(slicerDatabase));

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        await using SlicerDbContext fromFactory = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<SlicerDbContext>>()
            .CreateDbContextAsync();

        Assert.Equal(expectedConnection, context.Database.GetConnectionString());
        Assert.Equal(expectedConnection, fromFactory.Database.GetConnectionString());
    }
}

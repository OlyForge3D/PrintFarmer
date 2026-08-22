using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Tests;

/// <summary>
/// Regression tests for <see cref="SlicerModuleExtensions.AddSlicerCalibrationProfileRepositories"/>
/// (#1858): split/microservices API hosts must be able to resolve
/// <see cref="IMachineProfileRepository"/>, <see cref="IProcessProfileRepository"/>, and
/// <see cref="IFilamentProfileRepository"/> from their own composition root, without loading the
/// rest of the slicer module.
/// </summary>
public sealed class SlicerModuleExtensionsCalibrationProfileRepositoriesTests
{
    private static IConfiguration BuildSplitDeploymentConfiguration(string dbPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEPLOYMENT_MODE"] = "microservices",
                ["DB_PROVIDER"] = "sqlite",
                ["ConnectionStrings:Default"] = $"Data Source={dbPath}",
            })
            .Build();

    [Fact]
    public async Task AddSlicerCalibrationProfileRepositories_SplitDeployment_RegistersResolvableRepositories()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"slicer-calibration-repos-{Guid.NewGuid():N}.db");
        try
        {
            IConfiguration configuration = BuildSplitDeploymentConfiguration(dbPath);

            ServiceCollection services = new();
            _ = services.AddSlicerCalibrationProfileRepositories(configuration);
            await using ServiceProvider provider = services.BuildServiceProvider();

            await using (AsyncServiceScope initScope = provider.CreateAsyncScope())
            {
                SlicerDbContext db = initScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
                _ = await db.Database.EnsureCreatedAsync();
            }

            // This is exactly the resolution MoonrakerEmulatorSeeder.TrySeedCoreAsync performs
            // from its own DI scope. Before this fix, none of these were registered on a split
            // or microservices API host and GetRequiredService threw, surfacing as an
            // unconditional 500 on POST /api/test/moonraker-emulator/reset (#1858).
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            IMachineProfileRepository machineProfiles =
                scope.ServiceProvider.GetRequiredService<IMachineProfileRepository>();
            IProcessProfileRepository processProfiles =
                scope.ServiceProvider.GetRequiredService<IProcessProfileRepository>();
            IFilamentProfileRepository filamentProfiles =
                scope.ServiceProvider.GetRequiredService<IFilamentProfileRepository>();

            // Prove the repositories are backed by a real, functioning SlicerDbContext
            // connection, not just resolvable-but-broken registrations.
            Assert.Null(await machineProfiles.GetByHashAsync("missing-hash", CancellationToken.None));
            Assert.Null(await processProfiles.GetByHashAsync("missing-hash", CancellationToken.None));
            Assert.Null(await filamentProfiles.GetByHashAsync("missing-hash", CancellationToken.None));
        }
        finally
        {
            // Microsoft.Data.Sqlite pools connections by default, which keeps the file locked
            // for a short while after disposal — clear the pool before deleting.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddSlicerCalibrationProfileRepositories_WhenSlicerModuleAlreadyRegistered_IsNoOp()
    {
        // Monolith hosts call AddSlicerModule, which already registers everything these
        // repositories need. AddSlicerCalibrationProfileRepositories must not add duplicate or
        // conflicting registrations on top of it.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PROVIDER"] = "sqlite",
                ["ConnectionStrings:Default"] = "Data Source=:memory:",
            })
            .Build();

        ServiceCollection services = new();
        _ = services.AddSlicerModule(configuration);
        int countAfterModule = services.Count;

        _ = services.AddSlicerCalibrationProfileRepositories(configuration);

        Assert.Equal(countAfterModule, services.Count);
    }

    [Fact]
    public void AddSlicerCalibrationProfileRepositories_CalledTwice_IsIdempotent()
    {
        IConfiguration configuration = BuildSplitDeploymentConfiguration(
            Path.Combine(Path.GetTempPath(), $"slicer-calibration-repos-{Guid.NewGuid():N}.db"));

        ServiceCollection services = new();
        _ = services.AddSlicerCalibrationProfileRepositories(configuration);
        int countAfterFirst = services.Count;

        _ = services.AddSlicerCalibrationProfileRepositories(configuration);

        Assert.Equal(countAfterFirst, services.Count);
    }
}

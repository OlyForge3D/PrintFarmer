using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Tests;

/// <summary>
/// Regression tests for <see cref="SlicerModuleExtensions.AddSlicerCalibrationProfileRepositories"/>
/// (#1858, extended by #2179): split/microservices API hosts must be able to resolve
/// <see cref="IMachineProfileRepository"/>, <see cref="IProcessProfileRepository"/>,
/// <see cref="IFilamentProfileRepository"/>, and <see cref="IModel3DFileRepository"/> from their
/// own composition root, without loading the rest of the slicer module.
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
            IModel3DFileRepository modelFiles =
                scope.ServiceProvider.GetRequiredService<IModel3DFileRepository>();

            // Prove the repositories are backed by a real, functioning SlicerDbContext
            // connection, not just resolvable-but-broken registrations.
            Assert.Null(await machineProfiles.GetByHashAsync("missing-hash", CancellationToken.None));
            Assert.Null(await processProfiles.GetByHashAsync("missing-hash", CancellationToken.None));
            Assert.Null(await filamentProfiles.GetByHashAsync("missing-hash", CancellationToken.None));
            Assert.Null(await modelFiles.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));
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
    public async Task AddSlicerCalibrationProfileRepositories_AfterSplitModeAddSlicerModule_StillRegistersRepositories()
    {
        // DEPLOYMENT_MODE=split (as opposed to "microservices") takes a DIFFERENT short-circuit
        // inside AddSlicerModule: Program.cs's own literal "microservices" check does not match
        // "split", so on a split-mode host Program.cs still calls AddSlicerModule. AddSlicerModule
        // adds its SlicerModuleMarker unconditionally before its own split/microservices early
        // return, so the marker ends up present on this host even though AddSlicerModule never
        // reached AddSlicerRepositories. AddSlicerCalibrationProfileRepositories must not mistake
        // that marker for "repositories already registered" and must still register them here.
        string dbPath = Path.Combine(Path.GetTempPath(), $"slicer-calibration-repos-{Guid.NewGuid():N}.db");
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DEPLOYMENT_MODE"] = "split",
                    ["DB_PROVIDER"] = "sqlite",
                    ["ConnectionStrings:Default"] = $"Data Source={dbPath}",
                })
                .Build();

            ServiceCollection services = new();
            _ = services.AddSlicerModule(configuration);

            // Confirm the split-mode early return really did skip repository registration, so
            // this test actually exercises the scenario it claims to.
            Assert.DoesNotContain(services, sd => sd.ServiceType == typeof(IMachineProfileRepository));

            _ = services.AddSlicerCalibrationProfileRepositories(configuration);
            await using ServiceProvider provider = services.BuildServiceProvider();

            await using (AsyncServiceScope initScope = provider.CreateAsyncScope())
            {
                SlicerDbContext db = initScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
                _ = await db.Database.EnsureCreatedAsync();
            }

            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            IMachineProfileRepository machineProfiles =
                scope.ServiceProvider.GetRequiredService<IMachineProfileRepository>();
            IProcessProfileRepository processProfiles =
                scope.ServiceProvider.GetRequiredService<IProcessProfileRepository>();
            IFilamentProfileRepository filamentProfiles =
                scope.ServiceProvider.GetRequiredService<IFilamentProfileRepository>();
            IModel3DFileRepository modelFiles =
                scope.ServiceProvider.GetRequiredService<IModel3DFileRepository>();

            Assert.Null(await machineProfiles.GetByHashAsync("missing-hash", CancellationToken.None));
            Assert.Null(await processProfiles.GetByHashAsync("missing-hash", CancellationToken.None));
            Assert.Null(await filamentProfiles.GetByHashAsync("missing-hash", CancellationToken.None));
            Assert.Null(await modelFiles.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));
        }
        finally
        {
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

    [Fact]
    public async Task EnsureSlicerDatabaseRegistered_WhenIMachineProfileRepositoryRegisteredWithoutDbContext_StillRegistersSlicerDbContext()
    {
        // Regression for a gap a reviewer found in #2179's fix-up round: AddSlicerCalibrationProfileRepositories
        // early-returns as soon as IMachineProfileRepository is present, on the assumption that
        // whoever registered it also registered SlicerDbContext alongside it (true for every
        // caller today). ModelStorageResolutionStartup's TryAddScoped<IModel3DFileRepository>
        // insurance would be unable to construct that repository if a future caller ever broke
        // that assumption. EnsureSlicerDatabaseRegistered must guarantee SlicerDbContext directly,
        // independent of whatever AddSlicerCalibrationProfileRepositories's own guard decided.
        string dbPath = Path.Combine(Path.GetTempPath(), $"slicer-calibration-repos-{Guid.NewGuid():N}.db");
        try
        {
            IConfiguration configuration = BuildSplitDeploymentConfiguration(dbPath);

            ServiceCollection services = new();
            // Simulate the divergent future caller: IMachineProfileRepository registered, but
            // deliberately NOT SlicerDbContext, so AddSlicerCalibrationProfileRepositories's
            // early-return guard fires without the database having been registered.
            _ = services.AddScoped<IMachineProfileRepository>(_ =>
                throw new InvalidOperationException("not used by this test"));
            Assert.DoesNotContain(services, sd => sd.ServiceType == typeof(SlicerDbContext));

            // This is exactly the scenario AddSlicerCalibrationProfileRepositories's early return
            // is meant to no-op on — confirm it really does no-op and really does leave
            // SlicerDbContext unregistered, so this test exercises the gap it claims to close.
            _ = services.AddSlicerCalibrationProfileRepositories(configuration);
            Assert.DoesNotContain(services, sd => sd.ServiceType == typeof(SlicerDbContext));

            _ = services.EnsureSlicerDatabaseRegistered(configuration);

            await using ServiceProvider provider = services.BuildServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            _ = await db.Database.EnsureCreatedAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void EnsureSlicerDatabaseRegistered_WhenSlicerDbContextAlreadyRegistered_IsNoOp()
    {
        IConfiguration configuration = BuildSplitDeploymentConfiguration(
            Path.Combine(Path.GetTempPath(), $"slicer-calibration-repos-{Guid.NewGuid():N}.db"));

        ServiceCollection services = new();
        _ = services.EnsureSlicerDatabaseRegistered(configuration);
        int countAfterFirst = services.Count;

        _ = services.EnsureSlicerDatabaseRegistered(configuration);

        Assert.Equal(countAfterFirst, services.Count);
    }
}

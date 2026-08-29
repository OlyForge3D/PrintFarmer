using Farm.Modules.Calibration.Startup;
using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Calibration.Tests.Calibration;

/// <summary>
/// Regression tests for
/// <see cref="CalibrationWorkerHealthDependenciesStartup.AddCalibrationWorkerHealthDependencies"/>
/// (#2178): split/microservices API hosts must be able to resolve
/// <see cref="IDbContextFactory{TContext}"/> of <see cref="SlicerDbContext"/> from their own
/// composition root — the dependency <c>CalibrationCapabilityService.GetWorkerHealthAsync</c>
/// needs to query slicer-worker heartbeats — WITHOUT any Moonraker-emulator-seeder wiring
/// (<c>MoonrakerEmulatorSeed</c>, <c>MoonrakerEmulatorSeederDependenciesStartup</c>) involved in
/// the test setup at all, so this dependency is proven explicit and independently verified rather
/// than an incidental side effect of a startup method that has nothing to do with it.
/// </summary>
public sealed class CalibrationWorkerHealthDependenciesStartupTests
{
    private static IConfiguration BuildSplitDeploymentConfiguration(
        string dbPath,
        string deploymentModeKey = "DEPLOYMENT_MODE",
        string deploymentModeValue = "microservices") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [deploymentModeKey] = deploymentModeValue,
                ["DB_PROVIDER"] = "sqlite",
                ["ConnectionStrings:Default"] = $"Data Source={dbPath}",
            })
            .Build();

    /// <summary>
    /// Every configuration shape that
    /// <see cref="CalibrationProfileResolutionStartup.IsSplitDeployment"/> recognizes as a
    /// split/microservices host: <c>DEPLOYMENT_MODE=split</c>, <c>DEPLOYMENT_MODE=microservices</c>,
    /// and <c>DEPLOYMENT_TYPE=microservices</c>.
    /// </summary>
    public static IEnumerable<object[]> SplitAndMicroservicesDeploymentConfigurations()
    {
        yield return ["DEPLOYMENT_MODE", "split"];
        yield return ["DEPLOYMENT_MODE", "microservices"];
        yield return ["DEPLOYMENT_TYPE", "microservices"];
    }

    [Theory]
    [MemberData(nameof(SplitAndMicroservicesDeploymentConfigurations))]
    public async Task AddCalibrationWorkerHealthDependencies_SplitOrMicroservicesDeployment_RegistersUsableDbContextFactory(
        string deploymentModeKey,
        string deploymentModeValue)
    {
        // This is the acceptance criterion end-to-end: with ONLY this registration called (no
        // AddMoonrakerEmulatorSeederDependencies, no AddModelStorageResolution, no
        // MoonrakerEmulatorSeed involved anywhere), a split/microservices host must be able to
        // resolve IDbContextFactory<SlicerDbContext> and actually create a working DbContext from
        // it — exactly what CalibrationCapabilityService.GetWorkerHealthAsync depends on.
        string dbPath = Path.Combine(
            Path.GetTempPath(), $"calibration-worker-health-{Guid.NewGuid():N}.db");
        try
        {
            IConfiguration configuration = BuildSplitDeploymentConfiguration(
                dbPath, deploymentModeKey, deploymentModeValue);

            ServiceCollection services = new();

            _ = services.AddCalibrationWorkerHealthDependencies(configuration);

            await using ServiceProvider provider = services.BuildServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();

            IDbContextFactory<SlicerDbContext>? factory =
                scope.ServiceProvider.GetService<IDbContextFactory<SlicerDbContext>>();
            _ = factory.Should().NotBeNull(
                "split/microservices hosts must resolve IDbContextFactory<SlicerDbContext> so " +
                "CalibrationCapabilityService.GetWorkerHealthAsync can query worker heartbeats " +
                "independently of any Moonraker-emulator-seeder wiring");

            await using SlicerDbContext db = await factory!.CreateDbContextAsync();
            _ = await db.Database.EnsureCreatedAsync();
            _ = await db.SlicerServices.CountAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddCalibrationWorkerHealthDependencies_MonolithDeployment_IsNoOp()
    {
        // No DEPLOYMENT_MODE configured => monolith. Monolith hosts get IDbContextFactory<SlicerDbContext>
        // from AddSlicerModule instead, so this method must not add anything on its own.
        IConfiguration configuration = new ConfigurationBuilder().Build();

        ServiceCollection services = new();
        int countBefore = services.Count;

        _ = services.AddCalibrationWorkerHealthDependencies(configuration);

        _ = services.Count.Should().Be(countBefore);
    }

    [Fact]
    public void AddCalibrationWorkerHealthDependencies_ForMonolithDeploymentModes_DoesNotRegisterAnything()
    {
        foreach (string? deploymentMode in new[] { null, "monolith", "standalone" })
        {
            Dictionary<string, string?> settings = new()
            {
                ["DB_PROVIDER"] = "sqlite",
                ["ConnectionStrings:Default"] = "Data Source=:memory:",
            };
            if (deploymentMode is not null)
            {
                settings["DEPLOYMENT_MODE"] = deploymentMode;
            }

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();

            ServiceCollection services = new();
            _ = services.AddCalibrationWorkerHealthDependencies(configuration);

            _ = services.Should().BeEmpty();
        }
    }

    [Fact]
    public void AddCalibrationWorkerHealthDependencies_CalledTwice_IsIdempotent()
    {
        IConfiguration configuration = BuildSplitDeploymentConfiguration(
            Path.Combine(Path.GetTempPath(), $"calibration-worker-health-{Guid.NewGuid():N}.db"));

        ServiceCollection services = new();
        _ = services.AddCalibrationWorkerHealthDependencies(configuration);
        int countAfterFirst = services.Count;

        _ = services.AddCalibrationWorkerHealthDependencies(configuration);

        _ = services.Count.Should().Be(countAfterFirst);
    }

    [Fact]
    public void AddCalibrationWorkerHealthDependencies_SplitDeploymentWithDbContextAlreadyRegistered_IsNoOp()
    {
        // Distinct from the idempotency test above: this is a split/microservices host where some
        // other caller (e.g. AddModelStorageResolution, or AddMoonrakerEmulatorSeederDependencies)
        // already registered SlicerDbContext. AddCalibrationWorkerHealthDependencies must defer to
        // that existing registration via EnsureSlicerDatabaseRegistered's own guard, rather than
        // adding a second, conflicting one.
        IConfiguration configuration = BuildSplitDeploymentConfiguration(
            Path.Combine(Path.GetTempPath(), $"calibration-worker-health-{Guid.NewGuid():N}.db"));

        ServiceCollection services = new();
        _ = services.AddDbContext<SlicerDbContext>(options => options.UseInMemoryDatabase("stand-in"));
        int countBefore = services.Count;

        _ = services.AddCalibrationWorkerHealthDependencies(configuration);

        _ = services.Count.Should().Be(countBefore);
    }
}

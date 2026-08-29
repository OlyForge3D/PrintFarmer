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
    /// <c>DEPLOYMENT_TYPE=microservices</c>, <c>Deployment:Mode=split</c>, and
    /// <c>Deployment:Mode=microservices</c>.
    /// </summary>
    public static IEnumerable<object[]> SplitAndMicroservicesDeploymentConfigurations()
    {
        yield return ["DEPLOYMENT_MODE", "split"];
        yield return ["DEPLOYMENT_MODE", "microservices"];
        yield return ["DEPLOYMENT_TYPE", "microservices"];
        yield return ["Deployment:Mode", "split"];
        yield return ["Deployment:Mode", "microservices"];
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
    public async Task AddCalibrationWorkerHealthDependencies_SplitDeploymentWithDbContextAndFactoryAlreadyRegistered_IsNoOp()
    {
        // Distinct from the idempotency test above: this is a split/microservices host where some
        // other caller (e.g. AddModelStorageResolution, or AddMoonrakerEmulatorSeederDependencies)
        // already registered SlicerDbContext AND its factory (the two are always added together
        // by SlicerModuleExtensions.AddSlicerDatabase, which is what every real caller goes
        // through). AddCalibrationWorkerHealthDependencies must defer to that existing
        // registration entirely, adding nothing further, and IDbContextFactory<SlicerDbContext>
        // must still be resolvable afterward.
        IConfiguration configuration = BuildSplitDeploymentConfiguration(
            Path.Combine(Path.GetTempPath(), $"calibration-worker-health-{Guid.NewGuid():N}.db"));

        ServiceCollection services = new();
        _ = services.AddDbContext<SlicerDbContext>(options => options.UseInMemoryDatabase("stand-in"));
        _ = services.AddDbContextFactory<SlicerDbContext>(
            options => options.UseInMemoryDatabase("stand-in"), ServiceLifetime.Scoped);
        int countBefore = services.Count;

        _ = services.AddCalibrationWorkerHealthDependencies(configuration);

        _ = services.Count.Should().Be(countBefore);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        _ = scope.ServiceProvider.GetService<IDbContextFactory<SlicerDbContext>>().Should().NotBeNull(
            "the pre-existing registration must still resolve IDbContextFactory<SlicerDbContext> " +
            "after this no-op");
    }

    [Fact]
    public async Task AddCalibrationWorkerHealthDependencies_SplitDeploymentWithDbContextRegisteredWithoutFactory_StillRegistersFactory()
    {
        // Stress test for the defect two independent reviewers flagged in an earlier round: a
        // caller that registers SlicerDbContext WITHOUT its factory is unreachable through any
        // real startup path today (AddSlicerDatabase always adds both together), but
        // EnsureSlicerDatabaseRegistered's guard must not silently bless this state as "already
        // handled" just because SlicerDbContext happens to be present — doing so would leave
        // IDbContextFactory<SlicerDbContext> unregistered, and
        // CalibrationCapabilityService.GetWorkerHealthAsync would silently report worker health as
        // Unavailable forever. AddCalibrationWorkerHealthDependencies (via
        // EnsureSlicerDatabaseRegistered) must add the missing factory even when SlicerDbContext
        // alone is already present, without duplicating SlicerDbContext itself.
        //
        // The pre-existing context is registered using the SAME provider/connection string that
        // this configuration resolves to — mirroring the only realistic way this state can arise
        // (some other caller ran AddDbContext<SlicerDbContext> against the same IConfiguration and
        // forgot the factory). A follow-up review round confirmed by actually creating and
        // querying through the resulting factory that a mismatched-provider stand-in (e.g.
        // UseInMemoryDatabase) throws EF Core's "multiple database providers registered"
        // exception — that failure is an inherent, unavoidable consequence of configuring the same
        // DbContext type against two different providers in one container, not a defect in
        // EnsureSlicerDatabaseRegistered, so this test deliberately keeps both registrations on the
        // one provider a real caller would actually use.
        string dbPath = Path.Combine(Path.GetTempPath(), $"calibration-worker-health-{Guid.NewGuid():N}.db");
        IConfiguration configuration = BuildSplitDeploymentConfiguration(dbPath);
        string connectionString = configuration.GetValue<string>("ConnectionStrings:Default")!;

        ServiceCollection services = new();
        _ = services.AddDbContext<SlicerDbContext>(options => options.UseSqlite(connectionString));
        int dbContextRegistrationCountBefore =
            services.Count(sd => sd.ServiceType == typeof(SlicerDbContext));

        _ = services.AddCalibrationWorkerHealthDependencies(configuration);

        _ = services.Count(sd => sd.ServiceType == typeof(SlicerDbContext))
            .Should().Be(dbContextRegistrationCountBefore, "SlicerDbContext must not be registered a second time");

        try
        {
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            IDbContextFactory<SlicerDbContext>? factory =
                scope.ServiceProvider.GetService<IDbContextFactory<SlicerDbContext>>();
            _ = factory.Should().NotBeNull(
                "AddCalibrationWorkerHealthDependencies must guarantee IDbContextFactory<SlicerDbContext> " +
                "is resolvable even when some other caller registered SlicerDbContext on its own, " +
                "otherwise CalibrationCapabilityService.GetWorkerHealthAsync would silently break");

            // Not just resolvable — actually usable: create a real DbContext from the filled-in
            // factory and query it, proving the half-fill path leaves a fully working factory
            // rather than one that merely resolves and then explodes on first use.
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
}

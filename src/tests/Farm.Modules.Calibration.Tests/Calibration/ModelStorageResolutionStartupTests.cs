using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Modules.Calibration.Services.Capabilities;
using Farm.Modules.Calibration.Startup;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Modules.Calibration.Tests.Calibration;

/// <summary>
/// Regression tests for <see cref="ModelStorageResolutionStartup.AddModelStorageResolution"/>
/// (#2179): split/microservices API hosts must be able to resolve
/// <see cref="IModelStorageResolver"/> from their own composition root, backed by the shared
/// database/model-storage volume, without loading the rest of the slicer module.
/// </summary>
public sealed class ModelStorageResolutionStartupTests
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
    /// Every configuration shape that <see cref="CalibrationProfileResolutionStartup.IsSplitDeployment"/>
    /// recognizes as a split/microservices host: <c>DEPLOYMENT_MODE=split</c>,
    /// <c>DEPLOYMENT_MODE=microservices</c>, and <c>DEPLOYMENT_TYPE=microservices</c>.
    /// </summary>
    public static IEnumerable<object[]> SplitAndMicroservicesDeploymentConfigurations()
    {
        yield return ["DEPLOYMENT_MODE", "split"];
        yield return ["DEPLOYMENT_MODE", "microservices"];
        yield return ["DEPLOYMENT_TYPE", "microservices"];
    }

    [Theory]
    [MemberData(nameof(SplitAndMicroservicesDeploymentConfigurations))]
    public async Task AddModelStorageResolution_SplitOrMicroservicesDeployment_ResolvesAndServesStoredModelBytes(
        string deploymentModeKey,
        string deploymentModeValue)
    {
        // This is the fix's acceptance criterion end-to-end: in a split-mode host with a model on
        // the shared volume, IModelStorageResolver must be registered AND able to actually open
        // the bytes — proving CalibrationCapabilityService's
        // `_serviceProvider.GetService<IModelStorageResolver>() is not null` check now reflects a
        // functioning resolver, not just a non-null registration. Exercised for every
        // configuration shape IsSplitDeployment recognizes, not just DEPLOYMENT_MODE=microservices.
        string dbPath = Path.Combine(Path.GetTempPath(), $"model-storage-resolver-{Guid.NewGuid():N}.db");
        string modelRoot = Path.Combine(Path.GetTempPath(), $"model-storage-root-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(modelRoot);
        try
        {
            IConfiguration configuration = BuildSplitDeploymentConfiguration(
                dbPath, deploymentModeKey, deploymentModeValue);

            ServiceCollection services = new();
            _ = services.AddSingleton(NullLoggerFactory.Instance);
            _ = services.AddLogging();
            _ = services.AddSingleton<IStoragePathService>(new FakeStoragePathService(modelRoot));

            _ = services.AddModelStorageResolution(configuration);
            await using ServiceProvider provider = services.BuildServiceProvider();

            const string fileContent = "solid test\nendsolid test\n";
            string storedFileName = $"{Guid.NewGuid():N}.stl";
            await File.WriteAllTextAsync(Path.Combine(modelRoot, storedFileName), fileContent);

            Guid modelId = Guid.NewGuid();
            await using (AsyncServiceScope initScope = provider.CreateAsyncScope())
            {
                SlicerDbContext db = initScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
                _ = await db.Database.EnsureCreatedAsync();

                db.Models3D.Add(new Model3D
                {
                    Id = modelId,
                    Name = "test-model.stl",
                    FileName = storedFileName,
                    FilePath = "/",
                    FileSizeBytes = fileContent.Length,
                    FileHash = string.Empty,
                    FileFormat = ModelFileFormat.STL,
                    UploadedAt = DateTime.UtcNow,
                    IsValid = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                _ = await db.SaveChangesAsync();
            }

            // This is exactly the check CalibrationCapabilityService.GetCapabilitiesAsync
            // performs to compute modelStorageResolvable.
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            IModelStorageResolver? resolver = scope.ServiceProvider.GetService<IModelStorageResolver>();
            _ = resolver.Should().NotBeNull(
                "split/microservices hosts must resolve IModelStorageResolver so " +
                "calibrationSlicingOperational can become true");
            _ = resolver.Should().BeOfType<Model3DStorageResolver>();

            ModelResolutionResult result = await resolver!.OpenAsync(
                modelId, Guid.NewGuid(), expectedSha256: null, CancellationToken.None);

            _ = result.Succeeded.Should().BeTrue(
                "the resolver must actually serve bytes from the shared model-storage volume, " +
                "not just be non-null");

            // Read the served bytes back and compare against what was actually written, so this
            // proves the resolver serves the *correct* file, not merely that some stream opened.
            using StreamReader reader = new(result.Content!.Content);
            string servedContent = await reader.ReadToEndAsync();
            _ = servedContent.Should().Be(fileContent);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            Directory.Delete(modelRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AddModelStorageResolution_SplitDeployment_MakesCalibrationCapabilityServiceReportModelStorageResolvable()
    {
        // The full acceptance criterion from #2179: with a healthy, pinned-identity-attesting
        // worker and a resolvable model on the shared volume, CalibrationCapabilityService itself
        // (not just direct DI resolution) must report calibrationSlicingEnabled: true, and the
        // model_storage_unresolvable unavailable reason must no longer appear.
        string dbPath = Path.Combine(Path.GetTempPath(), $"model-storage-resolver-{Guid.NewGuid():N}.db");
        string modelRoot = Path.Combine(Path.GetTempPath(), $"model-storage-root-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(modelRoot);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DEPLOYMENT_MODE"] = "microservices",
                    ["DB_PROVIDER"] = "sqlite",
                    ["ConnectionStrings:Default"] = $"Data Source={dbPath}",
                    ["Slicer:Enabled"] = "true",
                })
                .Build();

            ServiceCollection services = new();
            _ = services.AddSingleton<IConfiguration>(configuration);
            _ = services.AddLogging();
            _ = services.AddSingleton<IStoragePathService>(new FakeStoragePathService(modelRoot));
            _ = services.AddModelStorageResolution(configuration);
            _ = services.AddScoped<ICalibrationCapabilityService, CalibrationCapabilityService>();

            await using ServiceProvider provider = services.BuildServiceProvider();

            const string fileContent = "solid healthy-worker\nendsolid healthy-worker\n";
            string storedFileName = $"{Guid.NewGuid():N}.stl";
            await File.WriteAllTextAsync(Path.Combine(modelRoot, storedFileName), fileContent);

            Guid serviceId = Guid.NewGuid();
            string workerServiceId = serviceId.ToString();
            const string workerVersion = CalibrationContractConstants.SlicerVersion;
            string pinnedCapabilitiesJson =
                "{\"capabilities\":[\"orcaslicer-upstream\"]," +
                "\"slicerBinarySha256\":\"deadbeef\",\"slicerContainerDigest\":\"sha256:cafef00d\"}";

            await using (AsyncServiceScope initScope = provider.CreateAsyncScope())
            {
                SlicerDbContext db = initScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
                _ = await db.Database.EnsureCreatedAsync();

                db.Models3D.Add(new Model3D
                {
                    Id = Guid.NewGuid(),
                    Name = "test-model.stl",
                    FileName = storedFileName,
                    FilePath = "/",
                    FileSizeBytes = fileContent.Length,
                    FileHash = string.Empty,
                    FileFormat = ModelFileFormat.STL,
                    UploadedAt = DateTime.UtcNow,
                    IsValid = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });

                db.SlicerServices.Add(new SlicerService
                {
                    Id = serviceId,
                    Name = "healthy-worker-service",
                    SlicerType = (int)SlicerType.OrcaSlicer,
                    Version = workerVersion,
                    CapabilitiesJson = pinnedCapabilitiesJson,
                    Status = WorkerStatus.Online,
                    LastSeen = DateTime.UtcNow,
                    ApiKey = "test-api-key",
                });

                db.Workers.Add(new Worker
                {
                    Id = Guid.NewGuid(),
                    ServiceId = workerServiceId,
                    Name = "healthy-worker",
                    Status = WorkerStatus.Online,
                    TotalSlots = 4,
                    ActiveJobs = 0,
                    LastHeartbeat = DateTime.UtcNow,
                    RegisteredAt = DateTime.UtcNow,
                    ApiKey = "test-api-key",
                    Version = workerVersion,
                    CapabilitiesJson = pinnedCapabilitiesJson,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    IsDisabled = false,
                });

                _ = await db.SaveChangesAsync();
            }

            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            ICalibrationCapabilityService capabilityService =
                scope.ServiceProvider.GetRequiredService<ICalibrationCapabilityService>();

            PlatformCapabilitiesDto capabilities =
                await capabilityService.GetCapabilitiesAsync(user: null, CancellationToken.None);

            _ = capabilities.CalibrationSlicingEnabled.Should().BeTrue(
                "a split-mode host with a healthy, pinned-identity worker and a resolvable model " +
                "on the shared volume must report calibrationSlicingEnabled: true end-to-end (#2179)");
            _ = capabilities.UnavailableReasons.Should().NotContain(
                reason => reason.Code == "model_storage_unresolvable",
                "IModelStorageResolver is now registered and can serve the model's bytes, so this " +
                "reason must no longer be reported in split/microservices deployments");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            Directory.Delete(modelRoot, recursive: true);
        }
    }

    [Fact]
    public void AddModelStorageResolution_SplitDeploymentWithResolverAlreadyRegistered_IsNoOp()
    {
        // Distinct from the monolith test above: this is a split/microservices host where some
        // other caller already registered IModelStorageResolver (e.g. a test harness, or a future
        // caller). AddModelStorageResolution must still exercise IsSplitDeployment's true branch
        // and stop at the "already registered" guard, rather than adding a second, conflicting
        // registration.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEPLOYMENT_MODE"] = "microservices",
                ["DB_PROVIDER"] = "sqlite",
                ["ConnectionStrings:Default"] = "Data Source=:memory:",
            })
            .Build();

        ServiceCollection services = new();
        _ = services.AddScoped<IModelStorageResolver, StandInModelStorageResolver>();
        int countBefore = services.Count;

        _ = services.AddModelStorageResolution(configuration);

        _ = services.Count.Should().Be(countBefore);
        _ = services.Should().ContainSingle(sd => sd.ServiceType == typeof(IModelStorageResolver))
            .Which.ImplementationType.Should().Be<StandInModelStorageResolver>();
    }

    [Fact]
    public void AddModelStorageResolution_MonolithDeployment_PreservesTheLocalResolverAndIsNoOp()
    {
        // Monolith hosts already register IModelStorageResolver via AddSlicerModule (or, here, a
        // stand-in). No DEPLOYMENT_MODE is set here, so this exercises IsSplitDeployment's false
        // branch (the method returns immediately, before the split-mode "already registered"
        // guard) — see the split-mode variant above for that second guard.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PROVIDER"] = "sqlite",
                ["ConnectionStrings:Default"] = "Data Source=:memory:",
            })
            .Build();

        ServiceCollection services = new();
        _ = services.AddScoped<IModelStorageResolver, StandInModelStorageResolver>();
        int countBefore = services.Count;

        _ = services.AddModelStorageResolution(configuration);

        _ = services.Count.Should().Be(countBefore);
        _ = services.Should().ContainSingle(sd => sd.ServiceType == typeof(IModelStorageResolver))
            .Which.ImplementationType.Should().Be<StandInModelStorageResolver>();
    }

    [Fact]
    public void AddModelStorageResolution_CalledTwice_IsIdempotent()
    {
        IConfiguration configuration = BuildSplitDeploymentConfiguration(
            Path.Combine(Path.GetTempPath(), $"model-storage-resolver-{Guid.NewGuid():N}.db"));

        ServiceCollection services = new();
        _ = services.AddSingleton<IStoragePathService>(new FakeStoragePathService(Path.GetTempPath()));
        _ = services.AddModelStorageResolution(configuration);
        int countAfterFirst = services.Count;

        _ = services.AddModelStorageResolution(configuration);

        _ = services.Count.Should().Be(countAfterFirst);
    }

    [Fact]
    public void AddModelStorageResolution_ForMonolithDeploymentModes_DoesNotRegisterAnything()
    {
        // Mirrors CalibrationProfileResolutionStartupTests' monolith coverage: neither an unset
        // deployment mode nor an explicit "monolith"/"standalone" value should trigger the
        // split-mode repository/resolver registration.
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
            _ = services.AddModelStorageResolution(configuration);

            _ = services.Should().BeEmpty();
        }
    }

    /// <summary>Stands in for the slicer module's in-process resolver on a monolith host.</summary>
    private sealed class StandInModelStorageResolver : IModelStorageResolver
    {
        public Task<ModelResolutionResult> OpenAsync(
            Guid model3DId, Guid requestingUserId, string? expectedSha256, CancellationToken ct) =>
            Task.FromResult(ModelResolutionResult.Failed(ModelResolutionFailure.NotFound));

        public Task<Model3D?> FindOwnedAsync(Guid model3DId, Guid requestingUserId, CancellationToken ct) =>
            Task.FromResult<Model3D?>(null);
    }

    /// <summary>Points model uploads at a real temp directory instead of a configured environment.</summary>
    private sealed class FakeStoragePathService(string modelUploadDirectory) : IStoragePathService
    {
        public string GetGcodeStorageDirectory() => modelUploadDirectory;

        public string GetThumbnailDirectory() => modelUploadDirectory;

        public string GetModelUploadDirectory() => modelUploadDirectory;

        public string GetSlicerProfilesDirectory() => modelUploadDirectory;

        public string GetSnapshotStorageDirectory() => modelUploadDirectory;

        public Task EnsureDirectoriesExistAsync() => Task.CompletedTask;
    }
}

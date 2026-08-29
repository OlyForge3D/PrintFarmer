using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Modules.Calibration.Startup;
using Farm.Slicer.Module;
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
    public async Task AddModelStorageResolution_SplitDeployment_ResolvesAndServesStoredModelBytes()
    {
        // This is the fix's acceptance criterion end-to-end: in a split-mode host with a model on
        // the shared volume, IModelStorageResolver must be registered AND able to actually open
        // the bytes — proving CalibrationCapabilityService's
        // `_serviceProvider.GetService<IModelStorageResolver>() is not null` check now reflects a
        // functioning resolver, not just a non-null registration.
        string dbPath = Path.Combine(Path.GetTempPath(), $"model-storage-resolver-{Guid.NewGuid():N}.db");
        string modelRoot = Path.Combine(Path.GetTempPath(), $"model-storage-root-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(modelRoot);
        try
        {
            IConfiguration configuration = BuildSplitDeploymentConfiguration(dbPath);

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
            await result.Content!.Content.DisposeAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            Directory.Delete(modelRoot, recursive: true);
        }
    }

    [Fact]
    public void AddModelStorageResolution_MonolithDeployment_PreservesTheLocalResolverAndIsNoOp()
    {
        // Monolith hosts already register IModelStorageResolver via AddSlicerModule (or, here, a
        // stand-in). AddModelStorageResolution must not add duplicate/conflicting registrations
        // on top of it.
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

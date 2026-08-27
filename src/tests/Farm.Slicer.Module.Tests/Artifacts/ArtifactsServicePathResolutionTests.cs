using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Slicer.Module.Tests.Artifacts;

/// <summary>
/// Covers <see cref="ArtifactsService.GetWithPathIfExistsAsync"/> directly, exercising both the
/// "artifact row exists but file missing from disk" and "artifact and file both exist" cases in a
/// single DB round trip, per issue #2094 (eliminating the previous double-lookup pattern of calling
/// ArtifactFileExistsAsync followed by GetWithPathAsync).
/// </summary>
public sealed class ArtifactsServicePathResolutionTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), $"printfarmer-artifact-path-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetWithPathIfExistsAsync_ArtifactRowMissing_ReturnsNull()
    {
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        Guid id = Guid.NewGuid();
        repository
            .Setup(candidate => candidate.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Artifact?)null);
        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);

        (Artifact Artifact, string FullPath)? result = await service.GetWithPathIfExistsAsync(id, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetWithPathIfExistsAsync_ArtifactRowExistsButFileMissing_ReturnsNull()
    {
        Artifact artifact = CreateArtifact("missing.gcode");
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(candidate => candidate.GetByIdAsync(artifact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);
        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);

        (Artifact Artifact, string FullPath)? result =
            await service.GetWithPathIfExistsAsync(artifact.Id, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetWithPathIfExistsAsync_ArtifactAndFileExist_ReturnsResolvedPath()
    {
        Artifact artifact = CreateArtifact("present.gcode");
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(candidate => candidate.GetByIdAsync(artifact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);
        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);

        // Build the expected path via Path.Combine on the individual segments (not by joining the
        // '/'-separated RelativePath as a single string) since ArtifactStorageFileSystem resolves
        // using the platform's native separator internally.
        string expectedFullPath = Path.Combine(_root, artifact.JobId.ToString(), artifact.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(expectedFullPath)!);
        await File.WriteAllTextAsync(expectedFullPath, "; test gcode");

        (Artifact Artifact, string FullPath)? result =
            await service.GetWithPathIfExistsAsync(artifact.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Artifact.Should().Be(artifact);
        result.Value.FullPath.Should().Be(expectedFullPath);
    }

    private Artifact CreateArtifact(string fileName)
    {
        Guid jobId = Guid.NewGuid();
        return new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            Kind = "gcode",
            FileName = fileName,
            RelativePath = $"{jobId}/{fileName}",
            ContentType = "application/octet-stream",
            SizeBytes = 1024,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private ArtifactsService CreateService(IArtifactsRepository repository, ArtifactsMetrics metrics)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(candidate => candidate.ContentRootPath).Returns(_root);
        IOptions<ArtifactStorageSettings> options = Options.Create(new ArtifactStorageSettings
        {
            RootPath = _root,
        });
        return new ArtifactsService(environment.Object, repository, options, metrics);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

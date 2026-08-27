using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Slicer.Module.Tests.Artifacts;

/// <summary>
/// Covers <see cref="ArtifactsService.OpenReadStreamAsync"/>'s hardened behavior: the file is
/// opened directly (no separate <c>File.Exists</c> precheck), and a file that vanishes or was
/// never there surfaces as a <see langword="null"/> result rather than an unhandled
/// <see cref="FileNotFoundException"/>/<see cref="DirectoryNotFoundException"/>. This mirrors the
/// hardening applied to artifact reads in #2094 and closes the residual check-then-open race
/// flagged during review of #2111.
/// </summary>
public sealed class ArtifactsServiceOpenReadStreamTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), $"printfarmer-artifact-openread-{Guid.NewGuid():N}");

    [Fact]
    public async Task OpenReadStreamAsync_FileExists_ReturnsContentStreamWithArtifactBytes()
    {
        Artifact artifact = CreateArtifact();
        WriteArtifactFile(artifact, "; gcode bytes");

        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetByIdAsync(artifact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);

        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);

        await using ArtifactContentStream? content = await service.OpenReadStreamAsync(artifact.Id, CancellationToken.None);

        content.Should().NotBeNull();
        content!.Artifact.Should().BeSameAs(artifact);
        using var reader = new StreamReader(content.Content, leaveOpen: true);
        (await reader.ReadToEndAsync()).Should().Be("; gcode bytes");
    }

    [Fact]
    public async Task OpenReadStreamAsync_ParentDirectoryMissing_ReturnsNullInsteadOfThrowing()
    {
        Artifact artifact = CreateArtifact();
        // Deliberately do not write the backing file or its parent directory: the artifact row
        // exists but nothing was ever written to disk. This exercises DirectoryNotFoundException.
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetByIdAsync(artifact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);

        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);

        ArtifactContentStream? content = await service.OpenReadStreamAsync(artifact.Id, CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task OpenReadStreamAsync_FileDeletedAfterUpload_ReturnsNullInsteadOfThrowing()
    {
        Artifact artifact = CreateArtifact();
        WriteArtifactFile(artifact, "; gcode bytes");
        // The parent directory exists (a file was uploaded here previously) but the specific
        // file itself has since vanished (e.g. concurrent cleanup). This exercises
        // FileNotFoundException, distinct from the DirectoryNotFoundException case above.
        File.Delete(ResolveFullPath(artifact));

        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetByIdAsync(artifact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);

        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);

        ArtifactContentStream? content = await service.OpenReadStreamAsync(artifact.Id, CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task OpenReadStreamAsync_PathReplacedByDirectory_ReturnsNullInsteadOfThrowing()
    {
        Artifact artifact = CreateArtifact();
        // The artifact's expected file path is occupied by a directory instead of a file (e.g.
        // it was replaced in the race window, or storage was corrupted externally). Opening a
        // directory as a FileStream throws UnauthorizedAccessException on both Windows and Unix
        // (EISDIR is normalized to UnauthorizedAccessException there), which is the exception
        // type added to OpenReadStreamAsync's catch filter alongside the not-found cases above.
        string fullPath = ResolveFullPath(artifact);
        Directory.CreateDirectory(fullPath);

        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetByIdAsync(artifact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);

        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);

        ArtifactContentStream? content = await service.OpenReadStreamAsync(artifact.Id, CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task OpenReadStreamAsync_ArtifactRowMissing_ReturnsNull()
    {
        Guid missingId = Guid.NewGuid();
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetByIdAsync(missingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Artifact?)null);

        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);

        ArtifactContentStream? content = await service.OpenReadStreamAsync(missingId, CancellationToken.None);

        content.Should().BeNull();
    }

    private Artifact CreateArtifact()
    {
        Guid jobId = Guid.NewGuid();
        return new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            Kind = "gcode",
            FileName = "model.gcode",
            RelativePath = $"{jobId}/model.gcode",
            ContentType = "application/octet-stream",
            SizeBytes = 13,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private string ResolveFullPath(Artifact artifact) => Path.Join(_root, artifact.RelativePath);

    private void WriteArtifactFile(Artifact artifact, string content)
    {
        string fullPath = ResolveFullPath(artifact);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
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

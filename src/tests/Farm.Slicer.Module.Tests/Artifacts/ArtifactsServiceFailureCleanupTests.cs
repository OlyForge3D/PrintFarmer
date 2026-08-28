using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Slicer.Module.Tests.Artifacts;

public sealed class ArtifactsServiceFailureCleanupTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), $"printfarmer-artifact-failure-{Guid.NewGuid():N}");

    [Fact]
    public async Task UploadAsync_CopyCancellation_DeletesPartialFile()
    {
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);
        IFormFile file = new TestFormFile(
            () => new CancelAfterFirstReadStream("partial upload"u8.ToArray()),
            "cancelled.gcode",
            length: 14);

        Func<Task> upload = async () => await service.UploadAsync(
            file,
            Guid.NewGuid(),
            workerId: null,
            "gcode",
            CancellationToken.None);

        await upload.Should().ThrowAsync<OperationCanceledException>();
        AssertNoArtifactFiles();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadAsync_RepositoryFailure_DeletesCompleteUncommittedFile()
    {
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(candidate => candidate.AddAsync(It.IsAny<Artifact>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("repository failed"));
        repository
            .Setup(candidate => candidate.GetByIdAsync(
                It.IsAny<Guid>(),
                CancellationToken.None))
            .ReturnsAsync((Artifact?)null);
        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);
        IFormFile file = new TestFormFile(
            () => new MemoryStream("complete upload"u8.ToArray(), writable: false),
            "failed.gcode",
            length: 15);

        Func<Task> upload = async () => await service.UploadAsync(
            file,
            Guid.NewGuid(),
            workerId: null,
            "gcode",
            CancellationToken.None);

        await upload.Should().ThrowAsync<InvalidOperationException>();
        AssertNoArtifactFiles();
    }

    [Fact]
    public async Task UploadForActiveLeaseAsync_RepositoryFailure_DeletesCompleteUncommittedFile()
    {
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(candidate => candidate.TryAddForActiveLeaseAsync(
                It.IsAny<Artifact>(),
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("repository failed"));
        repository
            .Setup(candidate => candidate.GetByIdAsync(
                It.IsAny<Guid>(),
                CancellationToken.None))
            .ReturnsAsync((Artifact?)null);
        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);
        IFormFile file = new TestFormFile(
            () => new MemoryStream("complete upload"u8.ToArray(), writable: false),
            "failed.gcode",
            length: 15);

        Func<Task> upload = async () => await service.UploadForActiveLeaseAsync(
            file,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "gcode",
            CancellationToken.None);

        await upload.Should().ThrowAsync<InvalidOperationException>();
        AssertNoArtifactFiles();
    }

    [Fact]
    public async Task UploadTextAsync_RepositoryFailure_DeletesCompleteUncommittedFile()
    {
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(candidate => candidate.AddAsync(It.IsAny<Artifact>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("repository failed"));
        repository
            .Setup(candidate => candidate.GetByIdAsync(
                It.IsAny<Guid>(),
                CancellationToken.None))
            .ReturnsAsync((Artifact?)null);
        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);

        Func<Task> upload = async () => await service.UploadTextAsync(
            "complete upload",
            "failed.log",
            Guid.NewGuid(),
            workerId: null,
            "log",
            CancellationToken.None);

        await upload.Should().ThrowAsync<InvalidOperationException>();
        AssertNoArtifactFiles();
    }

    [Fact]
    public async Task UploadAsync_AmbiguousRepositoryFailure_PreservesFileForReconciliation()
    {
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(candidate => candidate.AddAsync(
                It.IsAny<Artifact>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("commit outcome unknown"));
        repository
            .Setup(candidate => candidate.GetByIdAsync(
                It.IsAny<Guid>(),
                CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("probe failed"));
        using ArtifactsMetrics metrics = new();
        ArtifactsService service = CreateService(repository.Object, metrics);
        IFormFile file = new TestFormFile(
            () => new MemoryStream("complete upload"u8.ToArray(), writable: false),
            "ambiguous.gcode",
            length: 15);

        Func<Task> upload = async () => await service.UploadAsync(
            file,
            Guid.NewGuid(),
            workerId: null,
            "gcode",
            CancellationToken.None);

        await upload.Should().ThrowAsync<InvalidOperationException>();
        string[] remainingFiles =
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .ToArray();
        remainingFiles.Should().ContainSingle();
        Path.GetFileName(remainingFiles[0]).Should().EndWith("-ambiguous.gcode");
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

    private void AssertNoArtifactFiles()
    {
        Directory.Exists(_root).Should().BeTrue();
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestFormFile(
        Func<Stream> streamFactory,
        string fileName,
        long length) : IFormFile
    {
        public string ContentType { get; } = "application/octet-stream";
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length { get; } = length;
        public string Name { get; } = "file";
        public string FileName { get; } = fileName;
        public void CopyTo(Stream target) => throw new NotSupportedException();
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Stream OpenReadStream() => streamFactory();
    }

    private sealed class CancelAfterFirstReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private bool _hasRead;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_hasRead)
            {
                return ValueTask.FromException<int>(new OperationCanceledException(cancellationToken));
            }

            _hasRead = true;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 4)], cancellationToken);
        }
    }
}

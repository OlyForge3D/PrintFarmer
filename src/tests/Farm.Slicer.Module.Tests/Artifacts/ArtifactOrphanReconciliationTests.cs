using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Slicer.Module.Tests.Artifacts;

public sealed class ArtifactOrphanReconciliationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"artifact-reconciliation-{Guid.NewGuid():N}");
    private readonly string _outsideRoot =
        Path.Combine(Path.GetTempPath(), $"artifact-reconciliation-outside-{Guid.NewGuid():N}");

    [Fact]
    public async Task ScanAndCleanupAsync_StaleDatabaseLessPermanentFile_DeletesFile()
    {
        Guid artifactId = Guid.NewGuid();
        string path = CreatePermanentFile(artifactId, "orphan.gcode");
        SetStale(path);
        Mock<IArtifactsRepository> repository = CreateRepository();
        repository
            .Setup(candidate => candidate.GetByIdAsync(
                artifactId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Artifact?)null);

        int deleted = await CreateCleanupService(repository.Object)
            .ScanAndCleanupAsync(CancellationToken.None);

        deleted.Should().Be(1);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task ScanAndCleanupAsync_ActiveAndRecentStagingFiles_PreservesFiles()
    {
        Directory.CreateDirectory(_root);
        Guid activeArtifactId = Guid.NewGuid();
        string activeStagingPath = CreateStagingFile(
            activeArtifactId,
            ArtifactStorageFileSystem.StagingFileExtension,
            "active");
        string activeLeasePath = CreateStagingFile(
            activeArtifactId,
            ArtifactStorageFileSystem.LeaseFileExtension,
            string.Empty);
        SetStale(activeStagingPath);
        SetStale(activeLeasePath);
        using var activeLease = new FileStream(
            activeLeasePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        Guid recentArtifactId = Guid.NewGuid();
        string recentStagingPath = CreateStagingFile(
            recentArtifactId,
            ArtifactStorageFileSystem.StagingFileExtension,
            "recent");
        Mock<IArtifactsRepository> repository = CreateRepository();

        int deleted = await CreateCleanupService(repository.Object)
            .ScanAndCleanupAsync(CancellationToken.None);

        deleted.Should().Be(0);
        File.Exists(activeStagingPath).Should().BeTrue();
        File.Exists(activeLeasePath).Should().BeTrue();
        File.Exists(recentStagingPath).Should().BeTrue();
    }

    [Fact]
    public async Task ScanAndCleanupAsync_StaleStagingFileWithoutLease_DeletesFile()
    {
        Guid artifactId = Guid.NewGuid();
        string stagingPath = CreateStagingFile(
            artifactId,
            ArtifactStorageFileSystem.StagingFileExtension,
            "abandoned");
        SetStale(stagingPath);
        Mock<IArtifactsRepository> repository = CreateRepository();

        int deleted = await CreateCleanupService(repository.Object)
            .ScanAndCleanupAsync(CancellationToken.None);

        deleted.Should().Be(1);
        File.Exists(stagingPath).Should().BeFalse();
    }

    [Fact]
    public async Task ScanAndCleanupAsync_CommittedPermanentFile_PreservesFile()
    {
        Guid artifactId = Guid.NewGuid();
        string path = CreatePermanentFile(artifactId, "committed.gcode");
        SetStale(path);
        Artifact artifact = CreateArtifact(artifactId, path);
        Mock<IArtifactsRepository> repository = CreateRepository([artifact]);

        int deleted = await CreateCleanupService(repository.Object)
            .ScanAndCleanupAsync(CancellationToken.None);

        deleted.Should().Be(0);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ScanAndCleanupAsync_RecentDatabaseLessPermanentFile_PreservesFile()
    {
        Guid artifactId = Guid.NewGuid();
        string path = CreatePermanentFile(artifactId, "recent.gcode");
        Mock<IArtifactsRepository> repository = CreateRepository();

        int deleted = await CreateCleanupService(repository.Object)
            .ScanAndCleanupAsync(CancellationToken.None);

        deleted.Should().Be(0);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ScanAndCleanupAsync_RestartAfterPublishBeforeCommit_RemovesCrashState()
    {
        Guid artifactId = Guid.NewGuid();
        string permanentPath = CreatePermanentFile(artifactId, "crash-window.gcode");
        string leasePath = CreateStagingFile(
            artifactId,
            ArtifactStorageFileSystem.LeaseFileExtension,
            string.Empty);
        SetStale(permanentPath);
        SetStale(leasePath);
        Mock<IArtifactsRepository> repository = CreateRepository();
        repository
            .Setup(candidate => candidate.GetByIdAsync(
                artifactId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Artifact?)null);

        var restartedCleanupService =
            CreateCleanupService(repository.Object);
        int deleted = await restartedCleanupService.ScanAndCleanupAsync(
            CancellationToken.None);

        deleted.Should().Be(2);
        File.Exists(permanentPath).Should().BeFalse();
        File.Exists(leasePath).Should().BeFalse();
    }

    [Fact]
    public async Task ScanAndCleanupAsync_WriterPublishingBeforeRepositoryCommit_PreservesFile()
    {
        var repositoryEntered = new TaskCompletionSource<Artifact>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRepositoryCommit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writerRepository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        writerRepository
            .Setup(candidate => candidate.AddAsync(
                It.IsAny<Artifact>(),
                It.IsAny<CancellationToken>()))
            .Returns<Artifact, CancellationToken>(
                async (artifact, _) =>
                {
                    repositoryEntered.SetResult(artifact);
                    await allowRepositoryCommit.Task;
                    return artifact;
                });
        using ArtifactsMetrics metrics = new();
        ArtifactsService artifactsService =
            CreateArtifactsService(writerRepository.Object, metrics);
        IFormFile formFile = CreateFormFile("race.gcode", "race bytes");

        Task<Artifact> uploadTask = artifactsService.UploadAsync(
            formFile,
            Guid.NewGuid(),
            workerId: null,
            "gcode",
            CancellationToken.None);
        Artifact pendingArtifact = await repositoryEntered.Task;
        string permanentPath = Path.Combine(
            _root,
            pendingArtifact.RelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        string leasePath = Path.Combine(
            ArtifactStorageFileSystem.GetStagingDirectory(_root),
            pendingArtifact.Id.ToString("N") +
                ArtifactStorageFileSystem.LeaseFileExtension);
        SetStale(permanentPath);

        Mock<IArtifactsRepository> cleanupRepository = CreateRepository();
        int deleted = await CreateCleanupService(cleanupRepository.Object)
            .ScanAndCleanupAsync(CancellationToken.None);

        deleted.Should().Be(0);
        File.Exists(permanentPath).Should().BeTrue();
        allowRepositoryCommit.SetResult();
        Artifact committedArtifact = await uploadTask;
        committedArtifact.Id.Should().Be(pendingArtifact.Id);
        File.Exists(permanentPath).Should().BeTrue();
    }

    [Fact]
    public async Task ScanAndCleanupAsync_HostileAndOutsideEntries_PreservesEntries()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideRoot);
        string outsidePath = Path.Combine(_outsideRoot, "outside.gcode");
        await File.WriteAllTextAsync(outsidePath, "outside");
        SetStale(outsidePath);

        string unexpectedPath = Path.Combine(_root, "unexpected.bin");
        await File.WriteAllTextAsync(unexpectedPath, "unexpected");
        SetStale(unexpectedPath);

        string linkPath = Path.Combine(
            _root,
            $"{Guid.NewGuid()}-linked.gcode");
        bool linkCreated;
        try
        {
            _ = File.CreateSymbolicLink(linkPath, outsidePath);
            linkCreated = true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            linkCreated = false;
        }

        Artifact outsideArtifact = new()
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            Kind = "gcode",
            FileName = "outside.gcode",
            RelativePath = "../" + Path.GetFileName(_outsideRoot) + "/outside.gcode",
            SizeBytes = 7,
            Sha256 = "hash",
            CreatedAt = DateTime.UtcNow.AddDays(-2),
        };
        Mock<IArtifactsRepository> repository = CreateRepository();
        repository
            .Setup(candidate => candidate.GetCleanupInProgressAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([outsideArtifact]);

        int deleted = await CreateCleanupService(repository.Object)
            .ScanAndCleanupAsync(CancellationToken.None);

        deleted.Should().Be(0);
        File.Exists(outsidePath).Should().BeTrue();
        File.Exists(unexpectedPath).Should().BeTrue();
        if (linkCreated)
        {
            File.Exists(linkPath).Should().BeTrue();
        }
    }

    [Fact]
    public async Task UploadAsync_ReparseStagingDirectory_RejectsWriteOutsideRoot()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideRoot);
        string stagingPath =
            ArtifactStorageFileSystem.GetStagingDirectory(_root);
        _ = Directory.CreateSymbolicLink(stagingPath, _outsideRoot);
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        using ArtifactsMetrics metrics = new();
        ArtifactsService artifactsService =
            CreateArtifactsService(repository.Object, metrics);
        IFormFile formFile = CreateFormFile("hostile.gcode", "artifact");

        try
        {
            Func<Task> upload = () => artifactsService.UploadAsync(
                formFile,
                Guid.NewGuid(),
                workerId: null,
                "gcode",
                CancellationToken.None);

            await upload.Should().ThrowAsync<IOException>()
                .WithMessage("*staging directory must not be a reparse point*");
            Directory.EnumerateFileSystemEntries(_outsideRoot).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(stagingPath);
        }
    }

    [Fact]
    public async Task UploadAsync_HostileLegacyDirectories_PublishesDirectlyUnderRoot()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideRoot);
        string currentYearPath =
            Path.Combine(_root, DateTime.UtcNow.Year.ToString());
        string nextYearPath =
            Path.Combine(_root, DateTime.UtcNow.AddYears(1).Year.ToString());
        _ = Directory.CreateSymbolicLink(currentYearPath, _outsideRoot);
        _ = Directory.CreateSymbolicLink(nextYearPath, _outsideRoot);
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(candidate => candidate.AddAsync(
                It.IsAny<Artifact>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Artifact artifact, CancellationToken _) => artifact);
        using ArtifactsMetrics metrics = new();
        ArtifactsService artifactsService =
            CreateArtifactsService(repository.Object, metrics);
        IFormFile formFile = CreateFormFile("hostile.gcode", "artifact");

        try
        {
            Artifact artifact = await artifactsService.UploadAsync(
                formFile,
                Guid.NewGuid(),
                workerId: null,
                "gcode",
                CancellationToken.None);

            artifact.RelativePath.Should().Be(
                $"{artifact.Id}-hostile.gcode");
            File.Exists(Path.Combine(_root, artifact.RelativePath))
                .Should().BeTrue();
            Directory.EnumerateFileSystemEntries(_outsideRoot).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(currentYearPath);
            Directory.Delete(nextYearPath);
        }
    }

    private Mock<IArtifactsRepository> CreateRepository(
        IReadOnlyList<Artifact>? committedArtifacts = null)
    {
        IReadOnlyList<Artifact> artifacts = committedArtifacts ?? [];
        var repository = new Mock<IArtifactsRepository>(MockBehavior.Strict);
        repository
            .Setup(candidate => candidate.GetAllAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifacts);
        repository
            .Setup(candidate => candidate.GetByIdAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>(
                (artifactId, _) => Task.FromResult(
                    artifacts.FirstOrDefault(
                        artifact => artifact.Id == artifactId)));
        repository
            .Setup(candidate => candidate.GetCleanupInProgressAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return repository;
    }

    private ArtifactCleanupService CreateCleanupService(
        IArtifactsRepository repository)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(candidate => candidate.ContentRootPath).Returns(_root);
        return new ArtifactCleanupService(
            repository,
            Options.Create(new ArtifactStorageSettings
            {
                RootPath = _root,
                MaxAgeDays = null,
                MaxTotalBytes = null,
                EnableCleanupDryRun = false,
                CleanupReservationTimeoutMinutes = 1,
            }),
            environment.Object,
            Mock.Of<ILogger<ArtifactCleanupService>>());
    }

    private ArtifactsService CreateArtifactsService(
        IArtifactsRepository repository,
        ArtifactsMetrics metrics)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(candidate => candidate.ContentRootPath).Returns(_root);
        return new ArtifactsService(
            environment.Object,
            repository,
            Options.Create(new ArtifactStorageSettings { RootPath = _root }),
            metrics);
    }

    private string CreatePermanentFile(Guid artifactId, string fileName)
    {
        string directory = Path.Combine(
            _root,
            "2026",
            "08",
            "06",
            Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{artifactId}-{fileName}");
        File.WriteAllText(path, "artifact");
        return path;
    }

    private string CreateStagingFile(
        Guid artifactId,
        string extension,
        string content)
    {
        string stagingDirectory =
            ArtifactStorageFileSystem.GetStagingDirectory(_root);
        Directory.CreateDirectory(stagingDirectory);
        string path = Path.Combine(
            stagingDirectory,
            artifactId.ToString("N") + extension);
        File.WriteAllText(path, content);
        return path;
    }

    private Artifact CreateArtifact(Guid artifactId, string fullPath) => new()
    {
        Id = artifactId,
        JobId = Guid.NewGuid(),
        Kind = "gcode",
        FileName = Path.GetFileName(fullPath),
        RelativePath =
            ArtifactStorageFileSystem.GetRelativePath(_root, fullPath),
        SizeBytes = new FileInfo(fullPath).Length,
        Sha256 = "hash",
        CreatedAt = DateTime.UtcNow.AddDays(-2),
    };

    private static IFormFile CreateFormFile(string fileName, string content)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(
            new MemoryStream(bytes, writable: false),
            0,
            bytes.Length,
            "file",
            fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/octet-stream",
        };
    }

    private static void SetStale(string path) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(_outsideRoot))
        {
            Directory.Delete(_outsideRoot, recursive: true);
        }
    }

}

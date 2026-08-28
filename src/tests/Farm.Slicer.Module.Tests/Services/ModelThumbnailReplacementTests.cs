using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

public class ModelThumbnailReplacementTests : IDisposable
{
    private readonly List<MemoryStream> _streamsToDispose = [];

    public void Dispose()
    {
        foreach (MemoryStream stream in _streamsToDispose)
        {
            stream.Dispose();
        }

        GC.SuppressFinalize(this);
    }
    [Fact]
    public async Task ReplaceThumbnailAsync_WithMatchingOwner_AtomicallyReplacesThumbnailAndCleansArtifacts()
    {
        ReplacementFixture fixture = CreateFixture();
        byte[] replacementBytes = CreatePng(32, 24);

        Model3DThumbnailUpdateResultDto result = await fixture.Service.ReplaceThumbnailAsync(
            fixture.Model.Id,
            CreateFormFile(replacementBytes),
            fixture.Model.UploadedByUserId,
            isAdmin: false,
            ifMatch: RevisionETag.EncodeQuoted(fixture.Model.Revision),
            CancellationToken.None);

        Assert.Equal(fixture.Model.Id, result.Id);
        Assert.Equal($"/api/3d-models/thumbnail/{fixture.Model.Id}", result.ThumbnailUrl);
        string replacementPath = Path.Join(fixture.StoragePath, fixture.Model.ThumbnailFileName!);
        Assert.Equal(replacementBytes, await fixture.FileSystem.ReadAllBytesAsync(replacementPath));
        Assert.False(fixture.FileSystem.FileExists(fixture.ThumbnailPath));
        Assert.DoesNotContain(
            fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories),
            path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
        fixture.Repository.Verify(
            repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WithAdminAndDifferentOwner_ReplacesThumbnail()
    {
        ReplacementFixture fixture = CreateFixture();

        Model3DThumbnailUpdateResultDto result = await fixture.Service.ReplaceThumbnailAsync(
            fixture.Model.Id,
            CreateFormFile(CreatePng(8, 8)),
            Guid.NewGuid(),
            isAdmin: true,
            ifMatch: null,
            CancellationToken.None);

        Assert.Equal(fixture.Model.Id, result.Id);
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WithDifferentOwner_RejectsAndPreservesThumbnail()
    {
        ReplacementFixture fixture = CreateFixture();

        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ReplaceThumbnailAsync(
            fixture.Model.Id,
            CreateFormFile(CreatePng(8, 8)),
            Guid.NewGuid(),
            isAdmin: false,
            ifMatch: null,
            CancellationToken.None));

        Assert.Equal(fixture.OriginalThumbnailBytes, await fixture.FileSystem.ReadAllBytesAsync(fixture.ThumbnailPath));
        fixture.Repository.Verify(
            repository => repository.UpdateAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WithStaleETag_RejectsBeforeStorageMutation()
    {
        ReplacementFixture fixture = CreateFixture();

        _ = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => fixture.Service.ReplaceThumbnailAsync(
            fixture.Model.Id,
            CreateFormFile(CreatePng(8, 8)),
            fixture.Model.UploadedByUserId,
            isAdmin: false,
            ifMatch: "\"STALE\"",
            CancellationToken.None));

        Assert.Equal(fixture.OriginalThumbnailBytes, await fixture.FileSystem.ReadAllBytesAsync(fixture.ThumbnailPath));
        Assert.Single(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceThumbnailAsync_WithInvalidThumbnail_PreservesThumbnailAndCleansArtifacts(bool oversized)
    {
        ReplacementFixture fixture = CreateFixture();
        MemoryStream? oversizedStream = oversized ? new MemoryStream([0x89]) : null;
        if (oversizedStream is not null)
        {
            _streamsToDispose.Add(oversizedStream);
        }

        IFormFile thumbnail = oversized
            ? new FormFile(
                oversizedStream!,
                0,
                (10 * 1024 * 1024) + 1,
                "thumbnailFile",
                "thumbnail.png")
            : CreateFormFile([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x01]);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ReplaceThumbnailAsync(
            fixture.Model.Id,
            thumbnail,
            fixture.Model.UploadedByUserId,
            isAdmin: false,
            ifMatch: null,
            CancellationToken.None));

        Assert.Equal(fixture.OriginalThumbnailBytes, await fixture.FileSystem.ReadAllBytesAsync(fixture.ThumbnailPath));
        Assert.Single(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WhenCancelledDuringValidation_PreservesThumbnailAndCleansArtifacts()
    {
        ReplacementFixture fixture = CreateFixture();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ReplaceThumbnailAsync(
            fixture.Model.Id,
            CreateFormFile(CreatePng(8, 8)),
            fixture.Model.UploadedByUserId,
            isAdmin: false,
            ifMatch: null,
            cancellation.Token));

        Assert.Equal(fixture.OriginalThumbnailBytes, await fixture.FileSystem.ReadAllBytesAsync(fixture.ThumbnailPath));
        Assert.Single(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WhenAtomicStorageReplaceFails_PreservesThumbnailAndCleansArtifacts()
    {
        ReplacementFixture fixture = CreateFixture();
        fixture.FileSystem.MoveFileException = new IOException("storage failed");

        _ = await Assert.ThrowsAsync<IOException>(() => fixture.Service.ReplaceThumbnailAsync(
            fixture.Model.Id,
            CreateFormFile(CreatePng(8, 8)),
            fixture.Model.UploadedByUserId,
            isAdmin: false,
            ifMatch: null,
            CancellationToken.None));

        Assert.Equal(fixture.OriginalThumbnailBytes, await fixture.FileSystem.ReadAllBytesAsync(fixture.ThumbnailPath));
        Assert.Single(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WhenCommitFails_RestoresThumbnailAndCleansArtifacts()
    {
        ReplacementFixture fixture = CreateFixture(new InvalidOperationException("database failed"));
        DateTime previousUpdatedAt = fixture.Model.UpdatedAt;

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReplaceThumbnailAsync(
            fixture.Model.Id,
            CreateFormFile(CreatePng(8, 8)),
            fixture.Model.UploadedByUserId,
            isAdmin: false,
            ifMatch: null,
            CancellationToken.None));

        Assert.Equal(fixture.OriginalThumbnailBytes, await fixture.FileSystem.ReadAllBytesAsync(fixture.ThumbnailPath));
        Assert.Equal(previousUpdatedAt, fixture.Model.UpdatedAt);
        Assert.Equal(Path.GetFileName(fixture.ThumbnailPath), fixture.Model.ThumbnailFileName);
        Assert.Single(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WhenConcurrentCommitLoses_PreservesPriorAndDeletesCandidate()
    {
        ReplacementFixture fixture = CreateFixture(new DbUpdateConcurrencyException("concurrent update"));

        _ = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => fixture.Service.ReplaceThumbnailAsync(
            fixture.Model.Id,
            CreateFormFile(CreatePng(8, 8)),
            fixture.Model.UploadedByUserId,
            isAdmin: false,
            ifMatch: "\"0102\"",
            CancellationToken.None));

        Assert.Equal(fixture.OriginalThumbnailBytes, await fixture.FileSystem.ReadAllBytesAsync(fixture.ThumbnailPath));
        Assert.Single(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Model3DThumbnailUpdateResultDto_SerializesResponseFieldsAsCamelCase()
    {
        Model3DThumbnailUpdateResultDto dto = new()
        {
            Id = Guid.NewGuid(),
            ThumbnailUrl = "/api/3d-models/thumbnail/1",
            ETag = "\"0102\""
        };

        string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(dto.Id, document.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(dto.ThumbnailUrl, document.RootElement.GetProperty("thumbnailUrl").GetString());
        Assert.Equal(dto.ETag, document.RootElement.GetProperty("etag").GetString());
    }

    private static ReplacementFixture CreateFixture(Exception? saveException = null)
    {
        string storagePath = Path.Join(Path.GetTempPath(), "pfarm-thumbnail-replacement-tests", Guid.NewGuid().ToString("N"));
        Guid modelId = Guid.NewGuid();
        string thumbnailFileName = $"{modelId}_thumb.png";
        string thumbnailPath = Path.Join(storagePath, thumbnailFileName);
        byte[] originalThumbnailBytes = CreatePng(4, 4);
        TestFileSystem fileSystem = TestFileSystemFactory.WithThumbnail(thumbnailPath, originalThumbnailBytes);
        Model3D model = new()
        {
            Id = modelId,
            UploadedByUserId = Guid.NewGuid(),
            FileName = $"{modelId}.stl",
            FilePath = "/",
            FileHash = Guid.NewGuid().ToString("N"),
            FileFormat = ModelFileFormat.STL,
            FileSizeBytes = 1,
            IsValid = true,
            ThumbnailFileName = thumbnailFileName,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            Revision = 2
        };

        Mock<IModel3DFileRepository> repository = new(MockBehavior.Strict);
        repository.Setup(value => value.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        repository.Setup(value => value.UpdateAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        if (saveException is null)
        {
            repository.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }
        else
        {
            repository.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(saveException);
        }

        Mock<IFileManagementService> fileManagement = new();
        fileManagement.Setup(value => value.IsSafePath(It.IsAny<string>(), storagePath)).Returns(true);

        Mock<IStoragePathService> storagePathService = new(MockBehavior.Strict);
        storagePathService.Setup(value => value.GetModelUploadDirectory()).Returns(storagePath);

        Mock<IStoredFileOperationsService> fileOperations = new(MockBehavior.Strict);
        fileOperations.Setup(value => value.GenerateThumbnailFileName(modelId, ".png"))
            .Returns(thumbnailFileName);
        fileOperations.Setup(value => value.BuildModel3DThumbnailUrl(modelId))
            .Returns($"/api/3d-models/thumbnail/{modelId}");

        Model3DFileService service = new(
            repository.Object,
            new Mock<ITagRepository>().Object,
            new Mock<ILogger<Model3DFileService>>().Object,
            new ConfigurationBuilder().Build(),
            fileSystem,
            fileManagement.Object,
            new Mock<IFolderManagementService>().Object,
            storagePathService.Object,
            fileOperations.Object,
            thumbnailService: new Mock<IThumbnailGenerationService>().Object);

        return new ReplacementFixture(
            service,
            fileSystem,
            repository,
            model,
            storagePath,
            thumbnailPath,
            originalThumbnailBytes);
    }

    private FormFile CreateFormFile(byte[] bytes)
    {
        MemoryStream stream = new(bytes);
        _streamsToDispose.Add(stream);
        return new FormFile(stream, 0, stream.Length, "thumbnailFile", "thumbnail.png");
    }

    private static byte[] CreatePng(int width, int height)
    {
        using Image<Rgba32> image = new(width, height);
        using MemoryStream stream = new();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private sealed record ReplacementFixture(
        Model3DFileService Service,
        TestFileSystem FileSystem,
        Mock<IModel3DFileRepository> Repository,
        Model3D Model,
        string StoragePath,
        string ThumbnailPath,
        byte[] OriginalThumbnailBytes);
}

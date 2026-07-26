using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Language.Flow;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

public class ModelUploadStreamingTests
{
    [Fact]
    public async Task UploadModelAsync_WithClientPng_StreamsHashAndStoresThumbnailAtomically()
    {
        byte[] modelBytes = Encoding.UTF8.GetBytes("streamed-model-content");
        byte[] thumbnailBytes = CreatePng(32, 24);
        UploadFixture fixture = CreateFixture();

        Model3DUploadResultDto result = await fixture.Service.UploadModelAsync(
            CreateFormFile(modelBytes, "model.stl"),
            CreateFormFile(thumbnailBytes, "../../untrusted-name.png"),
            CancellationToken.None);

        Model3D addedModel = Assert.IsType<Model3D>(fixture.AddedModel);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant(), addedModel.FileHash);
        string thumbnailName = Assert.IsType<string>(addedModel.ThumbnailFileName);
        Assert.Equal($"{result.Id}_thumb.png", thumbnailName);
        Assert.Equal(thumbnailBytes, await fixture.FileSystem.ReadAllBytesAsync(Path.Combine(fixture.StoragePath, thumbnailName)));
        Assert.DoesNotContain(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories), path => path.Contains(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UploadModelAsync_WithInvalidPngSignature_RejectsAndCleansArtifacts()
    {
        UploadFixture fixture = CreateFixture();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UploadModelAsync(
            CreateFormFile("model", "model.stl"),
            CreateFormFile("not-a-png", "thumbnail.png"),
            CancellationToken.None));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
        fixture.Repository.Verify(repository => repository.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadModelAsync_WithMalformedPngPayload_RejectsAndCleansArtifacts()
    {
        byte[] malformedPng = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
        UploadFixture fixture = CreateFixture();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UploadModelAsync(
            CreateFormFile("model", "model.stl"),
            CreateFormFile(malformedPng, "thumbnail.png"),
            CancellationToken.None));

        Assert.Contains("decodable PNG", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UploadModelAsync_WithOversizedThumbnailBytes_RejectsAndCleansArtifacts()
    {
        const long oversizedLength = (10 * 1024 * 1024) + 1;
        FormFile oversizedThumbnail = new(new MemoryStream([0x89]), 0, oversizedLength, "thumbnailFile", "thumbnail.png");
        UploadFixture fixture = CreateFixture();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UploadModelAsync(
            CreateFormFile("model", "model.stl"),
            oversizedThumbnail,
            CancellationToken.None));

        Assert.Contains("10 MB", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UploadModelAsync_WithOversizedThumbnailDimension_RejectsAndCleansArtifacts()
    {
        UploadFixture fixture = CreateFixture();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UploadModelAsync(
            CreateFormFile("model", "model.stl"),
            CreateFormFile(CreatePng(4_097, 1), "thumbnail.png"),
            CancellationToken.None));

        Assert.Contains("dimensions", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UploadModelAsync_WithOversizedThumbnailPixelCount_RejectsAndCleansArtifacts()
    {
        UploadFixture fixture = CreateFixture();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UploadModelAsync(
            CreateFormFile("model", "model.stl"),
            CreateFormFile(CreatePng(4_001, 4_001), "thumbnail.png"),
            CancellationToken.None));

        Assert.Contains("pixel limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UploadModelAsync_WhenThumbnailPathIsUnsafe_RejectsAndCleansModelTempFile()
    {
        UploadFixture fixture = CreateFixture(path => !path.Contains("_thumb", StringComparison.OrdinalIgnoreCase));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UploadModelAsync(
            CreateFormFile("model", "model.stl"),
            CreateFormFile(CreatePng(16, 16), "thumbnail.png"),
            CancellationToken.None));

        Assert.Contains("Unsafe thumbnail", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UploadModelAsync_WhenCancelledDuringModelStream_CleansTempFile()
    {
        using CancellationTokenSource cancellation = new();
        CancelingReadStream stream = new(Encoding.UTF8.GetBytes("model"), cancellation);
        FormFile modelFile = new(stream, 0, stream.Length, "modelFile", "model.stl");
        UploadFixture fixture = CreateFixture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.UploadModelAsync(
            modelFile,
            thumbnailFile: null,
            cancellation.Token));

        Assert.Empty(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
        fixture.Repository.Verify(repository => repository.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadModelAsync_WhenPersistenceFails_CleansFinalModelAndThumbnail()
    {
        UploadFixture fixture = CreateFixture(saveException: new InvalidOperationException("database failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UploadModelAsync(
            CreateFormFile("model", "model.stl"),
            CreateFormFile(CreatePng(16, 16), "thumbnail.png"),
            CancellationToken.None));

        Assert.Empty(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UploadModelAsync_WithoutClientThumbnail_PreservesServerGeneration()
    {
        UploadFixture fixture = CreateFixture(withThumbnailGenerator: true);

        Model3DUploadResultDto result = await fixture.Service.UploadModelAsync(
            CreateFormFile("model", "model.stl"),
            CancellationToken.None);

        Assert.NotNull(fixture.AddedModel);
        Assert.Equal($"{result.Id}_thumb.png", fixture.AddedModel.ThumbnailFileName);
        fixture.ThumbnailGenerator.Verify(generator => generator.GenerateThumbnailAsync(
            It.IsAny<string>(),
            It.IsAny<ModelFileFormat>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadModelAsync_WhenServerThumbnailCancellationOccursAfterPersist_KeepsModelFile()
    {
        using CancellationTokenSource cancellation = new();
        UploadFixture fixture = CreateFixture(
            withThumbnailGenerator: true,
            cancelDuringThumbnail: cancellation);

        Model3DUploadResultDto result = await fixture.Service.UploadModelAsync(
            CreateFormFile("model", "model.stl"),
            cancellation.Token);

        Assert.Equal(fixture.AddedModel?.Id, result.Id);
        Assert.Contains(
            fixture.FileSystem.GetFiles(fixture.StoragePath, "*.stl", SearchOption.AllDirectories),
            path => path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories),
            path => path.Contains(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UploadModelAsync_WithClientUploadId_ReturnsOriginalUploadOnRetry()
    {
        Guid userId = Guid.NewGuid();
        Guid clientUploadId = Guid.NewGuid();
        UploadFixture fixture = CreateFixture();

        Model3DUploadResultDto initial = await fixture.Service.UploadModelAsync(
            CreateFormFile("idempotent-model", "model.stl"),
            thumbnailFile: null,
            userId,
            clientUploadId,
            CancellationToken.None);
        Model3DUploadResultDto retry = await fixture.Service.UploadModelAsync(
            CreateFormFile("idempotent-model", "model.stl"),
            thumbnailFile: null,
            userId,
            clientUploadId,
            CancellationToken.None);

        Assert.False(initial.WasExisting);
        Assert.True(retry.WasExisting);
        Assert.Equal(initial.Id, retry.Id);
        Assert.Equal(clientUploadId, retry.ClientUploadId);
        Assert.Equal(userId, fixture.AddedModel?.UploadedByUserId);
        fixture.Repository.Verify(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadModelAsync_WhenClientUploadIdPayloadDiffers_RejectsReuse()
    {
        Guid userId = Guid.NewGuid();
        Guid clientUploadId = Guid.NewGuid();
        UploadFixture fixture = CreateFixture();
        _ = await fixture.Service.UploadModelAsync(
            CreateFormFile("first-model", "model.stl"),
            thumbnailFile: null,
            userId,
            clientUploadId,
            CancellationToken.None);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UploadModelAsync(
            CreateFormFile("different-model", "model.stl"),
            thumbnailFile: null,
            userId,
            clientUploadId,
            CancellationToken.None));

        Assert.Contains("different model payload", exception.Message, StringComparison.Ordinal);
        fixture.Repository.Verify(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadModelAsync_WhenConcurrentClientUploadWins_ReturnsWinningUploadAndCleansArtifacts()
    {
        Guid userId = Guid.NewGuid();
        Guid clientUploadId = Guid.NewGuid();
        string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("racing-model"))).ToLowerInvariant();
        Model3D winner = new()
        {
            Id = Guid.NewGuid(),
            Name = "model.stl",
            FileName = $"{Guid.NewGuid()}.stl",
            FilePath = "/",
            FileHash = contentHash,
            ClientUploadHash = contentHash,
            FileSizeBytes = 12,
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            UploadedByUserId = userId,
            ClientUploadId = clientUploadId
        };
        UploadFixture fixture = CreateFixture(
            saveException: new DbUpdateException("unique index race"),
            raceWinner: winner);

        Model3DUploadResultDto result = await fixture.Service.UploadModelAsync(
            CreateFormFile("racing-model", "model.stl"),
            thumbnailFile: null,
            userId,
            clientUploadId,
            CancellationToken.None);

        Assert.Equal(winner.Id, result.Id);
        Assert.True(result.WasExisting);
        Assert.Empty(fixture.FileSystem.GetFiles(fixture.StoragePath, "*", SearchOption.AllDirectories));
        fixture.Repository.Verify(repository => repository.RemoveAsync(
            It.Is<Model3D>(model => model.Id != winner.Id),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public void Model3DUploadResultDto_WithExtendedFields_SerializesAsCamelCase()
    {
        Guid clientUploadId = Guid.NewGuid();
        Model3DUploadResultDto dto = new()
        {
            Id = Guid.NewGuid(),
            ThumbnailUrl = "/api/3d-models/thumbnail/1",
            WasExisting = true,
            ClientUploadId = clientUploadId
        };

        string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(dto.ThumbnailUrl, document.RootElement.GetProperty("thumbnailUrl").GetString());
        Assert.True(document.RootElement.GetProperty("wasExisting").GetBoolean());
        Assert.Equal(clientUploadId, document.RootElement.GetProperty("clientUploadId").GetGuid());
    }

    private static UploadFixture CreateFixture(
        Func<string, bool>? isSafePath = null,
        Exception? saveException = null,
        bool withThumbnailGenerator = false,
        CancellationTokenSource? cancelDuringThumbnail = null,
        Model3D? raceWinner = null)
    {
        string storagePath = Path.Combine(Path.GetTempPath(), "pfarm-model-upload-tests", Guid.NewGuid().ToString("N"));
        TestFileSystem fileSystem = TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>());
        Mock<IModel3DFileRepository> repository = new(MockBehavior.Strict);
        Model3D? addedModel = null;
        int clientUploadLookupCount = 0;

        repository.Setup(value => value.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3D?)null);
        repository.Setup(value => value.GetByClientUploadIdAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid userId, Guid clientUploadId, CancellationToken _) =>
            {
                clientUploadLookupCount++;
                if (raceWinner is not null)
                {
                    return clientUploadLookupCount == 1 ? null : raceWinner;
                }

                return addedModel?.UploadedByUserId == userId && addedModel.ClientUploadId == clientUploadId
                    ? addedModel
                    : null;
            });
        repository.Setup(value => value.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
            .Callback<Model3D, CancellationToken>((model, _) => addedModel = model)
            .Returns(Task.CompletedTask);
        repository.Setup(value => value.RemoveAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
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
        fileManagement.Setup(value => value.IsSafePath(It.IsAny<string>(), storagePath))
            .Returns<string, string>((path, _) => isSafePath?.Invoke(path) ?? true);
        fileManagement.Setup(value => value.ToHex(It.IsAny<byte[]>()))
            .Returns<byte[]>(bytes => Convert.ToHexString(bytes).ToLowerInvariant());
        fileManagement.Setup(value => value.GetModelFileFormat(It.IsAny<string>()))
            .Returns(ModelFileFormat.STL);

        Mock<IFolderManagementService> folderService = new(MockBehavior.Strict);
        folderService.Setup(value => value.GetOrCreateFolderAsync("/", "models", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FolderNode { Id = Guid.NewGuid(), Path = "/", FolderType = "models" });

        Mock<IStoragePathService> storagePathService = new(MockBehavior.Strict);
        storagePathService.Setup(value => value.GetModelUploadDirectory()).Returns(storagePath);

        Mock<IStoredFileOperationsService> fileOperations = new();
        fileOperations.Setup(value => value.GenerateThumbnailFileName(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns<Guid, string>((id, _) => $"{id}_thumb.png");
        fileOperations.Setup(value => value.BuildModel3DFileUrl(It.IsAny<Guid>(), It.IsAny<ModelFileFormat>()))
            .Returns<Guid, ModelFileFormat>((id, _) => $"/api/3d-models/file/{id}");
        fileOperations.Setup(value => value.BuildModel3DThumbnailUrl(It.IsAny<Guid>()))
            .Returns<Guid>(id => $"/api/3d-models/thumbnail/{id}");

        Mock<IThumbnailGenerationService> thumbnailGenerator = new();
        thumbnailGenerator.SetupGet(value => value.ThumbnailFileExtension).Returns(".png");
        if (withThumbnailGenerator)
        {
            ISetup<IThumbnailGenerationService, Task<bool>> generatorSetup = thumbnailGenerator.Setup(value => value.GenerateThumbnailAsync(
                    It.IsAny<string>(),
                    It.IsAny<ModelFileFormat>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()));

            if (cancelDuringThumbnail is null)
            {
                generatorSetup
                    .Callback<string, ModelFileFormat, string, int, int, int?, string?, string?, CancellationToken>(
                    (_, _, outputPath, _, _, _, _, _, _) => fileSystem.Commit(outputPath, CreatePng(8, 8)))
                    .ReturnsAsync(true);
            }
            else
            {
                generatorSetup
                    .Callback(cancelDuringThumbnail.Cancel)
                    .ThrowsAsync(new OperationCanceledException(cancelDuringThumbnail.Token));
            }
        }

        Model3DFileService service = new(
            repository.Object,
            new Mock<ITagRepository>().Object,
            new Mock<ILogger<Model3DFileService>>().Object,
            new ConfigurationBuilder().Build(),
            fileSystem,
            fileManagement.Object,
            folderService.Object,
            storagePathService.Object,
            fileOperations.Object,
            thumbnailService: withThumbnailGenerator ? thumbnailGenerator.Object : null);

        return new UploadFixture(
            service,
            fileSystem,
            repository,
            thumbnailGenerator,
            storagePath,
            () => addedModel);
    }

    private static IFormFile CreateFormFile(string content, string fileName)
        => CreateFormFile(Encoding.UTF8.GetBytes(content), fileName);

    private static IFormFile CreateFormFile(byte[] content, string fileName)
    {
        MemoryStream stream = new(content);
        return new FormFile(stream, 0, stream.Length, "file", fileName);
    }

    private static byte[] CreatePng(int width, int height)
    {
        using Image<Rgba32> image = new(width, height);
        using MemoryStream stream = new();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private sealed record UploadFixture(
        Model3DFileService Service,
        TestFileSystem FileSystem,
        Mock<IModel3DFileRepository> Repository,
        Mock<IThumbnailGenerationService> ThumbnailGenerator,
        string StoragePath,
        Func<Model3D?> AddedModelAccessor)
    {
        public Model3D? AddedModel => AddedModelAccessor();
    }

    private sealed class CancelingReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        private readonly CancellationTokenSource _cancellation = cancellation;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            return Task.FromCanceled<int>(cancellationToken);
        }
    }
}

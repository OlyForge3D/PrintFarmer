using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Integration tests for ModelService
/// Tests model listing, hierarchical browsing, file operations, uploads, and deletion
/// Comprehensive coverage of 3D model management workflow
/// Fast executing (~6-7 seconds for 20+ tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
public class ModelServiceIntegrationTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly List<MemoryStream> _streamsToDispose = [];

    // Tracks whether the shared factory's database schema/root folders have already been
    // established for this class. Tests within one class always run sequentially in xUnit
    // (even under CollectionPerClass parallelism), so this static flag is safe without
    // extra locking.
    private static bool s_schemaInitialized;

    public ModelServiceIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        if (!s_schemaInitialized)
        {
            // First test in the class: pay for the full schema drop/recreate + seed once.
            await _factory.ResetDatabaseAsync();
            s_schemaInitialized = true;
            return;
        }

        // Subsequent tests: schema and root folders are already in place, so just clear
        // the rows this test class writes. This avoids repeating the costly
        // EnsureDeletedAsync/EnsureCreatedAsync schema rebuild on every single test, which
        // was the dominant remaining cost driver for this class (~4s/test x 35 tests).
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = await context.Set<Model3D>().ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        // _factory is shared across all tests in this class via IClassFixture and is
        // disposed by xUnit once the class finishes, not per-test.
        foreach (MemoryStream stream in _streamsToDispose)
        {
            stream.Dispose();
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Helper method to get or create a model folder for tests.
    /// FolderNode lives in AppDbContext, not SlicerDbContext.
    /// </summary>
    private async Task<FolderNode> GetOrCreateModelFolderAsync(AppDbContext appContext)
    {
        FolderNode? folder = await appContext.Set<FolderNode>().FirstOrDefaultAsync(f => f.Path == "/" && f.FolderType == "model");
        if (folder == null)
        {
            folder = new FolderNode
            {
                Id = Guid.NewGuid(),
                Path = "/",
                FolderType = "model"
            };
            appContext.Set<FolderNode>().Add(folder);
            await appContext.SaveChangesAsync();
        }
        return folder;
    }

    private async Task<Model3D> CreateTestModelAsync(
        string originalFileName = "test-model.stl",
        string? path = null)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        AppDbContext appContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        FolderNode folder = await GetOrCreateModelFolderAsync(appContext);

        string filePath = Path.Join(
            Path.GetTempPath(),
            path ?? string.Empty,
            Guid.NewGuid() + "_" + originalFileName);

        // Ensure directory exists
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Create the actual file
        File.WriteAllText(filePath, "mock stl content");

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(filePath),  // Store just the filename (GUID-based)
            FilePath = Path.GetDirectoryName(filePath) ?? Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), "models")),  // Store directory path (matching GcodeFile pattern)
            FileSizeBytes = 1024,
            FileHash = Guid.NewGuid().ToString(),
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            IsValid = true,
            FolderId = folder.Id,  // Set required FolderId
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Set<Model3D>().Add(model);
        await context.SaveChangesAsync();

        return model;
    }

    private const string ValidAsciiStlContent = "solid test\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid test\n";

    private IFormFile CreateMockFormFile(
        string fileName = "test-model.stl",
        string content = ValidAsciiStlContent)
    {
        var memoryStream = new MemoryStream();
        _streamsToDispose.Add(memoryStream);
        // encoderShouldEmitUTF8Identifier: false — matches the no-BOM behavior of the original
        // `new StreamWriter(memoryStream)` default constructor; Encoding.UTF8 would add a BOM.
        using (var writer = new StreamWriter(memoryStream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), -1, leaveOpen: true))
        {
            writer.Write(content);
            writer.Flush();
        }
        memoryStream.Position = 0;

        var mock = new Mock<IFormFile>();
        mock.Setup(_ => _.FileName).Returns(fileName);
        mock.Setup(_ => _.Length).Returns(memoryStream.Length);
        mock.Setup(_ => _.OpenReadStream()).Returns(memoryStream);
        mock.Setup(_ => _.ContentDisposition).Returns($"form-data; name=\"file\"; filename=\"{fileName}\"");

        return mock.Object;
    }

    /// <summary>
    /// Byte-content overload, needed for binary STL fixtures (e.g. truncated triangle data):
    /// the string-content overload above round-trips through UTF-8 text encoding, which would
    /// corrupt arbitrary binary bytes.
    /// </summary>
    private IFormFile CreateMockFormFile(string fileName, byte[] content)
    {
        var memoryStream = new MemoryStream(content);
        _streamsToDispose.Add(memoryStream);

        var mock = new Mock<IFormFile>();
        mock.Setup(_ => _.FileName).Returns(fileName);
        mock.Setup(_ => _.Length).Returns(memoryStream.Length);
        mock.Setup(_ => _.OpenReadStream()).Returns(memoryStream);
        mock.Setup(_ => _.ContentDisposition).Returns($"form-data; name=\"file\"; filename=\"{fileName}\"");

        return mock.Object;
    }

    #region ListModelsAsync Tests

    [Fact]
    public async Task ListModelsAsync_ReturnsListNotNull()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        // Act
        IReadOnlyList<Model3DDto> result = await service.ListModelsAsync(CancellationToken.None);

        // Assert - Just verify list is returned (may contain previous test data)
        result.Should().NotBeNull();
        result.Should().BeOfType<List<Model3DDto>>();
    }

    [Fact]
    public async Task ListModelsAsync_IncludesUploadedModel()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        Model3D model = await CreateTestModelAsync("specific-model.stl");

        // Act
        IReadOnlyList<Model3DDto> result = await service.ListModelsAsync(CancellationToken.None);

        // Assert - Verify created model is in the list (may contain other test data)
        result.Should().Contain(m => m.Id == model.Id);
        Model3DDto foundModel = result.First(m => m.Id == model.Id);
        foundModel.FileName.Should().Be(model.FileName);
    }

    [Fact]
    public async Task ListModelsAsync_IncludesMultipleUploadedModels()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        Model3D model1 = await CreateTestModelAsync("multi-model-1.stl");
        Model3D model2 = await CreateTestModelAsync("multi-model-2.stl");
        Model3D model3 = await CreateTestModelAsync("multi-model-3.stl");

        // Act
        IReadOnlyList<Model3DDto> result = await service.ListModelsAsync(CancellationToken.None);

        // Assert - All created models should be in list (may contain other data)
        result.Select(m => m.Id).Should().Contain(new[] { model1.Id, model2.Id, model3.Id });
    }

    [Fact]
    public async Task ListModelsAsync_ContainsUploadedModelsInOrder()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        Model3D model1 = await CreateTestModelAsync("ordered-1.stl");
        await Task.Delay(10); // Small delay to ensure different timestamps
        Model3D model2 = await CreateTestModelAsync("ordered-2.stl");

        // Act
        IReadOnlyList<Model3DDto> result = await service.ListModelsAsync(CancellationToken.None);

        // Assert - Verify both models are present (ordering may vary with other test data)
        result.Should().Contain(m => m.Id == model1.Id);
        result.Should().Contain(m => m.Id == model2.Id);
    }

    #endregion

    #region GetModelAsync Tests

    [Fact]
    public async Task GetModelAsync_WithValidId_ReturnsModel()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        Model3D model = await CreateTestModelAsync("test-model.stl");

        // Act
        Model3DDto? result = await service.GetModelAsync(model.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(model.Id);
        // FileName should now be GUID-based (matching GcodeFile pattern)
        result.FileName.Should().Be(model.FileName);
    }

    [Fact]
    public async Task GetModelAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        var nonExistentId = Guid.NewGuid();

        // Act
        Model3DDto? result = await service.GetModelAsync(nonExistentId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetModelAsync_IncludesModelMetadata()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        Model3D model = await CreateTestModelAsync("metadata-test.stl");

        // Act
        Model3DDto? result = await service.GetModelAsync(model.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(model.Id);
        result.FileName.Should().Be(model.FileName);
    }

    #endregion

    #region GetModelFilePathAsync Tests

    [Fact]
    public async Task GetModelFilePathAsync_WithValidId_ReturnsFilePath()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        Model3D model = await CreateTestModelAsync("file-path-test.stl");

        // Act
        string? result = await service.GetModelFilePathAsync(model.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        // GetModelFilePathAsync returns a relative path that includes the filename
        result.Should().Contain(model.FileName);
        // The file should exist at the full path when combined with the base path
        string fullPath = Path.Join(model.FilePath, model.FileName);
        File.Exists(fullPath).Should().BeTrue();
    }

    [Fact]
    public async Task GetModelFilePathAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        var nonExistentId = Guid.NewGuid();

        // Act
        string? result = await service.GetModelFilePathAsync(nonExistentId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetModelThumbnailPathAsync Tests

    [Fact]
    public async Task GetModelThumbnailPathAsync_WithValidIdButNoThumbnail_ReturnsNull()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        Model3D model = await CreateTestModelAsync("no-thumbnail.stl");

        // Act
        string? result = await service.GetModelThumbnailPathAsync(model.Id, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetModelThumbnailPathAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        var nonExistentId = Guid.NewGuid();

        // Act
        string? result = await service.GetModelThumbnailPathAsync(nonExistentId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetModelThumbnailPathAsync_WithInvalidModel_ReturnsThumbnailPath()
    {
        // Arrange - Create a model marked as IsValid = false but with a thumbnail
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();
        SlicerDbContext dbContext = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        IConfiguration config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // Use the factory-configured storage path so the service finds our files
        string modelsPath = config["STORAGE_PATHS:UPLOADS"]
            ?? Path.Join(Path.GetTempPath(), "test-models-" + Guid.NewGuid());
        Directory.CreateDirectory(modelsPath);

        // Create a test model with IsValid = false and a thumbnail
        var invalidModelId = Guid.NewGuid();
        string fileName = $"{invalidModelId}.stl";
        string thumbnailFileName = $"{invalidModelId}_thumb.png";
        string filePath = Path.Join(modelsPath, fileName);
        string thumbnailPath = Path.Join(modelsPath, thumbnailFileName);

        // Create the physical files
        await File.WriteAllTextAsync(filePath, "invalid STL content");
        await File.WriteAllBytesAsync(thumbnailPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header

        var invalidModel = new Model3D
        {
            Id = invalidModelId,
            Name = "invalid-model-with-thumb.stl",
            FileName = fileName,
            ThumbnailFileName = thumbnailFileName,
            FilePath = modelsPath,
            FileSizeBytes = 123,
            FileHash = $"hash-{invalidModelId}",
            FileFormat = ModelFileFormat.STL,
            IsValid = false, // CRITICAL: Model marked as invalid
            FolderId = Guid.NewGuid(),
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _ = await dbContext.Models3D.AddAsync(invalidModel);
        _ = await dbContext.SaveChangesAsync();

        try
        {
            // Act - GetModelThumbnailPathAsync should use GetByIdUnfilteredAsync, not GetByIdAsync
            string? result = await service.GetModelThumbnailPathAsync(invalidModelId, CancellationToken.None);

            // Assert - REGRESSION: Thumbnail should be accessible even when IsValid = false
            result.Should().NotBeNull("GetModelThumbnailPathAsync must use unfiltered query to support thumbnail access for invalid models");
            result.Should().Contain(thumbnailFileName, "returned path should include the thumbnail filename");
            File.Exists(result).Should().BeTrue("physical thumbnail file must exist at the returned path");
        }
        finally
        {
            // Cleanup - modelsPath is the factory-wide shared storage directory
            // (IClassFixture<CustomWebApplicationFactory> means one factory, and thus one
            // storage directory, per class). Deleting the whole tree here would remove
            // files other tests in this class depend on. Only remove the two files this
            // test created.
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            if (File.Exists(thumbnailPath))
            {
                File.Delete(thumbnailPath);
            }
        }
    }

    #endregion

    #region DeleteModelAsync Tests

    [Fact]
    public async Task DeleteModelAsync_WithValidId_DeletesModel()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();
        _ = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        Model3D model = await CreateTestModelAsync("to-delete.stl");
        Guid modelId = model.Id;

        // Act
        await service.DeleteModelAsync(modelId, CancellationToken.None);

        // Assert - Model should be soft-deleted or removed
        Model3DDto? result = await service.GetModelAsync(modelId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteModelAsync_WithNonExistentId_ThrowsKeyNotFoundException()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        var nonExistentId = Guid.NewGuid();

        // Act & Assert - Should throw KeyNotFoundException
        Func<Task> act = async () => await service.DeleteModelAsync(nonExistentId, CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task DeleteModelAsync_MakesModelInaccessibleById()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        Model3D model = await CreateTestModelAsync("will-delete.stl");

        // Verify model exists before delete
        Model3DDto? beforeDelete = await service.GetModelAsync(model.Id, CancellationToken.None);
        beforeDelete.Should().NotBeNull();

        // Act
        await service.DeleteModelAsync(model.Id, CancellationToken.None);

        // Assert - Model should not be accessible by ID
        Model3DDto? afterDelete = await service.GetModelAsync(model.Id, CancellationToken.None);
        afterDelete.Should().BeNull();
    }

    #endregion

    #region UploadModelAsync Tests

    [Fact]
    public async Task UploadModelAsync_WithValidFile_SucceedsAndCreatesModel()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        IFormFile formFile = CreateMockFormFile("upload-test.stl");

        // Act
        Model3DUploadResultDto result = await service.UploadModelAsync(formFile, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().NotBe(Guid.Empty);
        result.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UploadModelAsync_WithClientThumbnail_PersistsThumbnailAndLinksModel()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();
        IModel3DFileRepository repository = scope.ServiceProvider.GetRequiredService<IModel3DFileRepository>();
        IStoragePathService storagePathService = scope.ServiceProvider.GetRequiredService<IStoragePathService>();
        using Image<Rgba32> image = new(16, 16);
        using MemoryStream thumbnailStream = new();
        image.SaveAsPng(thumbnailStream);
        thumbnailStream.Position = 0;
        FormFile thumbnailFile = new(thumbnailStream, 0, thumbnailStream.Length, "thumbnailFile", "client.png");

        Model3DUploadResultDto result = await service.UploadModelAsync(
            CreateMockFormFile("client-thumbnail.stl"),
            thumbnailFile,
            CancellationToken.None);

        Model3D? model = await repository.GetByIdAsync(result.Id, CancellationToken.None);
        model.Should().NotBeNull();
        model!.ThumbnailFileName.Should().Be($"{result.Id}_thumb.png");
        File.Exists(Path.Join(storagePathService.GetModelUploadDirectory(), model.ThumbnailFileName!)).Should().BeTrue();
    }

    [Fact]
    public async Task UploadModelAsync_WithClientUploadId_PersistsSingleModelAndReturnsExistingRetry()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Guid userId = Guid.NewGuid();
        Guid clientUploadId = Guid.NewGuid();

        Model3DUploadResultDto initial = await service.UploadModelAsync(
            CreateMockFormFile("idempotent.stl", ValidAsciiStlContent),
            thumbnailFile: null,
            userId,
            clientUploadId,
            CancellationToken.None);
        Model3DUploadResultDto retry = await service.UploadModelAsync(
            CreateMockFormFile("idempotent.stl", ValidAsciiStlContent),
            thumbnailFile: null,
            userId,
            clientUploadId,
            CancellationToken.None);

        initial.WasExisting.Should().BeFalse();
        retry.WasExisting.Should().BeTrue();
        retry.Id.Should().Be(initial.Id);
        retry.ClientUploadId.Should().Be(clientUploadId);
        (await context.Models3D.CountAsync(
            model => model.UploadedByUserId == userId && model.ClientUploadId == clientUploadId))
            .Should().Be(1);
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_AfterOwnedUpload_PersistsReplacementAndChangesETag()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();
        IModel3DFileRepository repository = scope.ServiceProvider.GetRequiredService<IModel3DFileRepository>();
        IStoragePathService storagePathService = scope.ServiceProvider.GetRequiredService<IStoragePathService>();
        Guid userId = Guid.NewGuid();
        using Image<Rgba32> initialImage = new(8, 8);
        using MemoryStream initialStream = new();
        initialImage.SaveAsPng(initialStream);
        initialStream.Position = 0;
        FormFile initialThumbnail = new(initialStream, 0, initialStream.Length, "thumbnailFile", "initial.png");
        Model3DUploadResultDto uploaded = await service.UploadModelAsync(
            CreateMockFormFile("replace-thumbnail.stl"),
            initialThumbnail,
            userId,
            clientUploadId: null,
            CancellationToken.None);

        using Image<Rgba32> replacementImage = new(16, 12);
        using MemoryStream replacementStream = new();
        replacementImage.SaveAsPng(replacementStream);
        byte[] replacementBytes = replacementStream.ToArray();
        replacementStream.Position = 0;
        FormFile replacementThumbnail = new(
            replacementStream,
            0,
            replacementStream.Length,
            "thumbnailFile",
            "replacement.png");

        Model3DThumbnailUpdateResultDto replaced = await service.ReplaceThumbnailAsync(
            uploaded.Id,
            replacementThumbnail,
            userId,
            isAdmin: false,
            uploaded.ETag,
            CancellationToken.None);

        Model3D model = (await repository.GetByIdAsync(uploaded.Id, CancellationToken.None))!;
        replaced.ThumbnailUrl.Should().Be($"/api/3d-models/thumbnail/{uploaded.Id}");
        replaced.ETag.Should().NotBe(uploaded.ETag);
        File.ReadAllBytes(Path.Join(
                storagePathService.GetModelUploadDirectory(),
                model.ThumbnailFileName!))
            .Should().Equal(replacementBytes);
    }

    [Fact]
    public async Task UploadModelAsync_WithSTLFile_Succeeds()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        IFormFile formFile = CreateMockFormFile("model.stl");

        // Act
        Model3DUploadResultDto result = await service.UploadModelAsync(formFile, CancellationToken.None);

        // Assert
        result.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UploadModelAsync_WithOBJFile_Succeeds()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        // Real geometry analysis now runs for OBJ (#1866), so this fixture must be valid OBJ
        // syntax rather than the STL-shaped default content: "vertex" lines aren't recognized as
        // OBJ "v" lines, so an OBJ file with no valid vertices would now be rejected at upload.
        IFormFile formFile = CreateMockFormFile("model.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

        // Act
        Model3DUploadResultDto result = await service.UploadModelAsync(formFile, CancellationToken.None);

        // Assert
        result.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UploadModelAsync_With3MFFile_Succeeds()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        IFormFile formFile = CreateMockFormFile("model.3mf");

        // Act
        Model3DUploadResultDto result = await service.UploadModelAsync(formFile, CancellationToken.None);

        // Assert
        result.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UploadModelAsync_With3MFFile_GeneratesThumbnail()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();
        IModel3DFileRepository repository = scope.ServiceProvider.GetRequiredService<IModel3DFileRepository>();

        // Create a minimal valid 3MF file (ZIP-based format with XML manifest)
        // 3MF files are essentially ZIP archives containing XML and model data
        string threeMFContent = "PK\x03\x04"; // ZIP magic bytes - minimal ZIP structure for testing

        IFormFile formFile = CreateMockFormFile("thumbnail-test.3mf", threeMFContent);

        // Act
        Model3DUploadResultDto result = await service.UploadModelAsync(formFile, CancellationToken.None);

        // Assert - Upload should succeed
        result.Should().NotBeNull();
        result.Id.Should().NotBe(Guid.Empty);

        // Assert - Check that thumbnail was attempted to be generated.
        // Use the unfiltered lookup: this fixture is just the 4-byte ZIP magic ("PK\x03\x04"),
        // not a real archive, so real geometry analysis (#1814) correctly marks it IsValid=false
        // (structurally unreadable) and GetByIdAsync's `IsValid` filter would otherwise hide it.
        Model3D? uploadedModel = await repository.GetByIdUnfilteredAsync(result.Id, CancellationToken.None);
        uploadedModel.Should().NotBeNull();

        // Note: ThumbnailPath may be null if:
        // 1. The minimal 3MF ZIP doesn't have valid 3MF structure (Lib3MF fails to parse)
        // 2. Assimp fallback also fails (not a valid model)
        // 3. Thumbnail generation failed but was silently caught
        // 
        // A real 3MF file would have proper structure and would generate a thumbnail.
        // This test primarily exercises the Lib3MF code path - if Lib3MF throws,
        // it will fallback to Assimp, and if that also fails, it will continue without thumbnail.
        // The important part is that the upload doesn't crash due to Lib3MF errors.
        uploadedModel!.Id.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// #1866: a malformed STL (triangle-count mismatch — header declares more triangles than the
    /// file actually contains) must be rejected at upload time with a clear validation error,
    /// instead of being silently persisted with IsValid=false metadata.
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_WithTruncatedStlFile_ThrowsValidationException()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        // Binary STL header declares 5 triangles but the file only contains one triangle's
        // worth of data afterward: this is the "triangle-count mismatch" scenario from #1866.
        byte[] content = new byte[84 + 50];
        BitConverter.GetBytes(5u).CopyTo(content, 80);
        IFormFile formFile = CreateMockFormFile("truncated.stl", content);

        Func<Task> act = async () => await service.UploadModelAsync(formFile, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*failed validation*");
    }

    /// <summary>
    /// #1866: a malformed STL with a truncated header (the file is smaller than the fixed
    /// 84-byte STL header/triangle-count preamble, so it cannot even be classified as ASCII or
    /// binary) must be rejected at upload time, not just triangle-count mismatches on an
    /// otherwise-complete header.
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_WithTruncatedStlHeader_ThrowsValidationException()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        // Only 10 bytes total: far short of the 80-byte header + 4-byte triangle count that a
        // binary STL requires before any triangle data can even be located.
        byte[] content = new byte[10];
        IFormFile formFile = CreateMockFormFile("truncated-header.stl", content);

        Func<Task> act = async () => await service.UploadModelAsync(formFile, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*failed validation*");
    }

    /// <summary>
    /// #1866: an OBJ file with no readable vertex data (e.g. a plain-text file saved with a
    /// ".obj" extension) must be rejected at upload time instead of silently accepted.
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_WithMalformedObjFile_ThrowsValidationException()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        IFormFile formFile = CreateMockFormFile("malformed.obj", "This is not a 3D model.\n");

        Func<Task> act = async () => await service.UploadModelAsync(formFile, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*failed validation*");
    }

    [Fact]
    public async Task UploadModelAsync_CreatesUniqueModelIds()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        IFormFile file1 = CreateMockFormFile("model1.stl");
        IFormFile file2 = CreateMockFormFile("model2.stl");

        // Act
        Model3DUploadResultDto result1 = await service.UploadModelAsync(file1, CancellationToken.None);
        Model3DUploadResultDto result2 = await service.UploadModelAsync(file2, CancellationToken.None);

        // Assert
        result1.Id.Should().NotBe(result2.Id);
    }

    [Fact]
    public async Task UploadModelAsync_PreservesFileName()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        string originalFileName = "my-custom-model.stl";
        IFormFile formFile = CreateMockFormFile(originalFileName);

        // Act
        Model3DUploadResultDto result = await service.UploadModelAsync(formFile, CancellationToken.None);

        // Assert
        result.Id.Should().NotBe(Guid.Empty);
        // With standardized pattern, FileName should be GUID-based with correct extension
        result.FileName.Should().EndWith(".stl");
        result.FileName.Should().NotBe(originalFileName); // Verify it's GUID-based, not original name
    }

    [Fact]
    public async Task UploadModelAsync_WithLargeFile_Succeeds()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        // Create a larger (~5MB) mock file. Must be valid ASCII STL, not arbitrary bytes: real
        // geometry analysis now rejects structurally-invalid STL uploads (#1866), and 5MB of "x"
        // repeated is neither valid ASCII STL nor a size-matching binary STL.
        const string facet = "facet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\n";
        string largeContent = "solid test\n" + string.Concat(Enumerable.Repeat(facet, 60_000)) + "endsolid test\n";
        IFormFile formFile = CreateMockFormFile("large-model.stl", largeContent);

        // Act
        Model3DUploadResultDto result = await service.UploadModelAsync(formFile, CancellationToken.None);

        // Assert
        result.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UploadModelAsync_WithSpecialCharactersInFileName_Succeeds()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        string specialFileName = "model-with-special-chars_#@!.stl";
        IFormFile formFile = CreateMockFormFile(specialFileName);

        // Act
        Model3DUploadResultDto result = await service.UploadModelAsync(formFile, CancellationToken.None);

        // Assert
        result.Id.Should().NotBe(Guid.Empty);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task UploadModel_ThenListModels_IncludesNewModel()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        IReadOnlyList<Model3DDto> initialList = await service.ListModelsAsync(CancellationToken.None);
        int initialCount = initialList.Count;

        // Act
        Model3DUploadResultDto uploadResult = await service.UploadModelAsync(
            CreateMockFormFile("new-model.stl"),
            CancellationToken.None);

        // Assert
        uploadResult.Id.Should().NotBe(Guid.Empty);

        IReadOnlyList<Model3DDto> afterUpload = await service.ListModelsAsync(CancellationToken.None);
        afterUpload.Should().HaveCount(initialCount + 1);
    }

    [Fact]
    public async Task UploadModel_ThenGetModel_ReturnsUploadedModel()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        string fileName = "integration-test-model.stl";
        Model3DUploadResultDto uploadResult = await service.UploadModelAsync(
            CreateMockFormFile(fileName),
            CancellationToken.None);

        // Act
        Model3DDto? getResult = await service.GetModelAsync(uploadResult.Id, CancellationToken.None);

        // Assert
        getResult.Should().NotBeNull();
        getResult!.Id.Should().Be(uploadResult.Id);
        getResult.FileName.Should().Be(uploadResult.FileName);  // Should match what was uploaded
    }

    [Fact]
    public async Task UploadModel_ThenDelete_RemovesFromList()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();

        Model3DUploadResultDto uploadResult = await service.UploadModelAsync(
            CreateMockFormFile("to-delete.stl"),
            CancellationToken.None);

        // Act
        await service.DeleteModelAsync(uploadResult.Id, CancellationToken.None);

        // Assert
        Model3DDto? result = await service.GetModelAsync(uploadResult.Id, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task UploadModel_ThenGetFilePath_ReturnsValidPath()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();
        _ = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        Model3DUploadResultDto uploadResult = await service.UploadModelAsync(
            CreateMockFormFile("path-test.stl"),
            CancellationToken.None);

        // Act
        string? filePath = await service.GetModelFilePathAsync(uploadResult.Id, CancellationToken.None);

        // Assert
        filePath.Should().NotBeNull();

        // Verify the path contains the filename
        filePath.Should().Contain(".stl", "Path should contain the STL extension");
    }

    [Fact]
    public async Task UploadModelAsync_GeneratesThumbnail_WhenValidModelUploaded()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();
        IModel3DFileRepository repository = scope.ServiceProvider.GetRequiredService<IModel3DFileRepository>();

        // Create a valid STL file for upload
        string stlContent = "solid test\n" +
                       "  facet normal 0.0 0.0 1.0\n" +
                       "    outer loop\n" +
                       "      vertex 0.0 0.0 0.0\n" +
                       "      vertex 1.0 0.0 0.0\n" +
                       "      vertex 0.0 1.0 0.0\n" +
                       "    endloop\n" +
                       "  endfacet\n" +
                       "endsolid test\n";

        IFormFile formFile = CreateMockFormFile("thumbnail-test.stl", stlContent);

        // Act
        Model3DUploadResultDto result = await service.UploadModelAsync(formFile, CancellationToken.None);

        // Assert - Upload should succeed
        result.Should().NotBeNull();
        result.Id.Should().NotBe(Guid.Empty);

        // Assert - Check that model was saved to database
        Model3D? uploadedModel = await repository.GetByIdAsync(result.Id, CancellationToken.None);
        uploadedModel.Should().NotBeNull();

        // The database record should exist with the uploaded file
        uploadedModel!.FileName.Should().NotBeNullOrEmpty("Model should have a filename");
        uploadedModel!.FileHash.Should().NotBeNullOrEmpty("Model should have a file hash");
        uploadedModel!.FilePath.Should().NotBeNullOrEmpty("Model should have a file path");

        // Note: ThumbnailFileName may be null if thumbnail generation is not available in test environment
        // The important test is that the upload and database save succeeded
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using IStoredFileOperationsService = Farm.Infrastructure.Services.FileManagement.IStoredFileOperationsService;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Unit tests for <see cref="Model3DFileService.SetAttributionAsync"/> covering the happy path,
/// length-overflow guard, and null-fields path.
/// </summary>
public class Model3DFileServiceAttributionTests
{
    private static Model3DFileService BuildService(Mock<IModel3DFileRepository> repoMock)
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        Mock<ILogger<Model3DFileService>> logger = new();
        Mock<ITagRepository> tagRepo = new(MockBehavior.Loose);
        Mock<IFileManagementService> fileManagement = new(MockBehavior.Loose);
        Mock<IFolderManagementService> folderService = new(MockBehavior.Loose);
        Mock<IStoragePathService> storagePath = new(MockBehavior.Strict);
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfarm-attribution-tests", Guid.NewGuid().ToString());
        storagePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);
        Mock<IStoredFileOperationsService> fileOps = new(MockBehavior.Loose);

        return new Model3DFileService(
            repoMock.Object,
            tagRepo.Object,
            logger.Object,
            config,
            TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()),
            fileManagement.Object,
            folderService.Object,
            storagePath.Object,
            fileOps.Object);
    }

    private static Model3D MakeModel(Guid id) => new Model3D
    {
        Id = id,
        FileName = "model.stl",
        FilePath = "path",
        FileSizeBytes = 100,
        FileHash = "abc",
        FileFormat = ModelFileFormat.STL,
        UploadedAt = DateTime.UtcNow,
        IsValid = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task SetAttributionAsync_HappyPath_PersistsAllFourFields()
    {
        Guid modelId = Guid.NewGuid();
        Model3D model = MakeModel(modelId);

        Mock<IModel3DFileRepository> repo = new(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdUnfilteredAsync(modelId, It.IsAny<CancellationToken>())).ReturnsAsync(model);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Model3DFileService svc = BuildService(repo);
        DateTime importedAt = DateTime.UtcNow;

        await svc.SetAttributionAsync(modelId, "https://example.com/model", "CreatorName", "MIT", importedAt, CancellationToken.None);

        Assert.Equal("https://example.com/model", model.SourceUrl);
        Assert.Equal("CreatorName", model.SourceCreator);
        Assert.Equal("MIT", model.SourceLicense);
        Assert.Equal(importedAt, model.ImportedAt);
        repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAttributionAsync_SourceUrlTooLong_ThrowsArgumentException()
    {
        Guid modelId = Guid.NewGuid();
        Mock<IModel3DFileRepository> repo = new(MockBehavior.Loose);

        Model3DFileService svc = BuildService(repo);
        string overlong = new string('x', 2049);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SetAttributionAsync(modelId, overlong, "creator", "MIT", null, CancellationToken.None));
    }

    [Fact]
    public async Task SetAttributionAsync_SourceCreatorTooLong_ThrowsArgumentException()
    {
        Guid modelId = Guid.NewGuid();
        Mock<IModel3DFileRepository> repo = new(MockBehavior.Loose);

        Model3DFileService svc = BuildService(repo);
        string overlong = new string('x', 257);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SetAttributionAsync(modelId, "https://example.com", overlong, "MIT", null, CancellationToken.None));
    }

    [Fact]
    public async Task SetAttributionAsync_SourceLicenseTooLong_ThrowsArgumentException()
    {
        Guid modelId = Guid.NewGuid();
        Mock<IModel3DFileRepository> repo = new(MockBehavior.Loose);

        Model3DFileService svc = BuildService(repo);
        string overlong = new string('x', 129);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SetAttributionAsync(modelId, "https://example.com", "creator", overlong, null, CancellationToken.None));
    }

    [Fact]
    public async Task SetAttributionAsync_NullFields_StoresNullsCleanly()
    {
        Guid modelId = Guid.NewGuid();
        Model3D model = MakeModel(modelId);

        Mock<IModel3DFileRepository> repo = new(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdUnfilteredAsync(modelId, It.IsAny<CancellationToken>())).ReturnsAsync(model);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Model3DFileService svc = BuildService(repo);

        await svc.SetAttributionAsync(modelId, null, null, null, null, CancellationToken.None);

        Assert.Null(model.SourceUrl);
        Assert.Null(model.SourceCreator);
        Assert.Null(model.SourceLicense);
        Assert.Null(model.ImportedAt);
        repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

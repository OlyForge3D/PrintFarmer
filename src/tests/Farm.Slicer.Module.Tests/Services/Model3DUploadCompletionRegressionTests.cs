using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Regression tests for 3D model upload completion lifecycle.
/// Ensures the upload success state only occurs after full completion: file storage, database record, and post-processing.
/// 
/// User-visible failure mode: Success toast appears too early while Close button remains blocked during thumbnail generation.
/// Backend contract requirement: UploadModelAsync must not return until all post-processing is complete.
/// </summary>
public class Model3DUploadCompletionRegressionTests
{
    private static IFormFile CreateFormFile(string name, string content, string fileName)
    {
        MemoryStream ms = new(Encoding.UTF8.GetBytes(content));
        return new FormFile(ms, 0, ms.Length, name, fileName);
    }

    private static Mock<IFolderManagementService> CreateFolderServiceMock()
    {
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = "/",
            FolderType = "models"
        };

        var mock = new Mock<IFolderManagementService>(MockBehavior.Strict);
        mock.Setup(f => f.GetOrCreateFolderAsync("/", "models", It.IsAny<CancellationToken>()))
            .ReturnsAsync(folder);
        return mock;
    }

    private static Mock<IStoredFileOperationsService> CreateStoredFileOperationsServiceMock()
    {
        var mock = new Mock<IStoredFileOperationsService>(MockBehavior.Loose);
        mock.Setup(s => s.BuildModel3DThumbnailUrl(It.IsAny<Guid>()))
            .Returns<Guid>(modelId => $"/api/3d-models/thumbnail/{modelId}");
        mock.Setup(s => s.BuildModel3DFileUrl(It.IsAny<Guid>(), It.IsAny<ModelFileFormat>()))
            .Returns<Guid, ModelFileFormat>((id, format) => $"/api/3d-models/file/{id}");
        mock.Setup(s => s.GenerateThumbnailFileName(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns<Guid, string>((id, ext) => $"{id}_thumb{ext}");
        return mock;
    }

    private static Mock<IFileManagementService> CreateFileManagementServiceMock()
    {
        var mock = new Mock<IFileManagementService>();
        mock.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        mock.Setup(s => s.ValidateModelExtension(It.IsAny<string>()));
        mock.Setup(s => s.GetModelFileFormat(It.IsAny<string>())).Returns(ModelFileFormat.STL);
        mock.Setup(s => s.GetModelFileFormatString(It.IsAny<ModelFileFormat>())).Returns("stl");
        mock.Setup(s => s.ToHex(It.IsAny<byte[]>())).Returns("abc123hash");
        return mock;
    }

    /// <summary>
    /// Test 1: Verifies that UploadModelAsync does not return until file is physically written to disk.
    /// Ensures frontend doesn't get success response before file is actually stored.
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_OnlyReturnsAfterFileWriteComplete()
    {
        // Arrange
        var fileSystemCallSequence = new List<string>();
        var mockFileSystem = new Mock<Farm.Infrastructure.IO.IFileSystem>();
        
        // Track when file operations happen
        mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(fs => fs.OpenWrite(It.IsAny<string>()))
            .Returns(() =>
            {
                fileSystemCallSequence.Add("OpenWrite");
                return new MemoryStream();
            });
        mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>()))
            .Returns(() =>
            {
                fileSystemCallSequence.Add("FileExists");
                return fileSystemCallSequence.Contains("MoveFile");
            });
        mockFileSystem.Setup(fs => fs.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Callback(() => fileSystemCallSequence.Add("MoveFile"));

        var mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3D?)null);
        mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
            .Callback(() => fileSystemCallSequence.Add("AddAsync"))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => fileSystemCallSequence.Add("SaveChangesAsync"))
            .Returns(Task.CompletedTask);

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var mockLogger = new Mock<ILogger<Model3DFileService>>();
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        mockStoragePath.Setup(x => x.GetModelUploadDirectory())
            .Returns(Path.Combine(Path.GetTempPath(), "test-models"));

        var service = new Model3DFileService(
            mockRepo.Object,
            new Mock<ITagRepository>().Object,
            mockLogger.Object,
            config,
            mockFileSystem.Object,
            CreateFileManagementServiceMock().Object,
            CreateFolderServiceMock().Object,
            mockStoragePath.Object,
            CreateStoredFileOperationsServiceMock().Object);

        var file = CreateFormFile("file", "test-content", "model.stl");

        // Act
        var result = await service.UploadModelAsync(file, CancellationToken.None);

        // Assert
        result.Should().NotBeNull("upload should complete successfully");
        
        // Critical assertion: File operations must complete before method returns
        fileSystemCallSequence.Should().Contain("MoveFile", 
            "temp file must be moved to final location before returning success");
        fileSystemCallSequence.Should().Contain("AddAsync", 
            "database record must be created before returning success");
        
        var moveFileIndex = fileSystemCallSequence.IndexOf("MoveFile");
        var addAsyncIndex = fileSystemCallSequence.IndexOf("AddAsync");
        moveFileIndex.Should().BeLessThan(addAsyncIndex,
            "file must be physically written before database record is created");
    }

    /// <summary>
    /// Test 2: Verifies that UploadModelAsync does not return until database record is committed.
    /// Prevents race condition where frontend queries models list before new model is visible.
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_OnlyReturnsAfterDatabaseCommit()
    {
        // Arrange
        var operationSequence = new List<string>();
        var mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        
        mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3D?)null);
        mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
            .Callback(() => operationSequence.Add("AddAsync"))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => operationSequence.Add("SaveChangesAsync"))
            .Returns(Task.CompletedTask);

        var mockFileSystem = new Mock<Farm.Infrastructure.IO.IFileSystem>();
        mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(fs => fs.OpenWrite(It.IsAny<string>())).Returns(new MemoryStream());
        mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(fs => fs.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Callback(() => operationSequence.Add("MoveFile"));

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        mockStoragePath.Setup(x => x.GetModelUploadDirectory())
            .Returns(Path.Combine(Path.GetTempPath(), "test-models"));

        var service = new Model3DFileService(
            mockRepo.Object,
            new Mock<ITagRepository>().Object,
            new Mock<ILogger<Model3DFileService>>().Object,
            config,
            mockFileSystem.Object,
            CreateFileManagementServiceMock().Object,
            CreateFolderServiceMock().Object,
            mockStoragePath.Object,
            CreateStoredFileOperationsServiceMock().Object);

        var file = CreateFormFile("file", "test-content", "model.stl");

        // Act
        var result = await service.UploadModelAsync(file, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        operationSequence.Should().Contain("SaveChangesAsync",
            "database changes must be committed before returning success");
        
        var saveIndex = operationSequence.IndexOf("SaveChangesAsync");
        operationSequence.Count.Should().Be(saveIndex + 1,
            "SaveChangesAsync must be the final operation before method returns");
    }

    /// <summary>
    /// Test 3: Verifies thumbnail generation completes before method returns.
    /// Ensures Close button isn't blocked by background thumbnail work when user sees success toast.
    /// 
    /// This is the primary user-visible failure mode reported in the issue.
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_WithThumbnailService_OnlyReturnsAfterThumbnailComplete()
    {
        // Arrange
        var operationSequence = new List<string>();
        var thumbnailGenerationStarted = new TaskCompletionSource<bool>();
        var thumbnailGenerationComplete = new TaskCompletionSource<bool>();

        var mockThumbnailService = new Mock<IThumbnailGenerationService>(MockBehavior.Strict);
        mockThumbnailService.Setup(t => t.ThumbnailFileExtension).Returns(".png");
        mockThumbnailService.Setup(t => t.GenerateThumbnailAsync(
                It.IsAny<string>(),
                It.IsAny<ModelFileFormat>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                operationSequence.Add("ThumbnailGenerationStarted");
                thumbnailGenerationStarted.SetResult(true);
                
                // Simulate long-running thumbnail generation (e.g., complex 3MF conversion)
                await Task.Delay(100);
                
                operationSequence.Add("ThumbnailGenerationComplete");
                thumbnailGenerationComplete.SetResult(true);
                return true;
            });

        var mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3D?)null);
        mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => operationSequence.Add("SaveChangesAsync"))
            .Returns(Task.CompletedTask);

        var mockFileSystem = new Mock<Farm.Infrastructure.IO.IFileSystem>();
        mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(fs => fs.OpenWrite(It.IsAny<string>())).Returns(new MemoryStream());
        mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(fs => fs.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()));

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        mockStoragePath.Setup(x => x.GetModelUploadDirectory())
            .Returns(Path.Combine(Path.GetTempPath(), "test-models"));

        var service = new Model3DFileService(
            mockRepo.Object,
            new Mock<ITagRepository>().Object,
            new Mock<ILogger<Model3DFileService>>().Object,
            config,
            mockFileSystem.Object,
            CreateFileManagementServiceMock().Object,
            CreateFolderServiceMock().Object,
            mockStoragePath.Object,
            CreateStoredFileOperationsServiceMock().Object,
            analysisService: null,
            thumbnailService: mockThumbnailService.Object);

        var file = CreateFormFile("file", "test-content", "model.stl");

        // Act
        var uploadTask = service.UploadModelAsync(file, CancellationToken.None);
        
        // Wait for thumbnail generation to start
        await thumbnailGenerationStarted.Task;
        
        // Critical assertion: Upload should not complete while thumbnail is still generating
        uploadTask.IsCompleted.Should().BeFalse(
            "UploadModelAsync must wait for thumbnail generation to complete before returning");
        
        // Wait for upload to complete
        var result = await uploadTask;

        // Assert
        result.Should().NotBeNull();
        
        thumbnailGenerationComplete.Task.IsCompleted.Should().BeTrue(
            "thumbnail generation must be complete when upload returns");
        
        operationSequence.Should().Contain("ThumbnailGenerationComplete",
            "thumbnail must be fully generated before method returns");
        
        var thumbnailCompleteIndex = operationSequence.IndexOf("ThumbnailGenerationComplete");
        operationSequence.Count.Should().Be(thumbnailCompleteIndex + 1,
            "thumbnail generation must be the final operation before method returns");
    }

    /// <summary>
    /// Test 4: Verifies that even if thumbnail generation fails, the method still returns success
    /// after attempting generation (best-effort pattern).
    /// Upload shouldn't fail just because thumbnail generation failed.
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_ThumbnailFailure_StillReturnsSuccessAfterAttempt()
    {
        // Arrange
        var operationSequence = new List<string>();
        var mockThumbnailService = new Mock<IThumbnailGenerationService>(MockBehavior.Strict);
        mockThumbnailService.Setup(t => t.ThumbnailFileExtension).Returns(".png");
        mockThumbnailService.Setup(t => t.GenerateThumbnailAsync(
                It.IsAny<string>(),
                It.IsAny<ModelFileFormat>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                operationSequence.Add("ThumbnailAttemptStarted");
                await Task.Delay(50);
                operationSequence.Add("ThumbnailAttemptFailed");
                throw new InvalidOperationException("Thumbnail generation failed");
            });

        var mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3D?)null);
        mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockFileSystem = new Mock<Farm.Infrastructure.IO.IFileSystem>();
        mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(fs => fs.OpenWrite(It.IsAny<string>())).Returns(new MemoryStream());
        mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(fs => fs.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()));

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        mockStoragePath.Setup(x => x.GetModelUploadDirectory())
            .Returns(Path.Combine(Path.GetTempPath(), "test-models"));

        var service = new Model3DFileService(
            mockRepo.Object,
            new Mock<ITagRepository>().Object,
            new Mock<ILogger<Model3DFileService>>().Object,
            config,
            mockFileSystem.Object,
            CreateFileManagementServiceMock().Object,
            CreateFolderServiceMock().Object,
            mockStoragePath.Object,
            CreateStoredFileOperationsServiceMock().Object,
            analysisService: null,
            thumbnailService: mockThumbnailService.Object);

        var file = CreateFormFile("file", "test-content", "model.stl");

        // Act
        var result = await service.UploadModelAsync(file, CancellationToken.None);

        // Assert
        result.Should().NotBeNull("upload should succeed despite thumbnail failure");
        
        operationSequence.Should().Contain("ThumbnailAttemptFailed",
            "thumbnail generation must be attempted (and allowed to fail) before returning");
        
        // Verify best-effort pattern: failure is logged but doesn't bubble up
        mockThumbnailService.Verify(t => t.GenerateThumbnailAsync(
            It.IsAny<string>(),
            It.IsAny<ModelFileFormat>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test 5: Verifies the complete upload pipeline order: file write → DB record → thumbnail.
    /// This is the golden path that the success toast depends on.
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_CompletePipeline_ExecutesInCorrectOrder()
    {
        // Arrange
        var operationSequence = new List<string>();
        
        var mockFileSystem = new Mock<Farm.Infrastructure.IO.IFileSystem>();
        mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(fs => fs.OpenWrite(It.IsAny<string>()))
            .Returns(() =>
            {
                operationSequence.Add("FileWrite");
                return new MemoryStream();
            });
        mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(fs => fs.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Callback(() => operationSequence.Add("FileMove"));

        var mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3D?)null);
        mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
            .Callback(() => operationSequence.Add("DatabaseAdd"))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => operationSequence.Add("DatabaseCommit"))
            .Returns(Task.CompletedTask);

        var mockThumbnailService = new Mock<IThumbnailGenerationService>(MockBehavior.Strict);
        mockThumbnailService.Setup(t => t.ThumbnailFileExtension).Returns(".png");
        mockThumbnailService.Setup(t => t.GenerateThumbnailAsync(
                It.IsAny<string>(),
                It.IsAny<ModelFileFormat>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                operationSequence.Add("ThumbnailGeneration");
                return Task.FromResult(true);
            });

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        mockStoragePath.Setup(x => x.GetModelUploadDirectory())
            .Returns(Path.Combine(Path.GetTempPath(), "test-models"));

        var service = new Model3DFileService(
            mockRepo.Object,
            new Mock<ITagRepository>().Object,
            new Mock<ILogger<Model3DFileService>>().Object,
            config,
            mockFileSystem.Object,
            CreateFileManagementServiceMock().Object,
            CreateFolderServiceMock().Object,
            mockStoragePath.Object,
            CreateStoredFileOperationsServiceMock().Object,
            analysisService: null,
            thumbnailService: mockThumbnailService.Object);

        var file = CreateFormFile("file", "test-content", "model.stl");

        // Act
        var result = await service.UploadModelAsync(file, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        
        // Verify complete pipeline executed in correct order
        operationSequence.Should().ContainInOrder(new[]
        {
            "FileWrite",
            "FileMove",
            "DatabaseAdd",
            "DatabaseCommit",
            "ThumbnailGeneration"
        }, "upload pipeline must execute in the correct order: file → database → thumbnail");
        
        // Verify no operations happen after the last expected step
        var thumbnailIndex = operationSequence.LastIndexOf("ThumbnailGeneration");
        thumbnailIndex.Should().Be(operationSequence.Count - 1,
            "no operations should occur after thumbnail generation completes");
    }
}

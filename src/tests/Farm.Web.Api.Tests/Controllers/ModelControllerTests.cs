using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.FolderManagement;
using Farm.Web.Api.Services.Model;
using Farm.Web.Api.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers
{
    public class ModelControllerTests
    {
        private Mock<IFileManagementService> CreateMockFileManagementService()
        {
            Mock<IFileManagementService> mock = new Mock<IFileManagementService>(MockBehavior.Loose);
            _ = mock.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            return mock;
        }

        private Mock<IUnitOfWork> CreateMockUnitOfWork()
        {
            var mockUoW = new Mock<IUnitOfWork>(MockBehavior.Loose);
            var mockModel3dRepo = new Mock<IModel3DFileRepository>(MockBehavior.Loose);
            mockUoW.Setup(u => u.Model3dFiles).Returns(mockModel3dRepo.Object);
            return mockUoW;
        }

        private Mock<IFolderManagementService> CreateMockFolderService()
        {
            return new Mock<IFolderManagementService>(MockBehavior.Loose);
        }

        private Mock<IStoredFileOperationsService> CreateStoredFileOperationsServiceMock()
        {
            var mock = new Mock<IStoredFileOperationsService>(MockBehavior.Loose);
            // Default behavior: delegate to real path construction
            mock.Setup(x => x.GetFullFilePath(It.IsAny<StoredFile>()))
                .Returns<StoredFile>(f => Path.Combine(f.FilePath, f.FileName));
            mock.Setup(x => x.GetFullThumbnailPath(It.IsAny<StoredFile>()))
                .Returns<StoredFile>(f => !string.IsNullOrEmpty(f.ThumbnailFileName)
                    ? Path.Combine(f.FilePath, f.ThumbnailFileName)
                    : null);
            mock.Setup(x => x.GenerateThumbnailFileName(It.IsAny<Guid>(), It.IsAny<string>()))
                .Returns<Guid, string>((id, ext) => $"{id}_thumb{ext}");
            mock.Setup(x => x.BuildModel3DThumbnailUrl(It.IsAny<Guid>()))
                .Returns<Guid>(modelId => $"/api/3d-models/thumbnail/{modelId}");
            mock.Setup(x => x.ExtractFileNameForStorage(It.IsAny<string>()))
                .Returns<string>(path => Path.GetFileName(path));
            // Mock FileExistsAndIsSafe to return true (allow file to be returned)
            mock.Setup(x => x.FileExistsAndIsSafe(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(true);
            // Mock ResolveStoragePath to resolve relative paths
            mock.Setup(x => x.ResolveStoragePath(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((relativePath, basePath) =>
                    string.IsNullOrEmpty(relativePath)
                        ? basePath
                        : Path.Combine(basePath, relativePath));
            // Mock GetContentTypeForFile to return appropriate MIME types
            mock.Setup(x => x.GetContentTypeForFile(It.IsAny<string>()))
                .Returns<string>(ext => ext switch
                {
                    ".stl" => "application/vnd.ms-pki.stl",
                    ".3mf" => "model/3mf",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "application/octet-stream"
                });
            return mock;
        }

        private Mock<I3MfToStlConversionService> CreateMock3MFConversionService()
        {
            var mock = new Mock<I3MfToStlConversionService>(MockBehavior.Loose);
            // Default behavior: return null (conversion failed)
            mock.Setup(x => x.ConvertToSTLAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);
            return mock;
        }

        [Fact]
        public async Task ListModelsAsync_DelegatesToService()
        {
            Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);
            List<Model3DDto> expected = new List<Model3DDto> { new Model3DDto { Id = Guid.NewGuid(), FileName = "TestModel" } };
            _ = mockService.Setup(s => s.ListModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            Mock<IUnitOfWork> mockUoW = CreateMockUnitOfWork();
            Model3DFilesController controller = new Model3DFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockUoW.Object, CreateMockFolderService().Object, CreateStoredFileOperationsServiceMock().Object, CreateMock3MFConversionService().Object);

            IActionResult result = await controller.ListModelsAsync();

            OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
            IEnumerable<Model3DDto> value = Assert.IsAssignableFrom<IEnumerable<Model3DDto>>(ok.Value);
            _ = Assert.Single(value);

            mockService.Verify(s => s.ListModelsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelAsync_DelegatesToService_ReturnsNotFoundWhenNull()
        {
            Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3DDto?)null);

            Mock<IUnitOfWork> mockUoW = CreateMockUnitOfWork();
            Model3DFilesController controller = new Model3DFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockUoW.Object, CreateMockFolderService().Object, CreateStoredFileOperationsServiceMock().Object, CreateMock3MFConversionService().Object);

            IActionResult result = await controller.GetModelAsync(Guid.NewGuid());

            _ = Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UploadModelAsync_DelegatesToService_ReturnsCreated()
        {
            Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            Model3DUploadResultDto uploadResult = new Model3DUploadResultDto { Id = Guid.NewGuid(), FileName = "model.stl", FileType = "stl" };
            _ = mockService.Setup(s => s.UploadModelAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>())).ReturnsAsync(uploadResult);

            Mock<IUnitOfWork> mockUoW = CreateMockUnitOfWork();
            Model3DFilesController controller = new Model3DFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockUoW.Object, CreateMockFolderService().Object, CreateStoredFileOperationsServiceMock().Object, CreateMock3MFConversionService().Object);

            FormFile fakeFile = new FormFile(new MemoryStream(Encoding.UTF8.GetBytes("x")), 0, 1, "file", "model.stl");

            IActionResult result = await controller.UploadModelAsync(fakeFile);

            CreatedAtRouteResult created = Assert.IsType<CreatedAtRouteResult>(result);
            Model3DUploadResultDto value = Assert.IsType<Model3DUploadResultDto>(created.Value);
            Assert.Equal(uploadResult.Id, value.Id);

            mockService.Verify(s => s.UploadModelAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteModelAsync_DelegatesToService_ReturnsNoContent()
        {
            Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            Mock<IUnitOfWork> mockUoW = CreateMockUnitOfWork();
            Model3DFilesController controller = new Model3DFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockUoW.Object, CreateMockFolderService().Object, CreateStoredFileOperationsServiceMock().Object, CreateMock3MFConversionService().Object);

            IActionResult result = await controller.DeleteModelAsync(Guid.NewGuid());

            _ = Assert.IsType<NoContentResult>(result);
            mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteModelAsync_DelegatesToService_ReturnsNotFoundOnMissing()
        {
            Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

            Mock<IUnitOfWork> mockUoW = CreateMockUnitOfWork();
            Model3DFilesController controller = new Model3DFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockUoW.Object, CreateMockFolderService().Object, CreateStoredFileOperationsServiceMock().Object, CreateMock3MFConversionService().Object);

            IActionResult result = await controller.DeleteModelAsync(Guid.NewGuid());

            _ = Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelFileAsync_NoPath_ReturnsNotFound()
        {
            Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            Mock<IUnitOfWork> mockUoW = CreateMockUnitOfWork();
            Model3DFilesController controller = new Model3DFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockUoW.Object, CreateMockFolderService().Object, CreateStoredFileOperationsServiceMock().Object, CreateMock3MFConversionService().Object);

            IActionResult result = await controller.GetModelFileAsync(Guid.NewGuid());

            _ = Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelThumbnailAsync_NoThumb_ReturnsNotFound()
        {
            Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            Mock<IUnitOfWork> mockUoW = CreateMockUnitOfWork();
            Model3DFilesController controller = new Model3DFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockUoW.Object, CreateMockFolderService().Object, CreateStoredFileOperationsServiceMock().Object, CreateMock3MFConversionService().Object);

            IActionResult result = await controller.GetModelThumbnailAsync(Guid.NewGuid());

            NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("Thumbnail not available", notFound.Value);
            mockService.Verify(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelFileAsync_ReturnsPhysicalFile()
        {
            Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            string fileId = Guid.NewGuid().ToString();
            string fileName = $"{fileId}.stl";
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), "models", fileName);

            _ = mockService.Setup(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(fileName);
            _ = mockService.Setup(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Model3DDto { Id = Guid.NewGuid(), FileName = fileName });

            // Create test file system that contains the file
            TestFileSystem testFs = TestFileSystemFactory.WithFile(filePath, Encoding.UTF8.GetBytes("test stl content"));

            Mock<IUnitOfWork> mockUoW = CreateMockUnitOfWork();
            Model3DFilesController controller = new Model3DFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, testFs, mockFileManagement.Object, mockUoW.Object, CreateMockFolderService().Object, CreateStoredFileOperationsServiceMock().Object, CreateMock3MFConversionService().Object);

            IActionResult result = await controller.GetModelFileAsync(Guid.NewGuid());

            PhysicalFileResult physical = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("application/vnd.ms-pki.stl", physical.ContentType);
            Assert.EndsWith(".stl", physical.FileDownloadName);
        }

        [Fact]
        public async Task GetModelThumbnailAsync_ReturnsPhysicalFile()
        {
            Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "models");
            IConfigurationRoot configReal = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("ModelStorage:Path", modelPath) }).Build();

            string tmpFileThumb = Path.Combine(modelPath, $"thumb-{Guid.NewGuid()}.png");
            _ = mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Path.GetFileName(tmpFileThumb));

            TestFileSystem testFs = TestFileSystemFactory.WithThumbnail(tmpFileThumb, Encoding.UTF8.GetBytes("pngcontent"));
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();

            Mock<IUnitOfWork> mockUoW = CreateMockUnitOfWork();
            Model3DFilesController controller = new Model3DFilesController(mockLogger.Object, mockService.Object, configReal, testFs, mockFileManagement.Object, mockUoW.Object, CreateMockFolderService().Object, CreateStoredFileOperationsServiceMock().Object, CreateMock3MFConversionService().Object);

            IActionResult result = await controller.GetModelThumbnailAsync(Guid.NewGuid());

            PhysicalFileResult physical = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("image/png", physical.ContentType);
        }
    }
}

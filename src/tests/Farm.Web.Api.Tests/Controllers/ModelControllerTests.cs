using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Model;
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

        private AppDbContext? CreateMockAppDbContext()
        {
            // Return null - the CreateFolderAsync method only uses _db if we actually call SaveChangesAsync
            // and the try/catch will handle the null gracefully
            return null!;
        }

        private Mock<Api.Services.Tags.ITagService> CreateMockTagService()
        {
            return new Mock<Api.Services.Tags.ITagService>(MockBehavior.Loose);
        }

        private Mock<IModel3dFileRepository> CreateMockModelRepository()
        {
            return new Mock<IModel3dFileRepository>(MockBehavior.Loose);
        }

        [Fact]
        public async Task ListModelsAsync_DelegatesToService()
        {
            Mock<IModel3dFileService> mockService = new Mock<IModel3dFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);
            List<Model3DDto> expected = new List<Model3DDto> { new Model3DDto { Id = Guid.NewGuid(), Name = "TestModel" } };
            _ = mockService.Setup(s => s.ListModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            Mock<IModel3dFileRepository> mockModelRepo = CreateMockModelRepository();
            Model3dFilesController controller = new Model3dFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object, CreateMockAppDbContext());

            IActionResult result = await controller.ListModelsAsync();

            OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
            IEnumerable<Model3DDto> value = Assert.IsAssignableFrom<IEnumerable<Model3DDto>>(ok.Value);
            _ = Assert.Single(value);

            mockService.Verify(s => s.ListModelsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelAsync_DelegatesToService_ReturnsNotFoundWhenNull()
        {
            Mock<IModel3dFileService> mockService = new Mock<IModel3dFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3DDto?)null);

            Mock<IModel3dFileRepository> mockModelRepo = CreateMockModelRepository();
            Model3dFilesController controller = new Model3dFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object, CreateMockAppDbContext());

            IActionResult result = await controller.GetModelAsync(Guid.NewGuid());

            _ = Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UploadModelAsync_DelegatesToService_ReturnsCreated()
        {
            Mock<IModel3dFileService> mockService = new Mock<IModel3dFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            Model3DUploadResultDto uploadResult = new Model3DUploadResultDto { Id = Guid.NewGuid(), FileName = "model.stl", FileType = "stl" };
            _ = mockService.Setup(s => s.UploadModelAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>())).ReturnsAsync(uploadResult);

            Mock<IModel3dFileRepository> mockModelRepo = CreateMockModelRepository();
            Model3dFilesController controller = new Model3dFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object, CreateMockAppDbContext());

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
            Mock<IModel3dFileService> mockService = new Mock<IModel3dFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            Mock<IModel3dFileRepository> mockModelRepo = CreateMockModelRepository();
            Model3dFilesController controller = new Model3dFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object, CreateMockAppDbContext());

            IActionResult result = await controller.DeleteModelAsync(Guid.NewGuid());

            _ = Assert.IsType<NoContentResult>(result);
            mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteModelAsync_DelegatesToService_ReturnsNotFoundOnMissing()
        {
            Mock<IModel3dFileService> mockService = new Mock<IModel3dFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

            Mock<IModel3dFileRepository> mockModelRepo = CreateMockModelRepository();
            Model3dFilesController controller = new Model3dFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object, CreateMockAppDbContext());

            IActionResult result = await controller.DeleteModelAsync(Guid.NewGuid());

            _ = Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelFileAsync_NoPath_ReturnsNotFound()
        {
            Mock<IModel3dFileService> mockService = new Mock<IModel3dFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            Mock<IModel3dFileRepository> mockModelRepo = CreateMockModelRepository();
            Model3dFilesController controller = new Model3dFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object, CreateMockAppDbContext());

            IActionResult result = await controller.GetModelFileAsync(Guid.NewGuid());

            _ = Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelThumbnailAsync_NoThumb_ReturnsNotFound()
        {
            Mock<IModel3dFileService> mockService = new Mock<IModel3dFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            _ = mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            _ = mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            Mock<IModel3dFileRepository> mockModelRepo = CreateMockModelRepository();
            Model3dFilesController controller = new Model3dFilesController(mockLogger.Object, mockService.Object, mockConfig.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object, CreateMockAppDbContext());

            IActionResult result = await controller.GetModelThumbnailAsync(Guid.NewGuid());

            NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("Thumbnail not available", notFound.Value);
            mockService.Verify(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelFileAsync_ReturnsPhysicalFile()
        {
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "models");
            IConfigurationRoot configReal = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("ModelStorage:Path", modelPath) }).Build();

            string tmpFile = Path.Combine(modelPath, $"model-{Guid.NewGuid()}.stl");

            Mock<IModel3dFileRepository> mockRepo = new Mock<IModel3dFileRepository>(MockBehavior.Strict);
            _ = mockRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Farm.Infrastructure.Domain.Model3D
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "orig.stl",
                FilePath = tmpFile,
                IsValid = true,
                UploadedAt = DateTime.UtcNow
            });

            TestFileSystem testFs = TestFileSystemFactory.WithFile(tmpFile, Encoding.UTF8.GetBytes("content"));
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();

            Mock<AppDbContext> mockDb = new Mock<AppDbContext>(MockBehavior.Loose);

            Mock<IFolderManagementService> mockFolderService = new Mock<IFolderManagementService>(MockBehavior.Loose);

            Farm.Web.Api.Services.Model.IModel3dFileService modelService = new Farm.Web.Api.Services.Model.Model3dFileService(mockRepo.Object, mockLogger.Object, configReal, testFs, mockFileManagement.Object, mockDb.Object, mockFolderService.Object);

            Model3dFilesController controller = new Model3dFilesController(mockLogger.Object, modelService, configReal, testFs, mockFileManagement.Object, CreateMockTagService().Object, mockRepo.Object, CreateMockAppDbContext());

            IActionResult result = await controller.GetModelFileAsync(Guid.NewGuid());

            PhysicalFileResult physical = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("application/vnd.ms-pki.stl", physical.ContentType);
            Assert.Equal("orig.stl", physical.FileDownloadName);
        }

        [Fact]
        public async Task GetModelThumbnailAsync_ReturnsPhysicalFile()
        {
            Mock<IModel3dFileService> mockService = new Mock<IModel3dFileService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "models");
            IConfigurationRoot configReal = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("ModelStorage:Path", modelPath) }).Build();

            string tmpFileThumb = Path.Combine(modelPath, $"thumb-{Guid.NewGuid()}.png");
            _ = mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(tmpFileThumb);

            TestFileSystem testFs = TestFileSystemFactory.WithThumbnail(tmpFileThumb, Encoding.UTF8.GetBytes("pngcontent"));
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();

            Mock<IModel3dFileRepository> mockModelRepo = CreateMockModelRepository();
            Model3dFilesController controller = new Model3dFilesController(mockLogger.Object, mockService.Object, configReal, testFs, mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object, CreateMockAppDbContext());

            IActionResult result = await controller.GetModelThumbnailAsync(Guid.NewGuid());

            PhysicalFileResult physical = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("image/png", physical.ContentType);
        }
    }
}

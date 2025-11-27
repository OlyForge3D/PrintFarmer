using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Model;
using Farm.Web.Api.Tests.Services;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using Shared = Farm.Web.Shared;

namespace Farm.Web.Api.Tests.Controllers
{
    public class ModelControllerTests
    {
        private Mock<IFileManagementService> CreateMockFileManagementService()
        {
            Mock<IFileManagementService> mock = new Mock<IFileManagementService>(MockBehavior.Loose);
            mock.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            return mock;
        }

        private Mock<Farm.Web.Api.Services.Tags.ITagService> CreateMockTagService()
        {
            return new Mock<Farm.Web.Api.Services.Tags.ITagService>(MockBehavior.Loose);
        }

        private Mock<Farm.Infrastructure.Repositories.Model.IModelRepository> CreateMockModelRepository()
        {
            return new Mock<Farm.Infrastructure.Repositories.Model.IModelRepository>(MockBehavior.Loose);
        }

        [Fact]
        public async Task ListModelsAsync_DelegatesToService()
        {
            Mock<IModelService> mockService = new Mock<IModelService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            Mock<IModelAnalysisService> mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            Mock<IVirusScanner> mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            Mock<IThumbnailGenerationService> mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);
            List<Model3DDto> expected = new List<Shared.Model3DDto> { new Shared.Model3DDto { Id = Guid.NewGuid(), Name = "TestModel" } };
            mockService.Setup(s => s.ListModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            Mock<IModelRepository> mockModelRepo = CreateMockModelRepository();
            ModelController controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object);

            IActionResult result = await controller.ListModelsAsync();

            OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
            IEnumerable<Model3DDto> value = Assert.IsAssignableFrom<IEnumerable<Shared.Model3DDto>>(ok.Value);
            Assert.Single(value);

            mockService.Verify(s => s.ListModelsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelAsync_DelegatesToService_ReturnsNotFoundWhenNull()
        {
            Mock<IModelService> mockService = new Mock<IModelService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            Mock<IModelAnalysisService> mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            Mock<IVirusScanner> mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            Mock<IThumbnailGenerationService> mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Shared.Model3DDto?)null);

            Mock<IModelRepository> mockModelRepo = CreateMockModelRepository();
            ModelController controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object);

            IActionResult result = await controller.GetModelAsync(Guid.NewGuid());

            Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UploadModelAsync_DelegatesToService_ReturnsCreated()
        {
            Mock<IModelService> mockService = new Mock<IModelService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            Mock<IModelAnalysisService> mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            Mock<IVirusScanner> mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            Mock<IThumbnailGenerationService> mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            Model3DUploadResultDto uploadResult = new Shared.Model3DUploadResultDto { Id = Guid.NewGuid(), FileName = "model.stl", FileType = "stl" };
            mockService.Setup(s => s.UploadModelAsync(It.IsAny<Microsoft.AspNetCore.Http.IFormFile>(), It.IsAny<CancellationToken>())).ReturnsAsync(uploadResult);

            Mock<IModelRepository> mockModelRepo = CreateMockModelRepository();
            ModelController controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object);

            FormFile fakeFile = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("x")), 0, 1, "file", "model.stl");

            IActionResult result = await controller.UploadModelAsync(fakeFile);

            CreatedAtRouteResult created = Assert.IsType<CreatedAtRouteResult>(result);
            Model3DUploadResultDto value = Assert.IsType<Shared.Model3DUploadResultDto>(created.Value);
            Assert.Equal(uploadResult.Id, value.Id);

            mockService.Verify(s => s.UploadModelAsync(It.IsAny<Microsoft.AspNetCore.Http.IFormFile>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteModelAsync_DelegatesToService_ReturnsNoContent()
        {
            Mock<IModelService> mockService = new Mock<IModelService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            Mock<IModelAnalysisService> mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            Mock<IVirusScanner> mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            Mock<IThumbnailGenerationService> mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            Mock<IModelRepository> mockModelRepo = CreateMockModelRepository();
            ModelController controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object);

            IActionResult result = await controller.DeleteModelAsync(Guid.NewGuid());

            Assert.IsType<NoContentResult>(result);
            mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteModelAsync_DelegatesToService_ReturnsNotFoundOnMissing()
        {
            Mock<IModelService> mockService = new Mock<IModelService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            Mock<IModelAnalysisService> mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            Mock<IVirusScanner> mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            Mock<IThumbnailGenerationService> mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

            Mock<IModelRepository> mockModelRepo = CreateMockModelRepository();
            ModelController controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object);

            IActionResult result = await controller.DeleteModelAsync(Guid.NewGuid());

            Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelFileAsync_NoPath_ReturnsNotFound()
        {
            Mock<IModelService> mockService = new Mock<IModelService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            Mock<IModelAnalysisService> mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            Mock<IVirusScanner> mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            Mock<IThumbnailGenerationService> mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            Mock<IModelRepository> mockModelRepo = CreateMockModelRepository();
            ModelController controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object);

            IActionResult result = await controller.GetModelFileAsync(Guid.NewGuid());

            Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelThumbnailAsync_NoThumb_ReturnsNotFound()
        {
            Mock<IModelService> mockService = new Mock<IModelService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            Mock<IModelAnalysisService> mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            Mock<IVirusScanner> mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            Mock<IThumbnailGenerationService> mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();
            Mock<IConfiguration> mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            Mock<IModelRepository> mockModelRepo = CreateMockModelRepository();
            ModelController controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new System.Collections.Generic.Dictionary<string, byte[]>()), mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object);

            IActionResult result = await controller.GetModelThumbnailAsync(Guid.NewGuid());

            NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("Thumbnail not available", notFound.Value);
            mockService.Verify(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelFileAsync_ReturnsPhysicalFile()
        {
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            Mock<IModelAnalysisService> mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            Mock<IVirusScanner> mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            Mock<IThumbnailGenerationService> mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();

            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "models");
            IConfigurationRoot configReal = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("ModelStorage:Path", modelPath) }).Build();

            string tmpFile = Path.Combine(modelPath, $"model-{Guid.NewGuid()}.stl");

            Mock<IModelRepository> mockRepo = new Mock<Farm.Infrastructure.Repositories.Model.IModelRepository>(MockBehavior.Strict);
            mockRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Farm.Infrastructure.Domain.Model3D
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "orig.stl",
                FilePath = tmpFile,
                IsValid = true,
                UploadedAt = DateTime.UtcNow
            });

            TestFileSystem testFs = TestFileSystemFactory.WithFile(tmpFile, System.Text.Encoding.UTF8.GetBytes("content"));
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();

            ModelService modelService = new Farm.Web.Api.Services.Model.ModelService(mockRepo.Object, mockLogger.Object, configReal, testFs, mockFileManagement.Object, mockAnalysis.Object);

            ModelController controller = new ModelController(mockLogger.Object, modelService, configReal, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, testFs, mockFileManagement.Object, CreateMockTagService().Object, mockRepo.Object);

            IActionResult result = await controller.GetModelFileAsync(Guid.NewGuid());

            PhysicalFileResult physical = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("application/vnd.ms-pki.stl", physical.ContentType);
            Assert.Equal("orig.stl", physical.FileDownloadName);
        }

        [Fact]
        public async Task GetModelThumbnailAsync_ReturnsPhysicalFile()
        {
            Mock<IModelService> mockService = new Mock<IModelService>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            Mock<IModelAnalysisService> mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            Mock<IVirusScanner> mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            Mock<IThumbnailGenerationService> mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();

            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "models");
            IConfigurationRoot configReal = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("ModelStorage:Path", modelPath) }).Build();

            string tmpFileThumb = Path.Combine(modelPath, $"thumb-{Guid.NewGuid()}.png");
            mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(tmpFileThumb);

            TestFileSystem testFs = TestFileSystemFactory.WithThumbnail(tmpFileThumb, System.Text.Encoding.UTF8.GetBytes("pngcontent"));
            Mock<IFileManagementService> mockFileManagement = CreateMockFileManagementService();

            Mock<IModelRepository> mockModelRepo = CreateMockModelRepository();
            ModelController controller = new ModelController(mockLogger.Object, mockService.Object, configReal, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, testFs, mockFileManagement.Object, CreateMockTagService().Object, mockModelRepo.Object);

            IActionResult result = await controller.GetModelThumbnailAsync(Guid.NewGuid());

            PhysicalFileResult physical = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("image/png", physical.ContentType);
        }
    }
}

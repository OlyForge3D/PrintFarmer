using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Model;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Microsoft.Extensions.Configuration;
using System.IO;
using Shared = Farm.Web.Shared;
using Xunit;
using Farm.Web.Api.Tests.Services;

namespace Farm.Web.Api.Tests.Controllers
{
    public class ModelControllerTests
    {
        [Fact]
        public async Task ListModelsAsync_DelegatesToService()
        {
            var mockService = new Mock<IModelService>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            var mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            var mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);
            var expected = new List<Shared.Model3DDto> { new Shared.Model3DDto { Id = Guid.NewGuid(), Name = "TestModel" } };
            mockService.Setup(s => s.ListModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            var controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()));

            var result = await controller.ListModelsAsync();

            var ok = Assert.IsType<OkObjectResult>(result);
            var value = Assert.IsAssignableFrom<IEnumerable<Shared.Model3DDto>>(ok.Value);
            Assert.Single(value);

            mockService.Verify(s => s.ListModelsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelAsync_DelegatesToService_ReturnsNotFoundWhenNull()
        {
            var mockService = new Mock<IModelService>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            var mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            var mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Shared.Model3DDto?)null);

            var controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()));

            var result = await controller.GetModelAsync(Guid.NewGuid());

            Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UploadModelAsync_DelegatesToService_ReturnsCreated()
        {
            var mockService = new Mock<IModelService>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            var mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            var mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            var uploadResult = new Shared.Model3DUploadResultDto { Id = Guid.NewGuid(), FileName = "model.stl", FileType = "stl" };
            mockService.Setup(s => s.UploadModelAsync(It.IsAny<Microsoft.AspNetCore.Http.IFormFile>(), It.IsAny<CancellationToken>())).ReturnsAsync(uploadResult);

            var controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()));

            var fakeFile = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("x")), 0, 1, "file", "model.stl");

            var result = await controller.UploadModelAsync(fakeFile);

            var created = Assert.IsType<CreatedAtRouteResult>(result);
            var value = Assert.IsType<Shared.Model3DUploadResultDto>(created.Value);
            Assert.Equal(uploadResult.Id, value.Id);

            mockService.Verify(s => s.UploadModelAsync(It.IsAny<Microsoft.AspNetCore.Http.IFormFile>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteModelAsync_DelegatesToService_ReturnsNoContent()
        {
            var mockService = new Mock<IModelService>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            var mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            var mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()));

            var result = await controller.DeleteModelAsync(Guid.NewGuid());

            Assert.IsType<NoContentResult>(result);
            mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteModelAsync_DelegatesToService_ReturnsNotFoundOnMissing()
        {
            var mockService = new Mock<IModelService>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            var mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            var mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

            var controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()));

            var result = await controller.DeleteModelAsync(Guid.NewGuid());

            Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelFileAsync_NoPath_ReturnsNotFound()
        {
            var mockService = new Mock<IModelService>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            var mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            var mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            var controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()));

            var result = await controller.GetModelFileAsync(Guid.NewGuid());

            Assert.IsType<NotFoundResult>(result);
            mockService.Verify(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelThumbnailAsync_NoThumb_ReturnsNotFound()
        {
            var mockService = new Mock<IModelService>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            var mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            var mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c[It.IsAny<string>()]).Returns((string?)null);

            mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            var controller = new ModelController(mockLogger.Object, mockService.Object, mockConfig.Object, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, TestFileSystemFactory.WithFiles(new System.Collections.Generic.Dictionary<string, byte[]>()));

            var result = await controller.GetModelThumbnailAsync(Guid.NewGuid());

            var notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("Thumbnail not available", notFound.Value);
            mockService.Verify(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetModelFileAsync_ReturnsPhysicalFile()
        {
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            var mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            var mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();

            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "models");
            var configReal = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("ModelStorage:Path", modelPath) }).Build();

            string tmpFile = Path.Combine(modelPath, $"model-{Guid.NewGuid()}.stl");

            var mockRepo = new Mock<Farm.Web.Api.Repositories.Model.IModelRepository>(MockBehavior.Strict);
            mockRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Farm.Infrastructure.Domain.Model3D
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "orig.stl",
                FilePath = tmpFile,
                IsValid = true,
                UploadedAt = DateTime.UtcNow
            });

            var testFs = TestFileSystemFactory.WithFile(tmpFile, System.Text.Encoding.UTF8.GetBytes("content"));

            var modelService = new Farm.Web.Api.Services.Model.ModelService(mockRepo.Object, mockLogger.Object, configReal, testFs, mockAnalysis.Object);

            var controller = new ModelController(mockLogger.Object, modelService, configReal, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, testFs);

            var result = await controller.GetModelFileAsync(Guid.NewGuid());

            var physical = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("application/vnd.ms-pki.stl", physical.ContentType);
            Assert.Equal("orig.stl", physical.FileDownloadName);
        }

        [Fact]
        public async Task GetModelThumbnailAsync_ReturnsPhysicalFile()
        {
            var mockService = new Mock<IModelService>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>();
            var mockVirus = new Mock<Farm.Web.Api.Services.Interfaces.IVirusScanner>();
            var mockThumb = new Mock<Farm.Web.Api.Services.Interfaces.IThumbnailGenerationService>();

            string modelPath2 = Path.Combine(Directory.GetCurrentDirectory(), "models");
            var configReal2 = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("ModelStorage:Path", modelPath2) }).Build();

            string tmpFileThumb = Path.Combine(modelPath2, $"thumb-{Guid.NewGuid()}.png");
            mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(tmpFileThumb);

            var testFs2 = TestFileSystemFactory.WithThumbnail(tmpFileThumb, System.Text.Encoding.UTF8.GetBytes("pngcontent"));

            var controller2 = new ModelController(mockLogger.Object, mockService.Object, configReal2, mockAnalysis.Object, mockVirus.Object, mockThumb.Object, testFs2);

            var result = await controller2.GetModelThumbnailAsync(Guid.NewGuid());

            var physical = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("image/png", physical.ContentType);
        }
    }
}

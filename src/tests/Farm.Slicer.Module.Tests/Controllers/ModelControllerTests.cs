using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

public class ModelControllerTests
{
    private static Model3DFilesController CreateController(
        Mock<IModel3DFileService> mockService,
        Mock<I3MfToStlConversionService>? mockConverter = null)
    {
        Mock<ILogger<Model3DFilesController>> mockLogger = new Mock<ILogger<Model3DFilesController>>();
        mockConverter ??= new Mock<I3MfToStlConversionService>();

        return new Model3DFilesController(
            mockLogger.Object,
            mockService.Object,
            mockConverter.Object);
    }

    [Fact]
    public async Task ListModelsAsync_DelegatesToService()
    {
        Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        List<Model3DDto> expected = new List<Model3DDto> { new Model3DDto { Id = Guid.NewGuid(), FileName = "TestModel" } };
        _ = mockService.Setup(s => s.ListModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        Model3DFilesController controller = CreateController(mockService);

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
        _ = mockService.Setup(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3DDto?)null);

        Model3DFilesController controller = CreateController(mockService);

        IActionResult result = await controller.GetModelAsync(Guid.NewGuid());

        _ = Assert.IsType<NotFoundResult>(result);
        mockService.Verify(s => s.GetModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadModelAsync_DelegatesToService_ReturnsCreated()
    {
        Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        Model3DUploadResultDto uploadResult = new Model3DUploadResultDto { Id = Guid.NewGuid(), FileName = "model.stl", FileType = "stl" };
        _ = mockService.Setup(s => s.UploadModelAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>())).ReturnsAsync(uploadResult);

        Model3DFilesController controller = CreateController(mockService);

        FormFile fakeFile = new FormFile(new MemoryStream(Encoding.UTF8.GetBytes("x")), 0, 1, "file", "model.stl");

        IActionResult result = await controller.UploadModelAsync(fakeFile);

        CreatedResult created = Assert.IsType<CreatedResult>(result);
        Model3DUploadResultDto value = Assert.IsType<Model3DUploadResultDto>(created.Value);
        Assert.Equal(uploadResult.Id, value.Id);

        mockService.Verify(s => s.UploadModelAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteModelAsync_DelegatesToService_ReturnsNoContent()
    {
        Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        _ = mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Model3DFilesController controller = CreateController(mockService);

        IActionResult result = await controller.DeleteModelAsync(Guid.NewGuid());

        _ = Assert.IsType<NoContentResult>(result);
        mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteModelAsync_DelegatesToService_ReturnsNotFoundOnMissing()
    {
        Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        _ = mockService.Setup(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

        Model3DFilesController controller = CreateController(mockService);

        IActionResult result = await controller.DeleteModelAsync(Guid.NewGuid());

        _ = Assert.IsType<NotFoundResult>(result);
        mockService.Verify(s => s.DeleteModelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModelFileAsync_NoPath_ReturnsNotFound()
    {
        Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        _ = mockService.Setup(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        Model3DFilesController controller = CreateController(mockService);

        IActionResult result = await controller.GetModelFileAsync(Guid.NewGuid());

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal("Model file not found", notFound.Value);
        mockService.Verify(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModelThumbnailAsync_NoThumb_ReturnsNotFound()
    {
        Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        _ = mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        Model3DFilesController controller = CreateController(mockService);

        IActionResult result = await controller.GetModelThumbnailAsync(Guid.NewGuid());

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal("Thumbnail not found", notFound.Value);
        mockService.Verify(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModelFileAsync_ReturnsFileStream()
    {
        Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);

        // Create a temp file on disk so File.Exists returns true
        string tempDir = Path.Combine(Path.GetTempPath(), $"model-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string filePath = Path.Combine(tempDir, "test-model.stl");
        await File.WriteAllTextAsync(filePath, "test stl content");

        try
        {
            _ = mockService.Setup(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(filePath);

            Model3DFilesController controller = CreateController(mockService);

            IActionResult result = await controller.GetModelFileAsync(Guid.NewGuid());

            FileStreamResult fileResult = Assert.IsType<FileStreamResult>(result);
            Assert.EndsWith(".stl", fileResult.FileDownloadName);

            mockService.Verify(s => s.GetModelFilePathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);

            // Dispose the stream before cleanup
            fileResult.FileStream.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task GetModelThumbnailAsync_ReturnsFileStream()
    {
        Mock<IModel3DFileService> mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);

        // Create a temp file on disk so File.Exists returns true
        string tempDir = Path.Combine(Path.GetTempPath(), $"thumb-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string thumbPath = Path.Combine(tempDir, "test-thumb.png");
        await File.WriteAllTextAsync(thumbPath, "pngcontent");

        try
        {
            _ = mockService.Setup(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(thumbPath);

            Model3DFilesController controller = CreateController(mockService);

            IActionResult result = await controller.GetModelThumbnailAsync(Guid.NewGuid());

            FileStreamResult fileResult = Assert.IsType<FileStreamResult>(result);
            Assert.Equal("image/png", fileResult.ContentType);

            mockService.Verify(s => s.GetModelThumbnailPathAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);

            // Dispose the stream before cleanup
            fileResult.FileStream.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}

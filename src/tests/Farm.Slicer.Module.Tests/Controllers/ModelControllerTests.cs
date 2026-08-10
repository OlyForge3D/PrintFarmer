using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

        Model3DFilesController controller = new(
            mockLogger.Object,
            mockService.Object,
            mockConverter.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                    "Test"))
            }
        };

        return controller;
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
        _ = mockService.Setup(s => s.UploadModelAsync(
            It.IsAny<IFormFile>(),
            It.IsAny<IFormFile?>(),
            It.IsAny<Guid>(),
            It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(uploadResult);

        Model3DFilesController controller = CreateController(mockService);

        using MemoryStream fakeFileStream = new(Encoding.UTF8.GetBytes("x"));
        FormFile fakeFile = new FormFile(fakeFileStream, 0, 1, "file", "model.stl");
        using MemoryStream thumbnailFileStream = new(Encoding.UTF8.GetBytes("png"));
        FormFile thumbnailFile = new FormFile(thumbnailFileStream, 0, 3, "thumbnailFile", "thumbnail.png");
        using CancellationTokenSource cancellation = new();

        Guid clientUploadId = Guid.NewGuid();
        IActionResult result = await controller.UploadModelAsync(
            fakeFile,
            thumbnailFile,
            clientUploadId.ToString(),
            cancellation.Token);

        CreatedResult created = Assert.IsType<CreatedResult>(result);
        Model3DUploadResultDto value = Assert.IsType<Model3DUploadResultDto>(created.Value);
        Assert.Equal(uploadResult.Id, value.Id);

        mockService.Verify(s => s.UploadModelAsync(
            fakeFile,
            thumbnailFile,
            It.IsAny<Guid>(),
            clientUploadId,
            cancellation.Token), Times.Once);
    }

    [Fact]
    public async Task UploadModelAsync_WithInvalidClientUploadId_ReturnsBadRequest()
    {
        Mock<IModel3DFileService> mockService = new(MockBehavior.Strict);
        Model3DFilesController controller = CreateController(mockService);
        using MemoryStream fakeFileStream = new(Encoding.UTF8.GetBytes("x"));
        FormFile fakeFile = new(fakeFileStream, 0, 1, "file", "model.stl");

        IActionResult result = await controller.UploadModelAsync(
            fakeFile,
            thumbnailFile: null,
            clientUploadId: "not-a-guid",
            CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("clientUploadId must be a non-empty GUID.", badRequest.Value);
    }

    [Fact]
    public async Task UploadModelAsync_WithClientUploadIdAndNoUserId_ReturnsUnauthorized()
    {
        Mock<IModel3DFileService> mockService = new(MockBehavior.Strict);
        Model3DFilesController controller = CreateController(mockService);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        using MemoryStream fakeFileStream = new(Encoding.UTF8.GetBytes("x"));
        FormFile fakeFile = new(fakeFileStream, 0, 1, "file", "model.stl");

        IActionResult result = await controller.UploadModelAsync(
            fakeFile,
            thumbnailFile: null,
            clientUploadId: Guid.NewGuid().ToString(),
            ct: CancellationToken.None);

        _ = Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WithOwnerAndMatchingETag_ReturnsResultAndETag()
    {
        Mock<IModel3DFileService> mockService = new(MockBehavior.Strict);
        Guid modelId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Model3DThumbnailUpdateResultDto replacement = new()
        {
            Id = modelId,
            ThumbnailUrl = $"/api/3d-models/thumbnail/{modelId}",
            ETag = "\"0203\""
        };
        _ = mockService.Setup(service => service.ReplaceThumbnailAsync(
                modelId,
                It.IsAny<IFormFile>(),
                userId,
                false,
                "\"0102\"",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(replacement);
        Model3DFilesController controller = CreateController(mockService);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "Test"));
        controller.Request.Headers.IfMatch = "\"0102\"";

        using MemoryStream thumbnailFileStream = new([1]);
        IActionResult result = await controller.ReplaceThumbnailAsync(
            modelId,
            new FormFile(thumbnailFileStream, 0, 1, "thumbnailFile", "thumbnail.png"),
            CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(replacement, ok.Value);
        Assert.Equal(replacement.ETag, controller.Response.Headers.ETag);
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WithAdminRole_ForwardsAdministratorAuthorization()
    {
        Mock<IModel3DFileService> mockService = new(MockBehavior.Strict);
        Guid modelId = Guid.NewGuid();
        _ = mockService.Setup(service => service.ReplaceThumbnailAsync(
                modelId,
                It.IsAny<IFormFile>(),
                null,
                true,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Model3DThumbnailUpdateResultDto { Id = modelId, ETag = "\"01\"" });
        Model3DFilesController controller = CreateController(mockService);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "farm_admin")],
            "Test"));

        using MemoryStream thumbnailFileStream = new([1]);
        IActionResult result = await controller.ReplaceThumbnailAsync(
            modelId,
            new FormFile(thumbnailFileStream, 0, 1, "thumbnailFile", "thumbnail.png"),
            CancellationToken.None);

        _ = Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WhenServiceRejectsOwnership_ReturnsForbidden()
    {
        Mock<IModel3DFileService> mockService = new(MockBehavior.Strict);
        Guid modelId = Guid.NewGuid();
        _ = mockService.Setup(service => service.ReplaceThumbnailAsync(
                modelId,
                It.IsAny<IFormFile>(),
                It.IsAny<Guid?>(),
                false,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());
        Model3DFilesController controller = CreateController(mockService);

        using MemoryStream thumbnailFileStream = new([1]);
        IActionResult result = await controller.ReplaceThumbnailAsync(
            modelId,
            new FormFile(thumbnailFileStream, 0, 1, "thumbnailFile", "thumbnail.png"),
            CancellationToken.None);

        _ = Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ReplaceThumbnailAsync_WhenConcurrencyConflictOccurs_ReturnsPreconditionFailed()
    {
        Mock<IModel3DFileService> mockService = new(MockBehavior.Strict);
        Guid modelId = Guid.NewGuid();
        _ = mockService.Setup(service => service.ReplaceThumbnailAsync(
                modelId,
                It.IsAny<IFormFile>(),
                It.IsAny<Guid?>(),
                false,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException());
        Model3DFilesController controller = CreateController(mockService);

        using MemoryStream thumbnailFileStream = new([1]);
        IActionResult result = await controller.ReplaceThumbnailAsync(
            modelId,
            new FormFile(thumbnailFileStream, 0, 1, "thumbnailFile", "thumbnail.png"),
            CancellationToken.None);

        ObjectResult conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status412PreconditionFailed, conflict.StatusCode);
    }

    [Fact]
    public void UploadModelAsync_ConfiguresMultipartLimitForModelAndThumbnail()
    {
        MethodInfo method = typeof(Model3DFilesController)
            .GetMethod(nameof(Model3DFilesController.UploadModelAsync))
            ?? throw new InvalidOperationException("Upload action was not found");
        RequestFormLimitsAttribute attribute = Assert.Single(
            method.GetCustomAttributes<RequestFormLimitsAttribute>());

        Assert.Equal(512_000_000, attribute.MultipartBodyLengthLimit);
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
        string tempDir = Path.Join(Path.GetTempPath(), $"model-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string filePath = Path.Join(tempDir, "test-model.stl");
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
        string tempDir = Path.Join(Path.GetTempPath(), $"thumb-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string thumbPath = Path.Join(tempDir, "test-thumb.png");
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

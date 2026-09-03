using Farm.Infrastructure.Contracts.FileManagement;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Quota;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Modules.Gcode.Controllers;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Controllers;

/// <summary>
/// Regression coverage for issue #1762: G-code downloads must stream from disk via
/// <see cref="PhysicalFileResult"/> instead of buffering whole files into a managed byte[],
/// and HEAD requests must report Content-Length from file metadata without reading content.
/// </summary>
public class GcodeFilesControllerDownloadTests
{
    private static GcodeFilesController CreateController(
        Mock<IGcodeFilesService> gcodeFilesServiceMock,
        Mock<IFileManagementService>? fileManagementService = null,
        Mock<IStoragePathService>? storagePathService = null,
        Mock<IStoredFileOperationsService>? storedFileOperationsService = null)
    {
        var logger = new Mock<ILogger<GcodeFilesController>>();
        var uploadSettings = new Mock<IGcodeUploadSettings>();
        var quotaService = new Mock<IGcodeUploadQuotaService>();
        var chunkedUploadService = new Mock<IChunkedUploadService>();
        fileManagementService ??= new Mock<IFileManagementService>();
        storagePathService ??= new Mock<IStoragePathService>();
        storedFileOperationsService ??= new Mock<IStoredFileOperationsService>();

        var controller = new GcodeFilesController(
            logger.Object,
            uploadSettings.Object,
            quotaService.Object,
            gcodeFilesServiceMock.Object,
            chunkedUploadService.Object,
            fileManagementService.Object,
            storagePathService.Object,
            storedFileOperationsService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        return controller;
    }

    [Fact]
    public async Task DownloadAsync_Get_WithExistingFile_ReturnsPhysicalFileWithRangeProcessing()
    {
        // Arrange
        string fullPath = Path.Combine(Path.GetTempPath(), $"pfarm-download-test-{Guid.NewGuid()}.gcode");
        byte[] content = new byte[4096];
        await File.WriteAllBytesAsync(fullPath, content);

        try
        {
            var gcodeFilesServiceMock = new Mock<IGcodeFilesService>();
            gcodeFilesServiceMock
                .Setup(s => s.DownloadAsync("/large.gcode", It.IsAny<CancellationToken>()))
                .ReturnsAsync((fullPath, "large.gcode"));

            GcodeFilesController controller = CreateController(gcodeFilesServiceMock);
            controller.ControllerContext.HttpContext.Request.Method = "GET";

            // Act
            ActionResult result = await controller.DownloadAsync("/large.gcode");

            // Assert
            PhysicalFileResult fileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal(fullPath, fileResult.FileName);
            Assert.Equal("application/octet-stream", fileResult.ContentType);
            Assert.Equal("large.gcode", fileResult.FileDownloadName);
            Assert.True(fileResult.EnableRangeProcessing);
        }
        finally
        {
            File.Delete(fullPath);
        }
    }

    [Fact]
    public async Task DownloadAsync_Head_ReportsContentLengthWithoutReadingContent()
    {
        // Arrange
        string fullPath = Path.Combine(Path.GetTempPath(), $"pfarm-download-test-{Guid.NewGuid()}.gcode");
        byte[] content = new byte[1024 * 1024]; // 1 MiB - big enough that a full read would be measurable.
        await File.WriteAllBytesAsync(fullPath, content);

        try
        {
            var gcodeFilesServiceMock = new Mock<IGcodeFilesService>();
            gcodeFilesServiceMock
                .Setup(s => s.DownloadAsync("/large.gcode", It.IsAny<CancellationToken>()))
                .ReturnsAsync((fullPath, "large.gcode"));

            GcodeFilesController controller = CreateController(gcodeFilesServiceMock);
            controller.ControllerContext.HttpContext.Request.Method = "HEAD";

            // Act
            ActionResult result = await controller.DownloadAsync("/large.gcode");

            // Assert
            StatusCodeResult statusResult = Assert.IsType<StatusCodeResult>(result);
            Assert.Equal(200, statusResult.StatusCode);
            Assert.Equal(content.Length, controller.ControllerContext.HttpContext.Response.ContentLength);

            // The service must never be asked to buffer file bytes for HEAD - it only resolves the path.
            gcodeFilesServiceMock.Verify(s => s.DownloadAsync("/large.gcode", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(fullPath);
        }
    }

    [Fact]
    public async Task DownloadAsync_WithMissingFile_ReturnsNotFound()
    {
        // Arrange
        var gcodeFilesServiceMock = new Mock<IGcodeFilesService>();
        gcodeFilesServiceMock
            .Setup(s => s.DownloadAsync("/missing.gcode", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string FullPath, string FileName)?)null);

        GcodeFilesController controller = CreateController(gcodeFilesServiceMock);
        controller.ControllerContext.HttpContext.Request.Method = "GET";

        // Act
        ActionResult result = await controller.DownloadAsync("/missing.gcode");

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nested/thumbnails")]
    public async Task GetGcodeThumbnailAsync_UsesContainedPhysicalPathWithoutResolvingTwice(
        string relativeDirectory)
    {
        string storageRoot = Path.Join(
            Path.GetTempPath(),
            "pfarm-thumbnail-controller-tests",
            Guid.NewGuid().ToString());
        string directory = Path.Join(storageRoot, relativeDirectory);
        Directory.CreateDirectory(directory);
        string thumbnailPath = Path.Join(directory, "preview.png");
        await File.WriteAllBytesAsync(thumbnailPath, [1, 2, 3]);

        try
        {
            Guid fileId = Guid.NewGuid();
            var gcodeFiles = new Mock<IGcodeFilesService>(MockBehavior.Strict);
            gcodeFiles.Setup(service => service.GetThumbnailPathAsync(
                    fileId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(thumbnailPath);
            var fileManagement = new Mock<IFileManagementService>(MockBehavior.Strict);
            fileManagement.Setup(service => service.IsSafePath(thumbnailPath, storageRoot))
                .Returns(true);
            var storagePaths = new Mock<IStoragePathService>(MockBehavior.Strict);
            storagePaths.Setup(service => service.GetGcodeStorageDirectory()).Returns(storageRoot);
            var storedFileOperations = new Mock<IStoredFileOperationsService>(MockBehavior.Strict);
            GcodeFilesController controller = CreateController(
                gcodeFiles,
                fileManagement,
                storagePaths,
                storedFileOperations);

            IActionResult result = await controller.GetGcodeThumbnailAsync(fileId);

            PhysicalFileResult physicalFile = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal(thumbnailPath, physicalFile.FileName);
            Assert.Equal("image/png", physicalFile.ContentType);
            storedFileOperations.Verify(
                service => service.ResolveStoragePath(It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetGcodeThumbnailAsync_WhenServiceRejectsUnsafePath_ReturnsNotFound()
    {
        Guid fileId = Guid.NewGuid();
        var gcodeFiles = new Mock<IGcodeFilesService>(MockBehavior.Strict);
        gcodeFiles.Setup(service => service.GetThumbnailPathAsync(
                fileId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var storedFileOperations = new Mock<IStoredFileOperationsService>(MockBehavior.Strict);
        GcodeFilesController controller = CreateController(
            gcodeFiles,
            storedFileOperationsService: storedFileOperations);

        IActionResult result = await controller.GetGcodeThumbnailAsync(fileId);

        Assert.IsType<NotFoundObjectResult>(result);
        storedFileOperations.Verify(
            service => service.ResolveStoragePath(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }
}

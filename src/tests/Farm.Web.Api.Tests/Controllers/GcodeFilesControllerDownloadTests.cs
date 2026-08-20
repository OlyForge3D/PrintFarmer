using Farm.Infrastructure.Contracts.FileManagement;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Quota;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Gcode;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Regression coverage for issue #1762: G-code downloads must stream from disk via
/// <see cref="PhysicalFileResult"/> instead of buffering whole files into a managed byte[],
/// and HEAD requests must report Content-Length from file metadata without reading content.
/// </summary>
public class GcodeFilesControllerDownloadTests
{
    private static GcodeFilesController CreateController(Mock<IGcodeFilesService> gcodeFilesServiceMock)
    {
        var logger = new Mock<ILogger<GcodeFilesController>>();
        var uploadSettings = new Mock<IGcodeUploadSettings>();
        var quotaService = new Mock<IGcodeUploadQuotaService>();
        var chunkedUploadService = new Mock<IChunkedUploadService>();
        var fileManagementService = new Mock<IFileManagementService>();
        var storagePathService = new Mock<IStoragePathService>();
        var storedFileOperationsService = new Mock<IStoredFileOperationsService>();

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
}

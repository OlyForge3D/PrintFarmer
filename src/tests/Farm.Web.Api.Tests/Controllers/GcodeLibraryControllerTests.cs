using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Gcode;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class GcodeLibraryControllerTests
{
    private readonly Mock<IGcodeFilesService> _gcodeServiceMock;
    private readonly Mock<IWebHostEnvironment> _envMock;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly GcodeLibraryController _controller;

    public GcodeLibraryControllerTests()
    {
        _gcodeServiceMock = new Mock<IGcodeFilesService>();
        _envMock = new Mock<IWebHostEnvironment>();
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _envMock.Setup(e => e.WebRootPath).Returns("/app/wwwroot");
        _controller = new GcodeLibraryController(_gcodeServiceMock.Object, _envMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task GetLibraryAsync_WithNoFilters_ReturnsOkWithFiles()
    {
        // Arrange
        var files = new List<GcodeFileDto>
        {
            new GcodeFileDto(Id: Guid.NewGuid(), FileName: "test.gcode", FileSize: 1024, UploadedAt: DateTime.UtcNow)
        };
        _gcodeServiceMock
            .Setup(s => s.QueryLibraryAsync(null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        var result = await _controller.GetLibraryAsync();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(files, okResult.Value);
    }

    [Fact]
    public async Task GetLibraryAsync_WithFilters_ReturnsFilteredResults()
    {
        // Arrange
        var files = new List<GcodeFileDto>
        {
            new GcodeFileDto(Id: Guid.NewGuid(), FileName: "pla_print.gcode", FileSize: 2048, UploadedAt: DateTime.UtcNow)
        };
        _gcodeServiceMock
            .Setup(s => s.QueryLibraryAsync("pla", "PLA", 0.4, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        var result = await _controller.GetLibraryAsync("pla", "PLA", 0.4, null);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(files, okResult.Value);
    }

    [Fact]
    public async Task GetLibraryAsync_WhenServiceThrows_ReturnsInternalServerError()
    {
        // Arrange
        _gcodeServiceMock
            .Setup(s => s.QueryLibraryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act
        var result = await _controller.GetLibraryAsync();

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetFileAsync_WithValidId_ReturnsOkWithFile()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var file = new GcodeFileDto(Id: fileId, FileName: "test.gcode", FileSize: 1024, UploadedAt: DateTime.UtcNow);
        _gcodeServiceMock
            .Setup(s => s.GetFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(file);

        // Act
        var result = await _controller.GetFileAsync(fileId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(file, okResult.Value);
    }

    [Fact]
    public async Task GetFileAsync_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        _gcodeServiceMock
            .Setup(s => s.GetFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFileDto?)null);

        // Act
        var result = await _controller.GetFileAsync(fileId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains(fileId.ToString(), notFoundResult.Value?.ToString());
    }

    [Fact]
    public async Task UploadFileAsync_WithValidFile_ReturnsCreated()
    {
        // Arrange
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns("test.gcode");
        fileMock.Setup(f => f.Length).Returns(1024);

        var metadata = new CreateGcodeFileDto { FileName = "Test File" };
        var created = new GcodeFileDto(Id: Guid.NewGuid(), FileName: "test.gcode", FileSize: 1024, UploadedAt: DateTime.UtcNow);

        _gcodeServiceMock
            .Setup(s => s.UploadFileAsync(fileMock.Object, metadata, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        // Act
        var result = await _controller.UploadFileAsync(fileMock.Object, metadata);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(GcodeLibraryController.GetFileAsync), createdResult.ActionName);
        Assert.Equal(created, createdResult.Value);
    }

    [Fact]
    public async Task UploadFileAsync_WithNullMetadata_ReturnsBadRequest()
    {
        // Arrange
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns("test.gcode");

        // Act
        var result = await _controller.UploadFileAsync(fileMock.Object, null!);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Metadata is required", badRequestResult.Value);
    }

    [Fact]
    public async Task UploadFileAsync_WithNullFile_ReturnsBadRequest()
    {
        // Arrange
        var metadata = new CreateGcodeFileDto { FileName = "Test" };

        // Act
        var result = await _controller.UploadFileAsync(null!, metadata);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("No file provided", badRequestResult.Value);
    }

    [Fact]
    public async Task UploadFileAsync_WithInvalidExtension_ReturnsBadRequest()
    {
        // Arrange
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns("test.txt");
        fileMock.Setup(f => f.Length).Returns(1024);
        var metadata = new CreateGcodeFileDto { FileName = "Test" };

        // Act
        var result = await _controller.UploadFileAsync(fileMock.Object, metadata);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("File must be a .gcode file", badRequestResult.Value);
    }

    [Fact]
    public async Task UploadFileAsync_WithDuplicateFile_ReturnsConflict()
    {
        // Arrange
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns("test.gcode");
        fileMock.Setup(f => f.Length).Returns(1024);
        var metadata = new CreateGcodeFileDto { FileName = "Test" };

        _gcodeServiceMock
            .Setup(s => s.UploadFileAsync(fileMock.Object, metadata, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("duplicate"));

        // Act
        var result = await _controller.UploadFileAsync(fileMock.Object, metadata);

        // Assert
        var conflictResult = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("File already exists in library", conflictResult.Value);
    }

    [Fact]
    public async Task UpdateFileAsync_WithValidRequest_ReturnsOkWithUpdatedFile()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var request = new UpdateGcodeFileDto(FileName: "Updated Name");
        var updated = new GcodeFileDto(Id: fileId, FileName: "test.gcode", FileSize: 1024, UploadedAt: DateTime.UtcNow);

        _gcodeServiceMock
            .Setup(s => s.UpdateFileAsync(fileId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        // Act
        var result = await _controller.UpdateFileAsync(fileId, request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(updated, okResult.Value);
    }

    [Fact]
    public async Task UpdateFileAsync_WithNullRequest_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.UpdateFileAsync(Guid.NewGuid(), null!);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Request body is required", badRequestResult.Value);
    }

    [Fact]
    public async Task DeleteFileAsync_WithValidId_ReturnsNoContent()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        _gcodeServiceMock
            .Setup(s => s.DeleteFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteFileAsync(fileId);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteFileAsync_WhenServiceReturnsFalse_ReturnsBadRequest()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        _gcodeServiceMock
            .Setup(s => s.DeleteFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteFileAsync(fileId);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Cannot delete file", badRequestResult.Value?.ToString());
    }

    [Fact]
    public async Task DownloadFileAsync_WithValidId_ReturnsFileContent()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var file = new GcodeFileDto(Id: fileId, FileName: "test.gcode", FileSize: 1024, UploadedAt: DateTime.UtcNow);
        var fileBytes = new byte[] { 1, 2, 3, 4 };

        _gcodeServiceMock
            .Setup(s => s.GetFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(file);
        _gcodeServiceMock
            .Setup(s => s.DownloadFileAsync(fileId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fileBytes);

        // Act
        var result = await _controller.DownloadFileAsync(fileId);

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/octet-stream", fileResult.ContentType);
        Assert.Equal("test.gcode", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task DownloadFileAsync_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        _gcodeServiceMock
            .Setup(s => s.GetFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFileDto?)null);

        // Act
        var result = await _controller.DownloadFileAsync(fileId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains(fileId.ToString(), notFoundResult.Value?.ToString());
    }
}

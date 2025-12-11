using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Comprehensive tests for PrusaLinkClient covering all critical API operations.
/// Tests interaction with mocked IPrusaLinkApiClient, error handling, and data transformation.
/// </summary>
public class PrusaLinkClientTests
{
    private readonly Mock<IPrusaLinkApiClient> _mockApiClient;
    private readonly Mock<IUnifiedLoggingService> _mockLogger;
    private readonly PrusaLinkClient _sut;

    public PrusaLinkClientTests()
    {
        _mockApiClient = new Mock<IPrusaLinkApiClient>();
        _mockLogger = new Mock<IUnifiedLoggingService>();
        _sut = new PrusaLinkClient(_mockApiClient.Object, _mockLogger.Object);
    }

    #region GetStatusAsync Tests

    [Fact]
    public async Task GetStatusAsync_WithValidStatus_ReturnsOnlineStatus()
    {
        // Arrange
        var statusInfo = new StatusInfo
        {
            Printer = new StatusPrinterInfo 
            { 
                State = "Operational",
                StatusPrinter = new PrinterStatusInfo { Ok = true },
                StatusConnect = new PrinterStatusInfo { Ok = true }
            }
        };
        _mockApiClient
            .Setup(x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(statusInfo);

        // Act
        var result = await _sut.GetStatusAsync("http://localhost:8080", "api-key");

        // Assert
        result.IsOnline.Should().BeTrue();
        result.State.Should().Be("Operational");
    }

    [Fact]
    public async Task GetStatusAsync_WithNullStatus_ReturnsOfflineStatus()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((StatusInfo?)null);

        // Act
        var result = await _sut.GetStatusAsync("http://localhost:8080", "api-key");

        // Assert
        result.IsOnline.Should().BeFalse();
        result.State.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_WithApiException_ReturnsOfflineStatusAndLogs()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        // Act
        var result = await _sut.GetStatusAsync("http://localhost:8080", "api-key");

        // Assert
        result.IsOnline.Should().BeFalse();
        _mockLogger.Verify(
            x => x.LogError(
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<object?>()),
            Times.Once);
    }

    [Fact]
    public async Task GetStatusAsync_WithNullPrinter_ReturnsOfflineStatus()
    {
        // Arrange - Status exists but Printer property is null
        var statusInfo = new StatusInfo { Printer = null };
        _mockApiClient
            .Setup(x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(statusInfo);

        // Act
        var result = await _sut.GetStatusAsync("http://localhost:8080", "api-key");

        // Assert
        result.IsOnline.Should().BeFalse();
    }

    #endregion

    #region GetJobAsync Tests

    [Fact]
    public async Task GetJobAsync_WithActivePrint_ReturnsJobDetails()
    {
        // Arrange
        var job = new Job
        {
            State = "printing",
            Progress = 0.65,
            File = new JobFile { Name = "model.gcode" }
        };
        _mockApiClient
            .Setup(x => x.GetJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        var result = await _sut.GetJobAsync("http://localhost:8080", "api-key");

        // Assert
        result.Should().NotBeNull();
        result!.PrintState.Should().Be("printing");
        result.Progress.Should().Be(0.65);
        result.JobName.Should().Be("model.gcode");
    }

    [Fact]
    public async Task GetJobAsync_WithNoActiveJob_ReturnsNull()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.GetJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Job?)null);

        // Act
        var result = await _sut.GetJobAsync("http://localhost:8080", "api-key");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetJobAsync_WithRelativeThumbnailPath_ConvertsToAbsoluteUrl()
    {
        // Arrange
        var job = new Job
        {
            State = "printing",
            Progress = 0.5,
            File = new JobFile
            {
                Name = "model.gcode",
                Refs = new PrintFileRefs { Thumbnail = "/../0.jpg" }
            }
        };
        _mockApiClient
            .Setup(x => x.GetJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        var result = await _sut.GetJobAsync("http://localhost:8080", "api-key");

        // Assert
        result!.ThumbnailUrl.Should().NotBeNullOrEmpty();
        result.ThumbnailUrl.Should().StartWith("http");
    }

    [Fact]
    public async Task GetJobAsync_WithAbsoluteThumbnailUrl_PreservesUrl()
    {
        // Arrange
        var thumbnailUrl = "http://localhost:8080/0.jpg";
        var job = new Job
        {
            State = "printing",
            Progress = 0.5,
            File = new JobFile
            {
                Name = "model.gcode",
                Refs = new PrintFileRefs { Thumbnail = thumbnailUrl }
            }
        };
        _mockApiClient
            .Setup(x => x.GetJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        var result = await _sut.GetJobAsync("http://localhost:8080", "api-key");

        // Assert
        result!.ThumbnailUrl.Should().Be(thumbnailUrl);
    }

    [Fact]
    public async Task GetJobAsync_WithApiException_ReturnsNullAndLogs()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.GetJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));

        // Act
        var result = await _sut.GetJobAsync("http://localhost:8080", "api-key");

        // Assert
        result.Should().BeNull();
        _mockLogger.Verify(
            x => x.LogError(
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<object?>()),
            Times.Once);
    }

    #endregion

    #region GetCompositeStatusAsync Tests

    [Fact]
    public async Task GetCompositeStatusAsync_WithAllData_ReturnsCompleteStatus()
    {
        // Arrange
        var statusInfo = new StatusInfo
        {
            Printer = new StatusPrinterInfo 
            { 
                State = "Printing",
                StatusPrinter = new PrinterStatusInfo { Ok = true },
                StatusConnect = new PrinterStatusInfo { Ok = true }
            }
        };
        var job = new Job
        {
            State = "printing",
            Progress = 0.75,
            File = new JobFile { Name = "model.gcode" }
        };
        _mockApiClient
            .Setup(x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(statusInfo);
        _mockApiClient
            .Setup(x => x.GetJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        // Act
        var result = await _sut.GetCompositeStatusAsync("http://localhost:8080", "api-key");

        // Assert
        result.IsOnline.Should().BeTrue();
        result.State.Should().Be("Printing");
        result.Progress.Should().Be(0.75);
        result.JobName.Should().Be("model.gcode");
    }

    [Fact]
    public async Task GetCompositeStatusAsync_WithMinimalData_ReturnsBasicStatus()
    {
        // Arrange
        var statusInfo = new StatusInfo
        {
            Printer = new StatusPrinterInfo 
            { 
                State = "Idle",
                StatusPrinter = new PrinterStatusInfo { Ok = true },
                StatusConnect = new PrinterStatusInfo { Ok = true }
            }
        };
        _mockApiClient
            .Setup(x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(statusInfo);
        _mockApiClient
            .Setup(x => x.GetJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Job?)null);

        // Act
        var result = await _sut.GetCompositeStatusAsync("http://localhost:8080", "api-key");

        // Assert
        result.IsOnline.Should().BeTrue();
        result.State.Should().Be("Idle");
        result.Progress.Should().BeNull();
        result.JobName.Should().BeNull();
    }

    [Fact]
    public async Task GetCompositeStatusAsync_WithException_ReturnsOfflineStatus()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        // Act
        var result = await _sut.GetCompositeStatusAsync("http://localhost:8080", "api-key");

        // Assert
        result.IsOnline.Should().BeFalse();
    }

    #endregion

    #region Camera URL Tests

    [Fact]
    public async Task GetCameraStreamUrlAsync_ReturnsProperlyFormedUrl()
    {
        // Act
        var result = await _sut.GetCameraStreamUrlAsync("http://localhost:8080");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("webcam");
        result.Should().Contain("action=stream");
    }

    [Fact]
    public async Task GetCameraSnapshotUrlAsync_ReturnsProperlyFormedUrl()
    {
        // Act
        var result = await _sut.GetCameraSnapshotUrlAsync("http://localhost:8080");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("webcam");
        result.Should().Contain("action=snapshot");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetCameraStreamUrlAsync_WithNullOrWhitespaceUrl_ReturnsNull(string? baseUrl)
    {
        // Act
        var result = await _sut.GetCameraStreamUrlAsync(baseUrl);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region File Operations Tests

    [Fact]
    public void UploadGcodeAsync_UriConstruction_ProducesCorrectPath()
    {
        // Act - Test the fixed Uri construction used in the implementation
        var filePath1 = new Uri(new Uri("http://localhost/"), "model.gcode").LocalPath;
        var filePath2 = new Uri(new Uri("http://localhost/"), "subfolder/model.gcode").LocalPath;

        // Assert
        filePath1.Should().Be("/model.gcode");
        filePath2.Should().Be("/subfolder/model.gcode");
    }

    [Fact]
    public async Task UploadGcodeAsync_WithValidFile_ReturnsTrue()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.UploadFileAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<System.IO.Stream>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        using var stream = new System.IO.MemoryStream(new byte[] { 71, 50, 56 }); // "G28"

        // Act
        var result = await _sut.UploadGcodeAsync("http://localhost:8080", "model.gcode", stream, "api-key");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task UploadGcodeAsync_WithApiFailure_ReturnsFalse()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.UploadFileAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<System.IO.Stream>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        using var stream = new System.IO.MemoryStream();

        // Act
        var result = await _sut.UploadGcodeAsync(
            "http://localhost:8080",
            "model.gcode",
            stream,
            "api-key");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task StartPrintAsync_WithValidFile_ReturnsTrue()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.StartPrintAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _sut.StartPrintAsync("http://localhost:8080", "model.gcode", "api-key");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task StartPrintAsync_WithNonexistentFile_ReturnsFalse()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.StartPrintAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _sut.StartPrintAsync("http://localhost:8080", "missing.gcode", "api-key");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetFileListAsync_WithFiles_ReturnsFileList()
    {
        // Arrange
        var folderInfo = new FolderInfo
        {
            Children = new FileInfoBase[]
            {
                new PrintFileInfo { Name = "model1.gcode", Type = FileTypes.File },
                new PrintFileInfo { Name = "model2.gcode", Type = FileTypes.File },
                new FolderInfo { Name = "subfolder", Type = FileTypes.Folder }
            }
        };
        _mockApiClient
            .Setup(x => x.GetFileInfoAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(folderInfo);

        // Act
        var result = await _sut.GetFileListAsync("http://localhost:8080", "api-key");

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result.Should().Contain("model1.gcode");
        result.Should().Contain("model2.gcode");
        result.Should().NotContain("subfolder");
    }

    [Fact]
    public async Task GetFileListAsync_WithEmptyFolder_ReturnsEmptyArray()
    {
        // Arrange
        var folderInfo = new FolderInfo { Children = null };
        _mockApiClient
            .Setup(x => x.GetFileInfoAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(folderInfo);

        // Act
        var result = await _sut.GetFileListAsync("http://localhost:8080", "api-key");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFileListAsync_WithApiException_FallsBackToLegacyAndReturnsEmpty()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.GetFileInfoAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));
        _mockApiClient
            .Setup(x => x.GetFilesLegacyAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Legacy API also failed"));

        // Act
        var result = await _sut.GetFileListAsync("http://localhost:8080", "api-key");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
        _mockLogger.Verify(
            x => x.LogError(
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<object?>()),
            Times.Once);
    }

    #endregion

    #region URL Normalization Tests

    [Theory]
    [InlineData("localhost:8080")]
    [InlineData("http://localhost:8080")]
    [InlineData("192.168.1.50:8080")]
    [InlineData("https://localhost:8080")]
    public async Task GetStatusAsync_WithVariousUrlFormats_ProcessesCorrectly(string baseUrl)
    {
        // Arrange
        var statusInfo = new StatusInfo
        {
            Printer = new StatusPrinterInfo 
            { 
                State = "Operational",
                StatusPrinter = new PrinterStatusInfo { Ok = true },
                StatusConnect = new PrinterStatusInfo { Ok = true }
            }
        };
        _mockApiClient
            .Setup(x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(statusInfo);

        // Act
        var result = await _sut.GetStatusAsync(baseUrl, "api-key");

        // Assert
        result.IsOnline.Should().BeTrue();
        _mockApiClient.Verify(
            x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task GetStatusAsync_WhenCancelled_ReturnsFalse()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockApiClient
            .Setup(x => x.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var result = await _sut.GetStatusAsync("http://localhost:8080", "api-key", cts.Token);

        // Assert
        // Implementation catches all exceptions, so cancellation doesn't propagate
        result.IsOnline.Should().BeFalse();
        result.State.Should().BeNull();
    }

    #endregion
}

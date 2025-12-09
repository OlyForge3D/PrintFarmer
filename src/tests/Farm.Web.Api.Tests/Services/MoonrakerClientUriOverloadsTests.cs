using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class MoonrakerClientUriOverloadsTests
{
    private readonly MoonrakerClient _client;
    private readonly Mock<HttpClient> _httpClientMock;

    public MoonrakerClientUriOverloadsTests()
    {
        _httpClientMock = new Mock<HttpClient>();
        _client = new MoonrakerClient(_httpClientMock.Object, new Mock<IUnifiedLoggingService>().Object);
    }

    [Fact]
    public async Task GetStatusAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetStatusAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetPrinterInfoAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetPrinterInfoAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetJobAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetJobAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetCompositeStatusAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetCompositeStatusAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetCameraSnapshotAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetCameraSnapshotAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetConfiguredCameraUrlsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetConfiguredCameraUrlsAsync(nullUri!, null, CancellationToken.None));
    }

    [Fact]
    public async Task SendHomeAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.SendHomeAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task HomeXYAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.HomeXYAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task HomeZAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.HomeZAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task SetTempsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.SetTempsAsync(nullUri!, 200, 60, CancellationToken.None));
    }

    [Fact]
    public async Task MoveAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.MoveAsync(nullUri!, 10, 20, 30, 100, CancellationToken.None));
    }

    [Fact]
    public async Task MoveToAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.MoveToAsync(nullUri!, 10, 20, 30, 100, CancellationToken.None));
    }

    [Fact]
    public async Task PauseAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.PauseAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task ResumeAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.ResumeAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task EmergencyStopAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.EmergencyStopAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task StartPrintAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.StartPrintAsync(nullUri!, "file.gcode", CancellationToken.None));
    }

    [Fact]
    public async Task StartPrintAsync_WithNullFileName_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullFileName = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.StartPrintAsync(uri, nullFileName!, CancellationToken.None));
    }

    [Fact]
    public async Task GetFileListAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetFileListAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetFileRootsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetFileRootsAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetDirectoryAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetDirectoryAsync(nullUri!, "path", false, CancellationToken.None));
    }

    [Fact]
    public async Task GetDirectoryAsync_WithNullPath_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullPath = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetDirectoryAsync(uri, nullPath!, false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateDirectoryAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.CreateDirectoryAsync(nullUri!, "path", CancellationToken.None));
    }

    [Fact]
    public async Task CreateDirectoryAsync_WithNullPath_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullPath = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.CreateDirectoryAsync(uri, nullPath!, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteFileOrDirectoryAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DeleteFileOrDirectoryAsync(nullUri!, "path", false, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteFileOrDirectoryAsync_WithNullPath_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullPath = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DeleteFileOrDirectoryAsync(uri, nullPath!, false, CancellationToken.None));
    }

    [Fact]
    public async Task MoveFileAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.MoveFileAsync(nullUri!, "source", "dest", CancellationToken.None));
    }

    [Fact]
    public async Task MoveFileAsync_WithNullSource_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullSource = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.MoveFileAsync(uri, nullSource!, "dest", CancellationToken.None));
    }

    [Fact]
    public async Task MoveFileAsync_WithNullDest_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullDest = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.MoveFileAsync(uri, "source", nullDest!, CancellationToken.None));
    }

    [Fact]
    public async Task CopyFileAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.CopyFileAsync(nullUri!, "source", "dest", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteFileAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DeleteFileAsync(nullUri!, "path", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteFileAsync_WithNullPath_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullPath = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DeleteFileAsync(uri, nullPath!, CancellationToken.None));
    }

    [Fact]
    public async Task GetFileStreamAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetFileStreamAsync(nullUri!, "file.gcode", CancellationToken.None));
    }

    [Fact]
    public async Task GetFileStreamAsync_WithNullFilename_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullFilename = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetFileStreamAsync(uri, nullFilename!, CancellationToken.None));
    }

    [Fact]
    public async Task GetFileMetadataAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetFileMetadataAsync(nullUri!, "file.gcode", CancellationToken.None));
    }

    [Fact]
    public async Task GetFileMetadataAsync_WithNullFilename_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullFilename = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetFileMetadataAsync(uri, nullFilename!, CancellationToken.None));
    }

    [Fact]
    public async Task StartMetadataScanAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.StartMetadataScanAsync(nullUri!, "file.gcode", CancellationToken.None));
    }

    [Fact]
    public async Task StartMetadataScanAsync_WithNullFilename_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullFilename = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.StartMetadataScanAsync(uri, nullFilename!, CancellationToken.None));
    }

    [Fact]
    public async Task GetFileThumbnailAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetFileThumbnailAsync(nullUri!, "file.gcode", CancellationToken.None));
    }

    [Fact]
    public async Task GetFileThumbnailAsync_WithNullFilename_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullFilename = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetFileThumbnailAsync(uri, nullFilename!, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadFileAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DownloadFileAsync(nullUri!, "file.gcode", CancellationToken.None));
    }

    [Fact]
    public async Task DownloadFileAsync_WithNullFilename_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullFilename = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DownloadFileAsync(uri, nullFilename!, CancellationToken.None));
    }

    [Fact]
    public async Task GetDetailedFileListAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetDetailedFileListAsync(nullUri!, "gcodes", null, CancellationToken.None));
    }

    [Fact]
    public async Task UploadGcodeAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UploadGcodeAsync(nullUri!, "file.gcode", new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public async Task UploadGcodeAsync_WithNullFileName_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullFileName = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UploadGcodeAsync(uri, nullFileName!, new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public async Task UploadGcodeAsync_WithNullContent_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        Stream? nullStream = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UploadGcodeAsync(uri, "file.gcode", nullStream!, CancellationToken.None));
    }

    [Fact]
    public async Task UploadFileAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UploadFileAsync(nullUri!, "gcodes", "file.gcode", new MemoryStream(), false, CancellationToken.None));
    }

    [Fact]
    public async Task UploadFileAsync_WithNullRoot_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullRoot = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UploadFileAsync(uri, nullRoot!, "file.gcode", new MemoryStream(), false, CancellationToken.None));
    }

    [Fact]
    public async Task UploadFileAsync_WithNullFilename_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullFilename = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UploadFileAsync(uri, "gcodes", nullFilename!, new MemoryStream(), false, CancellationToken.None));
    }

    [Fact]
    public async Task UploadFileAsync_WithNullContent_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        Stream? nullStream = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UploadFileAsync(uri, "gcodes", "file.gcode", nullStream!, false, CancellationToken.None));
    }

    [Fact]
    public async Task GetHistoryListAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetHistoryListAsync(nullUri!, null, null, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetHistoryJobAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetHistoryJobAsync(nullUri!, "jobId", CancellationToken.None));
    }

    [Fact]
    public async Task GetHistoryJobAsync_WithNullJobId_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullJobId = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetHistoryJobAsync(uri, nullJobId!, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteHistoryJobAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DeleteHistoryJobAsync(nullUri!, "jobId", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteHistoryJobAsync_WithNullJobId_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullJobId = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DeleteHistoryJobAsync(uri, nullJobId!, CancellationToken.None));
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetHistoryTotalsAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task ResetHistoryTotalsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.ResetHistoryTotalsAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanStatusAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanStatusAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanActiveSpoolAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanActiveSpoolAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task SetSpoolmanActiveSpoolAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.SetSpoolmanActiveSpoolAsync(nullUri!, 1, CancellationToken.None));
    }

    [Fact]
    public async Task SpoolmanProxyRequestAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.SpoolmanProxyRequestAsync(nullUri!, "GET", "/path", null, null, false, CancellationToken.None));
    }

    [Fact]
    public async Task SpoolmanProxyRequestAsync_WithNullMethod_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullMethod = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.SpoolmanProxyRequestAsync(uri, nullMethod!, "/path", null, null, false, CancellationToken.None));
    }

    [Fact]
    public async Task SpoolmanProxyRequestAsync_WithNullPath_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        string? nullPath = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.SpoolmanProxyRequestAsync(uri, "GET", nullPath!, null, null, false, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanSpoolsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanSpoolsAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanSpoolByIdAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanSpoolByIdAsync(nullUri!, 1, CancellationToken.None));
    }

    [Fact]
    public async Task CreateSpoolmanSpoolAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.CreateSpoolmanSpoolAsync(nullUri!, new object(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateSpoolmanSpoolAsync_WithNullSpoolData_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        object? nullData = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.CreateSpoolmanSpoolAsync(uri, nullData!, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSpoolmanSpoolAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UpdateSpoolmanSpoolAsync(nullUri!, 1, new object(), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSpoolmanSpoolAsync_WithNullSpoolData_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        object? nullData = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UpdateSpoolmanSpoolAsync(uri, 1, nullData!, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteSpoolmanSpoolAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DeleteSpoolmanSpoolAsync(nullUri!, 1, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanFilamentsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanFilamentsAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanFilamentByIdAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanFilamentByIdAsync(nullUri!, 1, CancellationToken.None));
    }

    [Fact]
    public async Task CreateSpoolmanFilamentAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.CreateSpoolmanFilamentAsync(nullUri!, new object(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateSpoolmanFilamentAsync_WithNullFilamentData_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        object? nullData = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.CreateSpoolmanFilamentAsync(uri, nullData!, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSpoolmanFilamentAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UpdateSpoolmanFilamentAsync(nullUri!, 1, new object(), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSpoolmanFilamentAsync_WithNullFilamentData_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        object? nullData = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UpdateSpoolmanFilamentAsync(uri, 1, nullData!, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteSpoolmanFilamentAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DeleteSpoolmanFilamentAsync(nullUri!, 1, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanVendorsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanVendorsAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanVendorByIdAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanVendorByIdAsync(nullUri!, 1, CancellationToken.None));
    }

    [Fact]
    public async Task CreateSpoolmanVendorAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.CreateSpoolmanVendorAsync(nullUri!, new object(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateSpoolmanVendorAsync_WithNullVendorData_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        object? nullData = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.CreateSpoolmanVendorAsync(uri, nullData!, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSpoolmanVendorAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UpdateSpoolmanVendorAsync(nullUri!, 1, new object(), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSpoolmanVendorAsync_WithNullVendorData_ThrowsArgumentNullException()
    {
        // Arrange
        var uri = new Uri("http://localhost:7125");
        object? nullData = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UpdateSpoolmanVendorAsync(uri, 1, nullData!, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteSpoolmanVendorAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.DeleteSpoolmanVendorAsync(nullUri!, 1, CancellationToken.None));
    }

    [Fact]
    public async Task UseSpoolmanFilamentAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.UseSpoolmanFilamentAsync(nullUri!, 10.5, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanInfoAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanInfoAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanHealthAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanHealthAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task SearchSpoolmanSpoolsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.SearchSpoolmanSpoolsAsync(nullUri!, null, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task SearchSpoolmanFilamentsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.SearchSpoolmanFilamentsAsync(nullUri!, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveSpoolmanSpoolAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.ArchiveSpoolmanSpoolAsync(nullUri!, 1, true, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanStatsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanStatsAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task BackupSpoolmanAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.BackupSpoolmanAsync(nullUri!, CancellationToken.None));
    }

    [Fact]
    public async Task GetSpoolmanIntegrationsAsync_WithNullUri_ThrowsArgumentNullException()
    {
        // Arrange
        Uri? nullUri = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _client.GetSpoolmanIntegrationsAsync(nullUri!, CancellationToken.None));
    }
}

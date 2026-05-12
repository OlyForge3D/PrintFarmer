using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Cameras;

/// <summary>
/// Unit tests for <see cref="CameraSnapshotService"/>.
/// </summary>
public class CameraSnapshotServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<ICameraRepository> _cameraRepo;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;
    private readonly Mock<IStoragePathService> _storagePathService;
    private readonly string _snapshotRoot;

    public CameraSnapshotServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _cameraRepo = new Mock<ICameraRepository>(MockBehavior.Strict);
        _httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Loose);
        _storagePathService = new Mock<IStoragePathService>(MockBehavior.Strict);

        _snapshotRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfarm-snapshot-tests", Guid.NewGuid().ToString());
        _storagePathService.Setup(s => s.GetSnapshotStorageDirectory()).Returns(_snapshotRoot);
    }

    public void Dispose() => _db.Dispose();

    private CameraSnapshotService CreateService(HttpMessageHandler? handler = null)
    {
        if (handler is not null)
        {
            _httpClientFactory.Setup(f => f.CreateClient("CameraSnapshot"))
                              .Returns(new HttpClient(handler));
        }

        return new CameraSnapshotService(
            _db,
            _cameraRepo.Object,
            _httpClientFactory.Object,
            _storagePathService.Object,
            new Mock<ILogger<CameraSnapshotService>>(MockBehavior.Loose).Object);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_WhenNoCamerasForPrinter_ReturnsWithoutCapturing()
    {
        var printerId = Guid.NewGuid();
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([]);

        var service = CreateService();

        await service.CaptureSnapshotAsync(printerId, "PrintStarted");

        _db.CameraSnapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureSnapshotAsync_WhenAllCamerasDisabled_ReturnsWithoutCapturing()
    {
        var printerId = Guid.NewGuid();
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Disabled Camera",
            IsEnabled = false,
            SnapshotUrl = "http://192.168.1.10/snapshot.jpg",
        };
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([camera]);

        var service = CreateService();

        await service.CaptureSnapshotAsync(printerId, "PrintStarted");

        _db.CameraSnapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureSnapshotAsync_WhenCameraHasNoSnapshotUrl_ReturnsWithoutCapturing()
    {
        var printerId = Guid.NewGuid();
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "No URL Camera",
            IsEnabled = true,
            SnapshotUrl = null,
        };
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([camera]);

        var service = CreateService();

        await service.CaptureSnapshotAsync(printerId, "PrintStarted");

        _db.CameraSnapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureSnapshotAsync_WithValidCamera_StoresDatabaseRecord()
    {
        var printerId = Guid.NewGuid();
        var printJobId = Guid.NewGuid();
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Test Camera",
            IsEnabled = true,
            SnapshotUrl = "http://192.168.1.10/snapshot.jpg",
        };
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([camera]);

        var handler = CreateJpegResponseHandler();
        var service = CreateService(handler);

        await service.CaptureSnapshotAsync(printerId, "PrintStarted", printJobId);

        CameraSnapshot snapshot = _db.CameraSnapshots.Single();
        snapshot.PrinterId.Should().Be(printerId);
        snapshot.CameraId.Should().Be(camera.Id);
        snapshot.PrintJobId.Should().Be(printJobId);
        snapshot.EventType.Should().Be("PrintStarted");
        snapshot.FilePath.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CaptureSnapshotAsync_WithValidCamera_WritesFileToDisk()
    {
        var printerId = Guid.NewGuid();
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Test Camera",
            IsEnabled = true,
            SnapshotUrl = "http://192.168.1.10/snapshot.jpg",
        };
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([camera]);

        var handler = CreateJpegResponseHandler();
        var service = CreateService(handler);

        await service.CaptureSnapshotAsync(printerId, "PrintCompleted");

        CameraSnapshot snapshot = _db.CameraSnapshots.Single();
        string fullPath = System.IO.Path.Combine(_snapshotRoot, snapshot.FilePath);
        System.IO.File.Exists(fullPath).Should().BeTrue();
    }

    [Fact]
    public async Task CaptureSnapshotAsync_OneFailingCamera_StillCapturesOtherCameras()
    {
        var printerId = Guid.NewGuid();
        var goodCamera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Good Camera",
            IsEnabled = true,
            SnapshotUrl = "http://192.168.1.10/snapshot.jpg",
        };
        var badCamera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Bad Camera",
            IsEnabled = true,
            SnapshotUrl = "http://192.168.1.99/snapshot.jpg",
        };
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([goodCamera, badCamera]);

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("JFIF"))
            })
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        _httpClientFactory.Setup(f => f.CreateClient("CameraSnapshot"))
                          .Returns(new HttpClient(handlerMock.Object));

        var service = CreateService();

        await service.CaptureSnapshotAsync(printerId, "PrintStarted");

        _db.CameraSnapshots.Count().Should().Be(1);
        _db.CameraSnapshots.Single().CameraId.Should().Be(goodCamera.Id);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_FilePathContainsPrinterIdAndEventType()
    {
        var printerId = Guid.NewGuid();
        var printJobId = Guid.NewGuid();
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Test Camera",
            IsEnabled = true,
            SnapshotUrl = "http://192.168.1.10/snapshot.jpg",
        };
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([camera]);

        var service = CreateService(CreateJpegResponseHandler());

        await service.CaptureSnapshotAsync(printerId, "PrintFailed", printJobId);

        CameraSnapshot snapshot = _db.CameraSnapshots.Single();
        snapshot.FilePath.Should().Contain(printerId.ToString());
        snapshot.FilePath.Should().Contain(printJobId.ToString());
        snapshot.FilePath.Should().Contain("PrintFailed");
    }

    [Fact]
    public async Task CaptureSnapshotAsync_WhenCancelled_RethrowsOperationCancelledException()
    {
        var printerId = Guid.NewGuid();
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Test Camera",
            IsEnabled = true,
            SnapshotUrl = "http://192.168.1.10/snapshot.jpg",
        };
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([camera]);

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        _httpClientFactory.Setup(f => f.CreateClient("CameraSnapshot"))
                          .Returns(new HttpClient(handlerMock.Object));

        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => service.CaptureSnapshotAsync(printerId, "PrintStarted", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CaptureSnapshotAsync_WhenSnapshotUrlIsLoopback_SkipsCamera()
    {
        var printerId = Guid.NewGuid();
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Loopback Camera",
            IsEnabled = true,
            SnapshotUrl = "http://127.0.0.1/snapshot.jpg",
        };
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([camera]);

        var service = CreateService();

        await service.CaptureSnapshotAsync(printerId, "PrintStarted");

        _db.CameraSnapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureSnapshotAsync_WhenSnapshotUrlIsLinkLocal_SkipsCamera()
    {
        var printerId = Guid.NewGuid();
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Link-local Camera",
            IsEnabled = true,
            SnapshotUrl = "http://169.254.169.254/latest/meta-data/",
        };
        _cameraRepo.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync([camera]);

        var service = CreateService();

        await service.CaptureSnapshotAsync(printerId, "PrintStarted");

        _db.CameraSnapshots.Should().BeEmpty();
    }

    // --- Future tests (require Lambert's SSRF fix) ---
    // The SSRF fix is already present in CaptureFromCameraAsync — the above two tests cover it.

    private static HttpMessageHandler CreateJpegResponseHandler()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("JFIF_fake_jpeg_content"))
            });
        return handlerMock.Object;
    }
}

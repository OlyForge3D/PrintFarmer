using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Printers;

public class PrintersServiceSnapmakerU1CameraTests
{
    [Fact]
    public async Task GetCameraSnapshotAsync_PluginUnavailable_RetainsStoredDirectCameraFallback()
    {
        Printer printer = CreatePrinter("Voron", "V2.4");
        byte[] image = [0xff, 0xd8, 0xff, 0xd9];
        List<Camera> cameras =
        [
            new Camera
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Name = "camera",
                IsEnabled = true,
                Source = CameraSource.Moonraker,
                SnapshotUrl = "http://camera.local/snapshot.jpg",
            },
        ];
        var backendClients = new Mock<IBackendClientFactory>();
        backendClients.Setup(factory => factory.GetClient(PrinterBackend.Moonraker))
            .Throws(new InvalidOperationException("plugin unavailable"));
        using var handler = new SnapshotHandler(image);
        using var http = new HttpClient(handler);
        var httpClients = new Mock<IHttpClientFactory>();
        httpClients.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(http);
        await using AppDbContext db = CreateDbContext();
        PrintersService service = await CreateServiceAsync(
            db, printer, cameras, CreateDetectionClient((null, null)).Object, backendClients.Object, httpClients.Object);

        byte[]? result = await service.GetCameraSnapshotAsync(printer.Id, CancellationToken.None);

        result.Should().Equal(image);
        handler.RequestUrl.Should().Be("http://camera.local/snapshot.jpg");
    }

    [Fact]
    public async Task RefreshCameraUrlsAsync_WhenSnapmakerU1HasNoWebcamList_StoresSnapshotOnlyU1Strategy()
    {
        Printer printer = CreatePrinter("Snapmaker", "Snapmaker U1");
        List<Camera> cameras = [];
        Mock<ISupportsConfiguredCameraDetection> detection = CreateDetectionClient((null, null));
        await using AppDbContext db = CreateDbContext();
        PrintersService service = await CreateServiceAsync(db, printer, cameras, detection.Object);

        PrinterDto? dto = await service.RefreshCameraUrlsAsync(printer.Id, CancellationToken.None);

        cameras.Should().ContainSingle();
        Camera camera = cameras.Single();
        camera.StreamUrl.Should().BeNull();
        camera.SnapshotUrl.Should().Be("http://u1.local/server/files/camera/monitor.jpg");
        dto.Should().NotBeNull();
        dto!.CameraAccessMode.Should().Be(CameraAccessMode.SnapshotOnly);
        dto.CameraStreamFormat.Should().Be(CameraStreamFormat.Unknown);
        dto.CameraSnapshotStrategy.Should().Be(CameraSnapshotStrategy.SnapmakerU1MonitorJpeg);
    }

    [Fact]
    public async Task RefreshCameraUrlsAsync_WhenMoonrakerIsNotU1_UsesConfiguredWebcamsListWithoutU1Fallback()
    {
        Printer printer = CreatePrinter("Voron", "V2.4");
        List<Camera> cameras = [];
        string streamUrl = "http://voron.local/webcam/?action=stream";
        string snapshotUrl = "http://voron.local/webcam/?action=snapshot";
        Mock<ISupportsConfiguredCameraDetection> detection = CreateDetectionClient((streamUrl, snapshotUrl));
        await using AppDbContext db = CreateDbContext();
        PrintersService service = await CreateServiceAsync(db, printer, cameras, detection.Object);

        PrinterDto? dto = await service.RefreshCameraUrlsAsync(printer.Id, CancellationToken.None);

        cameras.Should().ContainSingle();
        Camera camera = cameras.Single();
        camera.StreamUrl.Should().Be(streamUrl);
        camera.SnapshotUrl.Should().Be(snapshotUrl);
        dto.Should().NotBeNull();
        dto!.CameraAccessMode.Should().Be(CameraAccessMode.StreamAndSnapshot);
        dto.CameraStreamFormat.Should().Be(CameraStreamFormat.Mjpeg);
        dto.CameraSnapshotStrategy.Should().Be(CameraSnapshotStrategy.DirectUrl);
        detection.Verify(c => c.DetectConfiguredCameraUrlsAsync(
            "http://voron.local:7125", 80, It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<ISupportsConfiguredCameraDetection> CreateDetectionClient((string? StreamUrl, string? SnapshotUrl) urls)
    {
        var detection = new Mock<ISupportsConfiguredCameraDetection>();
        detection
            .Setup(c => c.DetectConfiguredCameraUrlsAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(urls);
        return detection;
    }

    private static Printer CreatePrinter(string manufacturerName, string modelName)
    {
        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = manufacturerName };
        PrinterModel model = new() { Id = Guid.NewGuid(), Name = modelName, Manufacturer = manufacturer, ManufacturerId = manufacturer.Id };
        return new Printer
        {
            Id = Guid.NewGuid(),
            Name = modelName,
            ServerUrl = manufacturerName == "Snapmaker" ? "http://u1.local" : "http://voron.local",
            Backend = (int)PrinterBackend.Moonraker,
            BackendPort = manufacturerName == "Snapmaker" ? 80 : 7125,
            FrontendPort = 80,
            Manufacturer = manufacturer,
            ManufacturerId = manufacturer.Id,
            Model = model,
            ModelId = model.Id
        };
    }

    private static async Task<PrintersService> CreateServiceAsync(
        AppDbContext db,
        Printer printer,
        List<Camera> cameras,
        ISupportsConfiguredCameraDetection detectionClient,
        IBackendClientFactory? backendClients = null,
        IHttpClientFactory? httpClients = null)
    {
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository
            .Setup(r => r.FindByIdWithIncludesAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        printersRepository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        var cameraRepository = new Mock<ICameraRepository>();
        cameraRepository
            .Setup(r => r.FindByPrinterIdAndSourceAsync(printer.Id, CameraSource.Moonraker, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => cameras.FirstOrDefault(c => c.Source == CameraSource.Moonraker));
        cameraRepository
            .Setup(r => r.GetByPrinterIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cameras);
        cameraRepository
            .Setup(r => r.Add(It.IsAny<Camera>()))
            .Callback<Camera>(cameras.Add);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.Printers).Returns(printersRepository.Object);
        unitOfWork.Setup(u => u.Cameras).Returns(cameraRepository.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var capabilityFactory = new Mock<IBackendCapabilityFactory>();
        ISupportsConfiguredCameraDetection? detectionOut = detectionClient;
        capabilityFactory
            .Setup(f => f.TryGetConfiguredCameraDetectionClient(PrinterBackend.Moonraker, out detectionOut))
            .Returns(true);

        var statusFactory = new Mock<IPrinterStatusClientFactory>();
        statusFactory
            .Setup(f => f.GetStatusClient(It.IsAny<int>()))
            .Throws(new InvalidOperationException("offline"));

        var backendFactory = new Mock<IBackendClientFactory>();
        backendFactory.Setup(factory => factory.GetClient(PrinterBackend.Moonraker))
            .Returns(new MoonrakerClient(
                new HttpClient(),
                NullLogger<MoonrakerClient>.Instance,
                new Farm.Infrastructure.Settings.BackendTimeoutSettings()));

        return new PrintersService(
            unitOfWork.Object,
            db,
            backendClients ?? backendFactory.Object,
            capabilityFactory.Object,
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            httpClients ?? Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            statusFactory.Object,
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());
    }

    private sealed class SnapshotHandler(byte[] image) : HttpMessageHandler
    {
        public string? RequestUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(image),
            });
        }
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PrintersServiceSnapmakerU1CameraTests_{Guid.NewGuid():N}")
            .Options;

        return new AppDbContext(options);
    }
}

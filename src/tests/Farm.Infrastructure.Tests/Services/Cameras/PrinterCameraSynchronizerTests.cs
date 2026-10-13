using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Cameras;

public sealed class PrinterCameraSynchronizerTests : IAsyncDisposable
{
    private const string StreamUrl = "http://voron.local/webcam/?action=stream";
    private const string SnapshotUrl = "http://voron.local/webcam/?action=snapshot";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"PrinterCameraSynchronizerTests_{Guid.NewGuid():N}")
        .Options);

    private readonly Mock<IPrintersRepository> _printers = new();
    private readonly Mock<IBackendCapabilityFactory> _capabilities = new();
    private readonly Mock<ISupportsConfiguredCameraDetection> _detection = new();
    private readonly Mock<IPrinterStatusCacheReader> _statusCache = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly Mock<IHubClients> _hubClients = new();
    private readonly Mock<IHubContext<PrinterHub>> _hub = new();
    private readonly List<(string Group, string Method, object?[] Args)> _broadcasts = [];

    public PrinterCameraSynchronizerTests()
    {
        ISupportsConfiguredCameraDetection? detection = _detection.Object;
        _capabilities
            .Setup(f => f.TryGetConfiguredCameraDetectionClient(It.IsAny<PrinterBackend>(), out detection))
            .Returns(true);

        string? lastGroup = null;
        _hubClients.Setup(c => c.Group(It.IsAny<string>()))
            .Callback<string>(g => lastGroup = g)
            .Returns(_clientProxy.Object);
        _clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) => _broadcasts.Add((lastGroup!, method, args)))
            .Returns(Task.CompletedTask);
        _hub.Setup(h => h.Clients).Returns(_hubClients.Object);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task SyncPrinterAsync_FirstRun_CreatesPrimaryCameraAndBroadcasts()
    {
        Printer printer = await AddPrinterAsync();
        SetupDetection(StreamUrl, SnapshotUrl);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        result.Created.Should().Be(1);
        Camera camera = await _db.Cameras.Include(c => c.PrinterLinks).SingleAsync();
        camera.Name.Should().Be("Voron Camera");
        camera.Source.Should().Be(CameraSource.Moonraker);
        camera.StreamUrl.Should().Be(StreamUrl);
        camera.SnapshotUrl.Should().Be(SnapshotUrl);
        camera.UserModified.Should().BeFalse();
        CameraPrinter link = camera.PrinterLinks.Should().ContainSingle().Subject;
        link.PrinterId.Should().Be(printer.Id);
        link.IsPrimary.Should().BeTrue();

        (string group, string method, object?[] args) = _broadcasts.Should().ContainSingle().Subject;
        group.Should().Be(AuthorizedHubGroups.Printer(printer.Id));
        method.Should().Be("camerasupdated");
        CamerasUpdatedEvent payload = args.Should().ContainSingle().Which.Should().BeOfType<CamerasUpdatedEvent>().Subject;
        payload.PrinterId.Should().Be(printer.Id);
        payload.CameraIds.Should().Equal(camera.Id);
    }

    [Fact]
    public async Task SyncPrinterAsync_SecondRunWithSameUrls_IsIdempotent()
    {
        Printer printer = await AddPrinterAsync();
        SetupDetection(StreamUrl, SnapshotUrl);
        PrinterCameraSynchronizer sut = CreateSut();
        await sut.SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        PrinterCameraSyncResult second = await sut.SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        second.Changed.Should().BeFalse();
        second.CameraIds.Should().ContainSingle();
        (await _db.Cameras.CountAsync()).Should().Be(1);
        _broadcasts.Should().ContainSingle("only the first run changed anything");
    }

    [Fact]
    public async Task SyncPrinterAsync_ChangedUrls_UpdatesExistingManagedCamera()
    {
        Printer printer = await AddPrinterAsync();
        Camera existing = await AddCameraAsync(printer.Id, "Voron Camera", CameraSource.Moonraker, "http://old/stream", isPrimary: true);
        SetupDetection(StreamUrl, SnapshotUrl);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        result.Created.Should().Be(0);
        result.Updated.Should().Be(1);
        Camera camera = await _db.Cameras.SingleAsync();
        camera.Id.Should().Be(existing.Id);
        camera.StreamUrl.Should().Be(StreamUrl);
        camera.SnapshotUrl.Should().Be(SnapshotUrl);
    }

    [Fact]
    public async Task SyncPrinterAsync_UserModifiedCamera_IsNeverOverwrittenOrDuplicated()
    {
        Printer printer = await AddPrinterAsync();
        Camera existing = await AddCameraAsync(printer.Id, "My Cam", CameraSource.Moonraker, "http://custom/stream", isPrimary: true, userModified: true);
        SetupDetection(StreamUrl, SnapshotUrl);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: true, CancellationToken.None);

        result.Changed.Should().BeFalse();
        Camera camera = await _db.Cameras.SingleAsync();
        camera.Id.Should().Be(existing.Id);
        camera.Name.Should().Be("My Cam");
        camera.StreamUrl.Should().Be("http://custom/stream");
        camera.HealthStatus.Should().Be(CameraHealthStatus.Healthy);
        _broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncPrinterAsync_StandaloneCamera_IsNotOverwrittenAndKeepsPrimary()
    {
        Printer printer = await AddPrinterAsync();
        Camera standalone = await AddCameraAsync(printer.Id, "Room Cam", CameraSource.Standalone, "rtsp://room/cam", isPrimary: true);
        SetupDetection(StreamUrl, SnapshotUrl);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: true, CancellationToken.None);

        result.Created.Should().Be(1);
        List<Camera> cameras = await _db.Cameras.Include(c => c.PrinterLinks).ToListAsync();
        cameras.Should().HaveCount(2);
        Camera room = cameras.Single(c => c.Id == standalone.Id);
        room.StreamUrl.Should().Be("rtsp://room/cam");
        room.HealthStatus.Should().Be(CameraHealthStatus.Healthy);
        room.PrinterLinks.Single().IsPrimary.Should().BeTrue();
        cameras.Single(c => c.Id != standalone.Id).PrinterLinks.Single().IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task SyncPrinterAsync_PrinterNoLongerReportsCamera_MarksItOfflineWithoutDeleting()
    {
        Printer printer = await AddPrinterAsync();
        Camera existing = await AddCameraAsync(printer.Id, "Voron Camera", CameraSource.Moonraker, StreamUrl, isPrimary: true);
        SetupDetection(null, null);
        SetupOnline(printer.Id, isOnline: true);
        PrinterCameraSynchronizer sut = CreateSut();

        PrinterCameraSyncResult result = await sut.SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);
        PrinterCameraSyncResult second = await sut.SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        result.MarkedOffline.Should().Be(1);
        second.Changed.Should().BeFalse("an already-offline camera is not re-marked");
        Camera camera = await _db.Cameras.SingleAsync();
        camera.Id.Should().Be(existing.Id);
        camera.HealthStatus.Should().Be(CameraHealthStatus.Unhealthy);
        camera.HealthMessage.Should().Be(PrinterCameraSynchronizer.NotReportedHealthMessage);
        _broadcasts.Should().ContainSingle();
    }

    [Fact]
    public async Task SyncPrinterAsync_UserModifiedCameraNotReported_IsNeverMarkedOffline()
    {
        Printer printer = await AddPrinterAsync();
        Camera existing = await AddCameraAsync(printer.Id, "My Cam", CameraSource.Moonraker, StreamUrl, isPrimary: true, userModified: true);
        SetupDetection(null, null);
        SetupOnline(printer.Id, isOnline: true);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: true, CancellationToken.None);

        result.MarkedOffline.Should().Be(0);
        result.Changed.Should().BeFalse();
        Camera camera = await _db.Cameras.SingleAsync();
        camera.Id.Should().Be(existing.Id);
        camera.HealthStatus.Should().Be(CameraHealthStatus.Healthy);
        camera.HealthMessage.Should().BeNull();
        _broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncPrinterAsync_StandaloneCameraNotReported_IsNeverMarkedOffline()
    {
        Printer printer = await AddPrinterAsync();
        await AddCameraAsync(printer.Id, "Bench Cam", CameraSource.Standalone, "rtsp://bench/stream", isPrimary: true);
        SetupDetection(null, null);
        SetupOnline(printer.Id, isOnline: true);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: true, CancellationToken.None);

        result.MarkedOffline.Should().Be(0);
        Camera camera = await _db.Cameras.SingleAsync();
        camera.HealthStatus.Should().Be(CameraHealthStatus.Healthy);
        camera.HealthMessage.Should().BeNull();
    }

    [Fact]
    public async Task SyncPrinterAsync_DiscoveredCameraSharedWithAnotherPrinter_IsNotMarkedOffline()
    {
        Printer printer = await AddPrinterAsync();
        Printer other = await AddPrinterAsync();
        Camera shared = await AddCameraAsync(printer.Id, "Voron Camera", CameraSource.Moonraker, StreamUrl, isPrimary: true);
        shared.LinkToPrinter(other.Id, isPrimary: true, sortOrder: 0);
        await _db.SaveChangesAsync();
        SetupDetection(null, null);
        SetupOnline(printer.Id, isOnline: true);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: true, CancellationToken.None);

        result.MarkedOffline.Should().Be(0, "a camera still linked to another printer is not owned by this printer's plugin alone");
        Camera camera = await _db.Cameras.Include(c => c.PrinterLinks).SingleAsync();
        camera.HealthStatus.Should().Be(CameraHealthStatus.Healthy);
        camera.PrinterLinks.Select(l => l.PrinterId).Should().BeEquivalentTo([printer.Id, other.Id]);
    }

    [Fact]
    public async Task SyncPrinterAsync_ExplicitRequestForOfflinePrinter_MarksUnreportedCameraOffline()
    {
        Printer printer = await AddPrinterAsync();
        Camera existing = await AddCameraAsync(printer.Id, "Voron Camera", CameraSource.Moonraker, StreamUrl, isPrimary: true);
        SetupDetection(null, null);
        SetupOnline(printer.Id, isOnline: false);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: true, CancellationToken.None);

        result.MarkedOffline.Should().Be(1, "an explicit discover is authoritative even when the status cache says offline");
        Camera camera = await _db.Cameras.SingleAsync();
        camera.Id.Should().Be(existing.Id);
        camera.HealthStatus.Should().Be(CameraHealthStatus.Unhealthy);
        camera.HealthMessage.Should().Be(PrinterCameraSynchronizer.NotReportedHealthMessage);
    }

    /// <summary>
    /// Removing an auto-discovered camera (Deleted outcome) records a dismissal for the printer, so
    /// background sync must not re-create it while the plugin still reports the stream.
    /// </summary>
    [Fact]
    public async Task SyncPrinterAsync_DiscoveredCameraDeletedAndDismissed_IsNotRecreated()
    {
        Printer printer = await AddPrinterAsync();
        SetupDetection(StreamUrl, SnapshotUrl);
        PrinterCameraSynchronizer sut = CreateSut();
        await sut.SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);
        Camera original = await _db.Cameras.SingleAsync();
        _db.Cameras.Remove(original);
        AddDismissal(printer.Id);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        PrinterCameraSyncResult result = await sut.SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        result.Created.Should().Be(0);
        result.Updated.Should().Be(0);
        (await _db.Cameras.AnyAsync()).Should().BeFalse();
        (await _db.Set<CameraPrinter>().AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Removing an auto-discovered camera that stays in the library (Unlinked outcome) records a
    /// dismissal, so sync must not create a duplicate camera beside the unlinked row.
    /// </summary>
    [Fact]
    public async Task SyncPrinterAsync_DiscoveredCameraUnlinkedAndDismissed_DoesNotCreateDuplicate()
    {
        Printer printer = await AddPrinterAsync();
        SetupDetection(StreamUrl, SnapshotUrl);
        PrinterCameraSynchronizer sut = CreateSut();
        await sut.SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);
        Camera original = await _db.Cameras.Include(c => c.PrinterLinks).SingleAsync();
        original.PrinterLinks.Clear();
        original.UserModified = true;
        AddDismissal(printer.Id);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        PrinterCameraSyncResult result = await sut.SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        result.Created.Should().Be(0);
        Camera camera = await _db.Cameras.Include(c => c.PrinterLinks).SingleAsync();
        camera.Id.Should().Be(original.Id);
        camera.PrinterLinks.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAllAsync_DismissedCamera_KeepsDismissalAndDoesNotRecreate()
    {
        Printer printer = await AddPrinterAsync();
        SetupDetection(StreamUrl, SnapshotUrl);
        AddDismissal(printer.Id);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await CreateSut().SyncAllAsync(CancellationToken.None);

        (await _db.Cameras.AnyAsync()).Should().BeFalse();
        (await _db.DismissedPrinterCameras.SingleAsync()).PrinterId.Should().Be(printer.Id);
    }

    [Fact]
    public async Task ClearDismissalsAsync_ThenSync_RecreatesDismissedCamera()
    {
        Printer printer = await AddPrinterAsync();
        Printer otherPrinter = await AddPrinterAsync();
        SetupDetection(StreamUrl, SnapshotUrl);
        AddDismissal(printer.Id);
        AddDismissal(otherPrinter.Id);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        PrinterCameraSynchronizer sut = CreateSut();

        int cleared = await sut.ClearDismissalsAsync(printer.Id, CancellationToken.None);
        PrinterCameraSyncResult result = await sut.SyncPrinterAsync(printer.Id, explicitRequest: true, CancellationToken.None);

        cleared.Should().Be(1);
        result.Created.Should().Be(1);
        Camera camera = await _db.Cameras.Include(c => c.PrinterLinks).SingleAsync();
        camera.StreamUrl.Should().Be(StreamUrl);
        camera.PrinterLinks.Should().ContainSingle(l => l.PrinterId == printer.Id && l.IsPrimary);
        (await _db.DismissedPrinterCameras.SingleAsync()).PrinterId.Should().Be(otherPrinter.Id);
    }

    [Fact]
    public async Task SyncPrinterAsync_DismissalForOtherSource_DoesNotSuppressDiscoveredCamera()
    {
        Printer printer = await AddPrinterAsync();
        SetupDetection(StreamUrl, SnapshotUrl);
        AddDismissal(printer.Id, CameraSource.PrusaLink);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        result.Created.Should().Be(1);
    }

    [Fact]
    public async Task SyncPrinterAsync_CameraReappears_RestoresHealth()
    {
        Printer printer = await AddPrinterAsync();
        Camera existing = await AddCameraAsync(printer.Id, "Voron Camera", CameraSource.Moonraker, StreamUrl, isPrimary: true);
        existing.HealthStatus = CameraHealthStatus.Unhealthy;
        existing.HealthMessage = PrinterCameraSynchronizer.NotReportedHealthMessage;
        existing.SnapshotUrl = SnapshotUrl;
        await _db.SaveChangesAsync();
        SetupDetection(StreamUrl, SnapshotUrl);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        result.Updated.Should().Be(1);
        Camera camera = await _db.Cameras.SingleAsync();
        camera.HealthStatus.Should().Be(CameraHealthStatus.Healthy);
        camera.HealthMessage.Should().BeNull();
    }

    [Fact]
    public async Task SyncPrinterAsync_OfflinePrinterImplicitSync_DoesNotMarkCamerasOffline()
    {
        Printer printer = await AddPrinterAsync();
        await AddCameraAsync(printer.Id, "Voron Camera", CameraSource.Moonraker, StreamUrl, isPrimary: true);
        SetupDetection(null, null);
        SetupOnline(printer.Id, isOnline: false);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        result.Changed.Should().BeFalse();
        (await _db.Cameras.SingleAsync()).HealthStatus.Should().Be(CameraHealthStatus.Healthy);
    }

    [Fact]
    public async Task SyncPrinterAsync_DiscoveryFails_DoesNotMarkCamerasOffline()
    {
        Printer printer = await AddPrinterAsync();
        await AddCameraAsync(printer.Id, "Voron Camera", CameraSource.Moonraker, StreamUrl, isPrimary: true);
        _detection
            .Setup(c => c.DetectConfiguredCameraUrlsAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unreachable"));

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: true, CancellationToken.None);

        result.DiscoverySucceeded.Should().BeFalse();
        result.Changed.Should().BeFalse();
        (await _db.Cameras.SingleAsync()).HealthStatus.Should().Be(CameraHealthStatus.Healthy);
    }

    [Fact]
    public async Task SyncPrinterAsync_MultiplePrimaries_LeavesExactlyOnePrimary()
    {
        Printer printer = await AddPrinterAsync();
        await AddCameraAsync(printer.Id, "Voron Camera", CameraSource.Moonraker, StreamUrl, isPrimary: true, snapshotUrl: SnapshotUrl);
        await AddCameraAsync(printer.Id, "Side Cam", CameraSource.Standalone, "rtsp://side", isPrimary: true, sortOrder: 1);
        SetupDetection(StreamUrl, SnapshotUrl);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: false, CancellationToken.None);

        result.Updated.Should().Be(1);
        List<CameraPrinter> links = await _db.Set<CameraPrinter>().ToListAsync();
        links.Count(l => l.IsPrimary).Should().Be(1);
    }

    [Fact]
    public async Task SyncPrinterAsync_PrusaBuddyRtspCamera_IsLeftUntouched()
    {
        Printer printer = await AddPrinterAsync(PrinterBackend.PrusaLink);
        Camera buddy = await AddCameraAsync(printer.Id, "Buddy Camera", CameraSource.PrusaLink, "rtsp://10.0.0.5:554/live/", isPrimary: true);
        SetupDetection(null, "http://prusa.local/api/v1/cameras/snap");
        SetupOnline(printer.Id, isOnline: true);

        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(printer.Id, explicitRequest: true, CancellationToken.None);

        result.Created.Should().Be(1);
        result.MarkedOffline.Should().Be(0);
        Camera stored = await _db.Cameras.Include(c => c.PrinterLinks).SingleAsync(c => c.Id == buddy.Id);
        stored.StreamUrl.Should().Be("rtsp://10.0.0.5:554/live/");
        stored.Name.Should().Be("Buddy Camera");
        stored.HealthStatus.Should().Be(CameraHealthStatus.Healthy);
        stored.PrinterLinks.Single().IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task SyncPrinterAsync_UnknownPrinter_ReturnsNotFound()
    {
        PrinterCameraSyncResult result = await CreateSut().SyncPrinterAsync(Guid.NewGuid(), explicitRequest: true, CancellationToken.None);

        result.PrinterFound.Should().BeFalse();
        result.Changed.Should().BeFalse();
        _broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAllAsync_BackfillsEveryEnabledPrinter()
    {
        Printer first = await AddPrinterAsync();
        Printer second = await AddPrinterAsync();
        Printer disabled = await AddPrinterAsync(isEnabled: false);
        SetupDetection(StreamUrl, SnapshotUrl);

        IReadOnlyList<PrinterCameraSyncResult> results = await CreateSut().SyncAllAsync(CancellationToken.None);

        results.Select(r => r.PrinterId).Should().BeEquivalentTo([first.Id, second.Id]);
        List<CameraPrinter> links = await _db.Set<CameraPrinter>().ToListAsync();
        links.Select(l => l.PrinterId).Should().BeEquivalentTo([first.Id, second.Id]);
        links.Should().NotContain(l => l.PrinterId == disabled.Id);
    }

    private PrinterCameraSynchronizer CreateSut()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.Printers).Returns(_printers.Object);
        return new PrinterCameraSynchronizer(
            _db,
            unitOfWork.Object,
            _capabilities.Object,
            Mock.Of<IBackendClientFactory>(),
            _statusCache.Object,
            _hub.Object,
            NullLogger<PrinterCameraSynchronizer>.Instance);
    }

    private void SetupDetection(string? streamUrl, string? snapshotUrl) =>
        _detection
            .Setup(c => c.DetectConfiguredCameraUrlsAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((streamUrl, snapshotUrl));

    private void SetupOnline(Guid printerId, bool isOnline) =>
        _statusCache.Setup(c => c.GetStatus(printerId)).Returns(new PrinterStatusDto(printerId, isOnline, isOnline ? "Ready" : "Offline"));

    private async Task<Printer> AddPrinterAsync(PrinterBackend backend = PrinterBackend.Moonraker, bool isEnabled = true)
    {
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Voron",
            ServerUrl = "http://voron.local",
            Backend = (int)backend,
            BackendPort = 7125,
            FrontendPort = 80,
            IsEnabled = isEnabled,
        };
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync();
        _printers
            .Setup(r => r.FindByIdWithIncludesAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        return printer;
    }

    private void AddDismissal(Guid printerId, CameraSource source = CameraSource.Moonraker) =>
        _db.DismissedPrinterCameras.Add(new DismissedPrinterCamera
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Source = source,
            Name = "Voron Camera",
            DismissedAt = DateTime.UtcNow,
        });

    private async Task<Camera> AddCameraAsync(
        Guid printerId,
        string name,
        CameraSource source,
        string streamUrl,
        bool isPrimary,
        bool userModified = false,
        int sortOrder = 0,
        string? snapshotUrl = null)
    {
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = name,
            Source = source,
            StreamUrl = streamUrl,
            SnapshotUrl = snapshotUrl,
            IsEnabled = true,
            UserModified = userModified,
            HealthStatus = CameraHealthStatus.Healthy,
            CreatedAt = DateTime.UtcNow.AddMinutes(sortOrder),
        };
        camera.LinkToPrinter(printerId, isPrimary, sortOrder);
        _db.Cameras.Add(camera);
        await _db.SaveChangesAsync();
        return camera;
    }
}

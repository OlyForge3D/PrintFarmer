using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Cameras;

/// <summary>
/// User-initiated camera link changes raise the same <c>camerasupdated</c> event as background sync, so
/// open printer views refresh without polling (#3329 review R-V-03).
/// </summary>
public sealed class CameraServiceNotificationTests : IAsyncDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"CameraServiceNotificationTests_{Guid.NewGuid():N}")
        .Options);

    private readonly Mock<IHubContext<PrinterHub>> _hub = new();
    private readonly List<(string Group, string Method, object?[] Args)> _broadcasts = [];

    public CameraServiceNotificationTests()
    {
        var clientProxies = new Dictionary<string, IClientProxy>(StringComparer.Ordinal);
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns<string>(group =>
        {
            if (!clientProxies.TryGetValue(group, out IClientProxy? proxy))
            {
                var mock = new Mock<IClientProxy>();
                mock.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                    .Callback<string, object?[], CancellationToken>((method, args, _) => _broadcasts.Add((group, method, args)))
                    .Returns(Task.CompletedTask);
                proxy = mock.Object;
                clientProxies[group] = proxy;
            }

            return proxy;
        });
        _hub.Setup(h => h.Clients).Returns(hubClients.Object);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task UnlinkFromPrinterAsync_BroadcastsPrinterRemainingCameras()
    {
        Printer printer = await AddPrinterAsync();
        Camera removed = await AddCameraAsync("Removed", printer.Id, isPrimary: false, sortOrder: 1);
        Camera kept = await AddCameraAsync("Kept", printer.Id, isPrimary: true, sortOrder: 0);

        CameraPrinterRemovalResult result = await CreateSut().UnlinkFromPrinterAsync(removed.Id, printer.Id, CancellationToken.None);

        result.Should().Be(CameraPrinterRemovalResult.Unlinked);
        (string group, string method, object?[] args) = _broadcasts.Should().ContainSingle().Subject;
        group.Should().Be(AuthorizedHubGroups.Printer(printer.Id));
        method.Should().Be("camerasupdated");
        CamerasUpdatedEvent payload = args.Should().ContainSingle().Which.Should().BeOfType<CamerasUpdatedEvent>().Subject;
        payload.PrinterId.Should().Be(printer.Id);
        payload.CameraIds.Should().Equal(kept.Id);
    }

    [Fact]
    public async Task LinkToPrinterAsync_BroadcastsPrinterCamerasIncludingNewLink()
    {
        Printer printer = await AddPrinterAsync();
        Camera camera = await AddCameraAsync("Standalone", printerId: null, isPrimary: false, sortOrder: 0);

        CameraDto? dto = await CreateSut().LinkToPrinterAsync(camera.Id, printer.Id, isPrimary: null, CancellationToken.None);

        dto.Should().NotBeNull();
        (string group, string method, object?[] args) = _broadcasts.Should().ContainSingle().Subject;
        group.Should().Be(AuthorizedHubGroups.Printer(printer.Id));
        method.Should().Be(PrinterCameraSynchronizer.CamerasUpdatedEventName);
        args.Should().ContainSingle().Which.Should().BeOfType<CamerasUpdatedEvent>()
            .Which.CameraIds.Should().Equal(camera.Id);
    }

    private CameraService CreateSut()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var cameras = new EfCameraRepository(_db);
        unitOfWork.Setup(u => u.Cameras).Returns(cameras);
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _db.SaveChangesAsync(ct));
        return new CameraService(
            unitOfWork.Object,
            _db,
            NullLogger<CameraService>.Instance,
            Mock.Of<IPrintersService>(),
            Mock.Of<IStoragePathService>(),
            _hub.Object);
    }

    private async Task<Printer> AddPrinterAsync()
    {
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Voron",
            ServerUrl = "http://voron.local",
            IsEnabled = true,
        };
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync();
        return printer;
    }

    private async Task<Camera> AddCameraAsync(string name, Guid? printerId, bool isPrimary, int sortOrder)
    {
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = name,
            Source = CameraSource.Standalone,
            StreamUrl = "http://camera.local/stream",
            IsEnabled = true,
        };
        if (printerId is Guid id)
        {
            camera.PrinterLinks.Add(new CameraPrinter { CameraId = camera.Id, PrinterId = id, IsPrimary = isPrimary, SortOrder = sortOrder });
        }

        _db.Cameras.Add(camera);
        await _db.SaveChangesAsync();
        return camera;
    }
}

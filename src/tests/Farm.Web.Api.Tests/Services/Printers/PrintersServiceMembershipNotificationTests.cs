using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// #1731: verifies PrintersService.RemoveAsync notifies IQueueSubscriptionMembershipNotifier
/// exactly once after a printer is deleted (deletion changes which printers a queue-reader
/// client is authorized to subscribe to), and that the notifier is optional (existing call
/// sites that do not supply one keep working unchanged).
/// </summary>
public class PrintersServiceMembershipNotificationTests
{
    [Fact]
    public async Task RemoveAsync_NotifiesMembershipChangedExactlyOnce()
    {
        Printer printer = CreatePrinter();
        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository
            .Setup(r => r.RemoveAsync(printer, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.Printers).Returns(printersRepository.Object);

        var notifier = new Mock<IQueueSubscriptionMembershipNotifier>();
        notifier
            .Setup(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        PrintersService service = CreateService(unitOfWork.Object, notifier.Object);

        await service.RemoveAsync(printer, CancellationToken.None);

        notifier.Verify(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_WithoutNotifier_DoesNotThrow()
    {
        Printer printer = CreatePrinter();
        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository
            .Setup(r => r.RemoveAsync(printer, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.Printers).Returns(printersRepository.Object);

        PrintersService service = CreateService(unitOfWork.Object, membershipNotifier: null);

        Func<Task> act = () => service.RemoveAsync(printer, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PrintersServiceMembershipNotificationTests_{Guid.NewGuid():N}")
            .Options;

        return new AppDbContext(options);
    }

    private static Printer CreatePrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Moonraker",
        ServerUrl = "http://moonraker.local",
        FrontendPort = 7125,
        Backend = (int)PrinterBackend.Moonraker,
    };

    private static PrintersService CreateService(
        IUnitOfWork unitOfWork,
        IQueueSubscriptionMembershipNotifier? membershipNotifier)
    {
        return new PrintersService(
            unitOfWork,
            CreateDbContext(),
            Mock.Of<IBackendClientFactory>(),
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>(),
            coverageBroadcaster: null,
            activityAccumulator: null,
            configuration: null,
            membershipNotifier: membershipNotifier);
    }
}

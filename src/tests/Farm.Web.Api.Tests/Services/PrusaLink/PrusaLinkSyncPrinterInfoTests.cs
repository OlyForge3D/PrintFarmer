using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PrusaLink;

/// <summary>
/// Unit tests for <see cref="PrusaLinkPollingService.SyncPrinterInfoAsync"/>.
/// The method is private and invoked via reflection. A mocked <see cref="IServiceScopeFactory"/>
/// injects fake <see cref="IPrusaLinkClient"/> and <see cref="IUnitOfWork"/> instances.
/// </summary>
public class PrusaLinkSyncPrinterInfoTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactory;
    private readonly Mock<IServiceScope> _scope;
    private readonly Mock<IServiceProvider> _serviceProvider;
    private readonly Mock<IPrusaLinkClient> _prusaLinkClient;
    private readonly Mock<IPrintersRepository> _printersRepository;
    private readonly Mock<IUnitOfWork> _unitOfWork;
    private readonly PrusaLinkPollingService _service;
    private readonly MethodInfo _syncPrinterInfoAsync;

    public PrusaLinkSyncPrinterInfoTests()
    {
        _prusaLinkClient = new Mock<IPrusaLinkClient>(MockBehavior.Loose);
        _printersRepository = new Mock<IPrintersRepository>(MockBehavior.Loose);
        _unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
        _unitOfWork.Setup(u => u.Printers).Returns(_printersRepository.Object);

        _serviceProvider = new Mock<IServiceProvider>(MockBehavior.Loose);
        _serviceProvider
            .Setup(p => p.GetService(typeof(IPrusaLinkClient)))
            .Returns(_prusaLinkClient.Object);
        _serviceProvider
            .Setup(p => p.GetService(typeof(IUnitOfWork)))
            .Returns(_unitOfWork.Object);

        _scope = new Mock<IServiceScope>(MockBehavior.Loose);
        _scope.Setup(s => s.ServiceProvider).Returns(_serviceProvider.Object);

        _scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(_scope.Object);

        _service = new PrusaLinkPollingService(
            hub: new Mock<IHubContext<PrinterHub>>(MockBehavior.Loose).Object,
            scopeFactory: _scopeFactory.Object,
            logger: new Mock<ILogger<PrusaLinkPollingService>>(MockBehavior.Loose).Object,
            statusCacheWriter: new Mock<IPrinterStatusCacheWriter>(MockBehavior.Loose).Object);

        _syncPrinterInfoAsync = typeof(PrusaLinkPollingService)
            .GetMethod("SyncPrinterInfoAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    private Task InvokeAsync(Printer printer) =>
        (Task)_syncPrinterInfoAsync.Invoke(_service, [printer, CancellationToken.None])!;

    private static Printer MakePrinter(double? nozzleDiameter = 0.4, bool? hasMmu = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test Printer",
        ServerUrl = "http://prusa.local",
        NozzleDiameter = nozzleDiameter,
        HasMmu = hasMmu,
    };

    [Fact]
    public async Task SyncPrinterInfoAsync_WhenInfoIsNull_DoesNotSave()
    {
        var printer = MakePrinter();
        _prusaLinkClient
            .Setup(c => c.GetPrinterInformationAsync(printer.ServerUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterInformation?)null);

        await InvokeAsync(printer);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncPrinterInfoAsync_WhenNozzleDiameterChanges_SavesNewValue()
    {
        var printer = MakePrinter(nozzleDiameter: 0.4);
        var trackedPrinter = MakePrinter(nozzleDiameter: 0.4);
        trackedPrinter.Id = printer.Id;

        _prusaLinkClient
            .Setup(c => c.GetPrinterInformationAsync(printer.ServerUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterInformation { NozzleDiameter = 0.6, HasMmu = printer.HasMmu ?? false });
        _printersRepository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(trackedPrinter);

        await InvokeAsync(printer);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        trackedPrinter.NozzleDiameter.Should().Be(0.6);
    }

    [Fact]
    public async Task SyncPrinterInfoAsync_WhenHasMmuChanges_SavesNewValue()
    {
        var printer = MakePrinter(hasMmu: false);
        var trackedPrinter = MakePrinter(hasMmu: false);
        trackedPrinter.Id = printer.Id;

        _prusaLinkClient
            .Setup(c => c.GetPrinterInformationAsync(printer.ServerUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterInformation { NozzleDiameter = printer.NozzleDiameter ?? 0.4, HasMmu = true });
        _printersRepository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(trackedPrinter);

        await InvokeAsync(printer);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        trackedPrinter.HasMmu.Should().BeTrue();
    }

    [Fact]
    public async Task SyncPrinterInfoAsync_WhenNothingChanges_DoesNotSave()
    {
        var printer = MakePrinter(nozzleDiameter: 0.4, hasMmu: false);

        _prusaLinkClient
            .Setup(c => c.GetPrinterInformationAsync(printer.ServerUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterInformation { NozzleDiameter = 0.4, HasMmu = false });

        await InvokeAsync(printer);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncPrinterInfoAsync_WhenTrackedEntityNotFound_DoesNotSave()
    {
        var printer = MakePrinter(nozzleDiameter: 0.4);

        _prusaLinkClient
            .Setup(c => c.GetPrinterInformationAsync(printer.ServerUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterInformation { NozzleDiameter = 0.6, HasMmu = false });
        _printersRepository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);

        await InvokeAsync(printer);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncPrinterInfoAsync_WhenClientThrows_DoesNotPropagateException()
    {
        var printer = MakePrinter();
        _prusaLinkClient
            .Setup(c => c.GetPrinterInformationAsync(printer.ServerUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        Func<Task> act = () => InvokeAsync(printer);

        await act.Should().NotThrowAsync("SyncPrinterInfoAsync catches and logs all non-cancellation exceptions");
    }

    [Fact]
    public async Task SyncPrinterInfoAsync_WhenBothFieldsChange_SavesOnce()
    {
        var printer = MakePrinter(nozzleDiameter: 0.4, hasMmu: false);
        var trackedPrinter = MakePrinter(nozzleDiameter: 0.4, hasMmu: false);
        trackedPrinter.Id = printer.Id;

        _prusaLinkClient
            .Setup(c => c.GetPrinterInformationAsync(printer.ServerUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterInformation { NozzleDiameter = 0.6, HasMmu = true });
        _printersRepository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(trackedPrinter);

        await InvokeAsync(printer);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        trackedPrinter.NozzleDiameter.Should().Be(0.6);
        trackedPrinter.HasMmu.Should().BeTrue();
    }
}

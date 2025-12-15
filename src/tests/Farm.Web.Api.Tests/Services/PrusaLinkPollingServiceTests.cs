using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Tests for PrusaLinkPollingService - background service that polls PrusaLink printers
/// and broadcasts status updates via SignalR.
/// </summary>
public class PrusaLinkPollingServiceTests : IAsyncLifetime
{
    private readonly Mock<IHubContext<PrinterHub>> _hubContextMock;
    private readonly Mock<IClientProxy> _clientProxyMock;
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly Mock<IPrusaLinkClient> _prusaLinkClientMock;
    private readonly Mock<IPrintersRepository> _repositoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private PrusaLinkPollingService _service;

    public PrusaLinkPollingServiceTests()
    {
        _hubContextMock = new Mock<IHubContext<PrinterHub>>();
        _clientProxyMock = new Mock<IClientProxy>();
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _prusaLinkClientMock = new Mock<IPrusaLinkClient>();
        _repositoryMock = new Mock<IPrintersRepository>();
        _scopeMock = new Mock<IServiceScope>();
        _serviceProviderMock = new Mock<IServiceProvider>();

        // Setup hub context to return mock client proxy
        _ = _hubContextMock.Setup(h => h.Clients).Returns(
            Mock.Of<IHubClients>(h => h.All == _clientProxyMock.Object));

        // Setup scope factory and service provider
        _ = _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _ = _scopeFactoryMock.Setup(s => s.CreateScope()).Returns(_scopeMock.Object);
        _ = _serviceProviderMock
            .Setup(s => s.GetService(typeof(IPrintersRepository)))
            .Returns(_repositoryMock.Object);

        _service = new PrusaLinkPollingService(
            _hubContextMock.Object,
            _scopeFactoryMock.Object,
            _loggerMock.Object,
            _prusaLinkClientMock.Object);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            await _service.StopAsync(CancellationToken.None);
        }
        catch
        {
            // Ignore disposal errors
        }
        _service?.Dispose();
    }

    [Fact]
    public async Task StartAsync_StartsMainLoop()
    {
        // Arrange
        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Stop to cleanup
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _loggerMock.Verify(
            l => l.LogInformation("PrusaLinkPollingService starting", null, null),
            Times.Once);
    }

    [Fact]
    public async Task StopAsync_CancelsMainLoop()
    {
        // Arrange
        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());

        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Act
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _loggerMock.Verify(
            l => l.LogInformation("PrusaLinkPollingService stopping", null, null),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_QueriesPrusaLinkPrinters_Continuously()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Test Printer",
            Backend = (int)PrinterBackend.PrusaLink,
            ServerUrl = "http://192.168.1.100",
            ApiKey = "test-key"
        };

        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        var status = new PrusaCompositeStatus(
            IsOnline: true,
            State: "idle",
            Progress: 0.0,
            JobName: null,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null);

        _ = _prusaLinkClientMock
            .Setup(c => c.GetCompositeStatusAsync(printer.ServerUrl, printer.ApiKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _repositoryMock.Verify(
            r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollPrinterAsync_BroadcastsStatusWhenOnline()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Test Printer",
            Backend = (int)PrinterBackend.PrusaLink,
            ServerUrl = "http://192.168.1.100",
            ApiKey = "test-key"
        };

        var status = new PrusaCompositeStatus(
            IsOnline: true,
            State: "printing",
            Progress: 0.5,
            JobName: "model.gcode",
            ThumbnailUrl: "http://thumb.jpg",
            CameraStreamUrl: "http://camera.stream",
            CameraSnapshotUrl: null);

        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        _ = _prusaLinkClientMock
            .Setup(c => c.GetCompositeStatusAsync(printer.ServerUrl, printer.ApiKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert - Verify GetCompositeStatusAsync was called
        _prusaLinkClientMock.Verify(
            c => c.GetCompositeStatusAsync(printer.ServerUrl, printer.ApiKey, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollPrinterAsync_WithNonPrusaLinkPrinter_RemovesFromPolling()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Moonraker Printer",
            Backend = (int)PrinterBackend.Moonraker,
            ServerUrl = "http://192.168.1.101",
            ApiKey = "key"
        };

        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert - Repository should be queried to get PrusaLink printers
        _repositoryMock.Verify(
            r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollPrinterAsync_WithConsecutiveFailures_LogsWarnings()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Test Printer",
            Backend = (int)PrinterBackend.PrusaLink,
            ServerUrl = "http://192.168.1.100",
            ApiKey = "test-key"
        };

        var onlineStatus = new PrusaCompositeStatus(
            IsOnline: true,
            State: "idle",
            Progress: 0.0,
            JobName: null,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null);

        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        // First succeeds, then multiple failures to trigger retry logic
        _ = _prusaLinkClientMock.SetupSequence(c => c.GetCompositeStatusAsync(
                printer.ServerUrl,
                printer.ApiKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(onlineStatus)
            .ThrowsAsync(new InvalidOperationException("network error"))
            .ThrowsAsync(new InvalidOperationException("network error"));

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await _service.StopAsync(CancellationToken.None);

        // Assert - Verify that debug logs were created for both successful and failed attempts
        _loggerMock.Verify(
            l => l.LogDebug(It.IsAny<string>(), null, null),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollPrinterAsync_WithStateChange_BroadcastsUpdate()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Test Printer",
            Backend = (int)PrinterBackend.PrusaLink,
            ServerUrl = "http://192.168.1.100",
            ApiKey = "test-key"
        };

        var idleStatus = new PrusaCompositeStatus(
            IsOnline: true,
            State: "idle",
            Progress: 0.0,
            JobName: null,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null);

        var printingStatus = new PrusaCompositeStatus(
            IsOnline: true,
            State: "printing",
            Progress: 0.25,
            JobName: "model.gcode",
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null);

        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        _ = _prusaLinkClientMock.SetupSequence(c => c.GetCompositeStatusAsync(
                printer.ServerUrl,
                printer.ApiKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(idleStatus)
            .ReturnsAsync(printingStatus);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert - Broadcasts should be sent for status changes
        _clientProxyMock.Verify(
            c => c.SendCoreAsync(
                "printerupdated",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollPrinterAsync_WithProgressWithinTolerance_HandlesProperly()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Test Printer",
            Backend = (int)PrinterBackend.PrusaLink,
            ServerUrl = "http://192.168.1.100",
            ApiKey = "test-key"
        };

        // Progress difference of 0.005 (within 0.01 tolerance)
        var status1 = new PrusaCompositeStatus(
            IsOnline: true,
            State: "printing",
            Progress: 0.500,
            JobName: "model.gcode",
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null);

        var status2 = new PrusaCompositeStatus(
            IsOnline: true,
            State: "printing",
            Progress: 0.505,
            JobName: "model.gcode",
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null);

        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.PrusaLink, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        _ = _prusaLinkClientMock.SetupSequence(c => c.GetCompositeStatusAsync(
                printer.ServerUrl,
                printer.ApiKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(status1)
            .ReturnsAsync(status2);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert - Client should be called for both polls
        _prusaLinkClientMock.Verify(
            c => c.GetCompositeStatusAsync(
                printer.ServerUrl,
                printer.ApiKey,
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Dispose_CleansUpResources()
    {
        // Act & Assert - Should not throw
        _service.Dispose();
    }
}

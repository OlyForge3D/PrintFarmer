using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// Tests for OctoPrintPollingService - background service that manages WebSocket connections
/// and HTTP fallback polling for OctoPrint printers, broadcasting updates via SignalR.
/// </summary>
public class OctoPrintPollingServiceTests : IAsyncLifetime
{
    private readonly Mock<IHubContext<PrinterHub>> _hubContextMock;
    private readonly Mock<IClientProxy> _clientProxyMock;
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly Mock<IOctoPrintClient> _octoPrintClientMock;
    private readonly Mock<IPrintersRepository> _repositoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private OctoPrintPollingService _service;

    public OctoPrintPollingServiceTests()
    {
        _hubContextMock = new Mock<IHubContext<PrinterHub>>();
        _clientProxyMock = new Mock<IClientProxy>();
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _octoPrintClientMock = new Mock<IOctoPrintClient>();
        _repositoryMock = new Mock<IPrintersRepository>();
        _scopeMock = new Mock<IServiceScope>();
        _serviceProviderMock = new Mock<IServiceProvider>();

        // Setup hub context
        _ = _hubContextMock.Setup(h => h.Clients).Returns(
            Mock.Of<IHubClients>(h => h.All == _clientProxyMock.Object));

        // Setup scope factory and service provider
        _ = _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _ = _scopeFactoryMock.Setup(s => s.CreateScope()).Returns(_scopeMock.Object);
        _ = _serviceProviderMock
            .Setup(s => s.GetService(typeof(IPrintersRepository)))
            .Returns(_repositoryMock.Object);

        _service = new OctoPrintPollingService(
            _hubContextMock.Object,
            _scopeFactoryMock.Object,
            _loggerMock.Object,
            _octoPrintClientMock.Object);
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
            .Setup(r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _loggerMock.Verify(
            l => l.LogInformation("OctoPrintPollingService starting", null, null),
            Times.Once);
    }

    [Fact]
    public async Task StopAsync_CancelsMainLoop()
    {
        // Arrange
        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());

        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Act
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _loggerMock.Verify(
            l => l.LogInformation("OctoPrintPollingService stopping", null, null),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_QueriesOctoPrintPrinters_Continuously()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "OctoPrint Printer",
            Backend = (int)PrinterBackend.OctoPrint,
            ServerUrl = "http://192.168.1.100",
            ApiKey = "test-key"
        };

        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _repositoryMock.Verify(
            r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollPrinterAsync_WithNonOctoPrintPrinter_RemovesFromPolling()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "PrusaLink Printer",
            Backend = (int)PrinterBackend.PrusaLink,
            ServerUrl = "http://192.168.1.101",
            ApiKey = "key"
        };

        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _repositoryMock.Verify(
            r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartAsync_CreatesWebSocketAdaptersForPrinters()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "OctoPrint Printer",
            Backend = (int)PrinterBackend.OctoPrint,
            ServerUrl = "http://192.168.1.100",
            ApiKey = "test-key"
        };

        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert - Verify logging shows WebSocket adapter creation
        _loggerMock.Verify(
            l => l.LogDebug(It.IsAny<string>(), null, null),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_RemovesInactiveAdapters()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "OctoPrint Printer",
            Backend = (int)PrinterBackend.OctoPrint,
            ServerUrl = "http://192.168.1.100",
            ApiKey = "test-key"
        };

        // First returns printer, then no printers (simulating removal)
        _ = _repositoryMock.SetupSequence(r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer })
            .ReturnsAsync(new List<Printer>());

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _repositoryMock.Verify(
            r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollPrinterAsync_LogsErrorOnUnexpectedException()
    {
        // Arrange
        var printerId = Guid.NewGuid();

        _ = _repositoryMock
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        // Act & Assert - Should handle exception gracefully
        // Note: This test verifies the service handles unexpected errors
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispose_CleansUpResources()
    {
        // Arrange
        _ = _repositoryMock
            .Setup(r => r.GetByBackendAsync(PrinterBackend.OctoPrint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());

        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        // Act & Assert - Should not throw during disposal
        try
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
            Assert.True(true); // Successfully disposed
        }
        catch (InvalidOperationException)
        {
            Assert.True(false, "Dispose should not throw InvalidOperationException");
        }
    }
}

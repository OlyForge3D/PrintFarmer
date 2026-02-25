using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class ConnectionDiagnosticsControllerTests
{
    private readonly Mock<ILogger<ConnectionDiagnosticsController>> _loggerMock;
    private readonly List<Mock<IPrinterConnectionHealthProvider>> _providerMocks;
    private readonly ConnectionDiagnosticsController _controller;

    public ConnectionDiagnosticsControllerTests()
    {
        _loggerMock = new Mock<ILogger<ConnectionDiagnosticsController>>();
        _providerMocks = new List<Mock<IPrinterConnectionHealthProvider>>();
        _controller = CreateController();
    }

    private ConnectionDiagnosticsController CreateController(params Mock<IPrinterConnectionHealthProvider>[] providers)
    {
        var mocks = providers.Length > 0 ? providers.ToList() : _providerMocks;
        var controller = new ConnectionDiagnosticsController(
            mocks.Select(m => m.Object),
            _loggerMock.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public void GetConnectionHealth_NoProviders_ReturnsEmptyResponse()
    {
        var controller = new ConnectionDiagnosticsController(
            Enumerable.Empty<IPrinterConnectionHealthProvider>(),
            _loggerMock.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        IActionResult result = controller.GetConnectionHealth();

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ConnectionDiagnosticsResponse>(okResult.Value);
        Assert.Equal(0, response.TotalPrinters);
        Assert.Empty(response.Printers);
    }

    [Fact]
    public void GetConnectionHealth_WithPrinters_AggregatesFromMultipleProviders()
    {
        var moonrakerId = Guid.NewGuid();
        var sdcpId = Guid.NewGuid();

        var moonrakerHealth = new PrinterConnectionHealth
        {
            PrinterId = moonrakerId,
            PrinterName = "Voron",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Connected
        };

        var sdcpHealth = new PrinterConnectionHealth
        {
            PrinterId = sdcpId,
            PrinterName = "Saturn",
            Backend = PrinterBackend.SDCP,
            ConnectionState = PrinterConnectionState.Offline
        };

        var moonrakerProvider = new Mock<IPrinterConnectionHealthProvider>();
        moonrakerProvider.Setup(p => p.GetConnectionHealth())
            .Returns(new Dictionary<Guid, PrinterConnectionHealth> { { moonrakerId, moonrakerHealth } });

        var sdcpProvider = new Mock<IPrinterConnectionHealthProvider>();
        sdcpProvider.Setup(p => p.GetConnectionHealth())
            .Returns(new Dictionary<Guid, PrinterConnectionHealth> { { sdcpId, sdcpHealth } });

        var controller = CreateController(moonrakerProvider, sdcpProvider);

        IActionResult result = controller.GetConnectionHealth();

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ConnectionDiagnosticsResponse>(okResult.Value);
        Assert.Equal(2, response.TotalPrinters);
        Assert.Equal(1, response.ConnectedCount);
        Assert.Equal(1, response.OfflineCount);
        Assert.Equal(0, response.ReconnectingCount);
    }

    [Fact]
    public void GetConnectionHealth_ProviderThrows_ContinuesWithOtherProviders()
    {
        var healthId = Guid.NewGuid();
        var health = new PrinterConnectionHealth
        {
            PrinterId = healthId,
            PrinterName = "Ender",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Connected
        };

        var failingProvider = new Mock<IPrinterConnectionHealthProvider>();
        failingProvider.Setup(p => p.GetConnectionHealth())
            .Throws(new InvalidOperationException("Provider crashed"));

        var workingProvider = new Mock<IPrinterConnectionHealthProvider>();
        workingProvider.Setup(p => p.GetConnectionHealth())
            .Returns(new Dictionary<Guid, PrinterConnectionHealth> { { healthId, health } });

        var controller = CreateController(failingProvider, workingProvider);

        IActionResult result = controller.GetConnectionHealth();

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ConnectionDiagnosticsResponse>(okResult.Value);
        Assert.Equal(1, response.TotalPrinters);
    }
}

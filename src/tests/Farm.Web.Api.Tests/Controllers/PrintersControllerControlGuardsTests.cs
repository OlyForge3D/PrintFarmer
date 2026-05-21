using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Unit tests for the server-side capability guards on PrintersController control endpoints
/// (/temps, /move, /moveto). See GitHub issue OlyForge3D/PrintFarmer#290.
/// </summary>
public class PrintersControllerControlGuardsTests
{
    private static PrintersController CreateController(
        Mock<IPrintersService> printersService,
        Mock<IPrinterStatusCacheReader> statusCache,
        out Mock<IPrintFarmerTelemetryService> telemetry)
    {
        telemetry = new Mock<IPrintFarmerTelemetryService>();

        return new PrintersController(
            logger: Mock.Of<ILogger<PrintersController>>(),
            printersService: printersService.Object,
            catalogService: Mock.Of<Farm.Web.Api.Services.Catalog.ICatalogService>(),
            validator: Mock.Of<IValidator<CreatePrinterFromDiscoveryDto>>(),
            discoveryProxyService: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoveryProxyService>(),
            printerBackendCapabilitiesService: Mock.Of<IPrinterBackendCapabilitiesService>(),
            backendClientFactory: Mock.Of<IBackendClientFactory>(),
            httpClientFactory: Mock.Of<IHttpClientFactory>(),
            obicoServerAssignment: Mock.Of<Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService>(),
            settingsService: Mock.Of<Farm.Infrastructure.Settings.ISettingsService>(),
            printerSessionTimelineService: Mock.Of<IPrinterSessionTimelineService>(),
            telemetryService: telemetry.Object,
            bedTypeService: Mock.Of<Farm.Infrastructure.Services.BedTypes.IBedTypeService>(),
            printerStatusCache: statusCache.Object);
    }

    private static Printer SamplePrinter(Guid id) => new()
    {
        Id = id,
        Name = "printer-1",
        ServerUrl = "http://printer-1.local",
    };

    [Fact]
    public async Task SetTempsAsync_ReturnsConflict_WhenPrinterIsPrinting()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Printing"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.SetTempsAsync(
            id, new TempTargets(Hotend: 210, Bed: 60), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(conflict.Value);
        Assert.False(body.Success);
        Assert.Equal("Printer is currently printing.", body.Message);
        printersService.Verify(s => s.SetTempsAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_ReturnsConflict_WhenPrinterIsPrinting()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Printing"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.MoveAsync(
            id, new MoveRequest(X: 10, Y: null, Z: null, F: 3000), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(conflict.Value);
        Assert.False(body.Success);
        Assert.Equal("Printer is currently printing.", body.Message);
        printersService.Verify(s => s.MoveAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_ReturnsNotFound_WhenPrinterMissing()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);

        var statusCache = new Mock<IPrinterStatusCacheReader>();

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.MoveAsync(
            id, new MoveRequest(X: 10, Y: null, Z: null, F: 3000), CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(notFound.Value);
        Assert.False(body.Success);
        Assert.Equal("Printer not found.", body.Message);
        printersService.Verify(s => s.MoveAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetTempsAsync_ReturnsOk_WhenPrinterIdle()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(s => s.SetTempsAsync(id, 210, 60, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterControlOutcome.Ok);

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.SetTempsAsync(
            id, new TempTargets(Hotend: 210, Bed: 60), CancellationToken.None);

        CommandResult body = Assert.IsType<CommandResult>(result.Value);
        Assert.True(body.Success);
        Assert.Null(body.Message);
        printersService.Verify(s => s.SetTempsAsync(id, 210, 60, It.IsAny<CancellationToken>()), Times.Once);
    }
}

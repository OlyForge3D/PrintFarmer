using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Printers;
using Farm.Modules.Printers.Controllers;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.Printers.Tests.Controllers;

public class PrintersControllerDiscoverCamerasTests
{
    [Fact]
    public async Task DiscoverCamerasAsync_RunsExplicitSyncAndReturnsPrinterCameras()
    {
        Guid printerId = Guid.NewGuid();
        var synchronizer = new Mock<IPrinterCameraSynchronizer>();
        synchronizer
            .Setup(s => s.SyncPrinterAsync(printerId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterCameraSyncResult(printerId, true, true, 1, 0, 0, [Guid.NewGuid()]));
        List<CameraDto> cameras = [new CameraDto { Id = Guid.NewGuid(), Name = "Voron Camera" }];
        var cameraService = new Mock<ICameraService>();
        cameraService.Setup(s => s.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>())).ReturnsAsync(cameras);

        ActionResult<List<CameraDto>> result = await CreateController()
            .DiscoverCamerasAsync(printerId, synchronizer.Object, cameraService.Object, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        ok.Value.Should().BeSameAs(cameras);
        synchronizer.Verify(s => s.SyncPrinterAsync(printerId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiscoverCamerasAsync_UnknownPrinter_ReturnsNotFound()
    {
        Guid printerId = Guid.NewGuid();
        var synchronizer = new Mock<IPrinterCameraSynchronizer>();
        synchronizer
            .Setup(s => s.SyncPrinterAsync(printerId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterCameraSyncResult.NotFound(printerId));
        var cameraService = new Mock<ICameraService>(MockBehavior.Strict);

        ActionResult<List<CameraDto>> result = await CreateController()
            .DiscoverCamerasAsync(printerId, synchronizer.Object, cameraService.Object, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private static PrintersController CreateController() =>
        new(
            logger: Mock.Of<ILogger<PrintersController>>(),
            printersService: Mock.Of<IPrintersService>(),
            catalogService: Mock.Of<Farm.Modules.Printers.Services.Catalog.ICatalogService>(),
            validator: Mock.Of<IValidator<Farm.Infrastructure.Discovery.CreatePrinterFromDiscoveryDto>>(),
            discoveryProxyService: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoveryProxyService>(),
            discoverySessions: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoverySessionRegistry>(),
            printerBackendCapabilitiesService: Mock.Of<IPrinterBackendCapabilitiesService>(),
            backendClientFactory: Mock.Of<IBackendClientFactory>(),
            httpClientFactory: Mock.Of<System.Net.Http.IHttpClientFactory>(),
            egressGuard: Mock.Of<Farm.Infrastructure.Network.IEgressGuard>(),
            obicoServerAssignment: Mock.Of<Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService>(),
            settingsService: Mock.Of<Farm.Infrastructure.Settings.ISettingsService>(),
            printerSessionTimelineService: Mock.Of<IPrinterSessionTimelineService>(),
            telemetryService: Mock.Of<Farm.Infrastructure.Telemetry.IPrintFarmerTelemetryService>(),
            bedTypeService: Mock.Of<Farm.Infrastructure.Services.BedTypes.IBedTypeService>());
}

using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Startup;
using Farm.Infrastructure.Settings;
using Farm.Modules.Observability.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class FailureDetectionManualRoutingTests
{
    [Fact]
    public async Task AnalyzePrinterAsync_ActivePrint_UsesReservedEndpointAndCredentials()
    {
        Guid printerId = Guid.NewGuid();
        Mock<IObicoServerAssignmentService> routing = new();
        routing.Setup(service => service.GetRouteAsync(printerId, It.IsAny<ObicoSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObicoPrintRoute("pooled", "Pool", "http://pool.invalid", "test-token"));
        Mock<IObicoFailureDetectionService> detection = new();
        detection.Setup(service => service.AnalyzeImageFromUrlAsync(
                "http://snapshot.invalid", "http://pool.invalid", "test-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailureDetectionResult.Success(0.2m, false));
        FailureDetectionController controller = CreateController(routing.Object, detection.Object, canAccess: true);

        ActionResult<object> result = await controller.AnalyzePrinterAsync(printerId, "http://snapshot.invalid");

        result.Result.Should().BeOfType<OkObjectResult>();
        detection.Verify(service => service.AnalyzeImageFromUrlAsync(
            "http://snapshot.invalid", "http://pool.invalid", "test-token", It.IsAny<CancellationToken>()), Times.Once);
        detection.Verify(service => service.AnalyzeImageFromUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnalyzePrinterAsync_NoAvailableRoute_ReportsUnavailableWithoutInference()
    {
        Mock<IObicoServerAssignmentService> routing = new();
        routing.Setup(service => service.GetRouteAsync(It.IsAny<Guid>(), It.IsAny<ObicoSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObicoPrintRoute("none", null, null, null, "No monitoring capacity."));
        Mock<IObicoFailureDetectionService> detection = new(MockBehavior.Strict);
        FailureDetectionController controller = CreateController(routing.Object, detection.Object, canAccess: true);

        ActionResult<object> result = await controller.AnalyzePrinterAsync(Guid.NewGuid(), "http://snapshot.invalid");

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        unavailable.StatusCode.Should().Be(503);
        System.Text.Json.JsonSerializer.Serialize(unavailable.Value).Should().Contain("No monitoring capacity.");
        detection.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnalyzePrinterAsync_PrinterNotVisible_DoesNotResolveOrUseItsRoute()
    {
        Mock<IObicoServerAssignmentService> routing = new(MockBehavior.Strict);
        Mock<IObicoFailureDetectionService> detection = new(MockBehavior.Strict);
        FailureDetectionController controller = CreateController(routing.Object, detection.Object, canAccess: false);

        ActionResult<object> result = await controller.AnalyzePrinterAsync(Guid.NewGuid(), "http://snapshot.invalid");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        routing.VerifyNoOtherCalls();
        detection.VerifyNoOtherCalls();
    }

    private static FailureDetectionController CreateController(
        IObicoServerAssignmentService routing,
        IObicoFailureDetectionService detection,
        bool canAccess)
    {
        Mock<IStartupStatus> startup = new();
        startup.SetupGet(status => status.IsReady).Returns(true);
        Mock<IQueueResourceAuthorizationService> authorization = new();
        authorization.Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<Guid>(),
                PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        Mock<ISettingsService> settings = new();
        settings.Setup(service => service.Get<ObicoSettings>()).Returns(new ObicoSettings { Enabled = true });
        return new FailureDetectionController(
            detection, Mock.Of<IFailureDetectionIncidentHistoryService>(), new FailureDetectionMonitorStatusStore(),
            startup.Object, NullLogger<FailureDetectionController>.Instance, authorization.Object, routing, settings.Object);
    }
}

using System.Threading;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Issue #711 round-5 FIX 2: <see cref="MaintenanceController.ResolveAlertAsync"/> must not persist a
/// toolhead-scoped maintenance log while the MultiSlotFallback feature gate is disabled. Resolving a
/// per-tool alert is rejected with 400; printer-wide alerts continue to resolve normally.
/// </summary>
public sealed class MaintenanceControllerResolveAlertGateTests
{
    private readonly Mock<IMaintenanceAlertRepository> _alertRepository = new(MockBehavior.Loose);
    private readonly Mock<IMaintenanceLogRepository> _logRepository = new(MockBehavior.Loose);
    private readonly Mock<IPrinterStatisticsRepository> _statisticsRepository = new(MockBehavior.Loose);
    private readonly Mock<IToolheadStatisticsRepository> _toolheadStatisticsRepository = new(MockBehavior.Loose);
    private readonly Mock<IMaintenanceAlertService> _alertService = new(MockBehavior.Loose);
    private readonly Mock<IOperatorFeatureGate> _operatorFeatureGate = new(MockBehavior.Loose);
    private readonly Mock<IHubContext<MaintenanceHub>> _maintenanceHub = new(MockBehavior.Loose);

    private MaintenanceController CreateController()
    {
        // Wire a minimal SignalR hub chain so the success path can broadcast without NREs.
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.SetupGet(c => c.All).Returns(clientProxy.Object);
        _maintenanceHub.SetupGet(h => h.Clients).Returns(clients.Object);

        _logRepository
            .Setup(r => r.AddAsync(It.IsAny<MaintenanceLog>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MaintenanceLog log, CancellationToken _) => log);

        return new MaintenanceController(
            logger: Mock.Of<ILogger<MaintenanceController>>(),
            alertRepository: _alertRepository.Object,
            logRepository: _logRepository.Object,
            deploymentRepository: Mock.Of<IPrinterMaintenanceScheduleRepository>(),
            statisticsRepository: _statisticsRepository.Object,
            toolheadStatisticsRepository: _toolheadStatisticsRepository.Object,
            alertService: _alertService.Object,
            printersService: Mock.Of<IPrintersService>(),
            operatorFeatureGate: _operatorFeatureGate.Object,
            maintenanceHub: _maintenanceHub.Object,
            webhookService: Mock.Of<IWebhookService>());
    }

    private static ResolveAlertRequest Request() =>
        new(PerformedBy: "operator", Notes: null, DurationMinutes: null, Cost: null, PartsReplaced: null);

    [Fact]
    public async Task ResolveAlert_ToolheadScopedAlert_GateDisabled_ReturnsBadRequest()
    {
        Guid alertId = Guid.NewGuid();
        var alert = new MaintenanceAlert
        {
            Id = alertId,
            PrinterId = Guid.NewGuid(),
            ToolheadId = Guid.NewGuid(),
            Title = "Lubricate rails"
        };

        _alertRepository.Setup(r => r.GetByIdAsync(alertId, It.IsAny<CancellationToken>())).ReturnsAsync(alert);
        _operatorFeatureGate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);

        MaintenanceController controller = CreateController();

        ActionResult<ResolveAlertResponse> result = await controller.ResolveAlertAsync(alertId, Request(), CancellationToken.None);

        BadRequestObjectResult bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        bad.Value.Should().Be("Per-tool maintenance is disabled.");

        // The rejection must happen before any log is written.
        _logRepository.Verify(r => r.AddAsync(It.IsAny<MaintenanceLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAlert_PrinterWideAlert_GateDisabled_ResolvesNormally()
    {
        Guid alertId = Guid.NewGuid();
        var alert = new MaintenanceAlert
        {
            Id = alertId,
            PrinterId = Guid.NewGuid(),
            ToolheadId = null,
            Title = "Lubricate rails"
        };

        _alertRepository.Setup(r => r.GetByIdAsync(alertId, It.IsAny<CancellationToken>())).ReturnsAsync(alert);
        _statisticsRepository.Setup(r => r.GetByPrinterIdAsync(alert.PrinterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterStatistics?)null);
        _operatorFeatureGate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);

        MaintenanceController controller = CreateController();

        ActionResult<ResolveAlertResponse> result = await controller.ResolveAlertAsync(alertId, Request(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        _logRepository.Verify(r => r.AddAsync(It.Is<MaintenanceLog>(l => l.ToolheadId == null), It.IsAny<CancellationToken>()), Times.Once);
    }
}

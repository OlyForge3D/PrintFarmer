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
    private readonly Mock<IMaintenanceAlertResolutionService> _alertResolutionService = new(MockBehavior.Loose);
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

        // The resolve path now delegates the atomic gate-recheck + log + alert mutation to the
        // resolution service (issue #711, round-7 Finding 5). Echo the staged log back with a
        // resolved alert so the controller's success path can broadcast without NREs.
        _alertResolutionService
            .Setup(s => s.ResolveWithLogAsync(
                It.IsAny<Guid>(),
                It.IsAny<MaintenanceLog>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid alertId, MaintenanceLog log, string resolvedBy, CancellationToken _) =>
                new MaintenanceAlertResolutionResult(
                    new MaintenanceAlert
                    {
                        Id = alertId,
                        PrinterId = log.PrinterId,
                        ToolheadId = log.ToolheadId,
                        Status = MaintenanceAlertStatus.Resolved,
                        ResolvedAt = DateTime.UtcNow,
                        ResolvedBy = resolvedBy
                    },
                    log));

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
            webhookService: Mock.Of<IWebhookService>(),
            alertResolutionService: _alertResolutionService.Object);
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

        // The rejection must happen before the atomic resolve-with-log op runs, so no log is written.
        _alertResolutionService.Verify(
            s => s.ResolveWithLogAsync(
                It.IsAny<Guid>(),
                It.IsAny<MaintenanceLog>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
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
        _alertResolutionService.Verify(
            s => s.ResolveWithLogAsync(
                alertId,
                It.Is<MaintenanceLog>(l => l.ToolheadId == null),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveAlert_DismissedAlert_ReturnsConflictWithoutCreatingLog()
    {
        Guid alertId = Guid.NewGuid();
        MaintenanceAlert alert = new()
        {
            Id = alertId,
            PrinterId = Guid.NewGuid(),
            Title = "Lubricate rails",
            Status = MaintenanceAlertStatus.Dismissed
        };
        _alertRepository
            .Setup(repository => repository.GetByIdAsync(
                alertId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(alert);
        MaintenanceController controller = CreateController();
        _alertResolutionService
            .Setup(service => service.ResolveWithLogAsync(
                alertId,
                It.IsAny<MaintenanceLog>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MaintenanceAlertNotResolvableException(
                alertId,
                MaintenanceAlertStatus.Dismissed));

        ActionResult<ResolveAlertResponse> result = await controller.ResolveAlertAsync(
            alertId,
            Request(),
            CancellationToken.None);

        ConflictObjectResult conflict =
            Assert.IsType<ConflictObjectResult>(result.Result);
        conflict.Value.Should().Be(
            $"Maintenance alert {alertId} is Dismissed and cannot be resolved as completed maintenance.");
        _logRepository.Verify(
            repository => repository.AddAsync(
                It.IsAny<MaintenanceLog>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAlert_PostCommitHubThrows_ReturnsOkWithCreatedLog()
    {
        Guid alertId = Guid.NewGuid();
        MaintenanceAlert alert = new()
        {
            Id = alertId,
            PrinterId = Guid.NewGuid(),
            Title = "Lubricate rails",
            Status = MaintenanceAlertStatus.Active
        };
        _alertRepository
            .Setup(repository => repository.GetByIdAsync(
                alertId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(alert);
        MaintenanceController controller = CreateController();
        _maintenanceHub
            .SetupGet(context => context.Clients)
            .Throws(new InvalidOperationException("hub unavailable"));

        ActionResult<ResolveAlertResponse> result = await controller.ResolveAlertAsync(
            alertId,
            Request(),
            CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        ResolveAlertResponse response =
            Assert.IsType<ResolveAlertResponse>(ok.Value);
        response.Created.Should().BeTrue();
        response.MaintenanceLog.Should().NotBeNull();
    }

    [Fact]
    public async Task AcknowledgeAlert_ToolheadScopedAlert_GateDisabled_ReturnsBadRequest()
    {
        Guid alertId = Guid.NewGuid();
        MaintenanceAlert alert = new()
        {
            Id = alertId,
            PrinterId = Guid.NewGuid(),
            ToolheadId = Guid.NewGuid()
        };
        _alertRepository.Setup(r => r.GetByIdAsync(alertId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alert);
        _alertService
            .Setup(s => s.AcknowledgeAlertAsync(alertId, "operator", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PerToolMaintenanceDisabledException());

        ActionResult<MaintenanceAlert> result = await CreateController().AcknowledgeAlertAsync(
            alertId,
            new AcknowledgeAlertRequest("operator"),
            CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        badRequest.Value.Should().Be("Per-tool maintenance is disabled.");
    }

    [Fact]
    public async Task DismissAlert_ToolheadScopedAlert_GateDisabled_ReturnsBadRequest()
    {
        Guid alertId = Guid.NewGuid();
        MaintenanceAlert alert = new()
        {
            Id = alertId,
            PrinterId = Guid.NewGuid(),
            ToolheadId = Guid.NewGuid()
        };
        _alertRepository.Setup(r => r.GetByIdAsync(alertId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alert);
        _alertService
            .Setup(s => s.DismissAlertAsync(
                alertId,
                "operator",
                "later",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PerToolMaintenanceDisabledException());

        ActionResult<MaintenanceAlert> result = await CreateController().DismissAlertAsync(
            alertId,
            new DismissAlertRequest("operator", "later"),
            CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        badRequest.Value.Should().Be("Per-tool maintenance is disabled.");
    }

    [Fact]
    public async Task AcknowledgeAndDismissAlert_PrinterWideAlert_GateDisabled_ReturnOk()
    {
        Guid alertId = Guid.NewGuid();
        MaintenanceAlert alert = new()
        {
            Id = alertId,
            PrinterId = Guid.NewGuid(),
            ToolheadId = null
        };
        _alertRepository.Setup(r => r.GetByIdAsync(alertId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alert);

        MaintenanceController controller = CreateController();
        ActionResult<MaintenanceAlert> acknowledge = await controller.AcknowledgeAlertAsync(
            alertId,
            new AcknowledgeAlertRequest("operator"),
            CancellationToken.None);
        ActionResult<MaintenanceAlert> dismiss = await controller.DismissAlertAsync(
            alertId,
            new DismissAlertRequest("operator", null),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(acknowledge.Result);
        Assert.IsType<OkObjectResult>(dismiss.Result);
    }
}

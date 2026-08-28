using System.Threading;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Modules.Maintenance.Controllers;
using Farm.Modules.Maintenance.Controllers.Responses;
using Farm.Modules.Maintenance.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.Maintenance.Tests.Controllers;

/// <summary>
/// Issue #711 round-5 FIX 3: fleet statistics must project toolhead-scoped schedules against each
/// head's own cumulative hours rather than collapsing them onto the printer-wide counter. Two heads
/// at 50h each under an 80h interval must not be reported overdue merely because the printer-wide
/// total reached 100h.
/// </summary>
public sealed class MaintenanceControllerFleetToolheadScopeTests
{
    private readonly Mock<IMaintenanceLogRepository> _logRepository = new(MockBehavior.Loose);
    private readonly Mock<IPrinterMaintenanceScheduleRepository> _deploymentRepository = new(MockBehavior.Loose);
    private readonly Mock<IPrinterStatisticsRepository> _statisticsRepository = new(MockBehavior.Loose);
    private readonly Mock<IToolheadStatisticsRepository> _toolheadStatisticsRepository = new(MockBehavior.Loose);
    private readonly Mock<IPrintersService> _printersService = new(MockBehavior.Loose);
    private readonly Mock<IMaintenanceAlertRepository> _alertRepository = new(MockBehavior.Loose);
    private readonly Mock<IOperatorFeatureGate> _operatorFeatureGate = new(MockBehavior.Loose);
    private bool _multiSlotEnabled = true;

    public MaintenanceControllerFleetToolheadScopeTests()
    {
        _operatorFeatureGate
            .Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback))
            .Returns(() => _multiSlotEnabled);
        _operatorFeatureGate
            .Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(_multiSlotEnabled));
    }

    private MaintenanceController CreateController()
    {
        return new MaintenanceController(
            logger: Mock.Of<ILogger<MaintenanceController>>(),
            alertRepository: _alertRepository.Object,
            logRepository: _logRepository.Object,
            deploymentRepository: _deploymentRepository.Object,
            statisticsRepository: _statisticsRepository.Object,
            toolheadStatisticsRepository: _toolheadStatisticsRepository.Object,
            alertService: Mock.Of<IMaintenanceAlertService>(),
            printersService: _printersService.Object,
            operatorFeatureGate: _operatorFeatureGate.Object,
            maintenanceHub: Mock.Of<IHubContext<MaintenanceHub>>(),
            webhookService: Mock.Of<IWebhookService>(),
            alertResolutionService: Mock.Of<IMaintenanceAlertResolutionService>());
    }

    private static PrinterMaintenanceSchedule BuildSchedule(
        Guid printerId,
        Guid taskId,
        Guid? toolheadId,
        double intervalHours,
        string taskName = "Lubricate rails")
    {
        MaintenanceTask task = new()
        {
            Id = taskId,
            TaskName = taskName,
            IsActive = true,
            IntervalHours = intervalHours,
            Priority = 2
        };

        MaintenancePlan plan = new()
        {
            Id = Guid.NewGuid(),
            Name = "Plan",
            IsActive = true,
            PlanTasks = new List<PlanTask>
            {
                new() { Id = Guid.NewGuid(), MaintenanceTaskId = taskId, MaintenanceTask = task }
            }
        };

        return new PrinterMaintenanceSchedule
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            MaintenancePlanId = plan.Id,
            MaintenancePlan = plan,
            ToolheadId = toolheadId,
            IsActive = true,
            DeployedAt = DateTime.UtcNow.AddDays(-30)
        };
    }

    private void SetupFleet(
        Guid printerId,
        double printerWideHours,
        IReadOnlyDictionary<Guid, double> toolheadHours,
        List<PrinterMaintenanceSchedule> schedules)
    {
        var printer = new Printer { Id = printerId, Name = "Printer", ServerUrl = "http://printer.local" };
        var stats = new PrinterStatistics { PrinterId = printerId, TotalPrintHours = printerWideHours };

        _statisticsRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrinterStatistics> { stats });
        _printersService.Setup(s => s.GetAllWithIncludesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer> { printer });
        _logRepository.Setup(r => r.GetAllAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MaintenanceLog>());
        _deploymentRepository.Setup(d => d.GetActiveWithTasksAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(schedules);
        _toolheadStatisticsRepository.Setup(t => t.GetCumulativeHoursByPrintersAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(toolheadHours);
    }

    [Fact]
    public async Task GetFleetStatistics_TwoHeadsUnderInterval_NotReportedOverdue()
    {
        Guid printerId = Guid.NewGuid();
        Guid taskId = Guid.NewGuid();
        Guid headA = Guid.NewGuid();
        Guid headB = Guid.NewGuid();

        // Printer-wide total is 100h (would exceed the 80h interval), but each head has only 50h.
        SetupFleet(
            printerId,
            printerWideHours: 100,
            toolheadHours: new Dictionary<Guid, double> { [headA] = 50, [headB] = 50 },
            schedules: new List<PrinterMaintenanceSchedule>
            {
                BuildSchedule(printerId, taskId, headA, intervalHours: 80),
                BuildSchedule(printerId, taskId, headB, intervalHours: 80)
            });

        MaintenanceController controller = CreateController();

        ActionResult<List<FleetPrinterStatisticsDto>> result =
            await controller.GetFleetStatisticsAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        var dtos = Assert.IsType<List<FleetPrinterStatisticsDto>>(ok.Value);
        FleetPrinterStatisticsDto dto = dtos.Should().ContainSingle().Which;

        // 80h interval - 50h per head = 30h remaining → 30/8 ≈ 3.75d → 3 days, NOT overdue.
        dto.DaysUntilNextMaintenance.Should().Be(3);
        dto.DaysUntilNextMaintenance.Should().BeGreaterThanOrEqualTo(0, "per-head hours must not collapse onto the printer-wide total");
        dto.NextMaintenanceTask.Should().Be("Lubricate rails");
    }

    [Fact]
    public async Task GetFleetStatistics_HeadsAtDifferentHours_ReportsMostUrgentHead()
    {
        Guid printerId = Guid.NewGuid();
        Guid taskId = Guid.NewGuid();
        Guid headA = Guid.NewGuid();
        Guid headB = Guid.NewGuid();

        // Head B is closer to its interval (70h of 80h) than head A (50h of 80h); the aggregate must
        // surface the most-urgent head.
        SetupFleet(
            printerId,
            printerWideHours: 120,
            toolheadHours: new Dictionary<Guid, double> { [headA] = 50, [headB] = 70 },
            schedules: new List<PrinterMaintenanceSchedule>
            {
                BuildSchedule(printerId, taskId, headA, intervalHours: 80),
                BuildSchedule(printerId, taskId, headB, intervalHours: 80)
            });

        MaintenanceController controller = CreateController();

        ActionResult<List<FleetPrinterStatisticsDto>> result =
            await controller.GetFleetStatisticsAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        var dtos = Assert.IsType<List<FleetPrinterStatisticsDto>>(ok.Value);
        FleetPrinterStatisticsDto dto = dtos.Should().ContainSingle().Which;

        // Head B: 80h - 70h = 10h remaining → 10/8 = 1.25d → 1 day (most urgent).
        dto.DaysUntilNextMaintenance.Should().Be(1);
    }

    [Fact]
    public async Task GetUpcomingMaintenance_TogglingFeature_HidesAndRestoresToolheadSchedule()
    {
        Guid printerId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();
        var printer = new Printer { Id = printerId, Name = "Printer", ServerUrl = "http://printer.local" };
        List<PrinterMaintenanceSchedule> schedules =
        [
            BuildSchedule(
                printerId,
                Guid.NewGuid(),
                toolheadId,
                intervalHours: 80,
                taskName: "Toolhead task"),
            BuildSchedule(
                printerId,
                Guid.NewGuid(),
                toolheadId: null,
                intervalHours: 80,
                taskName: "Printer task"),
        ];
        _printersService.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        _statisticsRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PrinterStatistics { PrinterId = printerId }]);
        _logRepository.Setup(r => r.GetByPrinterIdsAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _deploymentRepository.Setup(r => r.GetActiveWithTasksAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(schedules);
        _toolheadStatisticsRepository.Setup(r => r.GetCumulativeHoursByPrintersAsync(
                It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, double> { [toolheadId] = 0 });
        MaintenanceController controller = CreateController();

        List<UpcomingMaintenanceTaskDto> enabled = GetUpcomingBody(
            await controller.GetUpcomingMaintenanceAsync(ct: CancellationToken.None));
        enabled.Select(t => t.TaskName).Should().BeEquivalentTo("Toolhead task", "Printer task");

        _multiSlotEnabled = false;

        List<UpcomingMaintenanceTaskDto> hidden = GetUpcomingBody(
            await controller.GetUpcomingMaintenanceAsync(ct: CancellationToken.None));
        hidden.Should().ContainSingle().Which.TaskName.Should().Be("Printer task");

        _multiSlotEnabled = true;

        GetUpcomingBody(await controller.GetUpcomingMaintenanceAsync(ct: CancellationToken.None))
            .Select(t => t.TaskName)
            .Should().BeEquivalentTo("Toolhead task", "Printer task");
    }

    [Fact]
    public async Task GetFleetStatistics_FeatureDisabled_ProjectsPrinterWideScheduleOnly()
    {
        Guid printerId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();
        SetupFleet(
            printerId,
            printerWideHours: 0,
            toolheadHours: new Dictionary<Guid, double> { [toolheadId] = 100 },
            schedules:
            [
                BuildSchedule(
                    printerId,
                    Guid.NewGuid(),
                    toolheadId,
                    intervalHours: 20,
                    taskName: "Hidden toolhead task"),
                BuildSchedule(
                    printerId,
                    Guid.NewGuid(),
                    toolheadId: null,
                    intervalHours: 80,
                    taskName: "Visible printer task"),
            ]);
        _multiSlotEnabled = false;

        ActionResult<List<FleetPrinterStatisticsDto>> result =
            await CreateController().GetFleetStatisticsAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        var dtos = Assert.IsType<List<FleetPrinterStatisticsDto>>(ok.Value);
        dtos.Should().ContainSingle().Which.NextMaintenanceTask.Should().Be("Visible printer task");
    }

    [Fact]
    public async Task GetAlertsAndLogs_FeatureDisabled_HidesToolheadScopedRows()
    {
        Guid printerId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();
        MaintenanceAlert printerAlert = new() { Id = Guid.NewGuid(), PrinterId = printerId };
        MaintenanceAlert toolheadAlert = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            ToolheadId = toolheadId,
        };
        MaintenanceLog printerLog = new() { Id = Guid.NewGuid(), PrinterId = printerId };
        MaintenanceLog toolheadLog = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            ToolheadId = toolheadId,
        };
        _alertRepository.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([printerAlert, toolheadAlert]);
        _alertRepository.Setup(r => r.GetByIdAsync(toolheadAlert.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(toolheadAlert);
        _logRepository.Setup(r => r.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([printerLog, toolheadLog]);
        _multiSlotEnabled = false;
        MaintenanceController controller = CreateController();

        ActionResult<IEnumerable<MaintenanceAlert>> alertsResult =
            await controller.GetAllAlertsAsync(CancellationToken.None);
        OkObjectResult alertsOk = Assert.IsType<OkObjectResult>(alertsResult.Result);
        Assert.IsType<List<MaintenanceAlert>>(alertsOk.Value)
            .Should().ContainSingle().Which.Id.Should().Be(printerAlert.Id);

        ActionResult<IEnumerable<MaintenanceLog>> logsResult =
            await controller.GetPrinterMaintenanceLogsAsync(printerId, CancellationToken.None);
        OkObjectResult logsOk = Assert.IsType<OkObjectResult>(logsResult.Result);
        Assert.IsType<List<MaintenanceLog>>(logsOk.Value)
            .Should().ContainSingle().Which.Id.Should().Be(printerLog.Id);

        (await controller.GetAlertByIdAsync(toolheadAlert.Id, CancellationToken.None)).Result
            .Should().BeOfType<NotFoundObjectResult>();
    }

    private static List<UpcomingMaintenanceTaskDto> GetUpcomingBody(
        ActionResult<IEnumerable<UpcomingMaintenanceTaskDto>> result)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<List<UpcomingMaintenanceTaskDto>>(ok.Value);
    }
}

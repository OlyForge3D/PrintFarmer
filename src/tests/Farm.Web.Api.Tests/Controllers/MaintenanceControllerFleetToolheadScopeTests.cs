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

    private MaintenanceController CreateController()
    {
        return new MaintenanceController(
            logger: Mock.Of<ILogger<MaintenanceController>>(),
            alertRepository: Mock.Of<IMaintenanceAlertRepository>(),
            logRepository: _logRepository.Object,
            deploymentRepository: _deploymentRepository.Object,
            statisticsRepository: _statisticsRepository.Object,
            toolheadStatisticsRepository: _toolheadStatisticsRepository.Object,
            alertService: Mock.Of<IMaintenanceAlertService>(),
            printersService: _printersService.Object,
            operatorFeatureGate: Mock.Of<IOperatorFeatureGate>(),
            maintenanceHub: Mock.Of<IHubContext<MaintenanceHub>>(),
            webhookService: Mock.Of<IWebhookService>());
    }

    private static PrinterMaintenanceSchedule BuildSchedule(Guid printerId, Guid taskId, Guid? toolheadId, double intervalHours)
    {
        MaintenanceTask task = new()
        {
            Id = taskId,
            TaskName = "Lubricate rails",
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
}

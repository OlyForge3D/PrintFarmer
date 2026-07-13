using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Maintenance;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

/// <summary>
/// Unit tests for the per-toolhead maintenance scope propagation of
/// <see cref="MaintenanceAlertEngine"/> (issue #711, F6). A per-toolhead-scoped schedule
/// must stamp its <c>ToolheadId</c> onto every alert it generates so per-tool history stays
/// independent, and two schedules that differ only by toolhead must accrue independently.
/// </summary>
public sealed class MaintenanceAlertEngineToolheadScopeTests
{
    private readonly Mock<IPrinterStatisticsRepository> _stats = new(MockBehavior.Loose);
    private readonly Mock<IPrinterMaintenanceScheduleRepository> _deployment = new(MockBehavior.Loose);
    private readonly Mock<IMaintenanceAlertRepository> _alerts = new(MockBehavior.Loose);
    private readonly Mock<IMaintenanceLogRepository> _logs = new(MockBehavior.Loose);
    private readonly Mock<IHubContext<MaintenanceHub>> _hub = new(MockBehavior.Loose);
    private readonly Mock<IOptionsMonitor<MaintenanceAlertSettings>> _settings = new(MockBehavior.Loose);
    private readonly Mock<IAttentionBroadcaster> _broadcaster = new(MockBehavior.Loose);

    private MaintenanceAlertEngine CreateEngine()
    {
        _settings.SetupGet(s => s.CurrentValue)
                 .Returns(new MaintenanceAlertSettings { EnableSignalRNotifications = false });
        _broadcaster.Setup(b => b.NotifyChangedAsync(It.IsAny<AttentionChangedPayload>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
        return new MaintenanceAlertEngine(
            _stats.Object,
            _deployment.Object,
            _alerts.Object,
            _logs.Object,
            _hub.Object,
            _settings.Object,
            NullLogger<MaintenanceAlertEngine>.Instance,
            _broadcaster.Object);
    }

    private static PrinterMaintenanceSchedule BuildSchedule(Guid printerId, Guid taskId, Guid? toolheadId)
    {
        MaintenanceTask task = new()
        {
            Id = taskId,
            TaskName = "Lubricate rails",
            IsActive = true,
            IntervalHours = 10,
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
            DeployedAt = DateTime.UtcNow.AddDays(-1)
        };
    }

    [Fact]
    public async Task EvaluatePrinter_PerToolheadSchedule_StampsToolheadIdOnGeneratedAlert()
    {
        Guid printerId = Guid.NewGuid();
        Guid taskId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();

        _stats.Setup(s => s.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PrinterStatistics { PrinterId = printerId, TotalPrintHours = 100 });
        _deployment.Setup(d => d.GetActiveWithTasksAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<PrinterMaintenanceSchedule> { BuildSchedule(printerId, taskId, toolheadId) });
        _logs.Setup(l => l.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<MaintenanceLog>());
        _alerts.Setup(a => a.HasActiveAlertAsync(printerId, taskId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        List<MaintenanceAlert> captured = new();
        _alerts.Setup(a => a.AddAsync(It.IsAny<MaintenanceAlert>(), It.IsAny<CancellationToken>()))
               .Callback<MaintenanceAlert, CancellationToken>((alert, _) => captured.Add(alert))
               .Returns(Task.CompletedTask);

        MaintenanceAlertEngine engine = CreateEngine();

        int generated = await engine.EvaluatePrinterMaintenanceAsync(printerId, CancellationToken.None);

        generated.Should().Be(1);
        captured.Should().ContainSingle();
        captured[0].ToolheadId.Should().Be(toolheadId);
    }

    [Fact]
    public async Task EvaluatePrinter_PrinterWideSchedule_LeavesAlertToolheadNull()
    {
        Guid printerId = Guid.NewGuid();
        Guid taskId = Guid.NewGuid();

        _stats.Setup(s => s.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PrinterStatistics { PrinterId = printerId, TotalPrintHours = 100 });
        _deployment.Setup(d => d.GetActiveWithTasksAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<PrinterMaintenanceSchedule> { BuildSchedule(printerId, taskId, toolheadId: null) });
        _logs.Setup(l => l.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<MaintenanceLog>());
        _alerts.Setup(a => a.HasActiveAlertAsync(printerId, taskId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        List<MaintenanceAlert> captured = new();
        _alerts.Setup(a => a.AddAsync(It.IsAny<MaintenanceAlert>(), It.IsAny<CancellationToken>()))
               .Callback<MaintenanceAlert, CancellationToken>((alert, _) => captured.Add(alert))
               .Returns(Task.CompletedTask);

        MaintenanceAlertEngine engine = CreateEngine();

        await engine.EvaluatePrinterMaintenanceAsync(printerId, CancellationToken.None);

        captured.Should().ContainSingle();
        captured[0].ToolheadId.Should().BeNull();
    }

    [Fact]
    public async Task EvaluatePrinter_TwoSchedulesDifferingOnlyByToolhead_GenerateIndependentAlerts()
    {
        Guid printerId = Guid.NewGuid();
        Guid taskId = Guid.NewGuid();
        Guid toolheadA = Guid.NewGuid();
        Guid toolheadB = Guid.NewGuid();

        PrinterMaintenanceSchedule scheduleA = BuildSchedule(printerId, taskId, toolheadA);
        PrinterMaintenanceSchedule scheduleB = BuildSchedule(printerId, taskId, toolheadB);

        _stats.Setup(s => s.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PrinterStatistics { PrinterId = printerId, TotalPrintHours = 100 });
        _deployment.Setup(d => d.GetActiveWithTasksAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<PrinterMaintenanceSchedule> { scheduleA, scheduleB });
        _logs.Setup(l => l.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<MaintenanceLog>());
        // Dedup is keyed by (printer, task, schedule) — distinct schedule ids mean both fire.
        _alerts.Setup(a => a.HasActiveAlertAsync(printerId, taskId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        List<MaintenanceAlert> captured = new();
        _alerts.Setup(a => a.AddAsync(It.IsAny<MaintenanceAlert>(), It.IsAny<CancellationToken>()))
               .Callback<MaintenanceAlert, CancellationToken>((alert, _) => captured.Add(alert))
               .Returns(Task.CompletedTask);

        MaintenanceAlertEngine engine = CreateEngine();

        int generated = await engine.EvaluatePrinterMaintenanceAsync(printerId, CancellationToken.None);

        generated.Should().Be(2);
        captured.Should().HaveCount(2);
        captured.Should().Contain(a => a.ToolheadId == toolheadA);
        captured.Should().Contain(a => a.ToolheadId == toolheadB);
    }
}

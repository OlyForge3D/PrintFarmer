using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
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

    private MaintenanceAlertEngine CreateEngine(IOperatorFeatureGate? operatorFeatureGate = null)
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
            _broadcaster.Object,
            operatorFeatureGate: operatorFeatureGate);
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

    [Fact]
    public async Task EvaluatePrinter_PerToolheadHours_AccrueIndependentlyOfPrinterWide()
    {
        // Issue #711, FIX B: hour accrual for per-tool schedules uses per-TOOLHEAD cumulative
        // hours, not the printer-wide counter. Printer-wide hours are 0 here (would NOT trip
        // the 10h interval), yet toolhead A has printed 100h and toolhead B none — so ONLY the
        // schedule scoped to toolhead A should alert.
        Guid printerId = Guid.NewGuid();
        Guid taskId = Guid.NewGuid();
        Guid toolheadA = Guid.NewGuid();
        Guid toolheadB = Guid.NewGuid();

        _stats.Setup(s => s.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PrinterStatistics { PrinterId = printerId, TotalPrintHours = 0 });
        _deployment.Setup(d => d.GetActiveWithTasksAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<PrinterMaintenanceSchedule>
                   {
                       BuildSchedule(printerId, taskId, toolheadA),
                       BuildSchedule(printerId, taskId, toolheadB)
                   });
        _logs.Setup(l => l.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<MaintenanceLog>());
        _alerts.Setup(a => a.HasActiveAlertAsync(printerId, taskId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        List<MaintenanceAlert> captured = new();
        _alerts.Setup(a => a.AddAsync(It.IsAny<MaintenanceAlert>(), It.IsAny<CancellationToken>()))
               .Callback<MaintenanceAlert, CancellationToken>((alert, _) => captured.Add(alert))
               .Returns(Task.CompletedTask);

        Mock<IToolheadStatisticsRepository> toolheadStats = new(MockBehavior.Loose);
        toolheadStats.Setup(t => t.GetCumulativeHoursByPrinterAsync(printerId, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new Dictionary<Guid, double> { [toolheadA] = 100, [toolheadB] = 0 });

        _settings.SetupGet(s => s.CurrentValue)
                 .Returns(new MaintenanceAlertSettings { EnableSignalRNotifications = false });
        _broadcaster.Setup(b => b.NotifyChangedAsync(It.IsAny<AttentionChangedPayload>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

        MaintenanceAlertEngine engine = new(
            _stats.Object,
            _deployment.Object,
            _alerts.Object,
            _logs.Object,
            _hub.Object,
            _settings.Object,
            NullLogger<MaintenanceAlertEngine>.Instance,
            _broadcaster.Object,
            toolheadStats.Object);

        int generated = await engine.EvaluatePrinterMaintenanceAsync(printerId, CancellationToken.None);

        generated.Should().Be(1);
        captured.Should().ContainSingle();
        captured[0].ToolheadId.Should().Be(toolheadA);
    }

    [Fact]
    public async Task EvaluatePrinter_GateDisabled_SkipsPerToolButKeepsPrinterWideAlert()
    {
        // Issue #711, round-5 FIX 2: when the MultiSlotFallback feature gate is OFF, toolhead-scoped
        // deployments must not generate per-tool alerts, while printer-wide deployments continue to
        // fire normally. Both schedules below would trip the 10h interval at 100 printer-wide hours.
        Guid printerId = Guid.NewGuid();
        Guid perToolTaskId = Guid.NewGuid();
        Guid printerWideTaskId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();

        _stats.Setup(s => s.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PrinterStatistics { PrinterId = printerId, TotalPrintHours = 100 });
        _deployment.Setup(d => d.GetActiveWithTasksAsync(printerId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<PrinterMaintenanceSchedule>
                   {
                       BuildSchedule(printerId, perToolTaskId, toolheadId),
                       BuildSchedule(printerId, printerWideTaskId, toolheadId: null)
                   });
        _logs.Setup(l => l.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<MaintenanceLog>());
        _alerts.Setup(a => a.HasActiveAlertAsync(printerId, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        List<MaintenanceAlert> captured = new();
        _alerts.Setup(a => a.AddAsync(It.IsAny<MaintenanceAlert>(), It.IsAny<CancellationToken>()))
               .Callback<MaintenanceAlert, CancellationToken>((alert, _) => captured.Add(alert))
               .Returns(Task.CompletedTask);

        _settings.SetupGet(s => s.CurrentValue)
                 .Returns(new MaintenanceAlertSettings { EnableSignalRNotifications = false });
        _broadcaster.Setup(b => b.NotifyChangedAsync(It.IsAny<AttentionChangedPayload>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

        Mock<IOperatorFeatureGate> gate = new(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);

        MaintenanceAlertEngine engine = new(
            _stats.Object,
            _deployment.Object,
            _alerts.Object,
            _logs.Object,
            _hub.Object,
            _settings.Object,
            NullLogger<MaintenanceAlertEngine>.Instance,
            _broadcaster.Object,
            toolheadStatsRepo: null,
            operatorFeatureGate: gate.Object);

        int generated = await engine.EvaluatePrinterMaintenanceAsync(printerId, CancellationToken.None);

        generated.Should().Be(1, "only the printer-wide schedule may alert while per-tool maintenance is disabled");
        captured.Should().ContainSingle();
        captured[0].ToolheadId.Should().BeNull();
    }

    [Theory]
    [InlineData(AlertMutation.Acknowledge)]
    [InlineData(AlertMutation.Resolve)]
    [InlineData(AlertMutation.Dismiss)]
    public async Task MutateAlert_ToolheadScopedAndGateDisabled_ThrowsBeforePersistence(
        AlertMutation mutation)
    {
        Guid alertId = Guid.NewGuid();
        MaintenanceAlert alert = new()
        {
            Id = alertId,
            PrinterId = Guid.NewGuid(),
            ToolheadId = Guid.NewGuid()
        };
        _alerts.Setup(a => a.GetByIdAsync(alertId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alert);

        Mock<IOperatorFeatureGate> gate = new(MockBehavior.Strict);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);
        MaintenanceAlertEngine engine = CreateEngine(gate.Object);

        Func<Task> act = mutation switch
        {
            AlertMutation.Acknowledge => () => engine.AcknowledgeAlertAsync(alertId, "operator"),
            AlertMutation.Resolve => () => engine.ResolveAlertAsync(alertId, "operator"),
            AlertMutation.Dismiss => () => engine.DismissAlertAsync(alertId, "operator"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        await act.Should().ThrowAsync<PerToolMaintenanceDisabledException>()
            .WithMessage("Per-tool maintenance is disabled.");
        _alerts.Verify(
            a => a.UpdateAsync(It.IsAny<MaintenanceAlert>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _alerts.Verify(a => a.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AlertMutation.Acknowledge)]
    [InlineData(AlertMutation.Resolve)]
    [InlineData(AlertMutation.Dismiss)]
    public async Task MutateAlert_PrinterWideAndGateDisabled_PersistsNormally(
        AlertMutation mutation)
    {
        Guid alertId = Guid.NewGuid();
        MaintenanceAlert alert = new()
        {
            Id = alertId,
            PrinterId = Guid.NewGuid(),
            ToolheadId = null
        };
        _alerts.Setup(a => a.GetByIdAsync(alertId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alert);
        _alerts.Setup(a => a.UpdateAsync(alert, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _alerts.Setup(a => a.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IOperatorFeatureGate> gate = new(MockBehavior.Strict);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);
        MaintenanceAlertEngine engine = CreateEngine(gate.Object);

        Task act = mutation switch
        {
            AlertMutation.Acknowledge => engine.AcknowledgeAlertAsync(alertId, "operator"),
            AlertMutation.Resolve => engine.ResolveAlertAsync(alertId, "operator"),
            AlertMutation.Dismiss => engine.DismissAlertAsync(alertId, "operator"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        await act;

        _alerts.Verify(a => a.UpdateAsync(alert, It.IsAny<CancellationToken>()), Times.Once);
        _alerts.Verify(a => a.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    public enum AlertMutation
    {
        Acknowledge,
        Resolve,
        Dismiss
    }
}

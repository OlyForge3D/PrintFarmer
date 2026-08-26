using System;
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
/// Unit tests for the attention-feed invalidation topology of
/// <see cref="MaintenanceAlertEngine"/> (issue #707, review R3). The shared
/// <c>attentionchanged</c> event fires after the committed mutation and is INDEPENDENT of the
/// legacy <see cref="MaintenanceAlertSettings.EnableSignalRNotifications"/> toggle, so
/// operators who disabled the maintenance hub still receive attention invalidations.
/// </summary>
public sealed class MaintenanceAlertEngineAttentionTests
{
    private readonly Mock<IPrinterStatisticsRepository> _stats = new(MockBehavior.Loose);
    private readonly Mock<IPrinterMaintenanceScheduleRepository> _deployment = new(MockBehavior.Loose);
    private readonly Mock<IMaintenanceAlertRepository> _alerts = new(MockBehavior.Loose);
    private readonly Mock<IMaintenanceLogRepository> _logs = new(MockBehavior.Loose);
    private readonly Mock<IHubContext<MaintenanceHub>> _hub = new(MockBehavior.Loose);
    private readonly Mock<IOptionsMonitor<MaintenanceAlertSettings>> _settings = new(MockBehavior.Loose);
    private readonly Mock<IAttentionBroadcaster> _broadcaster = new(MockBehavior.Loose);

    private MaintenanceAlertEngine CreateEngine(bool enableSignalR)
    {
        _settings.SetupGet(s => s.CurrentValue)
                 .Returns(new MaintenanceAlertSettings { EnableSignalRNotifications = enableSignalR });
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

    [Fact]
    public async Task ResolveAlert_LegacySignalRDisabled_StillEmitsExactlyOneAttentionEvent()
    {
        Guid alertId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        MaintenanceAlert alert = new() { Id = alertId, PrinterId = printer, Status = MaintenanceAlertStatus.Active };
        _alerts.Setup(a => a.GetByIdAsync(alertId, It.IsAny<CancellationToken>())).ReturnsAsync(alert);

        MaintenanceAlertEngine engine = CreateEngine(enableSignalR: false);

        await engine.ResolveAlertAsync(alertId, "operator", CancellationToken.None);

        _broadcaster.Verify(
            b => b.NotifyChangedAsync(
                It.Is<AttentionChangedPayload>(p =>
                    p.ItemId == $"maintenance:{alertId:D}" && p.ChangeKind == AttentionChangeKind.Resolved),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AcknowledgeAlert_EmitsSingleUpdatedAttentionEvent_AfterCommit()
    {
        Guid alertId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        MaintenanceAlert alert = new() { Id = alertId, PrinterId = printer, Status = MaintenanceAlertStatus.Active };
        _alerts.Setup(a => a.GetByIdAsync(alertId, It.IsAny<CancellationToken>())).ReturnsAsync(alert);

        MaintenanceAlertEngine engine = CreateEngine(enableSignalR: true);

        await engine.AcknowledgeAlertAsync(alertId, "operator", CancellationToken.None);

        _alerts.Verify(a => a.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _broadcaster.Verify(
            b => b.NotifyChangedAsync(
                It.Is<AttentionChangedPayload>(p =>
                    p.ItemId == $"maintenance:{alertId:D}" && p.ChangeKind == AttentionChangeKind.Updated),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveAlert_MissingAlert_EmitsNoAttentionEvent()
    {
        Guid alertId = Guid.NewGuid();
        _alerts.Setup(a => a.GetByIdAsync(alertId, It.IsAny<CancellationToken>())).ReturnsAsync((MaintenanceAlert?)null);

        MaintenanceAlertEngine engine = CreateEngine(enableSignalR: false);

        await engine.ResolveAlertAsync(alertId, "operator", CancellationToken.None);

        _broadcaster.Verify(
            b => b.NotifyChangedAsync(It.IsAny<AttentionChangedPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

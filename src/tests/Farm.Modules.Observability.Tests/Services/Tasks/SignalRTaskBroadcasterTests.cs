using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Tasks;
using Farm.Modules.Observability.Services.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Farm.Modules.Observability.Tests.Services.Tasks;

/// <summary>
/// Maintenance-sourced task DTOs are restricted to the authenticated administrator
/// group. All other task events broadcast to authenticated farm clients.
/// </summary>
public class SignalRTaskBroadcasterTests
{
    private static UserTaskDto Dto(UserTaskSourceKind sourceKind, string title = "t") => new(
        Id: Guid.NewGuid(),
        TaskType: UserTaskType.MaintenanceDue,
        EntityType: "Printer",
        EntityId: Guid.NewGuid(),
        Title: title,
        Description: "sensitive maintenance detail",
        Status: UserTaskStatus.Pending,
        Priority: UserTaskPriority.Normal,
        CreatedAt: DateTime.UtcNow,
        DueAt: null,
        CompletedAt: null,
        RelatedEntityCount: 0,
        MetadataJson: null,
        AnchorKind: UserTaskAnchorKind.Window,
        AnchorAtUtc: null,
        WindowStartUtc: DateTime.UtcNow.AddHours(1),
        WindowEndUtc: null,
        SourceKind: sourceKind,
        SourceId: sourceKind == UserTaskSourceKind.Maintenance ? "maintenancealert:1" : "failure:1");

    [Fact]
    public async Task BroadcastTaskCreatedAsync_MaintenanceTask_SendsOnlyToAdminGroup()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        Mock<IClientProxy> adminGroup = new();
        clients.Setup(c => c.All).Returns(all.Object);
        clients.Setup(c => c.Group(PrinterHub.AdminTaskGroup)).Returns(adminGroup.Object);
        Mock<IClientProxy> farmGroup = ConfigureFarmGroup(clients);
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastTaskCreatedAsync(Dto(UserTaskSourceKind.Maintenance));

        adminGroup.Verify(
            p => p.SendCoreAsync("taskcreated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
        all.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        farmGroup.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BroadcastTaskUpdatedAsync_MaintenanceTask_SendsOnlyToAdminGroup()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        Mock<IClientProxy> adminGroup = new();
        clients.Setup(c => c.All).Returns(all.Object);
        clients.Setup(c => c.Group(PrinterHub.AdminTaskGroup)).Returns(adminGroup.Object);
        Mock<IClientProxy> farmGroup = ConfigureFarmGroup(clients);
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastTaskUpdatedAsync(Dto(UserTaskSourceKind.Maintenance));

        adminGroup.Verify(
            p => p.SendCoreAsync("taskupdated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
        all.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        farmGroup.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BroadcastTaskCreatedAsync_NonMaintenanceTask_SendsToAll()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        Mock<IClientProxy> adminGroup = new();
        clients.Setup(c => c.All).Returns(all.Object);
        clients.Setup(c => c.Group(PrinterHub.AdminTaskGroup)).Returns(adminGroup.Object);
        Mock<IClientProxy> farmGroup = ConfigureFarmGroup(clients);
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastTaskCreatedAsync(Dto(UserTaskSourceKind.FailureIncident));

        farmGroup.Verify(
            p => p.SendCoreAsync("taskcreated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
        all.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        adminGroup.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Fix R3-4: the pending-count broadcast always goes to everyone — the
    /// caller (<see cref="Farm.Infrastructure.Services.Tasks.UserTaskService"/>) is
    /// responsible for passing the non-maintenance-filtered count so this never
    /// disagrees with the non-admin REST count.</summary>
    [Fact]
    public async Task BroadcastPendingTaskCountAsync_SendsToAll()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        clients.Setup(c => c.All).Returns(all.Object);
        Mock<IClientProxy> farmGroup = ConfigureFarmGroup(clients);
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastPendingTaskCountAsync(3);

        farmGroup.Verify(
            p => p.SendCoreAsync("pendingtaskcount", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
        all.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Mock<IClientProxy> ConfigureFarmGroup(Mock<IHubClients> clients)
    {
        Mock<IClientProxy> farmGroup = new();
        _ = farmGroup
            .Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = clients
            .Setup(value => value.Group(AuthorizedHubGroups.Farm))
            .Returns(farmGroup.Object);
        return farmGroup;
    }

    private static SignalRTaskBroadcaster Build(Mock<IHubClients> clients)
    {
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return new SignalRTaskBroadcaster(hub.Object);
    }
}

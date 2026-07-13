using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Tasks;
using Farm.Web.Api.Services.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Tasks;

/// <summary>
/// Fix R3-3 (issue #713, round 3; supersedes round-2 Fix C): maintenance-sourced task
/// DTOs are no longer routed to <see cref="PrinterHub.AdminTaskGroup"/> — that group is
/// unreachable in practice because <see cref="PrinterHub"/> is mapped
/// <c>AllowAnonymous()</c> and the React client never authenticates against it. Routing
/// there was misleading (implying admins receive live updates when none do). Maintenance
/// DTOs are now a documented no-op broadcast; REST remains authoritative. All other
/// (non-maintenance) task events keep broadcasting to every connected client.
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
    public async Task BroadcastTaskCreatedAsync_MaintenanceTask_IsNoOp_DoesNotSendToAllOrAdminGroup()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        Mock<IClientProxy> adminGroup = new();
        clients.Setup(c => c.All).Returns(all.Object);
        clients.Setup(c => c.Group(PrinterHub.AdminTaskGroup)).Returns(adminGroup.Object);
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastTaskCreatedAsync(Dto(UserTaskSourceKind.Maintenance));

        // Fix R3-3: the admin group is unreachable via this (anonymous) hub, so
        // routing there would be misleading. The broadcast is a documented no-op;
        // REST remains the authoritative source for maintenance content.
        adminGroup.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        all.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BroadcastTaskUpdatedAsync_MaintenanceTask_IsNoOp_DoesNotSendToAllOrAdminGroup()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        Mock<IClientProxy> adminGroup = new();
        clients.Setup(c => c.All).Returns(all.Object);
        clients.Setup(c => c.Group(PrinterHub.AdminTaskGroup)).Returns(adminGroup.Object);
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastTaskUpdatedAsync(Dto(UserTaskSourceKind.Maintenance));

        adminGroup.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        all.Verify(
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
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastTaskCreatedAsync(Dto(UserTaskSourceKind.FailureIncident));

        all.Verify(
            p => p.SendCoreAsync("taskcreated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
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
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastPendingTaskCountAsync(3);

        all.Verify(
            p => p.SendCoreAsync("pendingtaskcount", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static SignalRTaskBroadcaster Build(Mock<IHubClients> clients)
    {
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return new SignalRTaskBroadcaster(hub.Object);
    }
}

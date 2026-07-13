using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Tasks;
using Farm.Web.Api.Services.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Tasks;

/// <summary>
/// Fix C (issue #713): the SignalR task broadcaster must not fan maintenance-sourced
/// task DTOs (which carry alert content) out to <c>Clients.All</c>. Maintenance events
/// are routed to the admin group only; all other task events keep broadcasting to
/// everyone. Guards the realtime channel against re-exposing content the REST gate hides.
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
    public async Task BroadcastTaskCreatedAsync_MaintenanceTask_SendsToAdminGroupNotAll()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        Mock<IClientProxy> adminGroup = new();
        clients.Setup(c => c.All).Returns(all.Object);
        clients.Setup(c => c.Group(PrinterHub.AdminTaskGroup)).Returns(adminGroup.Object);
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastTaskCreatedAsync(Dto(UserTaskSourceKind.Maintenance));

        adminGroup.Verify(
            p => p.SendCoreAsync("taskcreated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
        all.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BroadcastTaskUpdatedAsync_MaintenanceTask_SendsToAdminGroupNotAll()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        Mock<IClientProxy> adminGroup = new();
        clients.Setup(c => c.All).Returns(all.Object);
        clients.Setup(c => c.Group(PrinterHub.AdminTaskGroup)).Returns(adminGroup.Object);
        SignalRTaskBroadcaster broadcaster = Build(clients);

        await broadcaster.BroadcastTaskUpdatedAsync(Dto(UserTaskSourceKind.Maintenance));

        adminGroup.Verify(
            p => p.SendCoreAsync("taskupdated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
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

    private static SignalRTaskBroadcaster Build(Mock<IHubClients> clients)
    {
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return new SignalRTaskBroadcaster(hub.Object);
    }
}

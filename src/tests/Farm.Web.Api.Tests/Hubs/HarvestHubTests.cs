using System;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Hubs;

/// <summary>
/// Includes regression coverage for issue #2300: <see cref="HarvestHub"/> must not auto-join
/// every authenticated connection to the farm-wide harvest group, and
/// <c>JoinHarvestGroupAsync</c> must not let any authenticated caller join an arbitrary
/// per-operation group — both mirroring the <c>gcode_harvest:admin</c> REST gate on
/// <c>GcodeHarvestController</c> and the pattern already established by
/// <see cref="Farm.Modules.Maintenance.Hubs.MaintenanceHub"/> (issue #1966).
/// </summary>
public class HarvestHubTests : IDisposable
{
    private readonly Mock<IHubCallerClients> _clientsMock;
    private readonly Mock<IClientProxy> _groupMock;
    private readonly Mock<IGroupManager> _groupsMock;
    private readonly Mock<HubCallerContext> _contextMock;
    private readonly HarvestHub _hub;

    public HarvestHubTests()
    {
        _clientsMock = new Mock<IHubCallerClients>();
        _groupMock = new Mock<IClientProxy>();
        _groupsMock = new Mock<IGroupManager>();
        _contextMock = new Mock<HubCallerContext>();

        // Setup hub context
        _contextMock.Setup(c => c.ConnectionId).Returns("test-connection-id");
        // Default to a gcode_harvest:admin caller so the pre-existing tests below (which predate
        // issue #2300's permission gate) keep exercising JoinHarvestGroupAsync's group-join
        // behavior unchanged. The permission-gate regression tests further down set their own
        // Context.User per case.
        _contextMock.Setup(c => c.User).Returns(HarvestAdminUser());

        // Setup clients
        _clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupMock.Object);

        _hub = new HarvestHub
        {
            Clients = _clientsMock.Object,
            Groups = _groupsMock.Object,
            Context = _contextMock.Object
        };
    }

    public void Dispose() => _hub.Dispose();

    private static ClaimsPrincipal PlainAuthenticatedUser() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        ], "Test"));

    private static ClaimsPrincipal HarvestAdminUser() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(PrintFarmerPermissions.ClaimType, "gcode_harvest:admin"),
        ], "Test"));

    private static ClaimsPrincipal FarmAdminUser() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, PrintFarmerPermissions.FarmAdminRole),
        ], "Test"));

    [Fact]
    public async Task OnConnectedAsync_PlainAuthenticatedUser_DoesNotJoinFarmGroup()
    {
        _contextMock.Setup(c => c.User).Returns(PlainAuthenticatedUser());

        await _hub.OnConnectedAsync();

        _groupsMock.Verify(
            groups => groups.AddToGroupAsync(
                "test-connection-id",
                AuthorizedHubGroups.Farm,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_HarvestAdminPermission_JoinsFarmGroup()
    {
        _contextMock.Setup(c => c.User).Returns(HarvestAdminUser());

        await _hub.OnConnectedAsync();

        _groupsMock.Verify(
            groups => groups.AddToGroupAsync(
                "test-connection-id",
                AuthorizedHubGroups.Farm,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_FarmAdminRole_JoinsFarmGroup()
    {
        _contextMock.Setup(c => c.User).Returns(FarmAdminUser());

        await _hub.OnConnectedAsync();

        _groupsMock.Verify(
            groups => groups.AddToGroupAsync(
                "test-connection-id",
                AuthorizedHubGroups.Farm,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task JoinHarvestGroupAsync_PlainAuthenticatedUser_ThrowsAndDoesNotJoin()
    {
        _contextMock.Setup(c => c.User).Returns(PlainAuthenticatedUser());
        Guid operationId = Guid.NewGuid();

        HubException exception = await Assert.ThrowsAsync<HubException>(
            () => _hub.JoinHarvestGroupAsync(operationId));
        Assert.Contains("resource_forbidden", exception.Message, StringComparison.Ordinal);

        _groupsMock.Verify(
            groups => groups.AddToGroupAsync(
                It.IsAny<string>(),
                $"harvest-{operationId}",
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task JoinHarvestGroupAsync_AddsClientToGroup()
    {
        // Arrange
        var operationId = Guid.NewGuid();

        // Act
        await _hub.JoinHarvestGroupAsync(operationId);

        // Assert
        _groupsMock.Verify(g => g.AddToGroupAsync(
            "test-connection-id",
            $"harvest-{operationId}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinHarvestGroupAsync_WithDifferentOperationIds_CreatesDistinctGroups()
    {
        // Arrange
        var operationId1 = Guid.NewGuid();
        var operationId2 = Guid.NewGuid();

        // Act
        await _hub.JoinHarvestGroupAsync(operationId1);
        await _hub.JoinHarvestGroupAsync(operationId2);

        // Assert
        _groupsMock.Verify(g => g.AddToGroupAsync(
            "test-connection-id",
            $"harvest-{operationId1}",
            It.IsAny<CancellationToken>()), Times.Once);

        _groupsMock.Verify(g => g.AddToGroupAsync(
            "test-connection-id",
            $"harvest-{operationId2}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeaveHarvestGroupAsync_RemovesClientFromGroup()
    {
        // Arrange
        var operationId = Guid.NewGuid();

        // Act
        await _hub.LeaveHarvestGroupAsync(operationId);

        // Assert
        _groupsMock.Verify(g => g.RemoveFromGroupAsync(
            "test-connection-id",
            $"harvest-{operationId}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastFileProgressAsync_WithValidData_BroadcastsToGroup()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        string fileName = "test-file.gcode";
        long bytesCopied = 5000;
        long totalBytes = 10000;
        double expectedPercent = 50.0;

        // Act
        await _hub.BroadcastFileProgressAsync(operationId, fileName, bytesCopied, totalBytes);

        // Assert
        _clientsMock.Verify(c => c.Group($"harvest-{operationId}"), Times.Once);

        _groupMock.Verify(g => g.SendCoreAsync(
            "harvestfileprogress",
            It.Is<object[]>(args =>
                args.Length == 1 &&
                HasProperty(args[0], "operationId", operationId) &&
                HasProperty(args[0], "fileName", fileName) &&
                HasProperty(args[0], "bytesCopied", bytesCopied) &&
                HasProperty(args[0], "totalBytes", totalBytes) &&
                HasProperty(args[0], "percent", expectedPercent)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastFileProgressAsync_WithZeroTotalBytes_CalculatesZeroPercent()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        string fileName = "test-file.gcode";
        long bytesCopied = 0;
        long totalBytes = 0;
        double expectedPercent = 0.0;

        // Act
        await _hub.BroadcastFileProgressAsync(operationId, fileName, bytesCopied, totalBytes);

        // Assert
        _groupMock.Verify(g => g.SendCoreAsync(
            "harvestfileprogress",
            It.Is<object[]>(args =>
                args.Length == 1 &&
                HasProperty(args[0], "percent", expectedPercent)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastFileProgressAsync_WithCompleteFile_CalculatesHundredPercent()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        string fileName = "test-file.gcode";
        long bytesCopied = 10000;
        long totalBytes = 10000;
        double expectedPercent = 100.0;

        // Act
        await _hub.BroadcastFileProgressAsync(operationId, fileName, bytesCopied, totalBytes);

        // Assert
        _groupMock.Verify(g => g.SendCoreAsync(
            "harvestfileprogress",
            It.Is<object[]>(args =>
                args.Length == 1 &&
                HasProperty(args[0], "percent", expectedPercent)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastFileProgressAsync_WithPartialProgress_CalculatesCorrectPercent()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        string fileName = "test-file.gcode";
        long bytesCopied = 2500;
        long totalBytes = 10000;
        double expectedPercent = 25.0;

        // Act
        await _hub.BroadcastFileProgressAsync(operationId, fileName, bytesCopied, totalBytes);

        // Assert
        _groupMock.Verify(g => g.SendCoreAsync(
            "harvestfileprogress",
            It.Is<object[]>(args =>
                args.Length == 1 &&
                HasProperty(args[0], "percent", expectedPercent)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastFileProgressAsync_MultipleFiles_BroadcastsSeparately()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        string file1 = "file1.gcode";
        string file2 = "file2.gcode";

        // Act
        await _hub.BroadcastFileProgressAsync(operationId, file1, 1000, 10000);
        await _hub.BroadcastFileProgressAsync(operationId, file2, 5000, 10000);

        // Assert
        _groupMock.Verify(g => g.SendCoreAsync(
            "harvestfileprogress",
            It.Is<object[]>(args => HasProperty(args[0], "fileName", file1)),
            It.IsAny<CancellationToken>()), Times.Once);

        _groupMock.Verify(g => g.SendCoreAsync(
            "harvestfileprogress",
            It.Is<object[]>(args => HasProperty(args[0], "fileName", file2)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastFileProgressAsync_LargeFileSize_HandlesLongValues()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        string fileName = "large-file.gcode";
        long bytesCopied = 5_000_000_000; // 5GB
        long totalBytes = 10_000_000_000; // 10GB
        double expectedPercent = 50.0;

        // Act
        await _hub.BroadcastFileProgressAsync(operationId, fileName, bytesCopied, totalBytes);

        // Assert
        _groupMock.Verify(g => g.SendCoreAsync(
            "harvestfileprogress",
            It.Is<object[]>(args =>
                args.Length == 1 &&
                HasProperty(args[0], "bytesCopied", bytesCopied) &&
                HasProperty(args[0], "totalBytes", totalBytes) &&
                HasProperty(args[0], "percent", expectedPercent)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinHarvestGroupAsync_MultipleClients_CanJoinSameGroup()
    {
        // Arrange
        var operationId = Guid.NewGuid();

        // Setup different connection IDs
        _contextMock.SetupSequence(c => c.ConnectionId)
            .Returns("client-1")
            .Returns("client-2");

        // Act
        await _hub.JoinHarvestGroupAsync(operationId);
        await _hub.JoinHarvestGroupAsync(operationId);

        // Assert
        _groupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(),
            $"harvest-{operationId}",
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// Helper method to check if an anonymous object has a property with a specific value
    /// </summary>
    private bool HasProperty<T>(object obj, string propertyName, T expectedValue)
    {
        if (obj == null)
        {
            return false;
        }

        PropertyInfo? property = obj.GetType().GetProperty(propertyName);
        if (property == null)
        {
            return false;
        }

        object? value = property.GetValue(obj);
        bool expectedIsNull = expectedValue is null;
        if (value == null && expectedIsNull)
        {
            return true;
        }

        return value != null && !expectedIsNull && value.Equals(expectedValue);
    }
}

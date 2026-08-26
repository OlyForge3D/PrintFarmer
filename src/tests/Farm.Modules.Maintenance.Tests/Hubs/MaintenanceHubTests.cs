using System.Security.Claims;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Farm.Web.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Hubs;

/// <summary>
/// Unit tests for the issue #1966 fix: <see cref="MaintenanceHub"/> must not auto-join every
/// authenticated connection to the farm-wide maintenance group, and must gate per-printer
/// subscriptions on <see cref="IQueueResourceAuthorizationService.CanAccessPrinterAsync"/>,
/// mirroring <see cref="Farm.Infrastructure.Services.SignalR.PrinterHub"/>.
/// </summary>
public sealed class MaintenanceHubTests
{
    private readonly Mock<ILogger<MaintenanceHub>> _loggerMock;
    private readonly Mock<IHubCallerClients> _clientsMock;
    private readonly Mock<IGroupManager> _groupsMock;
    private readonly Mock<HubCallerContext> _contextMock;
    private readonly Mock<IQueueResourceAuthorizationService> _resourceAuthorizationMock;

    public MaintenanceHubTests()
    {
        _loggerMock = new Mock<ILogger<MaintenanceHub>>();
        _clientsMock = new Mock<IHubCallerClients>();
        _groupsMock = new Mock<IGroupManager>();
        _contextMock = new Mock<HubCallerContext>();
        _resourceAuthorizationMock = new Mock<IQueueResourceAuthorizationService>();
        _resourceAuthorizationMock
            .Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _contextMock.Setup(c => c.ConnectionId).Returns("test-connection-id");
        _contextMock.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);
    }

    private MaintenanceHub CreateHub(ClaimsPrincipal user, IQueueResourceAuthorizationService? resourceAuthorization)
    {
        _contextMock.Setup(c => c.User).Returns(user);
        return new MaintenanceHub(_loggerMock.Object, resourceAuthorization)
        {
            Clients = _clientsMock.Object,
            Groups = _groupsMock.Object,
            Context = _contextMock.Object
        };
    }

    private static ClaimsPrincipal PlainAuthenticatedUser() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        ], "Test"));

    private static ClaimsPrincipal MaintenanceAdminUser() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(PrintFarmerPermissions.ClaimType, "maintenance:admin"),
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
        MaintenanceHub hub = CreateHub(PlainAuthenticatedUser(), _resourceAuthorizationMock.Object);

        await hub.OnConnectedAsync();

        _groupsMock.Verify(
            groups => groups.AddToGroupAsync(
                "test-connection-id",
                AuthorizedHubGroups.Farm,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_MaintenanceAdminPermission_JoinsFarmGroup()
    {
        MaintenanceHub hub = CreateHub(MaintenanceAdminUser(), _resourceAuthorizationMock.Object);

        await hub.OnConnectedAsync();

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
        MaintenanceHub hub = CreateHub(FarmAdminUser(), _resourceAuthorizationMock.Object);

        await hub.OnConnectedAsync();

        _groupsMock.Verify(
            groups => groups.AddToGroupAsync(
                "test-connection-id",
                AuthorizedHubGroups.Farm,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubscribeToPrinterAsync_WithAccess_JoinsPerPrinterGroup()
    {
        Guid printerId = Guid.NewGuid();
        MaintenanceHub hub = CreateHub(PlainAuthenticatedUser(), _resourceAuthorizationMock.Object);

        await hub.SubscribeToPrinterAsync(printerId.ToString());

        _groupsMock.Verify(
            groups => groups.AddToGroupAsync(
                "test-connection-id",
                AuthorizedHubGroups.MaintenancePrinter(printerId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubscribeToPrinterAsync_WithoutResourceAccess_ThrowsAndDoesNotJoin()
    {
        _resourceAuthorizationMock
            .Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        MaintenanceHub hub = CreateHub(PlainAuthenticatedUser(), _resourceAuthorizationMock.Object);

        await Assert.ThrowsAsync<HubException>(
            () => hub.SubscribeToPrinterAsync(Guid.NewGuid().ToString()));

        _groupsMock.Verify(
            groups => groups.AddToGroupAsync(
                It.IsAny<string>(),
                It.Is<string>(group => group.StartsWith("maintenance-printer-", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubscribeToPrinterAsync_NoResourceAuthorizationServiceRegistered_ThrowsAndDoesNotJoin()
    {
        MaintenanceHub hub = CreateHub(PlainAuthenticatedUser(), resourceAuthorization: null);

        await Assert.ThrowsAsync<HubException>(
            () => hub.SubscribeToPrinterAsync(Guid.NewGuid().ToString()));

        _groupsMock.Verify(
            groups => groups.AddToGroupAsync(
                It.IsAny<string>(),
                It.Is<string>(group => group.StartsWith("maintenance-printer-", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubscribeToPrinterAsync_InvalidPrinterId_ThrowsHubException()
    {
        MaintenanceHub hub = CreateHub(PlainAuthenticatedUser(), _resourceAuthorizationMock.Object);

        await Assert.ThrowsAsync<HubException>(
            () => hub.SubscribeToPrinterAsync("not-a-guid"));
    }

    [Fact]
    public async Task UnsubscribeFromPrinterAsync_ValidId_RemovesFromGroup()
    {
        Guid printerId = Guid.NewGuid();
        MaintenanceHub hub = CreateHub(PlainAuthenticatedUser(), _resourceAuthorizationMock.Object);

        await hub.UnsubscribeFromPrinterAsync(printerId.ToString());

        _groupsMock.Verify(
            groups => groups.RemoveFromGroupAsync(
                "test-connection-id",
                AuthorizedHubGroups.MaintenancePrinter(printerId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Discovery;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Hubs;

public class PrinterHubTests
{
    private readonly Mock<IDiscoveryProgressCache> _progressCacheMock;
    private readonly Mock<IDiscoverySessionRegistry> _sessionRegistryMock;
    private readonly Mock<ILogger<PrinterHub>> _loggerMock;
    private readonly Mock<Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader> _statusCacheMock;
    private readonly Mock<IHubCallerClients> _clientsMock;
    private readonly Mock<ISingleClientProxy> _callerMock;
    private readonly Mock<IClientProxy> _groupMock;
    private readonly Mock<IGroupManager> _groupsMock;
    private readonly Mock<HubCallerContext> _contextMock;
    private readonly Mock<IQueueResourceAuthorizationService> _resourceAuthorizationMock;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly PrinterHub _hub;

    public PrinterHubTests()
    {
        _progressCacheMock = new Mock<IDiscoveryProgressCache>();
        _sessionRegistryMock = new Mock<IDiscoverySessionRegistry>();
        _loggerMock = new Mock<ILogger<PrinterHub>>();
        _statusCacheMock = new Mock<Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader>();
        _statusCacheMock
            .Setup(c => c.GetAllStatuses())
            .Returns(new Dictionary<Guid, PrinterStatusDto>());
        _clientsMock = new Mock<IHubCallerClients>();
        _callerMock = new Mock<ISingleClientProxy>();
        _groupMock = new Mock<IClientProxy>();
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

        // Setup hub context
        _contextMock.Setup(c => c.ConnectionId).Returns("test-connection-id");
        _contextMock.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);
        _contextMock.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
        ], "Test")));
        _sessionRegistryMock.Setup(r => r.SessionExists(It.IsAny<string>())).Returns(true);
        _sessionRegistryMock.Setup(r => r.IsSessionOwner(It.IsAny<string>(), _userId)).Returns(true);

        // Setup caller mock to handle SendCoreAsync (required for SendAsync extension method)
        // Configure both the ISingleClientProxy and IClientProxy interfaces
        _callerMock
            .Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Setup group mock for SendCoreAsync
        _groupMock
            .Setup(g => g.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _clientsMock.Setup(c => c.Caller).Returns(_callerMock.Object);
        // IHubCallerClients hides Caller from IHubCallerClients<IClientProxy> with `new`;
        // depending on which slot the hub's compilation binds to, both must be set up.
        _clientsMock.As<IHubCallerClients<IClientProxy>>().Setup(c => c.Caller).Returns(_callerMock.Object);
        _clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupMock.Object);

        _hub = new PrinterHub(
            _progressCacheMock.Object,
            _loggerMock.Object,
            _statusCacheMock.Object,
            _sessionRegistryMock.Object,
            _resourceAuthorizationMock.Object)
        {
            Clients = _clientsMock.Object,
            Groups = _groupsMock.Object,
            Context = _contextMock.Object
        };
    }

    [Fact]
    public async Task OnConnectedAsync_QueueReader_JoinsResourceDiscoveryGroup()
    {
        _contextMock.Setup(c => c.User).Returns(new ClaimsPrincipal(
            new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
                new Claim(
                    PrintFarmerPermissions.ClaimType,
                    PrintFarmerPermissions.Queue.Read),
            ],
            "Test")));

        await _hub.OnConnectedAsync();

        _groupsMock.Verify(group => group.AddToGroupAsync(
            "test-connection-id",
            AuthorizedHubGroups.QueueReaders,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinDiscoveryGroupAsync_AddsClientToGroup()
    {
        // Arrange
        string sessionId = "test-session-id";

        // Act
        await _hub.JoinDiscoveryGroupAsync(sessionId);

        // Assert
        _groupsMock.Verify(g => g.AddToGroupAsync(
            "test-connection-id",
            "discovery-test-session-id",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinDiscoveryGroupAsync_ForDifferentOwner_ThrowsHubException()
    {
        Guid differentUserId = Guid.NewGuid();
        _contextMock.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, differentUserId.ToString()),
        ], "Test")));

        await Assert.ThrowsAsync<HubException>(
            () => _hub.JoinDiscoveryGroupAsync("test-session-id"));

        _groupsMock.Verify(
            g => g.AddToGroupAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task JoinDiscoveryGroupAsync_ForFarmAdmin_AllowsAuditedBypass()
    {
        Guid adminUserId = Guid.NewGuid();
        _contextMock.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, adminUserId.ToString()),
            new Claim(ClaimTypes.Role, PrintFarmerPermissions.FarmAdminRole),
        ], "Test", ClaimTypes.Name, ClaimTypes.Role)));

        await _hub.JoinDiscoveryGroupAsync("test-session-id");

        _groupsMock.Verify(
            g => g.AddToGroupAsync(
                "test-connection-id",
                "discovery-test-session-id",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task JoinDiscoveryGroupAsync_WithoutCachedProgress_DoesNotSendProgress()
    {
        // Arrange
        string sessionId = "test-session-id";

        _progressCacheMock
            .Setup(c => c.TryGet(sessionId, out It.Ref<DiscoveryProgressDto?>.IsAny))
            .Returns((string sid, out DiscoveryProgressDto? progress) =>
            {
                progress = null;
                return false;
            });

        // Act
        await _hub.JoinDiscoveryGroupAsync(sessionId);

        // Assert
        _callerMock.Verify(c => c.SendCoreAsync(
            "discoveryprogress",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinDiscoveryGroupAsync_WithConnectionAborted_StopsRetrying()
    {
        // Arrange
        string sessionId = "test-session-id";
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel to simulate aborted connection

        _contextMock.Setup(c => c.ConnectionAborted).Returns(cts.Token);

        _progressCacheMock
            .Setup(c => c.TryGet(sessionId, out It.Ref<DiscoveryProgressDto?>.IsAny))
            .Returns((string sid, out DiscoveryProgressDto? progress) =>
            {
                progress = null;
                return false;
            });

        // Act
        await _hub.JoinDiscoveryGroupAsync(sessionId);

        // Assert - should attempt TryGet at least once, but stop early due to cancellation
        _progressCacheMock.Verify(c => c.TryGet(sessionId, out It.Ref<DiscoveryProgressDto?>.IsAny), Times.AtLeastOnce);
    }

    [Fact]
    public async Task LeaveDiscoveryGroupAsync_RemovesClientFromGroup()
    {
        // Arrange
        string sessionId = "test-session-id";

        // Act
        await _hub.LeaveDiscoveryGroupAsync(sessionId);

        // Assert
        _groupsMock.Verify(g => g.RemoveFromGroupAsync(
            "test-connection-id",
            "discovery-test-session-id",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinDiscoveryGroupAsync_LogsConnectionInfo()
    {
        // Arrange
        string sessionId = "test-session-id";

        // Act
        await _hub.JoinDiscoveryGroupAsync(sessionId);

        // Assert - verify logging was called (using ILogger extension method patterns)
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("joining discovery group")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LeaveDiscoveryGroupAsync_LogsConnectionInfo()
    {
        // Arrange
        string sessionId = "test-session-id";

        // Act
        await _hub.LeaveDiscoveryGroupAsync(sessionId);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("leaving discovery group")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_DoesNotReplayUnsubscribedPrinterStatuses()
    {
        // Arrange
        Guid printerA = Guid.NewGuid();
        Guid printerB = Guid.NewGuid();
        _statusCacheMock
            .Setup(c => c.GetAllStatuses())
            .Returns(new Dictionary<Guid, PrinterStatusDto>
            {
                [printerA] = new PrinterStatusDto(Id: printerA, IsOnline: true, State: "Printing"),
                [printerB] = new PrinterStatusDto(Id: printerB, IsOnline: false, State: "Offline"),
            });

        // Act
        await _hub.OnConnectedAsync();

        // Assert: resource data is never replayed before an explicit authorized subscription.
        _callerMock.Verify(
            c => c.SendCoreAsync(
                "printerupdated",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_WithEmptyCache_SendsNothing()
    {
        // Act
        await _hub.OnConnectedAsync();

        // Assert
        _callerMock.Verify(
            c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestPrinterStatus_WithCachedStatus_SendsToCaller()
    {
        // Arrange
        Guid printerId = Guid.NewGuid();
        var status = new PrinterStatusDto(Id: printerId, IsOnline: true, State: "Idle");
        _statusCacheMock.Setup(c => c.GetStatus(printerId)).Returns(status);

        // Act
        await _hub.RequestPrinterStatusAsync(printerId.ToString());

        // Assert
        _callerMock.Verify(
            c => c.SendCoreAsync(
                "printerupdated",
                It.Is<object?[]>(args => ReferenceEquals(args[0], status)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubscribeToPrinterAsync_WithAccess_JoinsAndReplaysOnlyThatPrinter()
    {
        Guid printerId = Guid.NewGuid();
        var status = new PrinterStatusDto(Id: printerId, IsOnline: true, State: "Idle");
        _statusCacheMock.Setup(cache => cache.GetStatus(printerId)).Returns(status);

        await _hub.SubscribeToPrinterAsync(printerId.ToString());

        _groupsMock.Verify(groups => groups.AddToGroupAsync(
            "test-connection-id",
            AuthorizedHubGroups.Printer(printerId),
            It.IsAny<CancellationToken>()), Times.Once);
        _callerMock.Verify(client => client.SendCoreAsync(
            "printerupdated",
            It.Is<object?[]>(arguments => ReferenceEquals(arguments[0], status)),
            It.IsAny<CancellationToken>()), Times.Once);
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

        await Assert.ThrowsAsync<HubException>(
            () => _hub.SubscribeToPrinterAsync(Guid.NewGuid().ToString()));

        _groupsMock.Verify(groups => groups.AddToGroupAsync(
            It.IsAny<string>(),
            It.Is<string>(group => group.StartsWith("Printer-", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubscribeToPrintersAsync_WithMixedAccess_JoinsAndReturnsOnlyAuthorizedIds()
    {
        Guid authorizedId = Guid.NewGuid();
        Guid unauthorizedId = Guid.NewGuid();
        var authorizedStatus = new PrinterStatusDto(Id: authorizedId, IsOnline: true, State: "Idle");
        _statusCacheMock.Setup(cache => cache.GetStatus(authorizedId)).Returns(authorizedStatus);
        _statusCacheMock.Setup(cache => cache.GetStatus(unauthorizedId)).Returns((PrinterStatusDto?)null);

        _resourceAuthorizationMock
            .Setup(service => service.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IReadOnlyCollection<Guid>>(ids =>
                    ids.Contains(authorizedId) && ids.Contains(unauthorizedId) && ids.Count == 2),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { authorizedId });

        string[] result = await _hub.SubscribeToPrintersAsync(
            [authorizedId.ToString(), unauthorizedId.ToString()]);

        Assert.Equal([authorizedId.ToString()], result);

        _groupsMock.Verify(groups => groups.AddToGroupAsync(
            "test-connection-id",
            AuthorizedHubGroups.Printer(authorizedId),
            It.IsAny<CancellationToken>()), Times.Once);
        _groupsMock.Verify(groups => groups.AddToGroupAsync(
            "test-connection-id",
            AuthorizedHubGroups.Printer(unauthorizedId),
            It.IsAny<CancellationToken>()), Times.Never);

        // A single batched status replay is sent instead of one "printerupdated"
        // frame per authorized printer (issue #1764).
        _callerMock.Verify(client => client.SendCoreAsync(
            "printerstatusesreplayed",
            It.Is<object?[]>(arguments => ContainsExactlyStatuses(arguments, authorizedStatus)),
            It.IsAny<CancellationToken>()), Times.Once);
        _callerMock.Verify(client => client.SendCoreAsync(
            "printerupdated",
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Batched authorization is used instead of a per-printer authorization check.
        _resourceAuthorizationMock.Verify(
            service => service.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _resourceAuthorizationMock.Verify(
            service => service.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubscribeToPrintersAsync_WithEmptyInput_ReturnsEmptyAndDoesNotJoin()
    {
        string[] result = await _hub.SubscribeToPrintersAsync([]);

        Assert.Empty(result);
        _groupsMock.Verify(groups => groups.AddToGroupAsync(
            It.IsAny<string>(),
            It.Is<string>(group => group.StartsWith("Printer-", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Never);
        _resourceAuthorizationMock.Verify(
            service => service.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubscribeToPrintersAsync_WithOnlyInvalidIds_ReturnsEmptyAndDoesNotAuthorize()
    {
        string[] result = await _hub.SubscribeToPrintersAsync(["not-a-guid", "also-not-a-guid"]);

        Assert.Empty(result);
        _resourceAuthorizationMock.Verify(
            service => service.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubscribeToPrintersAsync_WithDuplicateIds_DeduplicatesBeforeAuthorizationAndJoinsOnce()
    {
        Guid printerId = Guid.NewGuid();
        _resourceAuthorizationMock
            .Setup(service => service.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(printerId)),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { printerId });

        string[] result = await _hub.SubscribeToPrintersAsync(
            [printerId.ToString(), printerId.ToString()]);

        Assert.Equal([printerId.ToString()], result);
        _groupsMock.Verify(groups => groups.AddToGroupAsync(
            "test-connection-id",
            AuthorizedHubGroups.Printer(printerId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribeToPrintersAsync_WithNonCanonicalGuidString_EchoesBackOriginalRequestedString()
    {
        // A GUID string in an uppercase, hyphen-braced format is a valid GUID
        // (Guid.TryParse accepts it), but is not what Guid.ToString() would
        // produce. The response must echo back exactly what the client sent so
        // the client's own string-equality comparison recognizes it as
        // authorized (see PR #1801 review — Bishop).
        Guid printerId = Guid.NewGuid();
        string nonCanonicalRequestedId = "{" + printerId.ToString().ToUpperInvariant() + "}";

        _resourceAuthorizationMock
            .Setup(service => service.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(printerId)),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { printerId });

        string[] result = await _hub.SubscribeToPrintersAsync([nonCanonicalRequestedId]);

        Assert.Equal([nonCanonicalRequestedId], result);
        _groupsMock.Verify(groups => groups.AddToGroupAsync(
            "test-connection-id",
            AuthorizedHubGroups.Printer(printerId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribeToPrintersAsync_WithMultipleAuthorizedCachedStatuses_SendsSingleBatchedFrame()
    {
        Guid idA = Guid.NewGuid();
        Guid idB = Guid.NewGuid();
        Guid idC = Guid.NewGuid();
        var statusA = new PrinterStatusDto(Id: idA, IsOnline: true, State: "Idle");
        var statusB = new PrinterStatusDto(Id: idB, IsOnline: true, State: "Printing");
        _statusCacheMock.Setup(cache => cache.GetStatus(idA)).Returns(statusA);
        _statusCacheMock.Setup(cache => cache.GetStatus(idB)).Returns(statusB);
        _statusCacheMock.Setup(cache => cache.GetStatus(idC)).Returns((PrinterStatusDto?)null);

        _resourceAuthorizationMock
            .Setup(service => service.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 3),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { idA, idB, idC });

        string[] result = await _hub.SubscribeToPrintersAsync(
            [idA.ToString(), idB.ToString(), idC.ToString()]);

        Assert.Equal(3, result.Length);

        // Exactly one outbound frame carrying every authorized printer's cached
        // status, not N individual "printerupdated" frames.
        _callerMock.Verify(client => client.SendCoreAsync(
            It.IsAny<string>(),
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _callerMock.Verify(client => client.SendCoreAsync(
            "printerstatusesreplayed",
            It.Is<object?[]>(arguments => ContainsExactlyStatuses(arguments, statusA, statusB)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Matcher helper for "printerstatusesreplayed" payloads. Kept out of the Moq
    /// <c>It.Is</c> lambda because Moq builds those lambdas into expression trees,
    /// which cannot contain the <c>is</c> pattern-matching operator (CS8122).
    /// </summary>
    private static bool ContainsExactlyStatuses(object?[] arguments, params PrinterStatusDto[] expected)
    {
        if (arguments.Length == 0 || arguments[0] is not IReadOnlyCollection<PrinterStatusDto> statuses)
        {
            return false;
        }

        return statuses.Count == expected.Length && expected.All(statuses.Contains);
    }

    [Fact]
    public async Task RequestPrinterStatus_WithInvalidId_SendsNothing()
    {
        // Act
        await _hub.RequestPrinterStatusAsync("not-a-guid");

        // Assert
        _callerMock.Verify(
            c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestPrinterStatus_WithUnknownPrinter_SendsNothing()
    {
        // Arrange
        _statusCacheMock.Setup(c => c.GetStatus(It.IsAny<Guid>())).Returns((PrinterStatusDto?)null);

        // Act
        await _hub.RequestPrinterStatusAsync(Guid.NewGuid().ToString());

        // Assert
        _callerMock.Verify(
            c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

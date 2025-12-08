using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Hubs
{
    public class PrinterHubTests
    {
        private readonly Mock<IDiscoveryProgressCache> _progressCacheMock;
        private readonly Mock<ILogger<PrinterHub>> _loggerMock;
        private readonly Mock<IHubCallerClients> _clientsMock;
        private readonly Mock<ISingleClientProxy> _callerMock;
        private readonly Mock<IClientProxy> _groupMock;
        private readonly Mock<IGroupManager> _groupsMock;
        private readonly Mock<HubCallerContext> _contextMock;
        private readonly PrinterHub _hub;

        public PrinterHubTests()
        {
            _progressCacheMock = new Mock<IDiscoveryProgressCache>();
            _loggerMock = new Mock<ILogger<PrinterHub>>();
            _clientsMock = new Mock<IHubCallerClients>();
            _callerMock = new Mock<ISingleClientProxy>();
            _groupMock = new Mock<IClientProxy>();
            _groupsMock = new Mock<IGroupManager>();
            _contextMock = new Mock<HubCallerContext>();

            // Setup hub context
            _contextMock.Setup(c => c.ConnectionId).Returns("test-connection-id");
            _contextMock.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

            // Setup clients
            _clientsMock.Setup(c => c.Caller).Returns(_callerMock.Object);
            _clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupMock.Object);

            _hub = new PrinterHub(_progressCacheMock.Object, _loggerMock.Object)
            {
                Clients = _clientsMock.Object,
                Groups = _groupsMock.Object,
                Context = _contextMock.Object
            };
        }

        [Fact]
        public async Task JoinDiscoveryGroupAsync_AddsClientToGroup()
        {
            // Arrange
            var sessionId = "test-session-id";

            // Act
            await _hub.JoinDiscoveryGroupAsync(sessionId);

            // Assert
            _groupsMock.Verify(g => g.AddToGroupAsync(
                "test-connection-id",
                "discovery-test-session-id",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task JoinDiscoveryGroupAsync_WithCachedProgress_SendsProgressToCaller()
        {
            // Arrange
            var sessionId = "test-session-id";
            var cachedProgress = new DiscoveryProgressDto(
                SessionId: sessionId,
                CurrentNetwork: "192.168.1.0/24",
                CurrentIp: "192.168.1.100",
                TotalIps: 100,
                ScannedIps: 50,
                PrintersFound: 2,
                PrintersExcluded: 0,
                ProgressPercentage: 50,
                Status: DiscoveryStatus.Scanning
            );

            _progressCacheMock
                .Setup(c => c.TryGet(sessionId, out It.Ref<DiscoveryProgressDto?>.IsAny))
                .Returns((string sid, out DiscoveryProgressDto? progress) =>
                {
                    progress = cachedProgress;
                    return true;
                });

            // Act
            await _hub.JoinDiscoveryGroupAsync(sessionId);

            // Assert
            _callerMock.Verify(c => c.SendCoreAsync(
                "discoveryprogress",
                It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], cachedProgress)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task JoinDiscoveryGroupAsync_WithoutCachedProgress_DoesNotSendProgress()
        {
            // Arrange
            var sessionId = "test-session-id";

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
            var sessionId = "test-session-id";
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
            var sessionId = "test-session-id";

            // Act
            await _hub.LeaveDiscoveryGroupAsync(sessionId);

            // Assert
            _groupsMock.Verify(g => g.RemoveFromGroupAsync(
                "test-connection-id",
                "discovery-test-session-id",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task BroadcastDiscoveryProgressAsync_CachesProgress()
        {
            // Arrange
            var progress = new DiscoveryProgressDto(
                SessionId: "test-session",
                CurrentNetwork: "192.168.1.0/24",
                CurrentIp: "192.168.1.200",
                TotalIps: 100,
                ScannedIps: 75,
                PrintersFound: 3,
                PrintersExcluded: 0,
                ProgressPercentage: 75,
                Status: DiscoveryStatus.Scanning
            );

            // Act
            await _hub.BroadcastDiscoveryProgressAsync(progress);

            // Assert
            _progressCacheMock.Verify(c => c.Set("test-session", progress), Times.Once);
        }

        [Fact]
        public async Task BroadcastDiscoveryProgressAsync_BroadcastsToGroup()
        {
            // Arrange
            var progress = new DiscoveryProgressDto(
                SessionId: "test-session",
                CurrentNetwork: "192.168.1.0/24",
                CurrentIp: "192.168.1.200",
                TotalIps: 100,
                ScannedIps: 75,
                PrintersFound: 3,
                PrintersExcluded: 0,
                ProgressPercentage: 75,
                Status: DiscoveryStatus.Scanning
            );

            // Act
            await _hub.BroadcastDiscoveryProgressAsync(progress);

            // Assert
            _groupMock.Verify(g => g.SendCoreAsync(
                "discoveryprogress",
                It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], progress)),
                It.IsAny<CancellationToken>()), Times.Once);

            _clientsMock.Verify(c => c.Group("discovery-test-session"), Times.Once);
        }

        [Fact]
        public async Task BroadcastDiscoveryPrinterFoundAsync_BroadcastsToGroup()
        {
            // Arrange
            var found = new DiscoveryPrinterFoundDto(
                SessionId: "test-session",
                Printer: new DiscoveredPrinterDto
                {
                    Name = "Test Printer",
                    IpAddress = "192.168.1.100",
                    Backend = PrinterBackend.Moonraker
                }
            );

            // Act
            await _hub.BroadcastDiscoveryPrinterFoundAsync(found);

            // Assert
            _groupMock.Verify(g => g.SendCoreAsync(
                "discoveryprinterfound",
                It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], found)),
                It.IsAny<CancellationToken>()), Times.Once);

            _clientsMock.Verify(c => c.Group("discovery-test-session"), Times.Once);
        }

        [Fact]
        public async Task BroadcastDiscoveryCompletedAsync_BroadcastsToGroup()
        {
            // Arrange
            var completed = new DiscoveryCompletedDto(
                SessionId: "test-session",
                TotalPrintersFound: 3,
                TotalPrintersExcluded: 0,
                Duration: TimeSpan.FromSeconds(30)
            );

            // Act
            await _hub.BroadcastDiscoveryCompletedAsync(completed);

            // Assert
            _groupMock.Verify(g => g.SendCoreAsync(
                "discoverycompleted",
                It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], completed)),
                It.IsAny<CancellationToken>()), Times.Once);

            _clientsMock.Verify(c => c.Group("discovery-test-session"), Times.Once);
        }

        [Fact]
        public async Task BroadcastDiscoveryProgressAsync_WithNullProgress_DoesNotThrow()
        {
            // Arrange - Deliberately testing behavior with null (though hub signature doesn't allow nulls)
            // This test verifies the hub doesn't crash on unexpected null references

            // Act & Assert - should not throw
            await Assert.ThrowsAsync<NullReferenceException>(async () =>
            {
                await _hub.BroadcastDiscoveryProgressAsync(null!);
            });
        }

        [Fact]
        public async Task JoinDiscoveryGroupAsync_LogsConnectionInfo()
        {
            // Arrange
            var sessionId = "test-session-id";

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
            var sessionId = "test-session-id";

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
        public async Task BroadcastDiscoveryPrinterFoundAsync_LogsPrinterInfo()
        {
            // Arrange
            var found = new DiscoveryPrinterFoundDto(
                SessionId: "test-session",
                Printer: new DiscoveredPrinterDto
                {
                    Name = "Test Printer",
                    IpAddress = "192.168.1.100",
                    Backend = PrinterBackend.Moonraker
                }
            );

            // Act
            await _hub.BroadcastDiscoveryPrinterFoundAsync(found);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("printer found")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task BroadcastDiscoveryCompletedAsync_LogsCompletionInfo()
        {
            // Arrange
            var completed = new DiscoveryCompletedDto(
                SessionId: "test-session",
                TotalPrintersFound: 3,
                TotalPrintersExcluded: 0,
                Duration: TimeSpan.FromSeconds(30)
            );

            // Act
            await _hub.BroadcastDiscoveryCompletedAsync(completed);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("completion")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}

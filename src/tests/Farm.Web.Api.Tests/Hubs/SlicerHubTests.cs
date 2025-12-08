using System;
using System.Threading.Tasks;
using Farm.Web.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Hubs
{
    public class SlicerHubTests
    {
        private readonly Mock<ILogger<SlicerHub>> _loggerMock;
        private readonly Mock<IHubCallerClients> _clientsMock;
        private readonly Mock<ISingleClientProxy> _callerMock;
        private readonly Mock<HubCallerContext> _contextMock;
        private readonly SlicerHub _hub;

        public SlicerHubTests()
        {
            _loggerMock = new Mock<ILogger<SlicerHub>>();
            _clientsMock = new Mock<IHubCallerClients>();
            _callerMock = new Mock<ISingleClientProxy>();
            _contextMock = new Mock<HubCallerContext>();

            // Setup hub context
            _contextMock.Setup(c => c.ConnectionId).Returns("test-connection-id");

            // Setup clients
            _clientsMock.Setup(c => c.Caller).Returns(_callerMock.Object);

            _hub = new SlicerHub(_loggerMock.Object)
            {
                Clients = _clientsMock.Object,
                Context = _contextMock.Object
            };
        }

        [Fact]
        public void Constructor_WithValidLogger_InitializesSuccessfully()
        {
            // Act
            var hub = new SlicerHub(_loggerMock.Object);

            // Assert
            Assert.NotNull(hub);
        }

        [Fact]
        public void Constructor_WithNullLogger_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new SlicerHub(null!));
        }

        [Fact]
        public async Task OnConnectedAsync_LogsConnectionInfo()
        {
            // Act
            await _hub.OnConnectedAsync();

            // Assert - verify logging was called
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("connected") && v.ToString()!.Contains("SlicerHub")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task OnDisconnectedAsync_WithNoException_LogsDisconnectionInfo()
        {
            // Act
            await _hub.OnDisconnectedAsync(null);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("disconnected") && v.ToString()!.Contains("SlicerHub")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task OnDisconnectedAsync_WithException_LogsDisconnectionInfo()
        {
            // Arrange
            var exception = new InvalidOperationException("Test exception");

            // Act
            await _hub.OnDisconnectedAsync(exception);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("disconnected") && v.ToString()!.Contains("SlicerHub")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task RequestRegistryUpdateAsync_LogsRequestInfo()
        {
            // Act
            await _hub.RequestRegistryUpdateAsync();

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("requested registry update")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task RequestRegistryUpdateAsync_SendsAcknowledgmentToCaller()
        {
            // Act
            await _hub.RequestRegistryUpdateAsync();

            // Assert
            _callerMock.Verify(c => c.SendCoreAsync(
                "RegistryUpdateRequested",
                It.Is<object[]>(args => args.Length == 0),
                default), Times.Once);
        }

        [Fact]
        public async Task RequestRegistryUpdateAsync_MultipleRequests_SendsMultipleAcknowledgments()
        {
            // Act
            await _hub.RequestRegistryUpdateAsync();
            await _hub.RequestRegistryUpdateAsync();
            await _hub.RequestRegistryUpdateAsync();

            // Assert
            _callerMock.Verify(c => c.SendCoreAsync(
                "RegistryUpdateRequested",
                It.Is<object[]>(args => args.Length == 0),
                default), Times.Exactly(3));
        }

        [Fact]
        public void SlicerHubEvents_SlicerRegistered_HasCorrectValue()
        {
            // Assert
            Assert.Equal("SlicerRegistered", SlicerHubEvents.SlicerRegistered);
        }

        [Fact]
        public void SlicerHubEvents_SlicerHeartbeat_HasCorrectValue()
        {
            // Assert
            Assert.Equal("SlicerHeartbeat", SlicerHubEvents.SlicerHeartbeat);
        }

        [Fact]
        public void SlicerHubEvents_SlicerDeregistered_HasCorrectValue()
        {
            // Assert
            Assert.Equal("SlicerDeregistered", SlicerHubEvents.SlicerDeregistered);
        }

        [Fact]
        public void SlicerHubEvents_SlicerApiKeyRotated_HasCorrectValue()
        {
            // Assert
            Assert.Equal("SlicerApiKeyRotated", SlicerHubEvents.SlicerApiKeyRotated);
        }
    }
}

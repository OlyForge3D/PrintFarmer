using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.SignalR
{
    public class SignalRTestServiceTests
    {
        private static (SignalRTestService Service, Mock<IHubClients> Clients, Mock<ISingleClientProxy> ClientProxy, Mock<IClientProxy> GroupProxy, Mock<IClientProxy> AllProxy) CreateService()
        {
            var hubContext = new Mock<IHubContext<PrinterHub>>();
            var clients = new Mock<IHubClients>();
            var clientProxy = new Mock<ISingleClientProxy>();
            var groupProxy = new Mock<IClientProxy>();
            var allProxy = new Mock<IClientProxy>();

            hubContext.SetupGet(h => h.Clients).Returns(clients.Object);
            clients.SetupGet(c => c.All).Returns(allProxy.Object);

            return (new SignalRTestService(hubContext.Object), clients, clientProxy, groupProxy, allProxy);
        }

        [Fact]
        public async Task SendTestMessageAsync_WithConnectionId_SendsToClientOnly()
        {
            var (service, clients, clientProxy, groupProxy, allProxy) = CreateService();
            clients.Setup(c => c.Client("conn-1")).Returns(clientProxy.Object);
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);

            await service.SendTestMessageAsync("conn-1", null, "hello", CancellationToken.None);

            clientProxy.Verify(p => p.SendCoreAsync(
                "TestMessage",
                It.Is<object?[]>(args => args.Length == 1 && GetStringProperty(args[0], "Message") == "hello"),
                It.IsAny<CancellationToken>()), Times.Once);
            groupProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
            allProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SendTestMessageAsync_WithGroupName_SendsToGroup()
        {
            var (service, clients, clientProxy, groupProxy, allProxy) = CreateService();
            clients.Setup(c => c.Client(It.IsAny<string>())).Returns(clientProxy.Object);
            clients.Setup(c => c.Group("group-1")).Returns(groupProxy.Object);

            await service.SendTestMessageAsync(null, "group-1", "group-msg", CancellationToken.None);

            groupProxy.Verify(p => p.SendCoreAsync(
                "TestMessage",
                It.Is<object?[]>(args => args.Length == 1 && GetStringProperty(args[0], "Message") == "group-msg"),
                It.IsAny<CancellationToken>()), Times.Once);
            clientProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
            allProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SendTestMessageAsync_NoTarget_BroadcastsToAll()
        {
            var (service, clients, clientProxy, groupProxy, allProxy) = CreateService();
            clients.Setup(c => c.Client(It.IsAny<string>())).Returns(clientProxy.Object);
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);

            await service.SendTestMessageAsync(null, null, null, CancellationToken.None);

            allProxy.Verify(p => p.SendCoreAsync(
                "TestMessage",
                It.Is<object?[]>(args => args.Length == 1 && GetStringProperty(args[0], "Source") == "API Health Check"),
                It.IsAny<CancellationToken>()), Times.Once);
            clientProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
            groupProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task TestDiscoveryGroupAsync_SendsProgressFoundAndCompletion()
        {
            var (service, clients, clientProxy, groupProxy, _) = CreateService();
            clients.Setup(c => c.Group("discovery-session-123")).Returns(groupProxy.Object);

            await service.TestDiscoveryGroupAsync("session-123", delayBetweenMessages: false, CancellationToken.None);

            groupProxy.Verify(p => p.SendCoreAsync(
                "DiscoveryProgress",
                It.Is<object?[]>(args => args.Length == 1 && GetStringProperty(args[0], "SessionId") == "session-123"),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce);

            groupProxy.Verify(p => p.SendCoreAsync(
                "DiscoveryPrinterFound",
                It.Is<object?[]>(args => args.Length == 1 && GetStringProperty(args[0], "SessionId") == "session-123"),
                It.IsAny<CancellationToken>()), Times.Once);

            groupProxy.Verify(p => p.SendCoreAsync(
                "DiscoveryCompleted",
                It.Is<object?[]>(args => args.Length == 1 && GetStringProperty(args[0], "SessionId") == "session-123"),
                It.IsAny<CancellationToken>()), Times.Once);

            groupProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Exactly(6));
        }

        [Fact]
        public void GetConnectionStats_ReturnsExpectedMetadata()
        {
            var (service, _, _, _, _) = CreateService();

            dynamic stats = service.GetConnectionStats();

            Assert.Equal(nameof(PrinterHub), (string)stats.HubName);
            var availableMethods = ((object[])stats.AvailableMethods).Select(x => (string)x).ToArray();
            Assert.Contains("TestMessage", availableMethods);
            Assert.Contains("DiscoveryProgress", availableMethods);
            Assert.Equal("Hub context available and functional", (string)stats.HealthStatus);
        }

        private static string? GetStringProperty(object? obj, string propertyName)
        {
            if (obj == null)
            {
                return null;
            }

            var property = obj.GetType().GetProperty(propertyName);
            return property?.GetValue(obj) as string;
        }
    }
}

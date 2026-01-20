using System;
using System.Collections.Generic;
using Farm.Backend.Plugin.Core;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers
{
    /// <summary>
    /// Focused unit tests for GcodeHarvestService file discovery capability checks.
    /// Verifies the exact issue: TryGetFileListClient returns true but client doesn't implement ISupportsFileList.
    /// </summary>
    public class FileDiscoveryCapabilityTests
    {
        private readonly Mock<IUnifiedLoggingService> _mockLogger;

        public FileDiscoveryCapabilityTests()
        {
            _mockLogger = new Mock<IUnifiedLoggingService>();
        }

        #region Tests for File List Client Retrieval

        [Fact]
        public void TryGetFileListClient_Moonraker_ShouldReturnClientImplementingISupportsFileList()
        {
            // Arrange
            Mock<IBackendPluginRegistry> mockRegistry = CreateMockRegistry();
            IBackendClientFactory clientFactory = CreateClientFactory();
            var factory = new BackendCapabilityFactory(clientFactory, _mockLogger.Object, mockRegistry.Object);

            // Act
            bool result = factory.TryGetFileListClient(PrinterBackend.Moonraker, out IBackendClient? client);

            // Assert
            Assert.True(result, "TryGetFileListClient should return true for Moonraker");
            Assert.NotNull(client);
            Assert.True(client is ISupportsFileList,
                $"Client must implement ISupportsFileList. Actual type: {client.GetType().FullName}. " +
                $"Interfaces: {string.Join(", ", client.GetType().GetInterfaces().Select(i => i.Name))}");
        }

        [Fact]
        public void TryGetFileListClient_PrusaLink_ShouldReturnClientImplementingISupportsFileList()
        {
            // Arrange
            Mock<IBackendPluginRegistry> mockRegistry = CreateMockRegistry();
            IBackendClientFactory clientFactory = CreateClientFactory();
            var factory = new BackendCapabilityFactory(clientFactory, _mockLogger.Object, mockRegistry.Object);

            // Act
            bool result = factory.TryGetFileListClient(PrinterBackend.PrusaLink, out IBackendClient? client);

            // Assert
            Assert.True(result, "TryGetFileListClient should return true for PrusaLink");
            Assert.NotNull(client);
            Assert.True(client is ISupportsFileList,
                $"PrusaLink client must implement ISupportsFileList. Actual type: {client.GetType().FullName}");
        }

        [Fact]
        public void TryGetFileListClient_OctoPrint_ShouldReturnClientImplementingISupportsFileList()
        {
            // Arrange
            Mock<IBackendPluginRegistry> mockRegistry = CreateMockRegistry();
            IBackendClientFactory clientFactory = CreateClientFactory();
            var factory = new BackendCapabilityFactory(clientFactory, _mockLogger.Object, mockRegistry.Object);

            // Act
            bool result = factory.TryGetFileListClient(PrinterBackend.OctoPrint, out IBackendClient? client);

            // Assert
            Assert.True(result, "TryGetFileListClient should return true for OctoPrint");
            Assert.NotNull(client);
            Assert.True(client is ISupportsFileList,
                $"OctoPrint client must implement ISupportsFileList. Actual type: {client.GetType().FullName}");
        }

        [Fact]
        public void TryGetFileListClient_SDCP_ShouldReturnClientImplementingISupportsFileList()
        {
            // Arrange
            Mock<IBackendPluginRegistry> mockRegistry = CreateMockRegistry();
            IBackendClientFactory clientFactory = CreateClientFactory();
            var factory = new BackendCapabilityFactory(clientFactory, _mockLogger.Object, mockRegistry.Object);

            // Act
            bool result = factory.TryGetFileListClient(PrinterBackend.SDCP, out IBackendClient? client);

            // Assert
            Assert.True(result, "TryGetFileListClient should return true for SDCP");
            Assert.NotNull(client);
            Assert.True(client is ISupportsFileList,
                $"SDCP client must implement ISupportsFileList. Actual type: {client.GetType().FullName}");
        }

        #endregion

        #region Tests for Capability Cache Consistency

        [Fact]
        public void CapabilitiesCache_ShouldMatchClientImplementation()
        {
            // Arrange
            Mock<IBackendPluginRegistry> mockRegistry = CreateMockRegistry();
            IBackendClientFactory clientFactory = CreateClientFactory();
            var factory = new BackendCapabilityFactory(clientFactory, _mockLogger.Object, mockRegistry.Object);

            // Act & Assert - For each backend, if factory says it has capability, client must implement it
            VerifyCapabilityConsistency(factory, clientFactory, PrinterBackend.Moonraker, typeof(ISupportsFileList));
            VerifyCapabilityConsistency(factory, clientFactory, PrinterBackend.PrusaLink, typeof(ISupportsFileList));
            VerifyCapabilityConsistency(factory, clientFactory, PrinterBackend.OctoPrint, typeof(ISupportsFileList));
            VerifyCapabilityConsistency(factory, clientFactory, PrinterBackend.SDCP, typeof(ISupportsFileList));
        }

        [Fact]
        public void ClientFactory_ShouldReturnSameClientTypeEachTime()
        {
            // Arrange
            IBackendClientFactory clientFactory = CreateClientFactory();

            // Act - Get client multiple times
            IBackendClient client1 = clientFactory.GetClient(PrinterBackend.Moonraker);
            IBackendClient client2 = clientFactory.GetClient(PrinterBackend.Moonraker);

            // Assert - Should return clients of same type (though not necessarily same instance)
            Assert.Equal(client1.GetType(), client2.GetType());
            Assert.True(client1 is ISupportsFileList);
            Assert.True(client2 is ISupportsFileList);
        }

        #endregion

        #region Tests for Capability Detection Chain

        [Fact]
        public void CapabilityDetection_ShouldUsePluginRegistryWhenAvailable()
        {
            // Arrange
            Mock<IBackendPluginRegistry> mockRegistry = CreateMockRegistry();
            mockRegistry.Setup(r => r.IsRegistered("moonraker")).Returns(true);

            IBackendClientFactory clientFactory = CreateClientFactory();
            var factory = new BackendCapabilityFactory(clientFactory, _mockLogger.Object, mockRegistry.Object);

            // Act
            bool hasCapability = factory.TryGetFileListClient(PrinterBackend.Moonraker, out _);

            // Assert
            Assert.True(hasCapability);
            // Verify plugin registry was queried
            mockRegistry.Verify(r => r.IsRegistered(It.IsAny<string>()), Times.AtLeastOnce);
        }

        [Fact]
        public void AllBackends_ShouldSupportFileListCapability()
        {
            // Arrange
            Mock<IBackendPluginRegistry> mockRegistry = CreateMockRegistry();
            IBackendClientFactory clientFactory = CreateClientFactory();
            var factory = new BackendCapabilityFactory(clientFactory, _mockLogger.Object, mockRegistry.Object);

            // Act & Assert - Every backend should support file listing
            PrinterBackend[] backends = new[] { PrinterBackend.Moonraker, PrinterBackend.PrusaLink, PrinterBackend.OctoPrint, PrinterBackend.SDCP };

            foreach (PrinterBackend backend in backends)
            {
                bool result = factory.TryGetFileListClient(backend, out IBackendClient? client);
                Assert.True(result, $"{backend} should support file listing");
                Assert.NotNull(client);
                Assert.True(client is ISupportsFileList,
                    $"{backend} client must implement ISupportsFileList");
            }
        }

        #endregion

        #region Helper Methods

        private IBackendClientFactory CreateClientFactory()
        {
            // Create mock backend clients that implement required interfaces
            var moonrakerClient = new Mock<IMoonrakerClient>();
            moonrakerClient.As<ISupportsFileList>();
            moonrakerClient.As<ISupportsFileDownload>();
            moonrakerClient.As<ISupportsStartPrint>();
            moonrakerClient.As<ISupportsControlOperations>();
            moonrakerClient.As<ISupportsCamera>();
            moonrakerClient.As<ISupportsFileMetadata>();
            moonrakerClient.As<ISupportsMovement>();
            moonrakerClient.As<ISupportsTemperatureControl>();
            moonrakerClient.As<ISupportsPrinterInformation>();

            var prusaLinkClient = new Mock<IPrusaLinkClient>();
            prusaLinkClient.As<ISupportsFileList>();
            prusaLinkClient.As<ISupportsFileDownload>();
            prusaLinkClient.As<ISupportsFileUpload>();
            prusaLinkClient.As<ISupportsStartPrint>();
            prusaLinkClient.As<ISupportsCamera>();
            prusaLinkClient.As<ISupportsPrinterInformation>();

            var octoPrintClient = new Mock<IOctoPrintClient>();
            octoPrintClient.As<ISupportsFileDownload>();
            octoPrintClient.As<ISupportsFileList>();
            octoPrintClient.As<ISupportsFileUpload>();
            octoPrintClient.As<ISupportsCamera>();
            octoPrintClient.As<ISupportsPrinterInformation>();

            var sdcpClient = new Mock<ISdcpClient>();
            sdcpClient.As<ISupportsFileList>();
            sdcpClient.As<ISupportsFileDownload>();
            sdcpClient.As<ISupportsControlOperations>();

            // Create mock factory that returns the mock clients
            var mockFactory = new Mock<IBackendClientFactory>();
            mockFactory.Setup(f => f.GetClient(PrinterBackend.Moonraker)).Returns(moonrakerClient.Object);
            mockFactory.Setup(f => f.GetClient(PrinterBackend.PrusaLink)).Returns(prusaLinkClient.Object);
            mockFactory.Setup(f => f.GetClient(PrinterBackend.SDCP)).Returns(sdcpClient.Object);
            mockFactory.Setup(f => f.GetClient(PrinterBackend.OctoPrint)).Returns(octoPrintClient.Object);

            return mockFactory.Object;
        }

        private Mock<IBackendPluginRegistry> CreateMockRegistry()
        {
            var mockRegistry = new Mock<IBackendPluginRegistry>();

            // Setup all backends as registered
            mockRegistry.Setup(r => r.IsRegistered("moonraker")).Returns(true);
            mockRegistry.Setup(r => r.IsRegistered("prusalink")).Returns(true);
            mockRegistry.Setup(r => r.IsRegistered("octoprint")).Returns(true);
            mockRegistry.Setup(r => r.IsRegistered("sdcp")).Returns(true);

            // Return mock plugins with proper capabilities
            mockRegistry.Setup(r => r.GetPlugin("moonraker"))
                .Returns(CreateMockPlugin("moonraker", new[]
                {
                    typeof(ISupportsFileList),
                    typeof(ISupportsFileDownload),
                    typeof(ISupportsStartPrint),
                    typeof(ISupportsControlOperations),
                    typeof(ISupportsCamera),
                    typeof(ISupportsFileMetadata),
                    typeof(ISupportsMovement),
                    typeof(ISupportsTemperatureControl),
                    typeof(ISupportsPrinterInformation)
                }));

            mockRegistry.Setup(r => r.GetPlugin("prusalink"))
                .Returns(CreateMockPlugin("prusalink", new[]
                {
                    typeof(ISupportsFileList),
                    typeof(ISupportsFileDownload),
                    typeof(ISupportsFileUpload),
                    typeof(ISupportsStartPrint),
                    typeof(ISupportsCamera),
                    typeof(ISupportsPrinterInformation)
                }));

            mockRegistry.Setup(r => r.GetPlugin("octoprint"))
                .Returns(CreateMockPlugin("octoprint", new[]
                {
                    typeof(ISupportsFileDownload),
                    typeof(ISupportsFileList),
                    typeof(ISupportsFileUpload),
                    typeof(ISupportsCamera),
                    typeof(ISupportsPrinterInformation)
                }));

            mockRegistry.Setup(r => r.GetPlugin("sdcp"))
                .Returns(CreateMockPlugin("sdcp", new[]
                {
                    typeof(ISupportsFileList),
                    typeof(ISupportsFileDownload),
                    typeof(ISupportsControlOperations)
                }));

            return mockRegistry;
        }

        private IBackendClientPlugin CreateMockPlugin(string backendType, Type[] capabilities)
        {
            var plugin = new Mock<IBackendClientPlugin>();
            plugin.Setup(p => p.BackendType).Returns(backendType);
            plugin.Setup(p => p.DisplayName).Returns(backendType);
            plugin.Setup(p => p.Description).Returns($"{backendType} plugin");
            plugin.Setup(p => p.GetCapabilities()).Returns(capabilities);
            return plugin.Object;
        }

        private void VerifyCapabilityConsistency(
            IBackendCapabilityFactory factory,
            IBackendClientFactory clientFactory,
            PrinterBackend backend,
            Type requiredCapability)
        {
            // If factory says backend has capability, the client must implement it
            if (factory.TryGetFileListClient(backend, out IBackendClient? client))
            {
                Assert.NotNull(client);
                Assert.True(client is ISupportsFileList,
                    $"Backend {backend}: Factory says it has FileList capability, but client ({client.GetType().FullName}) doesn't implement ISupportsFileList");
            }
        }

        #endregion
    }
}

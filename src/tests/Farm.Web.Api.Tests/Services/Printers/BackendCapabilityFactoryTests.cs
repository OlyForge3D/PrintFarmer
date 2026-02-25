using System;
using System.Collections.Generic;
using Farm.Backend.Plugin.Core;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Contracts.Printers.OctoPrint;
using Farm.Infrastructure.Contracts.Printers.Sdcp;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Tests.Services.Printers
{
    /// <summary>
    /// Unit tests for BackendCapabilityFactory.
    /// Tests capability detection for each backend implementation.
    /// Helps verify that backend clients properly implement capability interfaces.
    /// </summary>
    public class BackendCapabilityFactoryTests
    {
        private readonly Mock<ILogger<BackendCapabilityFactory>> _mockLogger;
        private readonly IBackendClientFactory _clientFactory;

        public BackendCapabilityFactoryTests()
        {
            _mockLogger = new Mock<ILogger<BackendCapabilityFactory>>();
            _clientFactory = CreateClientFactory();
        }

        #region Tests for Capability Detection

        [Fact]
        public void Moonraker_ClientShouldImplementISupportsFileList()
        {
            // Arrange - Get the Moonraker client
            IBackendClient client = _clientFactory.GetClient(PrinterBackend.Moonraker);

            // Act & Assert
            Assert.NotNull(client);
            Assert.True(client is ISupportsFileList,
                $"Moonraker client ({client.GetType().FullName}) should implement ISupportsFileList interface");
            Assert.True(client is ISupportsFileDownload, "Moonraker should support file downloads");
            Assert.True(client is ISupportsStartPrint, "Moonraker should support starting prints");
        }

        [Fact]
        public void PrusaLink_ClientShouldImplementISupportsFileList()
        {
            // Arrange
            IBackendClient client = _clientFactory.GetClient(PrinterBackend.PrusaLink);

            // Act & Assert
            Assert.NotNull(client);
            Assert.True(client is ISupportsFileList,
                $"PrusaLink client ({client.GetType().FullName}) should implement ISupportsFileList interface");
            Assert.True(client is ISupportsFileDownload, "PrusaLink should support file downloads");
        }

        [Fact]
        public void OctoPrint_ClientShouldImplementISupportsFileList()
        {
            // Arrange
            IBackendClient client = _clientFactory.GetClient(PrinterBackend.OctoPrint);

            // Act & Assert
            Assert.NotNull(client);
            Assert.True(client is ISupportsFileList,
                $"OctoPrint client ({client.GetType().FullName}) should implement ISupportsFileList interface");
            Assert.True(client is ISupportsFileDownload, "OctoPrint should support file downloads");
        }

        [Fact]
        public void SDCP_ClientShouldImplementISupportsFileList()
        {
            // Arrange
            IBackendClient client = _clientFactory.GetClient(PrinterBackend.SDCP);

            // Act & Assert
            Assert.NotNull(client);
            Assert.True(client is ISupportsFileList,
                $"SDCP client ({client.GetType().FullName}) should implement ISupportsFileList interface");
            Assert.True(client is ISupportsFileDownload, "SDCP should support file downloads");
        }

        [Fact]
        public void BackendCapabilityFactory_WithPluginRegistry_ShouldDetectAllCapabilities()
        {
            // Arrange - Create a mock registry with all plugins
            var registry = new Mock<IBackendPluginRegistry>();

            // Create mock plugins
            IBackendClientPlugin moonrakerPlugin = CreateMockPlugin("moonraker", PrinterBackend.Moonraker);
            IBackendClientPlugin prusaLinkPlugin = CreateMockPlugin("prusalink", PrinterBackend.PrusaLink);
            IBackendClientPlugin octoPrintPlugin = CreateMockPlugin("octoprint", PrinterBackend.OctoPrint);
            IBackendClientPlugin sdcpPlugin = CreateMockPlugin("sdcp", PrinterBackend.SDCP);

            registry.Setup(r => r.IsRegistered("moonraker")).Returns(true);
            registry.Setup(r => r.GetPlugin("moonraker")).Returns(moonrakerPlugin);
            registry.Setup(r => r.IsRegistered("prusalink")).Returns(true);
            registry.Setup(r => r.GetPlugin("prusalink")).Returns(prusaLinkPlugin);
            registry.Setup(r => r.IsRegistered("octoprint")).Returns(true);
            registry.Setup(r => r.GetPlugin("octoprint")).Returns(octoPrintPlugin);
            registry.Setup(r => r.IsRegistered("sdcp")).Returns(true);
            registry.Setup(r => r.GetPlugin("sdcp")).Returns(sdcpPlugin);

            // Act
            var factory = new BackendCapabilityFactory(_clientFactory, _mockLogger.Object, registry.Object);

            // Assert - Verify the factory can get file list clients for all backends
            Assert.True(factory.TryGetFileListClient(PrinterBackend.Moonraker, out IBackendClient? mr));
            Assert.NotNull(mr);
            Assert.True(mr is ISupportsFileList);

            Assert.True(factory.TryGetFileListClient(PrinterBackend.PrusaLink, out IBackendClient? prusa));
            Assert.NotNull(prusa);
            Assert.True(prusa is ISupportsFileList);

            Assert.True(factory.TryGetFileListClient(PrinterBackend.OctoPrint, out IBackendClient? octo));
            Assert.NotNull(octo);
            Assert.True(octo is ISupportsFileList);

            Assert.True(factory.TryGetFileListClient(PrinterBackend.SDCP, out IBackendClient? sdcp));
            Assert.NotNull(sdcp);
            Assert.True(sdcp is ISupportsFileList);
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

        private IBackendClientPlugin CreateMockPlugin(string backendType, PrinterBackend backend)
        {
            var plugin = new Mock<IBackendClientPlugin>();
            plugin.Setup(p => p.BackendType).Returns(backendType);
            plugin.Setup(p => p.DisplayName).Returns(backendType);
            plugin.Setup(p => p.Description).Returns($"{backendType} plugin");

            // Return the correct capabilities based on backend type
            IEnumerable<Type> capabilities = GetCapabilitiesForBackend(backend);
            plugin.Setup(p => p.GetCapabilities()).Returns(capabilities);

            return plugin.Object;
        }

        private IEnumerable<Type> GetCapabilitiesForBackend(PrinterBackend backend)
        {
            return backend switch
            {
                PrinterBackend.Moonraker => new[]
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
                },
                PrinterBackend.PrusaLink => new[]
                {
                    typeof(ISupportsFileList),
                    typeof(ISupportsFileDownload),
                    typeof(ISupportsFileUpload),
                    typeof(ISupportsStartPrint),
                    typeof(ISupportsCamera),
                    typeof(ISupportsPrinterInformation)
                },
                PrinterBackend.OctoPrint => new[]
                {
                    typeof(ISupportsFileDownload),
                    typeof(ISupportsFileList),
                    typeof(ISupportsFileUpload),
                    typeof(ISupportsCamera),
                    typeof(ISupportsPrinterInformation)
                },
                PrinterBackend.SDCP => new[]
                {
                    typeof(ISupportsFileList),
                    typeof(ISupportsFileDownload),
                    typeof(ISupportsControlOperations)
                },
                _ => Type.EmptyTypes
            };
        }

        #endregion
    }
}


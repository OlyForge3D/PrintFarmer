using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Core;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers
{
    /// <summary>
    /// Unit tests for IPrinterStatusClient implementations (Moonraker, PrusaLink, SDCP, OctoPrint).
    /// Tests the abstraction layer for backend-specific printer status retrieval.
    /// </summary>
    public class PrinterStatusClientTests
    {
        #region Helper Methods

        private static PrinterStatusClientFactory CreateFactoryWithClients(
            IMoonrakerClient moonrakerClient,
            IPrusaLinkClient prusaLinkClient,
            ISdcpClient sdcpClient,
            IOctoPrintClient octoPrintClient,
            ICircuitBreakerService circuitBreaker,
            IUnifiedLoggingService logger)
        {
            // Create status clients directly
            var moonrakerStatusClient = new MoonrakerStatusClient(moonrakerClient, circuitBreaker, logger);
            var prusaLinkStatusClient = new PrusaLinkStatusClient(prusaLinkClient, circuitBreaker, logger);
            var sdcpStatusClient = new SdcpStatusClient(sdcpClient, circuitBreaker, logger);
            var octoPrintStatusClient = new OctoPrintStatusClient(octoPrintClient, circuitBreaker, logger);

            // Create a service collection and register all dependencies
            var services = new ServiceCollection();
            services.AddSingleton(logger);
            services.AddSingleton(circuitBreaker);
            services.AddSingleton(moonrakerClient);
            services.AddSingleton(prusaLinkClient);
            services.AddSingleton(sdcpClient);
            services.AddSingleton(octoPrintClient);
            
            // Register status clients - plugins would register these by their concrete type or a factory interface
            services.AddSingleton(moonrakerStatusClient);
            services.AddSingleton(prusaLinkStatusClient);
            services.AddSingleton(sdcpStatusClient);
            services.AddSingleton(octoPrintStatusClient);

            var serviceProvider = services.BuildServiceProvider();

            // Create mock extended plugins that will return the correct status client types
            var mockMoonrakerPlugin = new Mock<IExtendedBackendPlugin>();
            mockMoonrakerPlugin.Setup(p => p.BackendType).Returns("Moonraker");
            mockMoonrakerPlugin.Setup(p => p.StatusClientType).Returns(typeof(MoonrakerStatusClient));
            mockMoonrakerPlugin.Setup(p => p.StatusClientInterfaceType).Returns(typeof(MoonrakerStatusClient));
            
            var mockPrusaLinkPlugin = new Mock<IExtendedBackendPlugin>();
            mockPrusaLinkPlugin.Setup(p => p.BackendType).Returns("PrusaLink");
            mockPrusaLinkPlugin.Setup(p => p.StatusClientType).Returns(typeof(PrusaLinkStatusClient));
            mockPrusaLinkPlugin.Setup(p => p.StatusClientInterfaceType).Returns(typeof(PrusaLinkStatusClient));
            
            var mockSdcpPlugin = new Mock<IExtendedBackendPlugin>();
            mockSdcpPlugin.Setup(p => p.BackendType).Returns("SDCP");
            mockSdcpPlugin.Setup(p => p.StatusClientType).Returns(typeof(SdcpStatusClient));
            mockSdcpPlugin.Setup(p => p.StatusClientInterfaceType).Returns(typeof(SdcpStatusClient));
            
            var mockOctoPrintPlugin = new Mock<IExtendedBackendPlugin>();
            mockOctoPrintPlugin.Setup(p => p.BackendType).Returns("OctoPrint");
            mockOctoPrintPlugin.Setup(p => p.StatusClientType).Returns(typeof(OctoPrintStatusClient));
            mockOctoPrintPlugin.Setup(p => p.StatusClientInterfaceType).Returns(typeof(OctoPrintStatusClient));

            // Create mock plugin registry that returns all extended plugins
            var mockPluginRegistry = new Mock<IBackendPluginRegistry>();
            mockPluginRegistry.Setup(r => r.GetAllExtendedPlugins())
                .Returns(new[]
                {
                    mockMoonrakerPlugin.Object,
                    mockPrusaLinkPlugin.Object,
                    mockSdcpPlugin.Object,
                    mockOctoPrintPlugin.Object
                });

            // Create the factory with mocked plugin registry and service provider
            var factory = new PrinterStatusClientFactory(serviceProvider, mockPluginRegistry.Object, logger);

            return factory;
        }

        private static (Mock<IMoonrakerClient> moonraker, Mock<ICircuitBreakerService> breaker, Mock<IUnifiedLoggingService> logger)
            CreateMoonrakerMocks()
        {
            return (new Mock<IMoonrakerClient>(), new Mock<ICircuitBreakerService>(), new Mock<IUnifiedLoggingService>());
        }

        private static (Mock<IPrusaLinkClient> prusa, Mock<ICircuitBreakerService> breaker, Mock<IUnifiedLoggingService> logger)
            CreatePrusaLinkMocks()
        {
            return (new Mock<IPrusaLinkClient>(), new Mock<ICircuitBreakerService>(), new Mock<IUnifiedLoggingService>());
        }

        private static (Mock<ISdcpClient> sdcp, Mock<ICircuitBreakerService> breaker, Mock<IUnifiedLoggingService> logger)
            CreateSdcpMocks()
        {
            return (new Mock<ISdcpClient>(), new Mock<ICircuitBreakerService>(), new Mock<IUnifiedLoggingService>());
        }

        private static (Mock<IOctoPrintClient> octoprint, Mock<ICircuitBreakerService> breaker, Mock<IUnifiedLoggingService> logger)
            CreateOctoPrintMocks()
        {
            return (new Mock<IOctoPrintClient>(), new Mock<ICircuitBreakerService>(), new Mock<IUnifiedLoggingService>());
        }

        #endregion

        #region Moonraker Status Client Tests

        [Fact]
        public void MoonrakerStatusClient_SupportsCorrectBackend()
        {
            // Arrange
            var (mockMoonraker, mockBreaker, mockLogger) = CreateMoonrakerMocks();
            var client = new MoonrakerStatusClient(mockMoonraker.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            Assert.Equal(PrinterBackend.Moonraker, client.SupportedBackend);
        }

        [Fact]
        public async Task MoonrakerStatusClient_GetPrinterStatusAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var (mockMoonraker, mockBreaker, mockLogger) = CreateMoonrakerMocks();
            var client = new MoonrakerStatusClient(mockMoonraker.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.GetPrinterStatusAsync(null!, CancellationToken.None));
        }

        [Fact]
        public async Task MoonrakerStatusClient_GetCameraStreamUrlAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var (mockMoonraker, mockBreaker, mockLogger) = CreateMoonrakerMocks();
            var client = new MoonrakerStatusClient(mockMoonraker.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.GetCameraStreamUrlAsync(null!, CancellationToken.None));
        }

        [Fact]
        public async Task MoonrakerStatusClient_IsCameraAvailableAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var (mockMoonraker, mockBreaker, mockLogger) = CreateMoonrakerMocks();
            var client = new MoonrakerStatusClient(mockMoonraker.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.IsCameraAvailableAsync(null!, CancellationToken.None));
        }

        [Fact]
        public async Task MoonrakerStatusClient_GetPrinterDtoAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var (mockMoonraker, mockBreaker, mockLogger) = CreateMoonrakerMocks();
            var client = new MoonrakerStatusClient(mockMoonraker.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.GetPrinterDtoAsync(null!, CancellationToken.None));
        }

        #endregion

        #region PrusaLink Status Client Tests

        [Fact]
        public void PrusaLinkStatusClient_SupportsCorrectBackend()
        {
            // Arrange
            var (mockPrusa, mockBreaker, mockLogger) = CreatePrusaLinkMocks();
            var client = new PrusaLinkStatusClient(mockPrusa.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            Assert.Equal(PrinterBackend.PrusaLink, client.SupportedBackend);
        }

        [Fact]
        public async Task PrusaLinkStatusClient_GetPrinterStatusAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var (mockPrusa, mockBreaker, mockLogger) = CreatePrusaLinkMocks();
            var client = new PrusaLinkStatusClient(mockPrusa.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.GetPrinterStatusAsync(null!, CancellationToken.None));
        }

        [Fact]
        public async Task PrusaLinkStatusClient_GetCameraStreamUrlAsync_ReturnsNull()
        {
            // Arrange
            var printer = new Printer
            {
                Id = Guid.NewGuid(),
                Name = "PrusaLink Printer",
                Backend = (int)PrinterBackend.PrusaLink,
                ServerUrl = "http://prusa.local"
            };

            var (mockPrusa, mockBreaker, mockLogger) = CreatePrusaLinkMocks();
            var client = new PrusaLinkStatusClient(mockPrusa.Object, mockBreaker.Object, mockLogger.Object);

            // Act
            var result = await client.GetCameraStreamUrlAsync(printer, CancellationToken.None);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task PrusaLinkStatusClient_GetCameraSnapshotUrlAsync_ReturnsNull()
        {
            // Arrange
            var printer = new Printer
            {
                Id = Guid.NewGuid(),
                Name = "PrusaLink Printer",
                Backend = (int)PrinterBackend.PrusaLink,
                ServerUrl = "http://prusa.local"
            };

            var (mockPrusa, mockBreaker, mockLogger) = CreatePrusaLinkMocks();
            var client = new PrusaLinkStatusClient(mockPrusa.Object, mockBreaker.Object, mockLogger.Object);

            // Act
            var result = await client.GetCameraSnapshotUrlAsync(printer, CancellationToken.None);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task PrusaLinkStatusClient_IsCameraAvailableAsync_ReturnsFalse()
        {
            // Arrange
            var printer = new Printer
            {
                Id = Guid.NewGuid(),
                Name = "PrusaLink Printer",
                Backend = (int)PrinterBackend.PrusaLink,
                ServerUrl = "http://prusa.local"
            };

            var (mockPrusa, mockBreaker, mockLogger) = CreatePrusaLinkMocks();
            var client = new PrusaLinkStatusClient(mockPrusa.Object, mockBreaker.Object, mockLogger.Object);

            // Act
            var result = await client.IsCameraAvailableAsync(printer, CancellationToken.None);

            // Assert
            Assert.False(result);
        }

        #endregion

        #region SDCP Status Client Tests

        [Fact]
        public void SdcpStatusClient_SupportsCorrectBackend()
        {
            // Arrange
            var (mockSdcp, mockBreaker, mockLogger) = CreateSdcpMocks();
            var client = new SdcpStatusClient(mockSdcp.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            Assert.Equal(PrinterBackend.SDCP, client.SupportedBackend);
        }

        [Fact]
        public async Task SdcpStatusClient_GetPrinterStatusAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var (mockSdcp, mockBreaker, mockLogger) = CreateSdcpMocks();
            var client = new SdcpStatusClient(mockSdcp.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.GetPrinterStatusAsync(null!, CancellationToken.None));
        }

        #endregion

        #region OctoPrint Status Client Tests

        [Fact]
        public void OctoPrintStatusClient_SupportsCorrectBackend()
        {
            // Arrange
            var (mockOcto, mockBreaker, mockLogger) = CreateOctoPrintMocks();
            var client = new OctoPrintStatusClient(mockOcto.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            Assert.Equal(PrinterBackend.OctoPrint, client.SupportedBackend);
        }

        [Fact]
        public async Task OctoPrintStatusClient_GetPrinterStatusAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var (mockOcto, mockBreaker, mockLogger) = CreateOctoPrintMocks();
            var client = new OctoPrintStatusClient(mockOcto.Object, mockBreaker.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.GetPrinterStatusAsync(null!, CancellationToken.None));
        }

        [Fact]
        public async Task OctoPrintStatusClient_GetCameraStreamUrlAsync_ReturnsNull()
        {
            // Arrange
            var printer = new Printer
            {
                Id = Guid.NewGuid(),
                Name = "OctoPrint Printer",
                Backend = (int)PrinterBackend.OctoPrint,
                ServerUrl = "http://octoprint.local"
            };

            var (mockOcto, mockBreaker, mockLogger) = CreateOctoPrintMocks();
            var client = new OctoPrintStatusClient(mockOcto.Object, mockBreaker.Object, mockLogger.Object);

            // Act
            var result = await client.GetCameraStreamUrlAsync(printer, CancellationToken.None);

            // Assert
            Assert.Null(result);
        }

        #endregion

        #region Printer Status Client Factory Tests

        [Fact]
        public void PrinterStatusClientFactory_GetStatusClient_ReturnsMoonrakerClient()
        {
            // Arrange
            var mockMoonraker = new Mock<IMoonrakerClient>();
            var mockPrusa = new Mock<IPrusaLinkClient>();
            var mockSdcp = new Mock<ISdcpClient>();
            var mockOcto = new Mock<IOctoPrintClient>();
            var mockCircuitBreaker = new Mock<ICircuitBreakerService>();
            var mockLogger = new Mock<IUnifiedLoggingService>();

            var factory = CreateFactoryWithClients(
                mockMoonraker.Object,
                mockPrusa.Object,
                mockSdcp.Object,
                mockOcto.Object,
                mockCircuitBreaker.Object,
                mockLogger.Object);

            // Act
            var client = factory.GetStatusClient(PrinterBackend.Moonraker);

            // Assert
            Assert.NotNull(client);
            Assert.IsType<MoonrakerStatusClient>(client);
            Assert.Equal(PrinterBackend.Moonraker, client.SupportedBackend);
        }

        [Fact]
        public void PrinterStatusClientFactory_GetStatusClient_ReturnsPrusaLinkClient()
        {
            // Arrange
            var mockMoonraker = new Mock<IMoonrakerClient>();
            var mockPrusa = new Mock<IPrusaLinkClient>();
            var mockSdcp = new Mock<ISdcpClient>();
            var mockOcto = new Mock<IOctoPrintClient>();
            var mockCircuitBreaker = new Mock<ICircuitBreakerService>();
            var mockLogger = new Mock<IUnifiedLoggingService>();

            var factory = CreateFactoryWithClients(
                mockMoonraker.Object,
                mockPrusa.Object,
                mockSdcp.Object,
                mockOcto.Object,
                mockCircuitBreaker.Object,
                mockLogger.Object);

            // Act
            var client = factory.GetStatusClient(PrinterBackend.PrusaLink);

            // Assert
            Assert.NotNull(client);
            Assert.IsType<PrusaLinkStatusClient>(client);
            Assert.Equal(PrinterBackend.PrusaLink, client.SupportedBackend);
        }

        [Fact]
        public void PrinterStatusClientFactory_GetStatusClient_ReturnsSdcpClient()
        {
            // Arrange
            var mockMoonraker = new Mock<IMoonrakerClient>();
            var mockPrusa = new Mock<IPrusaLinkClient>();
            var mockSdcp = new Mock<ISdcpClient>();
            var mockOcto = new Mock<IOctoPrintClient>();
            var mockCircuitBreaker = new Mock<ICircuitBreakerService>();
            var mockLogger = new Mock<IUnifiedLoggingService>();

            var factory = CreateFactoryWithClients(
                mockMoonraker.Object,
                mockPrusa.Object,
                mockSdcp.Object,
                mockOcto.Object,
                mockCircuitBreaker.Object,
                mockLogger.Object);

            // Act
            var client = factory.GetStatusClient(PrinterBackend.SDCP);

            // Assert
            Assert.NotNull(client);
            Assert.IsType<SdcpStatusClient>(client);
            Assert.Equal(PrinterBackend.SDCP, client.SupportedBackend);
        }

        [Fact]
        public void PrinterStatusClientFactory_GetStatusClient_ReturnsOctoPrintClient()
        {
            // Arrange
            var mockMoonraker = new Mock<IMoonrakerClient>();
            var mockPrusa = new Mock<IPrusaLinkClient>();
            var mockSdcp = new Mock<ISdcpClient>();
            var mockOcto = new Mock<IOctoPrintClient>();
            var mockCircuitBreaker = new Mock<ICircuitBreakerService>();
            var mockLogger = new Mock<IUnifiedLoggingService>();

            var factory = CreateFactoryWithClients(
                mockMoonraker.Object,
                mockPrusa.Object,
                mockSdcp.Object,
                mockOcto.Object,
                mockCircuitBreaker.Object,
                mockLogger.Object);

            // Act
            var client = factory.GetStatusClient(PrinterBackend.OctoPrint);

            // Assert
            Assert.NotNull(client);
            Assert.IsType<OctoPrintStatusClient>(client);
            Assert.Equal(PrinterBackend.OctoPrint, client.SupportedBackend);
        }

        [Fact]
        public void PrinterStatusClientFactory_GetStatusClient_WithIntBackendValue_ReturnsMoonrakerClient()
        {
            // Arrange
            var mockMoonraker = new Mock<IMoonrakerClient>();
            var mockPrusa = new Mock<IPrusaLinkClient>();
            var mockSdcp = new Mock<ISdcpClient>();
            var mockOcto = new Mock<IOctoPrintClient>();
            var mockCircuitBreaker = new Mock<ICircuitBreakerService>();
            var mockLogger = new Mock<IUnifiedLoggingService>();

            var factory = CreateFactoryWithClients(
                mockMoonraker.Object,
                mockPrusa.Object,
                mockSdcp.Object,
                mockOcto.Object,
                mockCircuitBreaker.Object,
                mockLogger.Object);

            // Act
            var client = factory.GetStatusClient((int)PrinterBackend.Moonraker);

            // Assert
            Assert.NotNull(client);
            Assert.IsType<MoonrakerStatusClient>(client);
        }

        [Fact]
        public void PrinterStatusClientFactory_IsBackendSupported_ReturnsTrueForSupportedBackends()
        {
            // Arrange
            var mockMoonraker = new Mock<IMoonrakerClient>();
            var mockPrusa = new Mock<IPrusaLinkClient>();
            var mockSdcp = new Mock<ISdcpClient>();
            var mockOcto = new Mock<IOctoPrintClient>();
            var mockCircuitBreaker = new Mock<ICircuitBreakerService>();
            var mockLogger = new Mock<IUnifiedLoggingService>();

            var factory = CreateFactoryWithClients(
                mockMoonraker.Object,
                mockPrusa.Object,
                mockSdcp.Object,
                mockOcto.Object,
                mockCircuitBreaker.Object,
                mockLogger.Object);

            // Act & Assert
            Assert.True(factory.IsBackendSupported(PrinterBackend.Moonraker));
            Assert.True(factory.IsBackendSupported(PrinterBackend.PrusaLink));
            Assert.True(factory.IsBackendSupported(PrinterBackend.SDCP));
            Assert.True(factory.IsBackendSupported(PrinterBackend.OctoPrint));
        }

        [Fact]
        public void PrinterStatusClientFactory_GetStatusClient_WithUnsupportedBackend_ThrowsArgumentException()
        {
            // Arrange
            var mockMoonraker = new Mock<IMoonrakerClient>();
            var mockPrusa = new Mock<IPrusaLinkClient>();
            var mockSdcp = new Mock<ISdcpClient>();
            var mockOcto = new Mock<IOctoPrintClient>();
            var mockCircuitBreaker = new Mock<ICircuitBreakerService>();
            var mockLogger = new Mock<IUnifiedLoggingService>();

            var factory = CreateFactoryWithClients(
                mockMoonraker.Object,
                mockPrusa.Object,
                mockSdcp.Object,
                mockOcto.Object,
                mockCircuitBreaker.Object,
                mockLogger.Object);

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                factory.GetStatusClient((PrinterBackend)999));
        }

        #endregion
    }
}

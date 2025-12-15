using System;
using Farm.Backend.Plugin.Core;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Printers;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class BackendClientFactoryTests
    {
        private readonly Mock<IMoonrakerClient> _moonMock;
        private readonly Mock<IPrusaLinkClient> _prusaMock;
        private readonly Mock<ISdcpClient> _sdcpMock;
        private readonly Mock<IOctoPrintClient> _octoMock;
        private readonly Mock<IUnifiedLoggingService> _loggerMock;

        public BackendClientFactoryTests()
        {
            _moonMock = new Mock<IMoonrakerClient>();
            _prusaMock = new Mock<IPrusaLinkClient>();
            _sdcpMock = new Mock<ISdcpClient>();
            _octoMock = new Mock<IOctoPrintClient>();
            _loggerMock = new Mock<IUnifiedLoggingService>();
        }

        [Fact]
        public void Constructor_WithValidParameters_InitializesSuccessfully()
        {
            // Act
            var factory = CreateFactory();

            // Assert
            Assert.NotNull(factory);
        }

        [Fact]
        public void Constructor_WithNullMoonrakerClient_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new BackendClientFactory(
                null!,
                _prusaMock.Object,
                _sdcpMock.Object,
                _octoMock.Object,
                _loggerMock.Object));
        }

        [Fact]
        public void Constructor_WithNullPrusaLinkClient_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new BackendClientFactory(
                _moonMock.Object,
                null!,
                _sdcpMock.Object,
                _octoMock.Object,
                _loggerMock.Object));
        }

        [Fact]
        public void Constructor_WithNullSdcpClient_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new BackendClientFactory(
                _moonMock.Object,
                _prusaMock.Object,
                null!,
                _octoMock.Object,
                _loggerMock.Object));
        }

        [Fact]
        public void Constructor_WithNullOctoPrintClient_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new BackendClientFactory(
                _moonMock.Object,
                _prusaMock.Object,
                _sdcpMock.Object,
                null!,
                _loggerMock.Object));
        }

        [Fact]
        public void Constructor_WithNullLogger_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new BackendClientFactory(
                _moonMock.Object,
                _prusaMock.Object,
                _sdcpMock.Object,
                _octoMock.Object,
                null!));
        }

        [Fact]
        public void GetClient_WithMoonrakerBackend_ReturnsMoonrakerClient()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            var client = factory.GetClient(PrinterBackend.Moonraker);

            // Assert
            Assert.NotNull(client);
            Assert.IsAssignableFrom<IBackendClient>(client);
            Assert.IsAssignableFrom<IMoonrakerClient>(client);
            Assert.Same(_moonMock.Object, client);
        }

        [Fact]
        public void GetClient_WithPrusaLinkBackend_ReturnsPrusaLinkClient()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            var client = factory.GetClient(PrinterBackend.PrusaLink);

            // Assert
            Assert.NotNull(client);
            Assert.IsAssignableFrom<IBackendClient>(client);
            Assert.IsAssignableFrom<IPrusaLinkClient>(client);
            Assert.Same(_prusaMock.Object, client);
        }

        [Fact]
        public void GetClient_WithSdcpBackend_ReturnsSdcpClient()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            var client = factory.GetClient(PrinterBackend.SDCP);

            // Assert
            Assert.NotNull(client);
            Assert.IsAssignableFrom<IBackendClient>(client);
            Assert.IsAssignableFrom<ISdcpClient>(client);
            Assert.Same(_sdcpMock.Object, client);
        }

        [Fact]
        public void GetClient_WithOctoPrintBackend_ReturnsOctoPrintClient()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            var client = factory.GetClient(PrinterBackend.OctoPrint);

            // Assert
            Assert.NotNull(client);
            Assert.IsAssignableFrom<IBackendClient>(client);
            Assert.IsAssignableFrom<IOctoPrintClient>(client);
            Assert.Same(_octoMock.Object, client);
        }

        [Fact]
        public void GetClient_WithIntegerBackend_ReturnsMoonrakerClient()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            var client = factory.GetClient((int)PrinterBackend.Moonraker);

            // Assert
            Assert.NotNull(client);
            Assert.IsAssignableFrom<IMoonrakerClient>(client);
        }

        [Fact]
        public void GetClient_WithIntegerBackend_ReturnsPrusaLinkClient()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            var client = factory.GetClient((int)PrinterBackend.PrusaLink);

            // Assert
            Assert.NotNull(client);
            Assert.IsAssignableFrom<IPrusaLinkClient>(client);
        }

        [Fact]
        public void GetClient_WithInvalidBackend_ThrowsArgumentException()
        {
            // Arrange
            var factory = CreateFactory();

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => factory.GetClient((PrinterBackend)999));
            Assert.Contains("Unsupported printer backend", ex.Message);
        }

        [Fact]
        public void GetClient_WithInvalidIntegerBackend_ThrowsArgumentException()
        {
            // Arrange
            var factory = CreateFactory();

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => factory.GetClient(999));
            Assert.Contains("Unsupported printer backend", ex.Message);
        }

        [Fact]
        public void IsBackendSupported_WithMoonraker_ReturnsTrue()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            bool isSupported = factory.IsBackendSupported(PrinterBackend.Moonraker);

            // Assert
            Assert.True(isSupported);
        }

        [Fact]
        public void IsBackendSupported_WithPrusaLink_ReturnsTrue()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            bool isSupported = factory.IsBackendSupported(PrinterBackend.PrusaLink);

            // Assert
            Assert.True(isSupported);
        }

        [Fact]
        public void IsBackendSupported_WithSdcp_ReturnsTrue()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            bool isSupported = factory.IsBackendSupported(PrinterBackend.SDCP);

            // Assert
            Assert.True(isSupported);
        }

        [Fact]
        public void IsBackendSupported_WithOctoPrint_ReturnsTrue()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            bool isSupported = factory.IsBackendSupported(PrinterBackend.OctoPrint);

            // Assert
            Assert.True(isSupported);
        }

        [Fact]
        public void IsBackendSupported_WithInvalidBackend_ReturnsFalse()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            bool isSupported = factory.IsBackendSupported((PrinterBackend)999);

            // Assert
            Assert.False(isSupported);
        }

        [Fact]
        public void GetClient_CalledMultipleTimes_ReturnsSameInstance()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            var client1 = factory.GetClient(PrinterBackend.Moonraker);
            var client2 = factory.GetClient(PrinterBackend.Moonraker);

            // Assert
            Assert.Same(client1, client2);
        }

        [Fact]
        public void GetClient_WithDifferentBackends_ReturnsDifferentInstances()
        {
            // Arrange
            var factory = CreateFactory();

            // Act
            var moonClient = factory.GetClient(PrinterBackend.Moonraker);
            var prusaClient = factory.GetClient(PrinterBackend.PrusaLink);

            // Assert
            Assert.NotSame(moonClient, prusaClient);
        }

        private BackendClientFactory CreateFactory()
        {
            return new BackendClientFactory(
                _moonMock.Object,
                _prusaMock.Object,
                _sdcpMock.Object,
                _octoMock.Object,
                _loggerMock.Object);
        }
    }
}

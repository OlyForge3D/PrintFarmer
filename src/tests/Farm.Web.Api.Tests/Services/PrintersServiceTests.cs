using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Catalog;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Printers;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class PrintersServiceTests
    {
        [Fact]
        public async Task GetPrinterAsync_ReturnsPrinter_WhenFound()
        {
            Guid id = Guid.NewGuid();
            Mock<IPrintersRepository> repoMock = new Mock<IPrintersRepository>();
            Printer expected = new Printer { Id = id, Name = "TestPrinter" };
            _ = repoMock.Setup(r => r.FindByIdWithIncludesAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            Mock<IMoonrakerClient> moonMock = new Mock<IMoonrakerClient>();
            Mock<IPrusaLinkClient> prusaMock = new Mock<IPrusaLinkClient>();
            Mock<ISdcpClient> sdcpMock = new Mock<ISdcpClient>();
            Mock<IOctoPrintClient> octoMock = new Mock<IOctoPrintClient>();
            Mock<ICircuitBreakerService> circuitMock = new Mock<ICircuitBreakerService>();
            Mock<IPrinterCapabilityDiscoveryService> capDiscoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IDefaultCatalogService> defaultCatalogMock = new Mock<IDefaultCatalogService>();
            Mock<ICatalogService> catalogMock = new Mock<ICatalogService>();
            Mock<IHttpClientFactory> httpClientFactoryMock = new Mock<IHttpClientFactory>();
            Mock<INetworkUrlRewriteService> urlRewriterMock = new Mock<INetworkUrlRewriteService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            Mock<IHubContext<PrinterHub>> hubContextMock = new Mock<IHubContext<PrinterHub>>();

            // Provide a real AutoMapper instance for mapping dependencies
            MapperConfiguration mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile(new Mapping.PrinterMappingProfile()));
            IMapper mapper = mapperConfig.CreateMapper();

            PrintersService svc = new PrintersService(repoMock.Object, moonMock.Object, prusaMock.Object, sdcpMock.Object, octoMock.Object, circuitMock.Object, capDiscoveryMock.Object, defaultCatalogMock.Object, catalogMock.Object, httpClientFactoryMock.Object, urlRewriterMock.Object, loggerMock.Object, mapper, hubContextMock.Object);

            Printer? printer = await svc.FindByIdWithIncludesAsync(id, CancellationToken.None);

            Assert.NotNull(printer);
            Assert.Equal(expected.Id, printer!.Id);
            Assert.Equal(expected.Name, printer.Name);
        }

        [Fact]
        public async Task GetPrinterAsync_ReturnsNull_WhenNotFound()
        {
            Guid id = Guid.NewGuid();
            Mock<IPrintersRepository> repoMock = new Mock<IPrintersRepository>();
            _ = repoMock.Setup(r => r.FindByIdWithIncludesAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Printer?)null);

            Mock<IMoonrakerClient> moonMock = new Mock<IMoonrakerClient>();
            Mock<IPrusaLinkClient> prusaMock = new Mock<IPrusaLinkClient>();
            Mock<ISdcpClient> sdcpMock = new Mock<ISdcpClient>();
            Mock<IOctoPrintClient> octoMock = new Mock<IOctoPrintClient>();
            Mock<ICircuitBreakerService> circuitMock = new Mock<ICircuitBreakerService>();
            Mock<IPrinterCapabilityDiscoveryService> capDiscoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IDefaultCatalogService> defaultCatalogMock = new Mock<IDefaultCatalogService>();
            Mock<ICatalogService> catalogMock = new Mock<ICatalogService>();
            Mock<IHttpClientFactory> httpClientFactoryMock = new Mock<IHttpClientFactory>();
            Mock<INetworkUrlRewriteService> urlRewriterMock = new Mock<INetworkUrlRewriteService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            Mock<IHubContext<PrinterHub>> hubContextMock = new Mock<IHubContext<PrinterHub>>();

            MapperConfiguration mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile(new Mapping.PrinterMappingProfile()));
            IMapper mapper = mapperConfig.CreateMapper();

            PrintersService svc = new PrintersService(repoMock.Object, moonMock.Object, prusaMock.Object, sdcpMock.Object, octoMock.Object, circuitMock.Object, capDiscoveryMock.Object, defaultCatalogMock.Object, catalogMock.Object, httpClientFactoryMock.Object, urlRewriterMock.Object, loggerMock.Object, mapper, hubContextMock.Object);

            Printer? printer = await svc.FindByIdWithIncludesAsync(id, CancellationToken.None);

            Assert.Null(printer);
        }

        #region URL Normalization Tests

        /// <summary>
        /// Tests that NormalizeServerUrl removes explicit port numbers and returns only scheme + host
        /// This ensures ServerUrl is stored cleanly without port information
        /// </summary>
        [Theory]
        [InlineData("http://192.168.1.100:7125", 7125, "http://192.168.1.100")]
        [InlineData("http://printer.local:7125", 7125, "http://printer.local")]
        [InlineData("http://192.168.1.100", 7125, "http://192.168.1.100")]
        [InlineData("https://printer.local:443", 443, "https://printer.local")]
        [InlineData("https://192.168.1.100:8443", 8443, "https://192.168.1.100")]
        [InlineData("http://192.168.1.100:80", 80, "http://192.168.1.100")]
        [InlineData("http://192.168.1.100:80/api/path?query=1", 80, "http://192.168.1.100")]
        public void NormalizeServerUrl_RemovesPortAndPath_ReturnsSchemeAndHostOnly(string input, int defaultPort, string expected)
        {
            // Arrange
            PrintersService service = CreatePrintersService();

            // Act
            string result = service.NormalizeServerUrl(input, defaultPort);

            // Assert
            Assert.Equal(expected, result);
        }

        /// <summary>
        /// Tests that NormalizeServerUrl handles URLs without explicit ports correctly
        /// Port -1 in UriBuilder means "use default, don't show in URL"
        /// </summary>
        [Theory]
        [InlineData("192.168.1.100", 7125, "http://192.168.1.100")]
        [InlineData("printer.local", 7125, "http://printer.local")]
        [InlineData("http://192.168.1.100", 7125, "http://192.168.1.100")]
        public void NormalizeServerUrl_AddsScheme_ReturnsNormalizedUrl(string input, int defaultPort, string expected)
        {
            // Arrange
            PrintersService service = CreatePrintersService();

            // Act
            string result = service.NormalizeServerUrl(input, defaultPort);

            // Assert
            Assert.Equal(expected, result);
        }

        /// <summary>
        /// Tests that NormalizeServerUrl handles edge cases and invalid input gracefully
        /// </summary>
        [Theory]
        [InlineData(null, 7125)]
        [InlineData("", 7125)]
        [InlineData("   ", 7125)]
        public void NormalizeServerUrl_NullOrWhitespace_ReturnsEmptyString(string? input, int defaultPort)
        {
            // Arrange
            PrintersService service = CreatePrintersService();

            // Act
            string result = service.NormalizeServerUrl(input, defaultPort);

            // Assert
            Assert.Equal(string.Empty, result);
        }

        /// <summary>
        /// Tests that ResolveHostnameAsync returns normalized URLs without ports
        /// Ensures that even after DNS resolution, ports are not added to ServerUrl
        /// </summary>
        [Fact]
        public async Task ResolveHostnameAsync_ReturnsNormalizedUrl_WithoutExplicitPort()
        {
            // Arrange
            PrintersService service = CreatePrintersService();
            string input = "http://printer.local:7125";
            PrinterBackend backend = PrinterBackend.Moonraker;

            // Act
            ResolveHostnameResponse result = await service.ResolveHostnameAsync(input, backend, CancellationToken.None);

            // Assert
            // NormalizedInputUrl should not contain explicit port (Port = -1 in UriBuilder)
            Assert.NotNull(result);
            Assert.DoesNotContain(":7125", result.NormalizedInputUrl);
            // URL should start with scheme + host
            Assert.True(result.NormalizedInputUrl.StartsWith("http://"), "URL should have http scheme");
            Assert.False(result.NormalizedInputUrl.EndsWith("/"), "URL should not have trailing slash");
        }

        /// <summary>
        /// Tests that ResolveHostnameAsync preserves host information correctly
        /// </summary>
        [Theory]
        [InlineData("http://192.168.1.100:7125", PrinterBackend.Moonraker)]
        [InlineData("http://printer.local", PrinterBackend.Moonraker)]
        [InlineData("http://192.168.1.100:80", PrinterBackend.PrusaLink)]
        public async Task ResolveHostnameAsync_PreservesHost_InNormalizedUrl(string input, PrinterBackend backend)
        {
            // Arrange
            PrintersService service = CreatePrintersService();

            // Act
            ResolveHostnameResponse result = await service.ResolveHostnameAsync(input, backend, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            // Verify that host is preserved (even if hostname resolution fails, original host should be used)
            Assert.True(result.NormalizedInputUrl.Contains("192.168.1.100") || result.NormalizedInputUrl.Contains("printer.local"),
                "Normalized URL should preserve the host");
        }

        #endregion URL Normalization Tests

        private static PrintersService CreatePrintersService()
        {
            Mock<IPrintersRepository> repoMock = new Mock<IPrintersRepository>();
            Mock<IMoonrakerClient> moonMock = new Mock<IMoonrakerClient>();
            Mock<IPrusaLinkClient> prusaMock = new Mock<IPrusaLinkClient>();
            Mock<ISdcpClient> sdcpMock = new Mock<ISdcpClient>();
            Mock<IOctoPrintClient> octoMock = new Mock<IOctoPrintClient>();
            Mock<ICircuitBreakerService> circuitMock = new Mock<ICircuitBreakerService>();
            Mock<IPrinterCapabilityDiscoveryService> capDiscoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IDefaultCatalogService> defaultCatalogMock = new Mock<IDefaultCatalogService>();
            Mock<ICatalogService> catalogMock = new Mock<ICatalogService>();
            Mock<IHttpClientFactory> httpClientFactoryMock = new Mock<IHttpClientFactory>();
            Mock<INetworkUrlRewriteService> urlRewriterMock = new Mock<INetworkUrlRewriteService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            Mock<IHubContext<PrinterHub>> hubContextMock = new Mock<IHubContext<PrinterHub>>();

            MapperConfiguration mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile(new Mapping.PrinterMappingProfile()));
            IMapper mapper = mapperConfig.CreateMapper();

            return new PrintersService(
                repoMock.Object,
                moonMock.Object,
                prusaMock.Object,
                sdcpMock.Object,
                octoMock.Object,
                circuitMock.Object,
                capDiscoveryMock.Object,
                defaultCatalogMock.Object,
                catalogMock.Object,
                httpClientFactoryMock.Object,
                urlRewriterMock.Object,
                loggerMock.Object,
                mapper,
                hubContextMock.Object);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Printers;
using Farm.Web.Shared;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class PrintersServiceTests
    {
        [Fact]
        public async Task GetPrinterAsync_ReturnsPrinter_WhenFound()
        {
            var id = Guid.NewGuid();
            var repoMock = new Mock<Farm.Infrastructure.Repositories.Printers.IPrintersRepository>();
            var expected = new Farm.Infrastructure.Domain.Printer { Id = id, Name = "TestPrinter" };
            repoMock.Setup(r => r.FindByIdWithIncludesAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            var moonMock = new Mock<IMoonrakerClient>();
            var prusaMock = new Mock<IPrusaLinkClient>();
            var sdcpMock = new Mock<ISdcpClient>();
            var octoMock = new Mock<IOctoPrintClient>();
            var circuitMock = new Mock<Farm.Infrastructure.ICircuitBreakerService>();
            var capDiscoveryMock = new Mock<Farm.Web.Api.Services.Interfaces.IPrinterCapabilityDiscoveryService>();
            var defaultCatalogMock = new Mock<Farm.Web.Api.Services.IDefaultCatalogService>();
            var catalogMock = new Mock<Farm.Web.Api.Services.Catalog.ICatalogService>();
            var httpClientFactoryMock = new Mock<System.Net.Http.IHttpClientFactory>();
            var loggerMock = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var hubContextMock = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<Farm.Web.Api.Hubs.PrinterHub>>();

            // Provide a real AutoMapper instance for mapping dependencies
            var mapperConfig = new AutoMapper.MapperConfiguration(cfg => cfg.AddProfile(new Farm.Web.Api.Mapping.PrinterMappingProfile()));
            var mapper = mapperConfig.CreateMapper();

            var svc = new PrintersService(repoMock.Object, moonMock.Object, prusaMock.Object, sdcpMock.Object, octoMock.Object, circuitMock.Object, capDiscoveryMock.Object, defaultCatalogMock.Object, catalogMock.Object, httpClientFactoryMock.Object, loggerMock.Object, mapper, hubContextMock.Object);

            var printer = await svc.FindByIdWithIncludesAsync(id, CancellationToken.None);

            Assert.NotNull(printer);
            Assert.Equal(expected.Id, printer!.Id);
            Assert.Equal(expected.Name, printer.Name);
        }

        [Fact]
        public async Task GetPrinterAsync_ReturnsNull_WhenNotFound()
        {
            var id = Guid.NewGuid();
            var repoMock = new Mock<Farm.Infrastructure.Repositories.Printers.IPrintersRepository>();
            repoMock.Setup(r => r.FindByIdWithIncludesAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Farm.Infrastructure.Domain.Printer?)null);

            var moonMock = new Mock<IMoonrakerClient>();
            var prusaMock = new Mock<IPrusaLinkClient>();
            var sdcpMock = new Mock<ISdcpClient>();
            var octoMock = new Mock<IOctoPrintClient>();
            var circuitMock = new Mock<Farm.Infrastructure.ICircuitBreakerService>();
            var capDiscoveryMock = new Mock<Farm.Web.Api.Services.Interfaces.IPrinterCapabilityDiscoveryService>();
            var defaultCatalogMock = new Mock<Farm.Web.Api.Services.IDefaultCatalogService>();
            var catalogMock = new Mock<Farm.Web.Api.Services.Catalog.ICatalogService>();
            var httpClientFactoryMock = new Mock<System.Net.Http.IHttpClientFactory>();
            var loggerMock = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var hubContextMock = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<Farm.Web.Api.Hubs.PrinterHub>>();

            var mapperConfig = new AutoMapper.MapperConfiguration(cfg => cfg.AddProfile(new Farm.Web.Api.Mapping.PrinterMappingProfile()));
            var mapper = mapperConfig.CreateMapper();

            var svc = new PrintersService(repoMock.Object, moonMock.Object, prusaMock.Object, sdcpMock.Object, octoMock.Object, circuitMock.Object, capDiscoveryMock.Object, defaultCatalogMock.Object, catalogMock.Object, httpClientFactoryMock.Object, loggerMock.Object, mapper, hubContextMock.Object);

            var printer = await svc.FindByIdWithIncludesAsync(id, CancellationToken.None);

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
            var service = CreatePrintersService();

            // Act
            var result = service.NormalizeServerUrl(input, defaultPort);

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
            var service = CreatePrintersService();

            // Act
            var result = service.NormalizeServerUrl(input, defaultPort);

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
            var service = CreatePrintersService();

            // Act
            var result = service.NormalizeServerUrl(input, defaultPort);

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
            var service = CreatePrintersService();
            string input = "http://printer.local:7125";
            var backend = PrinterBackend.Moonraker;

            // Act
            var result = await service.ResolveHostnameAsync(input, backend, CancellationToken.None);

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
            var service = CreatePrintersService();

            // Act
            var result = await service.ResolveHostnameAsync(input, backend, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            // Verify that host is preserved (even if hostname resolution fails, original host should be used)
            Assert.True(result.NormalizedInputUrl.Contains("192.168.1.100") || result.NormalizedInputUrl.Contains("printer.local"),
                "Normalized URL should preserve the host");
        }

        #endregion URL Normalization Tests

        private static PrintersService CreatePrintersService()
        {
            var repoMock = new Mock<Farm.Infrastructure.Repositories.Printers.IPrintersRepository>();
            var moonMock = new Mock<IMoonrakerClient>();
            var prusaMock = new Mock<IPrusaLinkClient>();
            var sdcpMock = new Mock<ISdcpClient>();
            var octoMock = new Mock<IOctoPrintClient>();
            var circuitMock = new Mock<Farm.Infrastructure.ICircuitBreakerService>();
            var capDiscoveryMock = new Mock<Farm.Web.Api.Services.Interfaces.IPrinterCapabilityDiscoveryService>();
            var defaultCatalogMock = new Mock<Farm.Web.Api.Services.IDefaultCatalogService>();
            var catalogMock = new Mock<Farm.Web.Api.Services.Catalog.ICatalogService>();
            var httpClientFactoryMock = new Mock<System.Net.Http.IHttpClientFactory>();
            var loggerMock = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var hubContextMock = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<Farm.Web.Api.Hubs.PrinterHub>>();

            var mapperConfig = new AutoMapper.MapperConfiguration(cfg => cfg.AddProfile(new Farm.Web.Api.Mapping.PrinterMappingProfile()));
            var mapper = mapperConfig.CreateMapper();

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
                loggerMock.Object,
                mapper,
                hubContextMock.Object);
        }
    }
}

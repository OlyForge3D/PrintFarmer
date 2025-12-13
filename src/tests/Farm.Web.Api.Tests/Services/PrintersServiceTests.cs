using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Catalog;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Printers;
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
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            Mock<IHubContext<PrinterHub>> hubContextMock = new Mock<IHubContext<PrinterHub>>();

            // Provide a real AutoMapper instance for mapping dependencies
            MapperConfiguration mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile(new Mapping.PrinterMappingProfile()));
            IMapper mapper = mapperConfig.CreateMapper();

            PrintersService svc = CreatePrintersService(repoMock.Object);

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
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            Mock<IHubContext<PrinterHub>> hubContextMock = new Mock<IHubContext<PrinterHub>>();

            MapperConfiguration mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile(new Mapping.PrinterMappingProfile()));
            IMapper mapper = mapperConfig.CreateMapper();

            PrintersService svc = CreatePrintersService(repoMock.Object);

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

        #region Additional Printer Management Tests

        [Fact]
        public async Task GetAllFastDtosAsync_WithMultiplePrinters_ReturnsCorrectBackends()
        {
            // Arrange
            var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Prusa" };
            var model = new PrinterModel { Id = Guid.NewGuid(), Name = "CORE One" };
            
            var printers = new List<Printer>
            {
                new() { Id = Guid.NewGuid(), Name = "Moon", Backend = (int)PrinterBackend.Moonraker, Manufacturer = manufacturer, Model = model },
                new() { Id = Guid.NewGuid(), Name = "Prusa", Backend = (int)PrinterBackend.PrusaLink, Manufacturer = manufacturer, Model = model }
            };
            
            var mockRepo = new Mock<IPrintersRepository>();
            mockRepo.Setup(r => r.GetAllWithIncludesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(printers);
            var service = CreatePrintersService(mockRepo.Object);

            // Act
            var result = await service.GetAllFastDtosAsync(CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Length);
            Assert.Single(result, p => p.Backend == PrinterBackend.Moonraker);
            Assert.Single(result, p => p.Backend == PrinterBackend.PrusaLink);
        }

        [Fact]
        public async Task GetCapabilitiesListAsync_ReturnsList()
        {
            // Arrange
            var capabilities = new List<PrinterCapabilities>
            {
                new() { Id = Guid.NewGuid(), PrinterId = Guid.NewGuid(), NozzleDiameter = 0.4 },
                new() { Id = Guid.NewGuid(), PrinterId = Guid.NewGuid(), NozzleDiameter = 0.6 }
            };
            
            var mockRepo = new Mock<IPrintersRepository>();
            mockRepo.Setup(r => r.GetCapabilitiesListAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(capabilities);
            var service = CreatePrintersService(mockRepo.Object);

            // Act
            var result = await service.GetCapabilitiesListAsync(null, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task GetCapabilitiesDictionaryAsync_ReturnsDictionaryByPrinterId()
        {
            // Arrange
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var capabilities = new Dictionary<Guid, PrinterCapabilities>
            {
                { id1, new PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = id1, NozzleDiameter = 0.4 } },
                { id2, new PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = id2, NozzleDiameter = 0.6 } }
            };
            
            var mockRepo = new Mock<IPrintersRepository>();
            mockRepo.Setup(r => r.GetCapabilitiesDictionaryAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(capabilities);
            var service = CreatePrintersService(mockRepo.Object);

            // Act
            var result = await service.GetCapabilitiesDictionaryAsync(null, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.ContainsKey(id1));
            Assert.True(result.ContainsKey(id2));
        }

        [Fact]
        public async Task SaveCapabilitiesAsync_CallsRepository()
        {
            // Arrange
            var mockRepo = new Mock<IPrintersRepository>();
            var service = CreatePrintersService(mockRepo.Object);
            var capabilities = new PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = Guid.NewGuid() };

            // Act
            await service.SaveCapabilitiesAsync(capabilities, CancellationToken.None);

            // Assert
            mockRepo.Verify(r => r.SaveCapabilitiesAsync(capabilities, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetPrintersForExportAsync_WithIds_ReturnsFilteredPrinters()
        {
            // Arrange
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();
            var printers = new List<Printer> { new() { Id = id1 }, new() { Id = id2 }, new() { Id = id3 } };
            
            var mockRepo = new Mock<IPrintersRepository>();
            mockRepo.Setup(r => r.GetPrintersForExportAsync(It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(printers.Where(p => p.Id == id1 || p.Id == id2).ToList());
            var service = CreatePrintersService(mockRepo.Object);

            // Act
            var result = await service.GetPrintersForExportAsync(new[] { id1, id2 }, CancellationToken.None);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.DoesNotContain(result, p => p.Id == id3);
        }

        #endregion Additional Printer Management Tests

        private static PrintersService CreatePrintersService(IPrintersRepository? customRepo = null)
        {
            Mock<IPrintersRepository> repoMock = new Mock<IPrintersRepository>();
            var repo = customRepo ?? repoMock.Object;
            Mock<IMoonrakerClient> moonMock = new Mock<IMoonrakerClient>();
            Mock<IPrusaLinkClient> prusaMock = new Mock<IPrusaLinkClient>();
            Mock<ISdcpClient> sdcpMock = new Mock<ISdcpClient>();
            Mock<IOctoPrintClient> octoMock = new Mock<IOctoPrintClient>();
            Mock<ICircuitBreakerService> circuitMock = new Mock<ICircuitBreakerService>();
            Mock<IPrinterCapabilityDiscoveryService> capDiscoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IDefaultCatalogService> defaultCatalogMock = new Mock<IDefaultCatalogService>();
            Mock<ICatalogService> catalogMock = new Mock<ICatalogService>();
            Mock<IHttpClientFactory> httpClientFactoryMock = new Mock<IHttpClientFactory>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            Mock<IHubContext<PrinterHub>> hubContextMock = new Mock<IHubContext<PrinterHub>>();

            MapperConfiguration mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile(new Mapping.PrinterMappingProfile()));
            IMapper mapper = mapperConfig.CreateMapper();
            
            // Create the backend client factory mock
            var backendFactoryMock = new Mock<IBackendClientFactory>();
            backendFactoryMock.Setup(f => f.GetClient(PrinterBackend.Moonraker)).Returns((IBackendClient)(object)moonMock.Object);
            backendFactoryMock.Setup(f => f.GetClient(PrinterBackend.PrusaLink)).Returns((IBackendClient)(object)prusaMock.Object);
            backendFactoryMock.Setup(f => f.GetClient(PrinterBackend.SDCP)).Returns((IBackendClient)(object)sdcpMock.Object);
            backendFactoryMock.Setup(f => f.GetClient(PrinterBackend.OctoPrint)).Returns((IBackendClient)(object)octoMock.Object);
            
            // Create mocks for other extracted services
            var dtoBuilderMock = new Mock<IPrinterStatusDtoBuilder>();
            var coordinatorMock = new Mock<IMultiPrinterStatusCoordinator>();
            var fallbackServiceMock = new Mock<IPrinterStatusFallbackService>();
            var statusClientFactoryMock = new Mock<IPrinterStatusClientFactory>();
            var capabilityFactoryMock = new Mock<IBackendCapabilityFactory>();

            return new PrintersService(
                repo,
                backendFactoryMock.Object,
                capabilityFactoryMock.Object,
                circuitMock.Object,
                capDiscoveryMock.Object,
                defaultCatalogMock.Object,
                catalogMock.Object,
                httpClientFactoryMock.Object,
                loggerMock.Object,
                mapper,
                hubContextMock.Object,
                dtoBuilderMock.Object,
                coordinatorMock.Object,
                fallbackServiceMock.Object,
                statusClientFactoryMock.Object);
        }
    }
}

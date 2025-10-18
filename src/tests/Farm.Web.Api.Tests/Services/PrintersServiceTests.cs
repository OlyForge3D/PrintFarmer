using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.Printers;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services;
using Farm.Infrastructure;
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
            var httpClientFactoryMock = new Mock<System.Net.Http.IHttpClientFactory>();
            var loggerMock = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

            var svc = new PrintersService(repoMock.Object, moonMock.Object, prusaMock.Object, sdcpMock.Object, octoMock.Object, circuitMock.Object, capDiscoveryMock.Object, defaultCatalogMock.Object, httpClientFactoryMock.Object, loggerMock.Object);

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
            var httpClientFactoryMock = new Mock<System.Net.Http.IHttpClientFactory>();
            var loggerMock = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

            var svc = new PrintersService(repoMock.Object, moonMock.Object, prusaMock.Object, sdcpMock.Object, octoMock.Object, circuitMock.Object, capDiscoveryMock.Object, defaultCatalogMock.Object, httpClientFactoryMock.Object, loggerMock.Object);

            var printer = await svc.FindByIdWithIncludesAsync(id, CancellationToken.None);

            Assert.Null(printer);
        }
    }
}

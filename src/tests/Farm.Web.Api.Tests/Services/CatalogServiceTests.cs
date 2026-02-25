using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Catalog.Caching;
using Farm.Web.Api.Services.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class CatalogServiceTests
    {
        [Fact]
        public async Task GetManufacturersAsync_DelegatesToCacheProvider()
        {
            Mock<ICatalogCacheProvider> mockCacheProvider = new Mock<ICatalogCacheProvider>();
            (IReadOnlyList<ManufacturerDto>, string?) expected = (new List<ManufacturerDto> { new ManufacturerDto(Guid.NewGuid(), "Test") } as IReadOnlyList<ManufacturerDto>, "etag1");
            _ = mockCacheProvider.Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            Mock<INormalizationEventLogger> normLogger = new Mock<INormalizationEventLogger>();
            Mock<ILogger<CatalogService>> unifiedLogging = new Mock<ILogger<CatalogService>>();
            Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();

            Farm.Infrastructure.Services.Catalog.CatalogService svc = new Farm.Infrastructure.Services.Catalog.CatalogService(
                mockRepo.Object,
                normLogger.Object,
                mockCacheProvider.Object,
                unifiedLogging.Object);

            (IReadOnlyList<ManufacturerDto>? list, string? etag) = await svc.GetManufacturersAsync(CancellationToken.None);

            Assert.Equal(expected.Item2, etag);
            Assert.Equal(expected.Item1, list);
            mockCacheProvider.Verify(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}

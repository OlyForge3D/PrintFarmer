using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Services.Catalog;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class CatalogServiceTests
    {
        [Fact]
        public async Task GetManufacturersAsync_DelegatesToCache()
        {
            Mock<ICatalogCache> mockCache = new Mock<ICatalogCache>();
            (IReadOnlyList<ManufacturerDto>, string) expected = (new List<ManufacturerDto> { new ManufacturerDto(Guid.NewGuid(), "Test") } as IReadOnlyList<ManufacturerDto>, "etag1");
            _ = mockCache.Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            // Minimal dependencies: use NullLogger implementations and an in-memory AppDbContext is not required for this test
            Mock<INormalizationEventLogger> normLogger = new Mock<INormalizationEventLogger>();
            Mock<IUnifiedLoggingService> unifiedLogging = new Mock<IUnifiedLoggingService>();
            Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();

            CatalogService svc = new CatalogService(mockRepo.Object, normLogger.Object, mockCache.Object, unifiedLogging.Object);
            (IReadOnlyList<ManufacturerDto>? list, string? etag) = await svc.GetManufacturersAsync(CancellationToken.None);

            Assert.Equal(expected.Item2, etag);
            Assert.Equal(expected.Item1, list);
            mockCache.Verify(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}

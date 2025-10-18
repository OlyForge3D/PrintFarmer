using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
            var mockCache = new Mock<ICatalogCache>();
            var expected = (new List<ManufacturerDto> { new ManufacturerDto(Guid.NewGuid(), "Test") } as IReadOnlyList<ManufacturerDto>, "etag1");
            mockCache.Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            // Minimal dependencies: use NullLogger implementations and an in-memory AppDbContext is not required for this test
            var normLogger = new Moq.Mock<Farm.Infrastructure.Normalization.INormalizationEventLogger>();
            var unifiedLogging = new Moq.Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockRepo = new Moq.Mock<Farm.Infrastructure.Repositories.Catalog.ICatalogRepository>();

            var svc = new CatalogService(mockRepo.Object, normLogger.Object, mockCache.Object, unifiedLogging.Object);
            var (list, etag) = await svc.GetManufacturersAsync(CancellationToken.None);

            Assert.Equal(expected.Item2, etag);
            Assert.Equal(expected.Item1, list);
            mockCache.Verify(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}

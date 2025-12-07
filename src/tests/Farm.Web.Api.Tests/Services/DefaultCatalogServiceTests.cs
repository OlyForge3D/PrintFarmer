using Farm.Infrastructure.Repositories.Catalog;
using Farm.Web.Api.Services;
using FluentAssertions;
using Moq;

namespace Farm.Web.Api.Tests.Services;

public class DefaultCatalogServiceTests
{
    private readonly Mock<ICatalogRepository> _mockRepository;
    private readonly DefaultCatalogService _service;

    public DefaultCatalogServiceTests()
    {
        _mockRepository = new Mock<ICatalogRepository>();
        _service = new DefaultCatalogService(_mockRepository.Object);
    }

    [Fact]
    public async Task GetUnknownManufacturerIdAsync_Returns_Valid_Guid()
    {
        var expectedId = Guid.NewGuid();
        _mockRepository
            .Setup(x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var result = await _service.GetUnknownManufacturerIdAsync();

        result.Should().Be(expectedId);
    }

    [Fact]
    public async Task GetUnknownManufacturerIdAsync_Caches_Result()
    {
        var expectedId = Guid.NewGuid();
        _mockRepository
            .Setup(x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var result1 = await _service.GetUnknownManufacturerIdAsync();
        var result2 = await _service.GetUnknownManufacturerIdAsync();

        result1.Should().Be(result2);
        _mockRepository.Verify(
            x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()),
            Times.Once()
        );
    }

    [Fact]
    public async Task GetUnknownManufacturerIdAsync_WithNullResult_Throws()
    {
        _mockRepository
            .Setup(x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        Func<Task> act = () => _service.GetUnknownManufacturerIdAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unknown manufacturer not found*");
    }

    [Fact]
    public async Task GetUnknownModelIdAsync_Returns_Valid_Guid()
    {
        var expectedId = Guid.NewGuid();
        _mockRepository
            .Setup(x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var result = await _service.GetUnknownModelIdAsync();

        result.Should().Be(expectedId);
    }

    [Fact]
    public async Task GetUnknownModelIdAsync_Caches_Result()
    {
        var expectedId = Guid.NewGuid();
        _mockRepository
            .Setup(x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var result1 = await _service.GetUnknownModelIdAsync();
        var result2 = await _service.GetUnknownModelIdAsync();

        result1.Should().Be(result2);
        _mockRepository.Verify(
            x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()),
            Times.Once()
        );
    }

    [Fact]
    public async Task GetUnknownModelIdAsync_WithNullResult_Throws()
    {
        _mockRepository
            .Setup(x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        Func<Task> act = () => _service.GetUnknownModelIdAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unknown Model not found*");
    }

    [Fact]
    public async Task GetDefaultCatalogIdsAsync_Returns_Both_Ids()
    {
        var mfgId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        _mockRepository
            .Setup(x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mfgId);
        _mockRepository
            .Setup(x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelId);

        var (returnedMfgId, returnedModelId) = await _service.GetDefaultCatalogIdsAsync();

        returnedMfgId.Should().Be(mfgId);
        returnedModelId.Should().Be(modelId);
    }

    [Fact]
    public async Task GetDefaultCatalogIdsAsync_Caches_Manufacturer_Id()
    {
        var mfgId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        _mockRepository
            .Setup(x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mfgId);
        _mockRepository
            .Setup(x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelId);

        var result1 = await _service.GetDefaultCatalogIdsAsync();
        var result2 = await _service.GetDefaultCatalogIdsAsync();

        result1.Should().Be(result2);
        _mockRepository.Verify(
            x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()),
            Times.Once()
        );
    }

    [Fact]
    public async Task GetDefaultCatalogIdsAsync_Caches_Model_Id()
    {
        var mfgId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        _mockRepository
            .Setup(x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mfgId);
        _mockRepository
            .Setup(x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelId);

        await _service.GetDefaultCatalogIdsAsync();
        await _service.GetDefaultCatalogIdsAsync();

        _mockRepository.Verify(
            x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()),
            Times.Once()
        );
    }

    [Fact]
    public void Service_Can_Be_Created()
    {
        var service = new DefaultCatalogService(_mockRepository.Object);

        service.Should().NotBeNull();
    }

    [Fact]
    public async Task Multiple_Concurrent_Calls_Use_Cached_Values()
    {
        var mfgId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        _mockRepository
            .Setup(x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mfgId);
        _mockRepository
            .Setup(x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelId);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _service.GetDefaultCatalogIdsAsync())
            .ToList();

        await Task.WhenAll(tasks);

        _mockRepository.Verify(
            x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()),
            Times.Once()
        );
        _mockRepository.Verify(
            x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()),
            Times.Once()
        );
    }

    [Fact]
    public async Task GetDefaultCatalogIdsAsync_Throws_When_Manufacturer_Not_Found()
    {
        _mockRepository
            .Setup(x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        _mockRepository
            .Setup(x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        Func<Task> act = () => _service.GetDefaultCatalogIdsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetDefaultCatalogIdsAsync_Throws_When_Model_Not_Found()
    {
        _mockRepository
            .Setup(x => x.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        _mockRepository
            .Setup(x => x.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        Func<Task> act = () => _service.GetDefaultCatalogIdsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Tags;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Tags;

/// <summary>
/// Unit tests for tag filtering functionality.
/// Tests include/exclude, require all vs any, and complex filtering scenarios.
/// </summary>
public class TagFilteringTests
{
    private readonly Mock<ITagRepository> _tagRepositoryMock;
    private readonly Mock<IModelTagMappingRepository> _mappingRepositoryMock;
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly TagService _tagService;

    public TagFilteringTests()
    {
        _tagRepositoryMock = new Mock<ITagRepository>();
        _mappingRepositoryMock = new Mock<IModelTagMappingRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _loggerMock = new Mock<IUnifiedLoggingService>();

        _tagService = new TagService(
            _tagRepositoryMock.Object,
            _mappingRepositoryMock.Object,
            _unitOfWorkMock.Object,
            _loggerMock.Object);
    }

    #region GetModelsWithAllTagsAsync Tests

    [Fact]
    public async Task GetModelsWithAllTagsAsync_WithEmptyTagIds_ReturnsAllModels()
    {
        // Arrange
        var allModelIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        _mappingRepositoryMock.Setup(r => r.GetAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(allModelIds);

        // Act
        var result = await _tagService.GetModelsWithAllTagsAsync(Array.Empty<Guid>(), CancellationToken.None);

        // Assert
        Assert.Equal(allModelIds.Count, result.Count);
        _mappingRepositoryMock.Verify(r => r.GetAllModelsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModelsWithAllTagsAsync_WithSingleTag_ReturnsModelsWithAllTags()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var modelIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Contains(tagId)),
            true,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelIds);

        // Act
        var result = await _tagService.GetModelsWithAllTagsAsync(new[] { tagId }, CancellationToken.None);

        // Assert
        Assert.Equal(modelIds.Count, result.Count);
        _mappingRepositoryMock.Verify(r => r.GetModelsWithTagsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Contains(tagId)),
            true,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModelsWithAllTagsAsync_WithMultipleTags_RequiresAllTags()
    {
        // Arrange
        var tag1 = Guid.NewGuid();
        var tag2 = Guid.NewGuid();
        var tag3 = Guid.NewGuid();
        var modelIds = new List<Guid> { Guid.NewGuid() }; // Only 1 model has all 3 tags

        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            true,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelIds);

        // Act
        var result = await _tagService.GetModelsWithAllTagsAsync(new[] { tag1, tag2, tag3 }, CancellationToken.None);

        // Assert
        Assert.Single(result);
        _mappingRepositoryMock.Verify(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            true,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetModelsWithAnyTagAsync Tests

    [Fact]
    public async Task GetModelsWithAnyTagAsync_WithEmptyTagIds_ReturnsEmptyList()
    {
        // Act
        var result = await _tagService.GetModelsWithAnyTagAsync(Array.Empty<Guid>(), CancellationToken.None);

        // Assert
        Assert.Empty(result);
        _mappingRepositoryMock.Verify(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetModelsWithAnyTagAsync_WithMultipleTags_ReturnsModelsWithAnyTag()
    {
        // Arrange
        var tag1 = Guid.NewGuid();
        var tag2 = Guid.NewGuid();
        var modelIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            false,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelIds);

        // Act
        var result = await _tagService.GetModelsWithAnyTagAsync(new[] { tag1, tag2 }, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.Count);
        _mappingRepositoryMock.Verify(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetModelsExcludingTagsAsync Tests

    [Fact]
    public async Task GetModelsExcludingTagsAsync_WithEmptyTagIds_ReturnsAllModels()
    {
        // Arrange
        var allModelIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _mappingRepositoryMock.Setup(r => r.GetAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(allModelIds);

        // Act
        var result = await _tagService.GetModelsExcludingTagsAsync(Array.Empty<Guid>(), CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetModelsExcludingTagsAsync_ExcludesModelsWithTag()
    {
        // Arrange
        var excludeTagId = Guid.NewGuid();
        var allModels = new List<Guid>
        {
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        };
        var modelsWithExcludedTag = new List<Guid>
        {
            allModels[0],
            allModels[2]
        };

        _mappingRepositoryMock.Setup(r => r.GetModelsExcludingTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(allModels.Where(m => !modelsWithExcludedTag.Contains(m)).ToList());

        // Act
        var result = await _tagService.GetModelsExcludingTagsAsync(new[] { excludeTagId }, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(allModels[0], result);
        Assert.DoesNotContain(allModels[2], result);
    }

    #endregion

    #region GetModelsWithComplexFilterAsync Tests

    [Fact]
    public async Task GetModelsWithComplexFilterAsync_WithOnlyIncludeAll_ReturnsModelsWithAllTags()
    {
        // Arrange
        var tag1 = Guid.NewGuid();
        var tag2 = Guid.NewGuid();
        var expectedModels = new List<Guid> { Guid.NewGuid() };

        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Contains(tag1) && ids.Contains(tag2)),
            true,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedModels);

        // Act
        var result = await _tagService.GetModelsWithComplexFilterAsync(
            new[] { tag1, tag2 },
            Array.Empty<Guid>(),
            Array.Empty<Guid>(),
            CancellationToken.None);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task GetModelsWithComplexFilterAsync_WithIncludeAllAndExclude_AppliesBothFilters()
    {
        // Arrange
        var includeTag = Guid.NewGuid();
        var excludeTag = Guid.NewGuid();
        var modelsWithIncludeTag = new List<Guid>
        {
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        };
        var modelsWithExcludeTag = new List<Guid> { modelsWithIncludeTag[0] };

        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Contains(includeTag)),
            true,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelsWithIncludeTag);

        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Contains(excludeTag)),
            false,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelsWithExcludeTag);

        // Act
        var result = await _tagService.GetModelsWithComplexFilterAsync(
            new[] { includeTag },
            Array.Empty<Guid>(),
            new[] { excludeTag },
            CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(modelsWithIncludeTag[0], result);
    }

    [Fact]
    public async Task GetModelsWithComplexFilterAsync_WithIncludeAnyWhenNoIncludeAll_UsesIncludeAny()
    {
        // Arrange
        var tag1 = Guid.NewGuid();
        var tag2 = Guid.NewGuid();
        var expectedModels = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            false,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedModels);

        // Act
        var result = await _tagService.GetModelsWithComplexFilterAsync(
            Array.Empty<Guid>(),
            new[] { tag1, tag2 },
            Array.Empty<Guid>(),
            CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        _mappingRepositoryMock.Verify(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModelsWithComplexFilterAsync_WithNoFilters_ReturnsAllModels()
    {
        // Arrange
        var allModels = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        _mappingRepositoryMock.Setup(r => r.GetAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(allModels);

        // Act
        var result = await _tagService.GetModelsWithComplexFilterAsync(
            Array.Empty<Guid>(),
            Array.Empty<Guid>(),
            Array.Empty<Guid>(),
            CancellationToken.None);

        // Assert
        Assert.Equal(3, result.Count);
        _mappingRepositoryMock.Verify(r => r.GetAllModelsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModelsWithComplexFilterAsync_WithAllFilters_AppliesAllThree()
    {
        // Arrange
        var includeAll1 = Guid.NewGuid();
        var includeAll2 = Guid.NewGuid();
        var exclude1 = Guid.NewGuid();

        var modelsWithAllTags = new List<Guid>
        {
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        };

        var modelsWithExcludeTag = new List<Guid> { modelsWithAllTags[0] };

        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            true,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelsWithAllTags);

        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            false,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelsWithExcludeTag);

        // Act
        var result = await _tagService.GetModelsWithComplexFilterAsync(
            new[] { includeAll1, includeAll2 },
            Array.Empty<Guid>(),
            new[] { exclude1 },
            CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(modelsWithAllTags[0], result);
    }

    [Fact]
    public async Task GetModelsWithComplexFilterAsync_WithEmptyResultSet_SkipsExclusionFilter()
    {
        // Arrange
        var emptyResult = new List<Guid>();
        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            true,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyResult);

        // Act
        var result = await _tagService.GetModelsWithComplexFilterAsync(
            new[] { Guid.NewGuid() },
            Array.Empty<Guid>(),
            new[] { Guid.NewGuid() },
            CancellationToken.None);

        // Assert
        Assert.Empty(result);
        // Should not call GetModelsWithTagsAsync for exclusion when result is empty
        _mappingRepositoryMock.Verify(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            false,
            It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    [Fact]
    public async Task GetModelsWithAllTagsAsync_WhenRepositoryThrows_LogsErrorAndRethrows()
    {
        // Arrange
        var exception = new InvalidOperationException("Database error");
        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tagService.GetModelsWithAllTagsAsync(new[] { Guid.NewGuid() }, CancellationToken.None));
    }

    [Fact]
    public async Task GetModelsWithAnyTagAsync_WhenRepositoryThrows_LogsErrorAndRethrows()
    {
        // Arrange
        var exception = new InvalidOperationException("Database error");
        _mappingRepositoryMock.Setup(r => r.GetModelsWithTagsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tagService.GetModelsWithAnyTagAsync(new[] { Guid.NewGuid() }, CancellationToken.None));
    }
}

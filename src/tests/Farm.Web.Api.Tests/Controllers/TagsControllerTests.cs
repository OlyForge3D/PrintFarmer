using Farm.Api.Controllers;
using Farm.Infrastructure;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Tags;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class TagsControllerTests
{
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly Mock<ITagService> _tagServiceMock;
    private readonly TagsController _controller;

    public TagsControllerTests()
    {
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _tagServiceMock = new Mock<ITagService>();
        _controller = new TagsController(_loggerMock.Object, _tagServiceMock.Object);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    #region GetAllTagsAsync Tests

    [Fact]
    public async Task GetAllTagsAsync_WithTags_ReturnsOkWithTags()
    {
        // Arrange
        var tags = new List<TagDto>
        {
            new TagDto { Id = Guid.NewGuid(), Name = "Support", Color = "#FF0000" },
            new TagDto { Id = Guid.NewGuid(), Name = "Miniature", Color = "#00FF00" }
        } as IReadOnlyList<TagDto>;

        _tagServiceMock
            .Setup(s => s.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tags);

        // Act
        ActionResult<IEnumerable<TagDto>> result = await _controller.GetAllTagsAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(tags, okResult.Value);
        _tagServiceMock.Verify(s => s.GetAllTagsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAllTagsAsync_WithNoTags_ReturnsOkWithEmptyList()
    {
        // Arrange
        IReadOnlyList<TagDto> emptyTags = new List<TagDto>();
        _tagServiceMock
            .Setup(s => s.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyTags);

        // Act
        ActionResult<IEnumerable<TagDto>> result = await _controller.GetAllTagsAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        IReadOnlyList<TagDto> returnedTags = Assert.IsAssignableFrom<IReadOnlyList<TagDto>>(okResult.Value);
        Assert.Empty(returnedTags);
    }

    [Fact]
    public async Task GetAllTagsAsync_WhenServiceThrows_ReturnsInternalServerError()
    {
        // Arrange
        _tagServiceMock
            .Setup(s => s.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act
        ActionResult<IEnumerable<TagDto>> result = await _controller.GetAllTagsAsync(CancellationToken.None);

        // Assert
        ObjectResult statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, statusResult.StatusCode);
    }

    #endregion

    #region SearchTagsAsync Tests

    [Fact]
    public async Task SearchTagsAsync_WithValidQuery_ReturnsOkWithResults()
    {
        // Arrange
        string searchQuery = "support";
        var results = new List<TagSuggestionDto>
        {
            new TagSuggestionDto { Id = Guid.NewGuid(), Name = "Support Required", UsageCount = 5 },
            new TagSuggestionDto { Id = Guid.NewGuid(), Name = "Support Structures", UsageCount = 3 }
        } as IReadOnlyList<TagSuggestionDto>;

        _tagServiceMock
            .Setup(s => s.SearchTagsAsync(searchQuery, It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);

        // Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.SearchTagsAsync(searchQuery, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(results, okResult.Value);
        _tagServiceMock.Verify(s => s.SearchTagsAsync(searchQuery, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchTagsAsync_WithNullQuery_ReturnsOkWithEmptyList()
    {
        // Arrange & Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.SearchTagsAsync(null, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        IReadOnlyList<TagSuggestionDto> returnedTags = Assert.IsAssignableFrom<IReadOnlyList<TagSuggestionDto>>(okResult.Value);
        Assert.Empty(returnedTags);
        _tagServiceMock.Verify(s => s.SearchTagsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchTagsAsync_WithEmptyQuery_ReturnsOkWithEmptyList()
    {
        // Arrange & Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.SearchTagsAsync("   ", CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        IReadOnlyList<TagSuggestionDto> returnedTags = Assert.IsAssignableFrom<IReadOnlyList<TagSuggestionDto>>(okResult.Value);
        Assert.Empty(returnedTags);
    }

    [Fact]
    public async Task SearchTagsAsync_WhenServiceThrows_ReturnsInternalServerError()
    {
        // Arrange
        _tagServiceMock
            .Setup(s => s.SearchTagsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Search error"));

        // Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.SearchTagsAsync("test", CancellationToken.None);

        // Assert
        ObjectResult statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, statusResult.StatusCode);
    }

    #endregion

    #region GetPopularTagsAsync Tests

    [Fact]
    public async Task GetPopularTagsAsync_WithDefaultCount_ReturnsOkWithPopularTags()
    {
        // Arrange
        var popularTags = new List<TagSuggestionDto>
        {
            new TagSuggestionDto { Id = Guid.NewGuid(), Name = "Print Test", UsageCount = 150 },
            new TagSuggestionDto { Id = Guid.NewGuid(), Name = "Support", UsageCount = 120 }
        } as IReadOnlyList<TagSuggestionDto>;

        _tagServiceMock
            .Setup(s => s.GetPopularTagsAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(popularTags);

        // Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.GetPopularTagsAsync(10, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(popularTags, okResult.Value);
    }

    [Fact]
    public async Task GetPopularTagsAsync_WithCustomCount_ReturnsOkWithRequestedCount()
    {
        // Arrange
        int count = 20;
        var popularTags = new List<TagSuggestionDto>
        {
            new TagSuggestionDto { Id = Guid.NewGuid(), Name = "Tag1", UsageCount = 100 }
        } as IReadOnlyList<TagSuggestionDto>;

        _tagServiceMock
            .Setup(s => s.GetPopularTagsAsync(count, It.IsAny<CancellationToken>()))
            .ReturnsAsync(popularTags);

        // Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.GetPopularTagsAsync(count, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(popularTags, okResult.Value);
        _tagServiceMock.Verify(s => s.GetPopularTagsAsync(count, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetPopularTagsAsync_WithZeroCount_DefaultsToTen()
    {
        // Arrange
        var popularTags = new List<TagSuggestionDto>() as IReadOnlyList<TagSuggestionDto>;
        _tagServiceMock
            .Setup(s => s.GetPopularTagsAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(popularTags);

        // Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.GetPopularTagsAsync(0, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        _tagServiceMock.Verify(s => s.GetPopularTagsAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetTagAnalyticsAsync Tests

    [Fact]
    public async Task GetTagAnalyticsAsync_ReturnsOkWithAnalytics()
    {
        // Arrange
        var analytics = new TagAnalyticsDto
        {
            TotalTags = 42,
            TagsInUse = 35,
            UnusedTags = 7,
            TotalModelTagAssociations = 219,
            AverageTagsPerModel = 5.2,
            TopTags = new List<TagStatDto>
            {
                new TagStatDto { Id = Guid.NewGuid(), Name = "Support", ModelCount = 50, CreatedAt = DateTime.UtcNow }
            }
        };
        _tagServiceMock
            .Setup(s => s.GetAnalyticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(analytics);

        // Act
        ActionResult<TagAnalyticsDto> result = await _controller.GetTagAnalyticsAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(analytics, okResult.Value);
        _tagServiceMock.Verify(s => s.GetAnalyticsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTagAnalyticsAsync_WhenServiceThrows_ReturnsInternalServerError()
    {
        // Arrange
        _tagServiceMock
            .Setup(s => s.GetAnalyticsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Analytics calculation failed"));

        // Act
        ActionResult<TagAnalyticsDto> result = await _controller.GetTagAnalyticsAsync(CancellationToken.None);

        // Assert
        ObjectResult statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, statusResult.StatusCode);
    }

    #endregion

    #region GetTagSuggestionsAsync Tests

    [Fact]
    public async Task GetTagSuggestionsAsync_WithValidQuery_ReturnsOkWithSuggestions()
    {
        // Arrange
        string query = "sup";
        var suggestions = new List<TagSuggestionDto>
        {
            new TagSuggestionDto { Id = Guid.NewGuid(), Name = "Support", UsageCount = 50 },
            new TagSuggestionDto { Id = Guid.NewGuid(), Name = "Super Detail", UsageCount = 10 }
        } as IReadOnlyList<TagSuggestionDto>;

        _tagServiceMock
            .Setup(s => s.GetTagSuggestionsAsync(query, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(suggestions);

        // Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.GetTagSuggestionsAsync(query, 10, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(suggestions, okResult.Value);
    }

    [Fact]
    public async Task GetTagSuggestionsAsync_WithNullQuery_ReturnsOkWithEmptyList()
    {
        // Arrange
        var emptyResults = new List<TagSuggestionDto>() as IReadOnlyList<TagSuggestionDto>;
        _tagServiceMock
            .Setup(s => s.GetTagSuggestionsAsync("", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyResults);

        // Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.GetTagSuggestionsAsync(null, 10, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        IReadOnlyList<TagSuggestionDto> returnedSuggestions = Assert.IsAssignableFrom<IReadOnlyList<TagSuggestionDto>>(okResult.Value);
        Assert.Empty(returnedSuggestions);
    }

    [Fact]
    public async Task GetTagSuggestionsAsync_WithCustomLimit_ReturnsOkWithRequestedLimit()
    {
        // Arrange
        string query = "test";
        int limit = 25;
        var suggestions = new List<TagSuggestionDto>
        {
            new TagSuggestionDto { Id = Guid.NewGuid(), Name = "Test", UsageCount = 100 }
        } as IReadOnlyList<TagSuggestionDto>;

        _tagServiceMock
            .Setup(s => s.GetTagSuggestionsAsync(query, limit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(suggestions);

        // Act
        ActionResult<IEnumerable<TagSuggestionDto>> result = await _controller.GetTagSuggestionsAsync(query, limit, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(suggestions, okResult.Value);
        _tagServiceMock.Verify(s => s.GetTagSuggestionsAsync(query, limit, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}

using Farm.Api.Controllers;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Tags;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class TagsControllerTests
{
    private readonly Mock<ILogger<TagsController>> _loggerMock;
    private readonly Mock<ITagService> _tagServiceMock;
    private readonly TagsController _controller;

    public TagsControllerTests()
    {
        _loggerMock = new Mock<ILogger<TagsController>>();
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
        _ = Assert.IsType<OkObjectResult>(result.Result);
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

    #region UpdateTagAsync Tests

    [Fact]
    public async Task UpdateTagAsync_WithNullBody_ReturnsBadRequest()
    {
        ActionResult<TagDto> result = await _controller.UpdateTagAsync(Guid.NewGuid(), null!, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateTagAsync_WhenSuccessful_ReturnsOk()
    {
        Guid tagId = Guid.NewGuid();
        var updated = new TagDto { Id = tagId, Name = "Renamed", Revision = 2, ConcurrencyToken = Guid.NewGuid() };
        _tagServiceMock
            .Setup(s => s.UpdateTagAsync(tagId, It.IsAny<UpdateTagDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        ActionResult<TagDto> result = await _controller.UpdateTagAsync(
            tagId, new UpdateTagDto { Name = "Renamed", ExpectedRevision = 1 }, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(updated, okResult.Value);
    }

    [Fact]
    public async Task UpdateTagAsync_WhenNotFound_Returns404()
    {
        Guid tagId = Guid.NewGuid();
        _tagServiceMock
            .Setup(s => s.UpdateTagAsync(tagId, It.IsAny<UpdateTagDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("missing"));

        ActionResult<TagDto> result = await _controller.UpdateTagAsync(
            tagId, new UpdateTagDto { ExpectedRevision = 1 }, CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateTagAsync_OnConcurrencyConflict_Returns409()
    {
        Guid tagId = Guid.NewGuid();
        _tagServiceMock
            .Setup(s => s.UpdateTagAsync(tagId, It.IsAny<UpdateTagDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TagConcurrencyException(tagId, 1, 3));

        ActionResult<TagDto> result = await _controller.UpdateTagAsync(
            tagId, new UpdateTagDto { ExpectedRevision = 1 }, CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task UpdateTagAsync_OnDuplicateName_Returns409()
    {
        Guid tagId = Guid.NewGuid();
        _tagServiceMock
            .Setup(s => s.UpdateTagAsync(tagId, It.IsAny<UpdateTagDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DuplicateEntityException("A tag named 'Alpha' already exists"));

        ActionResult<TagDto> result = await _controller.UpdateTagAsync(
            tagId, new UpdateTagDto { Name = "Alpha", ExpectedRevision = 1 }, CancellationToken.None);

        _ = Assert.IsType<ConflictObjectResult>(result.Result);
    }

    #endregion

    #region GetObjectsTagsAsync Tests

    private static TagsController CreateControllerWithAuthorization(
        Mock<ITagService> tagServiceMock,
        IQueueResourceAuthorizationService? resourceAuthorization)
    {
        var controller = new TagsController(Mock.Of<ILogger<TagsController>>(), tagServiceMock.Object, resourceAuthorization);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PrintJob")]
    public async Task GetObjectsTagsAsync_InvalidObjectType_ReturnsBadRequest(string? objectType)
    {
        ActionResult<IEnumerable<ObjectTagsDto>> result =
            await _controller.GetObjectsTagsAsync(objectType, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
        _tagServiceMock.Verify(s => s.GetObjectsTagsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetObjectsTagsAsync_WithoutResourceAuthorization_ReturnsAllEntriesFromService()
    {
        var entries = new List<ObjectTagsDto>
        {
            new(Guid.NewGuid(), new List<TagDto> { new() { Id = Guid.NewGuid(), Name = "Red" } }),
            new(Guid.NewGuid(), []),
        };
        _tagServiceMock
            .Setup(s => s.GetObjectsTagsAsync("Printer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        // The default test controller is built without a resourceAuthorization dependency
        // (mirrors production DI resolving none registered): filtering must degrade to a
        // pure passthrough rather than hiding every printer.
        ActionResult<IEnumerable<ObjectTagsDto>> result =
            await _controller.GetObjectsTagsAsync("Printer", CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(entries, okResult.Value);
    }

    [Fact]
    public async Task GetObjectsTagsAsync_PrinterType_FiltersOutPrintersCallerCannotAccess()
    {
        Guid visiblePrinterId = Guid.NewGuid();
        Guid hiddenPrinterId = Guid.NewGuid();
        var entries = new List<ObjectTagsDto>
        {
            new(visiblePrinterId, []),
            new(hiddenPrinterId, []),
        };
        _tagServiceMock
            .Setup(s => s.GetObjectsTagsAsync("Printer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), visiblePrinterId, PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), hiddenPrinterId, PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        ActionResult<IEnumerable<ObjectTagsDto>> result =
            await controller.GetObjectsTagsAsync("Printer", CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returned = Assert.IsAssignableFrom<IEnumerable<ObjectTagsDto>>(okResult.Value).ToList();
        ObjectTagsDto onlyEntry = Assert.Single(returned);
        Assert.Equal(visiblePrinterId, onlyEntry.ObjectId);
    }

    [Fact]
    public async Task GetObjectsTagsAsync_NonPrinterType_SkipsAuthorizationFiltering()
    {
        Guid gcodeFileId = Guid.NewGuid();
        var entries = new List<ObjectTagsDto> { new(gcodeFileId, []) };
        _tagServiceMock
            .Setup(s => s.GetObjectsTagsAsync("GcodeFile", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        // Even a resourceAuthorization that would deny everything must not affect
        // GcodeFile/Model3D reads - only "Printer" carries printer-group ACLs today.
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        ActionResult<IEnumerable<ObjectTagsDto>> result =
            await controller.GetObjectsTagsAsync("GcodeFile", CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(entries, okResult.Value);
        resourceAuthorization.Verify(
            r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetObjectsTagsAsync_NoObjectsOfType_ReturnsOkWithEmptyList()
    {
        _tagServiceMock
            .Setup(s => s.GetObjectsTagsAsync("Printer", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        ActionResult<IEnumerable<ObjectTagsDto>> result =
            await _controller.GetObjectsTagsAsync("Printer", CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<ObjectTagsDto>>(okResult.Value));
    }

    [Fact]
    public async Task GetObjectsTagsAsync_WhenServiceThrows_ReturnsInternalServerError()
    {
        _tagServiceMock
            .Setup(s => s.GetObjectsTagsAsync("Printer", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        ActionResult<IEnumerable<ObjectTagsDto>> result =
            await _controller.GetObjectsTagsAsync("Printer", CancellationToken.None);

        ObjectResult statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, statusResult.StatusCode);
    }

    #endregion

    #region AssignTagToObjectAsync Tests

    [Fact]
    public async Task AssignTagToObjectAsync_PrinterType_Authorized_AssignsAndReturnsOk()
    {
        Guid printerId = Guid.NewGuid();
        Guid tagId = Guid.NewGuid();
        var tag = new TagDto { Id = tagId, Name = "Maintenance" };
        _tagServiceMock
            .Setup(s => s.GetTagByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), printerId, PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        IActionResult result = await controller.AssignTagToObjectAsync(printerId, tagId, "Printer", CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(tag, okResult.Value);
        _tagServiceMock.Verify(s => s.AssignTagAsync(printerId, tagId, "Printer", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AssignTagToObjectAsync_PrinterType_Denied_ReturnsNotFoundWithoutAssigning()
    {
        Guid printerId = Guid.NewGuid();
        Guid tagId = Guid.NewGuid();
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), printerId, PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        IActionResult result = await controller.AssignTagToObjectAsync(printerId, tagId, "Printer", CancellationToken.None);

        NotFoundResult notFound = Assert.IsType<NotFoundResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        _tagServiceMock.Verify(
            s => s.AssignTagAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AssignTagToObjectAsync_PrinterType_WithoutResourceAuthorization_FallsOpenAndAssigns()
    {
        Guid printerId = Guid.NewGuid();
        Guid tagId = Guid.NewGuid();
        var tag = new TagDto { Id = tagId, Name = "Maintenance" };
        _tagServiceMock
            .Setup(s => s.GetTagByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);

        // The default test controller is built without a resourceAuthorization dependency
        // (mirrors production DI resolving none registered): filtering must degrade to a
        // pure passthrough rather than denying every printer mutation.
        IActionResult result = await _controller.AssignTagToObjectAsync(printerId, tagId, "Printer", CancellationToken.None);

        _ = Assert.IsType<OkObjectResult>(result);
        _tagServiceMock.Verify(s => s.AssignTagAsync(printerId, tagId, "Printer", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AssignTagToObjectAsync_NonPrinterType_SkipsAuthorizationCheck()
    {
        Guid modelId = Guid.NewGuid();
        Guid tagId = Guid.NewGuid();
        var tag = new TagDto { Id = tagId, Name = "Draft" };
        _tagServiceMock
            .Setup(s => s.GetTagByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);

        // Even a resourceAuthorization that would deny everything must not affect Model3D/GcodeFile
        // assigns - only "Printer" carries printer-group ACLs today.
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        IActionResult result = await controller.AssignTagToObjectAsync(modelId, tagId, "Model3D", CancellationToken.None);

        _ = Assert.IsType<OkObjectResult>(result);
        _tagServiceMock.Verify(s => s.AssignTagAsync(modelId, tagId, "Model3D", It.IsAny<CancellationToken>()), Times.Once);
        resourceAuthorization.Verify(
            r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region RemoveTagFromObjectAsync Tests

    [Fact]
    public async Task RemoveTagFromObjectAsync_PrinterType_Authorized_RemovesAndReturnsNoContent()
    {
        Guid printerId = Guid.NewGuid();
        Guid tagId = Guid.NewGuid();
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), printerId, PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        IActionResult result = await controller.RemoveTagFromObjectAsync(printerId, tagId, "Printer", CancellationToken.None);

        _ = Assert.IsType<NoContentResult>(result);
        _tagServiceMock.Verify(s => s.RemoveTagAsync(printerId, tagId, "Printer", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveTagFromObjectAsync_PrinterType_Denied_ReturnsNotFoundWithoutRemoving()
    {
        Guid printerId = Guid.NewGuid();
        Guid tagId = Guid.NewGuid();
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), printerId, PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        IActionResult result = await controller.RemoveTagFromObjectAsync(printerId, tagId, "Printer", CancellationToken.None);

        NotFoundResult notFound = Assert.IsType<NotFoundResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        _tagServiceMock.Verify(
            s => s.RemoveTagAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveTagFromObjectAsync_PrinterType_WithoutResourceAuthorization_FallsOpenAndRemoves()
    {
        Guid printerId = Guid.NewGuid();
        Guid tagId = Guid.NewGuid();

        // Mirrors production DI resolving none registered: must fall open, not deny everyone.
        IActionResult result = await _controller.RemoveTagFromObjectAsync(printerId, tagId, "Printer", CancellationToken.None);

        _ = Assert.IsType<NoContentResult>(result);
        _tagServiceMock.Verify(s => s.RemoveTagAsync(printerId, tagId, "Printer", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveTagFromObjectAsync_NonPrinterType_SkipsAuthorizationCheck()
    {
        Guid gcodeFileId = Guid.NewGuid();
        Guid tagId = Guid.NewGuid();
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        IActionResult result = await controller.RemoveTagFromObjectAsync(gcodeFileId, tagId, "GcodeFile", CancellationToken.None);

        _ = Assert.IsType<NoContentResult>(result);
        _tagServiceMock.Verify(s => s.RemoveTagAsync(gcodeFileId, tagId, "GcodeFile", It.IsAny<CancellationToken>()), Times.Once);
        resourceAuthorization.Verify(
            r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region GetObjectTagsAsync (single object) Tests

    [Fact]
    public async Task GetObjectTagsAsync_PrinterType_Authorized_ReturnsOkWithTags()
    {
        Guid printerId = Guid.NewGuid();
        var tags = new List<TagDto> { new() { Id = Guid.NewGuid(), Name = "Maintenance" } };
        _tagServiceMock
            .Setup(s => s.GetObjectTagsAsync(printerId, "Printer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(tags);
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), printerId, PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        ActionResult<IEnumerable<TagDto>> result =
            await controller.GetObjectTagsAsync(printerId, "Printer", CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(tags, okResult.Value);
    }

    [Fact]
    public async Task GetObjectTagsAsync_PrinterType_Denied_ReturnsNotFoundWithoutCallingService()
    {
        Guid printerId = Guid.NewGuid();
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), printerId, PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        ActionResult<IEnumerable<TagDto>> result =
            await controller.GetObjectTagsAsync(printerId, "Printer", CancellationToken.None);

        NotFoundResult notFound = Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        _tagServiceMock.Verify(
            s => s.GetObjectTagsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetObjectTagsAsync_PrinterType_WithoutResourceAuthorization_FallsOpenAndReturnsTags()
    {
        Guid printerId = Guid.NewGuid();
        var tags = new List<TagDto> { new() { Id = Guid.NewGuid(), Name = "Maintenance" } };
        _tagServiceMock
            .Setup(s => s.GetObjectTagsAsync(printerId, "Printer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(tags);

        // Mirrors production DI resolving none registered: must fall open, not hide every printer.
        ActionResult<IEnumerable<TagDto>> result =
            await _controller.GetObjectTagsAsync(printerId, "Printer", CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(tags, okResult.Value);
    }

    [Fact]
    public async Task GetObjectTagsAsync_NonPrinterType_SkipsAuthorizationCheck()
    {
        Guid gcodeFileId = Guid.NewGuid();
        var tags = new List<TagDto> { new() { Id = Guid.NewGuid(), Name = "Draft" } };
        _tagServiceMock
            .Setup(s => s.GetObjectTagsAsync(gcodeFileId, "GcodeFile", It.IsAny<CancellationToken>()))
            .ReturnsAsync(tags);

        // Even a resourceAuthorization that would deny everything must not affect Model3D/GcodeFile
        // reads - only "Printer" carries printer-group ACLs today.
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TagsController controller = CreateControllerWithAuthorization(_tagServiceMock, resourceAuthorization.Object);

        ActionResult<IEnumerable<TagDto>> result =
            await controller.GetObjectTagsAsync(gcodeFileId, "GcodeFile", CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(tags, okResult.Value);
        resourceAuthorization.Verify(
            r => r.CanAccessPrinterAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion
}

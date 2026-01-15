using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Tags;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class TagServiceTests
{
    private readonly Mock<ITagRepository> _tagRepository;
    private readonly Mock<ITagMappingRepository> _mappingRepository;
    private readonly Mock<IUnitOfWork> _unitOfWork;
    private readonly Mock<IUnifiedLoggingService> _logger;
    private readonly TagService _service;

    public TagServiceTests()
    {
        _tagRepository = new Mock<ITagRepository>();
        _mappingRepository = new Mock<ITagMappingRepository>();
        _unitOfWork = new Mock<IUnitOfWork>();
        _logger = new Mock<IUnifiedLoggingService>();

        _service = new TagService(
            _tagRepository.Object,
            _mappingRepository.Object,
            _unitOfWork.Object,
            _logger.Object);
    }

    #region GetAllTagsAsync Tests

    [Fact]
    public async Task GetAllTagsAsync_WithNoTags_ReturnsEmptyList()
    {
        // Arrange
        _tagRepository.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Tag>());

        // Act
        var result = await _service.GetAllTagsAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllTagsAsync_WithMultipleTags_ReturnsAllTags()
    {
        // Arrange
        var tags = new[]
        {
            new Tag { Id = Guid.NewGuid(), Name = "Support", Color = "#FF0000", Description = "Support material" },
            new Tag { Id = Guid.NewGuid(), Name = "Finish", Color = "#00FF00", Description = "Post-processing" },
            new Tag { Id = Guid.NewGuid(), Name = "Complex", Color = "#0000FF", Description = "Complex geometry" }
        };

        _tagRepository.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tags);

        // Act
        var result = await _service.GetAllTagsAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("Support", result[0].Name);
        Assert.Equal("Finish", result[1].Name);
        Assert.Equal("Complex", result[2].Name);
    }

    #endregion

    #region GetTagByIdAsync Tests

    [Fact]
    public async Task GetTagByIdAsync_WithValidId_ReturnsTag()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var tag = new Tag { Id = tagId, Name = "Support", Color = "#FF0000", Description = "Support material" };

        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);

        // Act
        var result = await _service.GetTagByIdAsync(tagId, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(tagId, result.Id);
        Assert.Equal("Support", result.Name);
    }

    [Fact]
    public async Task GetTagByIdAsync_WithInvalidId_ReturnsNull()
    {
        // Arrange
        var tagId = Guid.NewGuid();

        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tag?)null);

        // Act
        var result = await _service.GetTagByIdAsync(tagId, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region CreateTagAsync Tests

    [Fact]
    public async Task CreateTagAsync_WithValidNewTag_CreatesAndReturnsTag()
    {
        // Arrange
        var dto = new CreateTagDto { Name = "support", Color = "#FF0000", Description = "Support material" };

        _tagRepository.Setup(r => r.GetByNameAsync("Support", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tag?)null);
        _tagRepository.Setup(r => r.AddAsync(It.IsAny<Tag>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Support", result.Name);
        Assert.Equal("#FF0000", result.Color);
        Assert.Equal("Support material", result.Description);
        _tagRepository.Verify(r => r.AddAsync(It.IsAny<Tag>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTagAsync_WithDuplicateName_ThrowsException()
    {
        // Arrange
        var existingTag = new Tag { Id = Guid.NewGuid(), Name = "Support", Color = "#FF0000", Description = "Support material" };
        var dto = new CreateTagDto { Name = "support", Color = "#00FF00", Description = "Different support" };

        _tagRepository.Setup(r => r.GetByNameAsync("Support", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTag);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateTagAsync(dto, CancellationToken.None));
    }

    #endregion

    #region DeleteTagAsync Tests

    [Fact]
    public async Task DeleteTagAsync_WithValidId_DeletesTag()
    {
        // Arrange
        var tagId = Guid.NewGuid();

        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tag { Id = tagId, Name = "Support", Color = "#FF0000", Description = "Support material" });
        _mappingRepository.Setup(r => r.RemoveByTagAsync(tagId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.RemoveAsync(It.IsAny<Tag>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.DeleteTagAsync(tagId, CancellationToken.None);

        // Assert
        _mappingRepository.Verify(r => r.RemoveByTagAsync(tagId, It.IsAny<CancellationToken>()), Times.Once);
        _tagRepository.Verify(r => r.RemoveAsync(It.IsAny<Tag>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region AssignTagAsync Tests

    [Fact]
    public async Task AssignTagAsync_WithValidObjectTypeAndId_AssignsTag()
    {
        // Arrange
        var objectType = "Model3D";
        var objectId = Guid.NewGuid();
        var tagId = Guid.NewGuid();

        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tag { Id = tagId, Name = "Support", Color = "#FF0000", Description = "Support material" });
        _mappingRepository.Setup(r => r.GetMappingAsync(objectId, tagId, objectType, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TagMapping?)null);
        _mappingRepository.Setup(r => r.AddAsync(It.IsAny<TagMapping>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mappingRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.AssignTagAsync(objectId, tagId, objectType, CancellationToken.None);

        // Assert
        _mappingRepository.Verify(r => r.AddAsync(It.IsAny<TagMapping>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region RemoveTagAsync Tests

    [Fact]
    public async Task RemoveTagAsync_WithValidObjectTypeAndId_RemovesTag()
    {
        // Arrange
        var objectType = "Model3D";
        var objectId = Guid.NewGuid();
        var tagId = Guid.NewGuid();

        _mappingRepository.Setup(r => r.RemoveByObjectAndTagAsync(objectId, tagId, objectType, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mappingRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.RemoveTagAsync(objectId, tagId, objectType, CancellationToken.None);

        // Assert
        _mappingRepository.Verify(r => r.RemoveByObjectAndTagAsync(objectId, tagId, objectType, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetObjectTagsAsync Tests

    [Fact]
    public async Task GetObjectTagsAsync_WithValidObject_ReturnsTags()
    {
        // Arrange
        var objectType = "Model3D";
        var objectId = Guid.NewGuid();
        var tagId1 = Guid.NewGuid();
        var tagId2 = Guid.NewGuid();

        _mappingRepository.Setup(r => r.GetByObjectAsync(objectId, objectType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TagMapping { Id = Guid.NewGuid(), ObjectType = objectType, ObjectId = objectId, TagId = tagId1 },
                new TagMapping { Id = Guid.NewGuid(), ObjectType = objectType, ObjectId = objectId, TagId = tagId2 }
            });

        // Act
        var result = await _service.GetObjectTagsAsync(objectId, objectType, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Contains(tagId1, result.Select(t => t.Id));
        Assert.Contains(tagId2, result.Select(t => t.Id));
    }

    #endregion

    #region FilterObjectsByTagsAsync Tests

    [Fact]
    public async Task FilterObjectsByTagsAsync_WithIncludeTags_ReturnsObjectsWithTags()
    {
        // Arrange
        var objectType = "Model3D";
        var tagId1 = Guid.NewGuid();
        var objectId1 = Guid.NewGuid();
        var objectId2 = Guid.NewGuid();

        _mappingRepository.Setup(r => r.GetByTagIdAndObjectTypeAsync(tagId1, objectType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TagMapping { Id = Guid.NewGuid(), ObjectType = objectType, ObjectId = objectId1, TagId = tagId1 },
                new TagMapping { Id = Guid.NewGuid(), ObjectType = objectType, ObjectId = objectId2, TagId = tagId1 }
            });

        // Act
        var result = await _service.FilterObjectsByTagsAsync(objectType, new[] { tagId1 }, null, false, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Contains(objectId1, result);
        Assert.Contains(objectId2, result);
    }

    #endregion
}

using Farm.Infrastructure;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Tags;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

public class TagServiceTests
{
    private readonly Mock<ITagRepository> _tagRepository;
    private readonly Mock<IUnifiedLoggingService> _logger;
    private readonly TagService _service;

    public TagServiceTests()
    {
        _tagRepository = new Mock<ITagRepository>();
        _logger = new Mock<IUnifiedLoggingService>();

        _service = new TagService(
            _tagRepository.Object,
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
        IReadOnlyList<TagDto> result = await _service.GetAllTagsAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllTagsAsync_WithTags_ReturnsDtos()
    {
        // Arrange
        var tag1 = new Tag { Id = Guid.NewGuid(), Name = "Tag1", Color = "#FF0000", CreatedAt = DateTime.UtcNow };
        var tag2 = new Tag { Id = Guid.NewGuid(), Name = "Tag2", Color = "#00FF00", CreatedAt = DateTime.UtcNow };
        _tagRepository.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { tag1, tag2 });

        // Act
        IReadOnlyList<TagDto> result = await _service.GetAllTagsAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(tag1.Name, result[0].Name);
        Assert.Equal(tag2.Name, result[1].Name);
    }

    #endregion

    #region GetTagByIdAsync Tests

    [Fact]
    public async Task GetTagByIdAsync_WithExistingTag_ReturnsTagDto()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var tag = new Tag { Id = tagId, Name = "TestTag", Color = "#FF0000", CreatedAt = DateTime.UtcNow };
        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);

        // Act
        TagDto? result = await _service.GetTagByIdAsync(tagId, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(tag.Id, result.Id);
        Assert.Equal(tag.Name, result.Name);
    }

    [Fact]
    public async Task GetTagByIdAsync_WithNonExistingTag_ReturnsNull()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tag?)null);

        // Act
        TagDto? result = await _service.GetTagByIdAsync(tagId, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region CreateTagAsync Tests

    [Fact]
    public async Task CreateTagAsync_WithValidInput_CreatesAndReturnsTag()
    {
        // Arrange
        var dto = new CreateTagDto { Name = "NewTag", Color = "#FF0000", Description = "Test" };
        // After normalization via ToPascalCase("NewTag"), it becomes "Newtag"
        _tagRepository.Setup(r => r.GetByNameAsync("Newtag", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tag?)null);
        _tagRepository.Setup(r => r.AddAsync(It.IsAny<Tag>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        TagDto result = await _service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Newtag", result.Name);
        _tagRepository.Verify(r => r.AddAsync(It.IsAny<Tag>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTagAsync_WithExistingTag_ReturnsExistingTag()
    {
        // Arrange
        var dto = new CreateTagDto { Name = "ExistingTag", Color = "#FF0000" };
        var existingTagId = Guid.NewGuid();
        // After normalization via ToPascalCase("ExistingTag"), it becomes "Existingtag"
        var existingTag = new Tag { Id = existingTagId, Name = "Existingtag", Color = "#0000FF", CreatedAt = DateTime.UtcNow };
        _tagRepository.Setup(r => r.GetByNameAsync("Existingtag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTag);

        // Act
        TagDto result = await _service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(existingTagId, result.Id);
        Assert.Equal("Existingtag", result.Name);
        _tagRepository.Verify(r => r.AddAsync(It.IsAny<Tag>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region DeleteTagAsync Tests

    [Fact]
    public async Task DeleteTagAsync_WithUsedTag_DeletesTagAndRemovesFromAllObjects()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var tag = new Tag { Id = tagId, Name = "TagToDelete", CreatedAt = DateTime.UtcNow };
        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);
        _tagRepository.Setup(r => r.RemoveAllObjectsFromTagAsync(tagId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.RemoveAsync(tag, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.DeleteTagAsync(tagId, CancellationToken.None);

        // Assert
        _tagRepository.Verify(r => r.RemoveAllObjectsFromTagAsync(tagId, It.IsAny<CancellationToken>()), Times.Once);
        _tagRepository.Verify(r => r.RemoveAsync(tag, It.IsAny<CancellationToken>()), Times.Once);
        _tagRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteTagAsync_WithNonExistingTag_ThrowsKeyNotFoundException()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tag?)null);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.DeleteTagAsync(tagId, CancellationToken.None));
    }

    #endregion

    #region AssignTagAsync Tests

    [Fact]
    public async Task AssignTagAsync_WithValidInput_AssignsTag()
    {
        // Arrange
        var objectId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        string objectType = "Model3D";
        var tag = new Tag { Id = tagId, Name = "TestTag", CreatedAt = DateTime.UtcNow };

        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);
        _tagRepository.Setup(r => r.HasTagAsync(objectId, tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _tagRepository.Setup(r => r.AssignTagAsync(objectId, tagId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.AssignTagAsync(objectId, tagId, objectType, CancellationToken.None);

        // Assert
        _tagRepository.Verify(r => r.AssignTagAsync(objectId, tagId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AssignTagAsync_WhenAlreadyAssigned_DoesNothing()
    {
        // Arrange
        var objectId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        string objectType = "Model3D";
        var tag = new Tag { Id = tagId, Name = "TestTag", CreatedAt = DateTime.UtcNow };

        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);
        _tagRepository.Setup(r => r.HasTagAsync(objectId, tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await _service.AssignTagAsync(objectId, tagId, objectType, CancellationToken.None);

        // Assert
        _tagRepository.Verify(r => r.AssignTagAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region RemoveTagAsync Tests

    [Fact]
    public async Task RemoveTagAsync_WithValidInput_RemovesTag()
    {
        // Arrange
        var objectId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        string objectType = "Model3D";

        _tagRepository.Setup(r => r.RemoveTagAsync(objectId, tagId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.RemoveTagAsync(objectId, tagId, objectType, CancellationToken.None);

        // Assert
        _tagRepository.Verify(r => r.RemoveTagAsync(objectId, tagId, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetObjectTagsAsync Tests

    [Fact]
    public async Task GetObjectTagsAsync_WithTags_ReturnsDtos()
    {
        // Arrange
        var objectId = Guid.NewGuid();
        string objectType = "Model3D";
        Tag[] tags = new[] {
            new Tag { Id = Guid.NewGuid(), Name = "Tag1", Color = "#FF0000", CreatedAt = DateTime.UtcNow },
            new Tag { Id = Guid.NewGuid(), Name = "Tag2", Color = "#00FF00", CreatedAt = DateTime.UtcNow }
        };

        _tagRepository.Setup(r => r.GetTagsByObjectAsync(objectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tags);

        // Act
        IReadOnlyList<TagDto> result = await _service.GetObjectTagsAsync(objectId, objectType, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(tags[0].Name, result[0].Name);
    }

    #endregion
}

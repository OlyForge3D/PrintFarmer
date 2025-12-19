using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Tags;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class TagServiceTests
{
    private readonly Mock<ITagRepository> _tagRepository;
    private readonly Mock<IModelTagMappingRepository> _mappingRepository;
    private readonly Mock<IModelRepository> _modelRepository;
    private readonly Mock<IUnifiedLoggingService> _logger;
    private readonly TagService _service;

    public TagServiceTests()
    {
        _tagRepository = new Mock<ITagRepository>();
        _mappingRepository = new Mock<IModelTagMappingRepository>();
        _modelRepository = new Mock<IModelRepository>();
        _logger = new Mock<IUnifiedLoggingService>();

        _service = new TagService(
            _tagRepository.Object,
            _mappingRepository.Object,
            _modelRepository.Object,
            _logger.Object);
    }

    #region GetAllTagsAsync Tests

    [Fact]
    public async Task GetAllTagsAsync_WithNoTags_ReturnsEmptyList()
    {
        // Arrange
        _tagRepository.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Model3DTag>());

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
            new Model3DTag { Id = Guid.NewGuid(), Name = "Support", Color = "#FF0000", Description = "Support material" },
            new Model3DTag { Id = Guid.NewGuid(), Name = "Finish", Color = "#00FF00", Description = "Post-processing" },
            new Model3DTag { Id = Guid.NewGuid(), Name = "Complex", Color = "#0000FF", Description = "Complex geometry" }
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

    [Fact]
    public async Task GetAllTagsAsync_WithDatabaseError_ThrowsException()
    {
        // Arrange
        _tagRepository.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.GetAllTagsAsync(CancellationToken.None));
    }

    #endregion

    #region GetTagByIdAsync Tests

    [Fact]
    public async Task GetTagByIdAsync_WithValidId_ReturnsTag()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var tag = new Model3DTag { Id = tagId, Name = "Support", Color = "#FF0000", Description = "Support material" };

        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);

        // Act
        var result = await _service.GetTagByIdAsync(tagId, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(tagId, result.Id);
        Assert.Equal("Support", result.Name);
        Assert.Equal("#FF0000", result.Color);
    }

    [Fact]
    public async Task GetTagByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTag?)null);

        // Act
        var result = await _service.GetTagByIdAsync(tagId, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region CreateTagAsync - Validation Tests

    [Fact]
    public async Task CreateTagAsync_WithNullDto_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.CreateTagAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task CreateTagAsync_WithEmptyName_ThrowsArgumentException()
    {
        // Arrange
        var dto = new CreateModel3DTagDto { Name = "", Color = "#FF0000", Description = "Test" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateTagAsync(dto, CancellationToken.None));
    }

    [Fact]
    public async Task CreateTagAsync_WithWhitespaceName_ThrowsArgumentException()
    {
        // Arrange
        var dto = new CreateModel3DTagDto { Name = "   ", Color = "#FF0000", Description = "Test" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateTagAsync(dto, CancellationToken.None));
    }

    [Fact]
    public async Task CreateTagAsync_WithNullName_ThrowsArgumentException()
    {
        // Arrange
        var dto = new CreateModel3DTagDto { Name = null!, Color = "#FF0000", Description = "Test" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateTagAsync(dto, CancellationToken.None));
    }

    #endregion

    #region CreateTagAsync - Normalization Tests

    [Fact]
    public async Task CreateTagAsync_WithLowercaseName_NormalizesToPascalCase()
    {
        // Arrange
        var dto = new CreateModel3DTagDto { Name = "support", Color = "#FF0000", Description = "Test" };

        _tagRepository.Setup(r => r.GetByNameAsync("Support", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTag?)null);
        _tagRepository.Setup(r => r.AddAsync(It.IsAny<Model3DTag>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        Assert.Equal("Support", result.Name);
        _tagRepository.Verify(r => r.GetByNameAsync("Support", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTagAsync_WithSpaceSeparatedName_NormalizesToPascalCase()
    {
        // Arrange
        var dto = new CreateModel3DTagDto { Name = "high detail", Color = "#FF0000", Description = "Test" };

        _tagRepository.Setup(r => r.GetByNameAsync("HighDetail", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTag?)null);
        _tagRepository.Setup(r => r.AddAsync(It.IsAny<Model3DTag>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        Assert.Equal("HighDetail", result.Name);
    }

    [Fact]
    public async Task CreateTagAsync_WithUnderscoreSeparatedName_NormalizesToPascalCase()
    {
        // Arrange
        var dto = new CreateModel3DTagDto { Name = "high_detail", Color = "#FF0000", Description = "Test" };

        _tagRepository.Setup(r => r.GetByNameAsync("HighDetail", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTag?)null);
        _tagRepository.Setup(r => r.AddAsync(It.IsAny<Model3DTag>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        Assert.Equal("HighDetail", result.Name);
    }

    [Fact]
    public async Task CreateTagAsync_WithDashSeparatedName_NormalizesToPascalCase()
    {
        // Arrange
        var dto = new CreateModel3DTagDto { Name = "high-detail", Color = "#FF0000", Description = "Test" };

        _tagRepository.Setup(r => r.GetByNameAsync("HighDetail", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTag?)null);
        _tagRepository.Setup(r => r.AddAsync(It.IsAny<Model3DTag>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        Assert.Equal("HighDetail", result.Name);
    }

    #endregion

    #region CreateTagAsync - Duplicate Handling Tests

    [Fact]
    public async Task CreateTagAsync_WithExistingTag_ReturnsExistingTag()
    {
        // Arrange
        var existingTagId = Guid.NewGuid();
        var dto = new CreateModel3DTagDto { Name = "support", Color = "#FF0000", Description = "Test" };
        var existingTag = new Model3DTag { Id = existingTagId, Name = "Support", Color = "#00FF00", Description = "Existing" };

        _tagRepository.Setup(r => r.GetByNameAsync("Support", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTag);

        // Act
        var result = await _service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        Assert.Equal(existingTagId, result.Id);
        Assert.Equal("Support", result.Name);
        Assert.Equal("#00FF00", result.Color);
        _tagRepository.Verify(r => r.AddAsync(It.IsAny<Model3DTag>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTagAsync_WithRaceCondition_ReturnsExistingTag()
    {
        // Arrange
        var existingTagId = Guid.NewGuid();
        var dto = new CreateModel3DTagDto { Name = "support", Color = "#FF0000", Description = "Test" };
        var existingTag = new Model3DTag { Id = existingTagId, Name = "Support", Color = "#00FF00" };

        _tagRepository.Setup(r => r.GetByNameAsync("Support", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTag?)null); // First check returns null
        _tagRepository.Setup(r => r.AddAsync(It.IsAny<Model3DTag>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Simulate race condition: another thread created the tag between check and insert
        var dbUpdateEx = new DbUpdateException("Duplicate", new Exception("UNIQUE constraint failed"));
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(dbUpdateEx);

        // Tag now exists (created by race condition)
        _tagRepository.Setup(r => r.GetByNameAsync("Support", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTag);

        // Act
        var result = await _service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        Assert.Equal(existingTagId, result.Id);
        Assert.Equal("Support", result.Name);
    }

    #endregion

    #region CreateTagAsync - Success Tests

    [Fact]
    public async Task CreateTagAsync_WithValidNewTag_CreatesAndReturnsTag()
    {
        // Arrange
        var newTagId = Guid.NewGuid();
        var dto = new CreateModel3DTagDto { Name = "support", Color = "#FF0000", Description = "Support material" };

        _tagRepository.Setup(r => r.GetByNameAsync("Support", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTag?)null);
        _tagRepository.Setup(r => r.AddAsync(It.IsAny<Model3DTag>(), It.IsAny<CancellationToken>()))
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
        _tagRepository.Verify(r => r.AddAsync(It.IsAny<Model3DTag>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region DeleteTagAsync Tests

    [Fact]
    public async Task DeleteTagAsync_WithExistingTag_DeletesTag()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var tag = new Model3DTag { Id = tagId, Name = "Support", Color = "#FF0000" };

        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);
        _tagRepository.Setup(r => r.RemoveAsync(tag, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.DeleteTagAsync(tagId, CancellationToken.None);

        // Assert
        _tagRepository.Verify(r => r.RemoveAsync(tag, It.IsAny<CancellationToken>()), Times.Once);
        _tagRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteTagAsync_WithNonExistentTag_ThrowsKeyNotFoundException()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTag?)null);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.DeleteTagAsync(tagId, CancellationToken.None));
    }

    #endregion

    #region AssignTagsToModelAsync Tests

    [Fact]
    public async Task AssignTagsToModelAsync_WithExistingModel_AssignsTags()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var tagId1 = Guid.NewGuid();
        var tagId2 = Guid.NewGuid();

        var model = new Model3D { Id = modelId, DisplayName = "Test Model", OriginalFileName = "test.stl", FilePath = "/models/test.stl" };
        var tag1 = new Model3DTag { Id = tagId1, Name = "Support" };
        var tag2 = new Model3DTag { Id = tagId2, Name = "Detail" };

        _modelRepository.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        _mappingRepository.Setup(r => r.RemoveByModelIdAsync(modelId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.GetByIdAsync(tagId1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag1);
        _tagRepository.Setup(r => r.GetByIdAsync(tagId2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag2);
        _mappingRepository.Setup(r => r.AddAsync(It.IsAny<Model3DTagMapping>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mappingRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.AssignTagsToModelAsync(modelId, new[] { tagId1, tagId2 }, CancellationToken.None);

        // Assert
        _mappingRepository.Verify(r => r.AddAsync(It.IsAny<Model3DTagMapping>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AssignTagsToModelAsync_WithNonExistentModel_ThrowsKeyNotFoundException()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var tagId = Guid.NewGuid();

        _modelRepository.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3D?)null);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => 
            _service.AssignTagsToModelAsync(modelId, new[] { tagId }, CancellationToken.None));
    }

    [Fact]
    public async Task AssignTagsToModelAsync_WithNonExistentTag_SkipsInvalidTag()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var validTagId = Guid.NewGuid();
        var invalidTagId = Guid.NewGuid();

        var model = new Model3D { Id = modelId, DisplayName = "Test Model", OriginalFileName = "test.stl", FilePath = "/models/test.stl" };
        var validTag = new Model3DTag { Id = validTagId, Name = "Support" };

        _modelRepository.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        _mappingRepository.Setup(r => r.RemoveByModelIdAsync(modelId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tagRepository.Setup(r => r.GetByIdAsync(validTagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(validTag);
        _tagRepository.Setup(r => r.GetByIdAsync(invalidTagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTag?)null);
        _mappingRepository.Setup(r => r.AddAsync(It.IsAny<Model3DTagMapping>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mappingRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.AssignTagsToModelAsync(modelId, new[] { validTagId, invalidTagId }, CancellationToken.None);

        // Assert - only 1 tag added (invalid tag was skipped)
        _mappingRepository.Verify(r => r.AddAsync(It.IsAny<Model3DTagMapping>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AssignTagsToModelAsync_WithEmptyTagList_ClearsExistingTags()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var model = new Model3D { Id = modelId, DisplayName = "Test Model", OriginalFileName = "test.stl", FilePath = "/models/test.stl" };

        _modelRepository.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        _mappingRepository.Setup(r => r.RemoveByModelIdAsync(modelId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mappingRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.AssignTagsToModelAsync(modelId, Array.Empty<Guid>(), CancellationToken.None);

        // Assert
        _mappingRepository.Verify(r => r.RemoveByModelIdAsync(modelId, It.IsAny<CancellationToken>()), Times.Once);
        _mappingRepository.Verify(r => r.AddAsync(It.IsAny<Model3DTagMapping>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region RemoveTagFromModelAsync Tests

    [Fact]
    public async Task RemoveTagFromModelAsync_WithExistingMapping_RemovesTag()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var mapping = new Model3DTagMapping { Id = Guid.NewGuid(), Model3DId = modelId, TagId = tagId };

        _mappingRepository.Setup(r => r.GetMappingAsync(modelId, tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mapping);
        _mappingRepository.Setup(r => r.RemoveAsync(mapping, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mappingRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.RemoveTagFromModelAsync(modelId, tagId, CancellationToken.None);

        // Assert
        _mappingRepository.Verify(r => r.RemoveAsync(mapping, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveTagFromModelAsync_WithNonExistentMapping_ThrowsKeyNotFoundException()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var tagId = Guid.NewGuid();

        _mappingRepository.Setup(r => r.GetMappingAsync(modelId, tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3DTagMapping?)null);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => 
            _service.RemoveTagFromModelAsync(modelId, tagId, CancellationToken.None));
    }

    #endregion

    #region GetModelTagsAsync Tests

    [Fact]
    public async Task GetModelTagsAsync_WithNoTags_ReturnsEmptyList()
    {
        // Arrange
        var modelId = Guid.NewGuid();

        _mappingRepository.Setup(r => r.GetByModelIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Model3DTagMapping>());

        // Act
        var result = await _service.GetModelTagsAsync(modelId, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetModelTagsAsync_WithMultipleTags_ReturnsAllTags()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var tagId1 = Guid.NewGuid();
        var tagId2 = Guid.NewGuid();

        var mappings = new[]
        {
            new Model3DTagMapping { Id = Guid.NewGuid(), Model3DId = modelId, TagId = tagId1 },
            new Model3DTagMapping { Id = Guid.NewGuid(), Model3DId = modelId, TagId = tagId2 }
        };

        var tags = new[]
        {
            new Model3DTag { Id = tagId1, Name = "Support" },
            new Model3DTag { Id = tagId2, Name = "Detail" }
        };

        _mappingRepository.Setup(r => r.GetByModelIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mappings);
        _tagRepository.Setup(r => r.GetByIdAsync(tagId1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tags[0]);
        _tagRepository.Setup(r => r.GetByIdAsync(tagId2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tags[1]);

        // Act
        var result = await _service.GetModelTagsAsync(modelId, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("Support", result[0].Name);
        Assert.Equal("Detail", result[1].Name);
    }

    #endregion

    #region BulkAssignTagsAsync Tests

    [Fact]
    public async Task BulkAssignTagsAsync_WithMultipleModels_AssignsTagsToEach()
    {
        // Arrange
        var modelId1 = Guid.NewGuid();
        var modelId2 = Guid.NewGuid();
        var tagId = Guid.NewGuid();

        var model1 = new Model3D { Id = modelId1, DisplayName = "Model1", OriginalFileName = "model1.stl", FilePath = "/models/model1.stl" };
        var model2 = new Model3D { Id = modelId2, DisplayName = "Model2", OriginalFileName = "model2.stl", FilePath = "/models/model2.stl" };
        var tag = new Model3DTag { Id = tagId, Name = "Support" };

        _modelRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken ct) => id == modelId1 ? model1 : (id == modelId2 ? model2 : null));

        _mappingRepository.Setup(r => r.RemoveByModelIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _tagRepository.Setup(r => r.GetByIdAsync(tagId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tag);

        _mappingRepository.Setup(r => r.AddAsync(It.IsAny<Model3DTagMapping>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mappingRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.BulkAssignTagsAsync(new[] { modelId1, modelId2 }, new[] { tagId }, CancellationToken.None);

        // Assert
        _mappingRepository.Verify(r => r.RemoveByModelIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task BulkAssignTagsAsync_WithEmptyModelList_DoesNothing()
    {
        // Act
        await _service.BulkAssignTagsAsync(Array.Empty<Guid>(), new[] { Guid.NewGuid() }, CancellationToken.None);

        // Assert
        _modelRepository.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Tags;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for TagService
/// Tests tag management, CRUD operations, model-tag assignments, and bulk operations
/// Fast executing (~4-5 seconds for 24 tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class TagServiceIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public TagServiceIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    private async Task<Model3D> CreateTestModelAsync(string displayName = "test-model")
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            OriginalFileName = $"{displayName}.stl",
            DisplayName = displayName,
            FilePath = $"/models/{displayName}.stl",
            FileSizeBytes = 1024,
            FileHash = Guid.NewGuid().ToString(),
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Models3D.Add(model);
        await context.SaveChangesAsync();
        return model;
    }

    private async Task<Model3DTagDto> CreateTestTagAsync(string? name = null)
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var uniqueName = name ?? $"tag-{Guid.NewGuid().ToString().Substring(0, 8)}";
        var dto = new CreateModel3DTagDto
        {
            Name = uniqueName,
            Color = "#FF5733",
            Description = "Test tag description"
        };

        return await service.CreateTagAsync(dto, CancellationToken.None);
    }

    #region GetAllTagsAsync Tests

    [Fact]
    public async Task GetAllTagsAsync_WhenEmpty_ReturnsEmptyList()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        // Clear existing tags
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Model3DTags.RemoveRange(context.Model3DTags);
        await context.SaveChangesAsync();

        // Act
        var result = await service.GetAllTagsAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Count.Should().Be(0);
    }

    [Fact]
    public async Task GetAllTagsAsync_WithExistingTags_ReturnsAllTags()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var tag1 = await CreateTestTagAsync($"tag-{Guid.NewGuid().ToString().Substring(0, 8)}");
        var tag2 = await CreateTestTagAsync($"tag-{Guid.NewGuid().ToString().Substring(0, 8)}");

        // Act
        var result = await service.GetAllTagsAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Count.Should().BeGreaterThanOrEqualTo(2);
        result.Should().Contain(t => t.Id == tag1.Id);
        result.Should().Contain(t => t.Id == tag2.Id);
    }

    [Fact]
    public async Task GetAllTagsAsync_IncludesTagProperties()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var tagName = $"prop-test-{Guid.NewGuid().ToString().Substring(0, 8)}";
        var created = await CreateTestTagAsync(tagName);

        // Act
        var result = await service.GetAllTagsAsync(CancellationToken.None);

        // Assert
        var tag = result.FirstOrDefault(t => t.Id == created.Id);
        tag.Should().NotBeNull();
        tag!.Name.Should().NotBeNullOrEmpty();
        tag.Color.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region GetTagByIdAsync Tests

    [Fact]
    public async Task GetTagByIdAsync_WithValidId_ReturnsTag()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var created = await CreateTestTagAsync();

        // Act
        var result = await service.GetTagByIdAsync(created.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(created.Id);
        result.Name.Should().Be(created.Name);
    }

    [Fact]
    public async Task GetTagByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await service.GetTagByIdAsync(nonExistentId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateTagAsync Tests

    [Fact]
    public async Task CreateTagAsync_WithValidRequest_CreatesTag()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var dto = new CreateModel3DTagDto
        {
            Name = "SimpleTag",
            Color = "#FF5733",
            Description = "New tag"
        };

        // Act
        var result = await service.CreateTagAsync(dto, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().NotBe(Guid.Empty);
        result.Name.Should().Be("Simpletag"); // Should be normalized to PascalCase
        result.Color.Should().Be("#FF5733");
        result.Description.Should().Be("New tag");
    }

    [Fact]
    public async Task CreateTagAsync_NormalizesNameToPascalCase()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var testCases = new[]
        {
            ("my tag", "MyTag"),
            ("my-tag", "MyTag"),
            ("my_tag", "MyTag"),
            ("MYTAG", "Mytag"),
            ("my  tag", "MyTag")
        };

        foreach (var (input, expected) in testCases)
        {
            // Act
            var dto = new CreateModel3DTagDto
            {
                Name = input,
                Color = "#000000",
                Description = ""
            };
            var result = await service.CreateTagAsync(dto, CancellationToken.None);

            // Assert
            result.Name.Should().Be(expected, $"Input '{input}' should normalize to '{expected}'");
        }
    }

    [Fact]
    public async Task CreateTagAsync_WithNullRequest_ThrowsArgumentNullException()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.CreateTagAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task CreateTagAsync_WithEmptyName_ThrowsArgumentException()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var dto = new CreateModel3DTagDto
        {
            Name = "   ",
            Color = "#000000",
            Description = ""
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateTagAsync(dto, CancellationToken.None));
    }

    [Fact]
    public async Task CreateTagAsync_WithDuplicateName_ReturnExistingTag()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var tagName = $"dup-tag-{Guid.NewGuid().ToString().Substring(0, 8)}";
        var dto = new CreateModel3DTagDto
        {
            Name = tagName,
            Color = "#FF0000",
            Description = "Original"
        };

        var first = await service.CreateTagAsync(dto, CancellationToken.None);

        // Act - Create with same name (different case/formatting)
        var dto2 = new CreateModel3DTagDto
        {
            Name = tagName.ToUpper(),
            Color = "#00FF00",
            Description = "Different"
        };
        var second = await service.CreateTagAsync(dto2, CancellationToken.None);

        // Assert - Should return existing tag
        second.Id.Should().Be(first.Id);
        second.Name.Should().Be(first.Name);
    }

    #endregion

    #region DeleteTagAsync Tests

    [Fact]
    public async Task DeleteTagAsync_WithValidId_DeletesTag()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var created = await CreateTestTagAsync();

        // Act
        await service.DeleteTagAsync(created.Id, CancellationToken.None);

        // Assert
        var result = await service.GetTagByIdAsync(created.Id, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteTagAsync_WithNonExistentId_ThrowsKeyNotFoundException()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var nonExistentId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.DeleteTagAsync(nonExistentId, CancellationToken.None));
    }

    #endregion

    #region AssignTagsToModelAsync Tests

    [Fact]
    public async Task AssignTagsToModelAsync_WithValidTags_AssignsTags()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model = await CreateTestModelAsync();
        var tag1 = await CreateTestTagAsync();
        var tag2 = await CreateTestTagAsync();

        // Act
        await service.AssignTagsToModelAsync(model.Id, new[] { tag1.Id, tag2.Id }, CancellationToken.None);

        // Assert
        var tags = await service.GetModelTagsAsync(model.Id, CancellationToken.None);
        tags.Should().HaveCount(2);
        tags.Should().Contain(t => t.Id == tag1.Id);
        tags.Should().Contain(t => t.Id == tag2.Id);
    }

    [Fact]
    public async Task AssignTagsToModelAsync_WithNonExistentModel_ThrowsKeyNotFoundException()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var tag = await CreateTestTagAsync();
        var nonExistentModelId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.AssignTagsToModelAsync(nonExistentModelId, new[] { tag.Id }, CancellationToken.None));
    }

    [Fact]
    public async Task AssignTagsToModelAsync_ReplacesExistingTags()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model = await CreateTestModelAsync();
        var tag1 = await CreateTestTagAsync();
        var tag2 = await CreateTestTagAsync();
        var tag3 = await CreateTestTagAsync();

        // Assign initial tags
        await service.AssignTagsToModelAsync(model.Id, new[] { tag1.Id, tag2.Id }, CancellationToken.None);

        // Act - Replace with different tags
        await service.AssignTagsToModelAsync(model.Id, new[] { tag3.Id }, CancellationToken.None);

        // Assert
        var tags = await service.GetModelTagsAsync(model.Id, CancellationToken.None);
        tags.Should().HaveCount(1);
        tags.Should().Contain(t => t.Id == tag3.Id);
        tags.Should().NotContain(t => t.Id == tag1.Id);
    }

    [Fact]
    public async Task AssignTagsToModelAsync_WithEmptyList_ClearsTags()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model = await CreateTestModelAsync();
        var tag = await CreateTestTagAsync();

        // Assign initial tag
        await service.AssignTagsToModelAsync(model.Id, new[] { tag.Id }, CancellationToken.None);

        // Act - Clear tags
        await service.AssignTagsToModelAsync(model.Id, new List<Guid>(), CancellationToken.None);

        // Assert
        var tags = await service.GetModelTagsAsync(model.Id, CancellationToken.None);
        tags.Should().HaveCount(0);
    }

    #endregion

    #region RemoveTagFromModelAsync Tests

    [Fact]
    public async Task RemoveTagFromModelAsync_WithAssignedTag_RemovesTag()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model = await CreateTestModelAsync();
        var tag1 = await CreateTestTagAsync();
        var tag2 = await CreateTestTagAsync();

        await service.AssignTagsToModelAsync(model.Id, new[] { tag1.Id, tag2.Id }, CancellationToken.None);

        // Act
        await service.RemoveTagFromModelAsync(model.Id, tag1.Id, CancellationToken.None);

        // Assert
        var tags = await service.GetModelTagsAsync(model.Id, CancellationToken.None);
        tags.Should().HaveCount(1);
        tags.Should().Contain(t => t.Id == tag2.Id);
        tags.Should().NotContain(t => t.Id == tag1.Id);
    }

    [Fact]
    public async Task RemoveTagFromModelAsync_WithUnassignedTag_ThrowsKeyNotFoundException()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model = await CreateTestModelAsync();
        var unassignedTag = await CreateTestTagAsync();

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.RemoveTagFromModelAsync(model.Id, unassignedTag.Id, CancellationToken.None));
    }

    #endregion

    #region GetModelTagsAsync Tests

    [Fact]
    public async Task GetModelTagsAsync_WithNoTags_ReturnsEmptyList()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model = await CreateTestModelAsync();

        // Act
        var result = await service.GetModelTagsAsync(model.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(0);
    }

    [Fact]
    public async Task GetModelTagsAsync_WithAssignedTags_ReturnsAllTags()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model = await CreateTestModelAsync();
        var tag1 = await CreateTestTagAsync();
        var tag2 = await CreateTestTagAsync();
        var tag3 = await CreateTestTagAsync();

        await service.AssignTagsToModelAsync(model.Id, new[] { tag1.Id, tag2.Id, tag3.Id }, CancellationToken.None);

        // Act
        var result = await service.GetModelTagsAsync(model.Id, CancellationToken.None);

        // Assert
        result.Should().HaveCount(3);
        result.Select(t => t.Id).Should().Contain(new[] { tag1.Id, tag2.Id, tag3.Id });
    }

    #endregion

    #region BulkAssignTagsAsync Tests

    [Fact]
    public async Task BulkAssignTagsAsync_WithMultipleModels_AssignsToAll()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model1 = await CreateTestModelAsync();
        var model2 = await CreateTestModelAsync();
        var model3 = await CreateTestModelAsync();
        var tag1 = await CreateTestTagAsync();
        var tag2 = await CreateTestTagAsync();

        // Act
        await service.BulkAssignTagsAsync(
            new[] { model1.Id, model2.Id, model3.Id },
            new[] { tag1.Id, tag2.Id },
            CancellationToken.None);

        // Assert
        var tags1 = await service.GetModelTagsAsync(model1.Id, CancellationToken.None);
        var tags2 = await service.GetModelTagsAsync(model2.Id, CancellationToken.None);
        var tags3 = await service.GetModelTagsAsync(model3.Id, CancellationToken.None);

        tags1.Should().HaveCount(2);
        tags2.Should().HaveCount(2);
        tags3.Should().HaveCount(2);
    }

    [Fact]
    public async Task BulkAssignTagsAsync_WithEmptyModelList_DoesNothing()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var tag = await CreateTestTagAsync();

        // Act
        await service.BulkAssignTagsAsync(
            new List<Guid>(),
            new[] { tag.Id },
            CancellationToken.None);

        // Assert - Should not throw
    }

    [Fact]
    public async Task BulkAssignTagsAsync_WithEmptyTagList_ClearsAllTags()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model1 = await CreateTestModelAsync();
        var model2 = await CreateTestModelAsync();
        var tag = await CreateTestTagAsync();

        // Assign initial tags
        await service.AssignTagsToModelAsync(model1.Id, new[] { tag.Id }, CancellationToken.None);
        await service.AssignTagsToModelAsync(model2.Id, new[] { tag.Id }, CancellationToken.None);

        // Act - Clear all tags
        await service.BulkAssignTagsAsync(
            new[] { model1.Id, model2.Id },
            new List<Guid>(),
            CancellationToken.None);

        // Assert
        var tags1 = await service.GetModelTagsAsync(model1.Id, CancellationToken.None);
        var tags2 = await service.GetModelTagsAsync(model2.Id, CancellationToken.None);

        tags1.Should().HaveCount(0);
        tags2.Should().HaveCount(0);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task CreateTag_ThenAssignToModel_ThenRemove_CompleteWorkflow()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var model = await CreateTestModelAsync();
        var tagName = $"workflow-{Guid.NewGuid().ToString().Substring(0, 8)}";
        var dto = new CreateModel3DTagDto
        {
            Name = tagName,
            Color = "#FF0000",
            Description = "Workflow tag"
        };

        // Create tag
        var tag = await service.CreateTagAsync(dto, CancellationToken.None);
        tag.Should().NotBeNull();

        // Assign to model
        await service.AssignTagsToModelAsync(model.Id, new[] { tag.Id }, CancellationToken.None);
        var assigned = await service.GetModelTagsAsync(model.Id, CancellationToken.None);
        assigned.Should().HaveCount(1);

        // Remove from model
        await service.RemoveTagFromModelAsync(model.Id, tag.Id, CancellationToken.None);
        var remaining = await service.GetModelTagsAsync(model.Id, CancellationToken.None);
        remaining.Should().HaveCount(0);

        // Delete tag
        await service.DeleteTagAsync(tag.Id, CancellationToken.None);
        var deleted = await service.GetTagByIdAsync(tag.Id, CancellationToken.None);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task CreateMultipleTags_ThenBulkAssignToMultipleModels()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var models = new[] {
            await CreateTestModelAsync(),
            await CreateTestModelAsync(),
            await CreateTestModelAsync()
        };

        var tags = new[] {
            await CreateTestTagAsync(),
            await CreateTestTagAsync(),
            await CreateTestTagAsync(),
            await CreateTestTagAsync()
        };

        // Act - Bulk assign all tags to all models
        await service.BulkAssignTagsAsync(
            models.Select(m => m.Id),
            tags.Select(t => t.Id),
            CancellationToken.None);

        // Assert
        foreach (var model in models)
        {
            var modelTags = await service.GetModelTagsAsync(model.Id, CancellationToken.None);
            modelTags.Should().HaveCount(4);
        }
    }

    [Fact]
    public async Task TagNormalization_ConsistsAcrossCalls()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITagService>();

        var variations = new[] { "test tag", "Test Tag", "TEST TAG", "test-tag" };

        // Act - Create tags with variations
        Model3DTagDto? firstTag = null;
        foreach (var variation in variations)
        {
            var dto = new CreateModel3DTagDto
            {
                Name = variation,
                Color = "#000000",
                Description = ""
            };
            var created = await service.CreateTagAsync(dto, CancellationToken.None);
            
            firstTag ??= created;
        }

        // Assert - All should map to same normalized form
        var allTags = await service.GetAllTagsAsync(CancellationToken.None);
        var testTags = allTags.Where(t => t.Name == firstTag!.Name);
        testTags.Should().HaveCount(1); // Only one unique tag despite variations
    }

    #endregion
}

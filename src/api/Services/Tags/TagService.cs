using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Tags
{
    /// <summary>
    /// Service for managing tags with automatic name normalization.
    /// Supports polymorphic tagging of any object type (Model3D, GcodeFile, etc.).
    /// Uses ITagRepository exclusively via skip-navigation pattern.
    /// </summary>
    /// <remarks>
    /// This service provides tag management capabilities including:
    /// - CRUD operations for tags (create, read, delete)
    /// - Automatic PascalCase normalization of tag names for consistency
    /// - Polymorphic tag-to-object associations (assign, remove, bulk operations)
    /// - Duplicate tag handling via normalization ("my tag" → "MyTag")
    /// - Support for any object type via ObjectType discriminator
    /// Tag names are normalized to PascalCase to prevent duplicates with different casing.
    /// See TAG_NORMALIZATION_IMPLEMENTATION.md for complete details.
    /// </remarks>
    public class TagService(
        ITagRepository tagRepository,
        IUnitOfWork unitOfWork,
        IUnifiedLoggingService logger) : ITagService
    {
        private readonly ITagRepository _tagRepository = tagRepository ?? throw new ArgumentNullException(nameof(tagRepository));
        private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        /// <summary>
        /// Retrieves all tags in the system.
        /// </summary>
        public async Task<IReadOnlyList<TagDto>> GetAllTagsAsync(CancellationToken ct)
        {
            try
            {
                IReadOnlyList<Tag> tags = await _tagRepository.ListAllAsync(ct);
                return tags.Select(t => new TagDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    Color = t.Color,
                    Description = t.Description
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get all tags: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Retrieves a specific tag by its unique identifier.
        /// </summary>
        public async Task<TagDto?> GetTagByIdAsync(Guid tagId, CancellationToken ct)
        {
            try
            {
                Tag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
                return tag == null
                    ? null
                    : new TagDto
                    {
                        Id = tag.Id,
                        Name = tag.Name,
                        Color = tag.Color,
                        Description = tag.Description
                    };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get tag {tagId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Creates a new tag with automatic name normalization to PascalCase.
        /// </summary>
        public async Task<TagDto> CreateTagAsync(CreateTagDto dto, CancellationToken ct)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(dto);

                if (string.IsNullOrWhiteSpace(dto.Name))
                {
                    throw new ArgumentException("Tag name is required", nameof(dto));
                }

                string trimmedName = dto.Name.Trim();
                string normalizedName = ToPascalCase(trimmedName);

                // Check if tag already exists (after normalization)
                Tag? existing = await _tagRepository.GetByNameAsync(normalizedName, ct);
                if (existing != null)
                {
                    // Return the existing tag
                    return new TagDto
                    {
                        Id = existing.Id,
                        Name = existing.Name,
                        Color = existing.Color,
                        Description = existing.Description
                    };
                }

                Tag tag = new Tag
                {
                    Id = Guid.NewGuid(),
                    Name = normalizedName,
                    Color = dto.Color,
                    Description = dto.Description,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _tagRepository.AddAsync(tag, ct);
                try
                {
                    await _tagRepository.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint failed") == true
                    || ex.InnerException?.Message.Contains("Violation of PRIMARY KEY") == true
                    || ex.InnerException?.Message.Contains("duplicate key") == true)
                {
                    // Handle race condition: tag was created between check and insert
                    // Fetch and return the existing tag
                    Tag? existingTag = await _tagRepository.GetByNameAsync(normalizedName, ct);
                    if (existingTag != null)
                    {
                        return new TagDto
                        {
                            Id = existingTag.Id,
                            Name = existingTag.Name,
                            Color = existingTag.Color,
                            Description = existingTag.Description
                        };
                    }
                    throw;
                }

                return new TagDto
                {
                    Id = tag.Id,
                    Name = tag.Name,
                    Color = tag.Color,
                    Description = tag.Description
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to create tag: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Deletes a tag from the system.
        /// </summary>
        public async Task DeleteTagAsync(Guid tagId, CancellationToken ct)
        {
            try
            {
                Tag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
                if (tag == null)
                {
                    throw new KeyNotFoundException($"Tag {tagId} not found");
                }

                // Remove the tag from all objects that use it
                await _tagRepository.RemoveAllObjectsFromTagAsync(tagId, ct);

                // Delete the tag
                await _tagRepository.RemoveAsync(tag, ct);
                await _tagRepository.SaveChangesAsync(ct);

                _logger.LogInformation($"Deleted tag '{tag.Name}' (ID: {tagId})");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete tag {tagId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Assigns a tag to an object (polymorphic - supports any object type)
        /// </summary>
        public async Task AssignTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
        {
            try
            {
                // Verify tag exists
                Tag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
                if (tag == null)
                {
                    throw new KeyNotFoundException($"Tag {tagId} not found");
                }

                // Check if mapping already exists (object-agnostic)
                bool exists = await _tagRepository.HasTagAsync(objectId, tagId, ct);
                if (exists)
                {
                    return; // Already assigned, nothing to do
                }

                // Assign the tag to the object (object-agnostic)
                await _tagRepository.AssignTagAsync(objectId, tagId, ct);
                await _tagRepository.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to assign tag {tagId} to object {objectId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Removes a tag from an object (polymorphic - supports any object type)
        /// </summary>
        public async Task RemoveTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
        {
            try
            {
                // Object type parameter ignored - tags are object-agnostic
                await _tagRepository.RemoveTagAsync(objectId, tagId, ct);
                await _tagRepository.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to remove tag {tagId} from object {objectId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets all tags assigned to an object (polymorphic)
        /// </summary>
        public async Task<IReadOnlyList<TagDto>> GetObjectTagsAsync(Guid objectId, string objectType, CancellationToken ct)
        {
            try
            {
                // Object type parameter ignored - tags are object-agnostic
                IReadOnlyList<Tag> tags = await _tagRepository.GetTagsByObjectAsync(objectId, ct);
                return tags.Select(t => new TagDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    Color = t.Color,
                    Description = t.Description
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get tags for object {objectId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Assign tags to an object (replaces existing tags - polymorphic)
        /// </summary>
        public async Task AssignTagsAsync(Guid objectId, IEnumerable<Guid> tagIds, string objectType, CancellationToken ct)
        {
            try
            {
                var tagIdList = tagIds?.ToList() ?? [];

                // Remove all existing tags for this object (object-agnostic)
                await _tagRepository.RemoveAllTagsFromObjectAsync(objectId, ct);

                // Add new tags (object-agnostic)
                foreach (var tagId in tagIdList)
                {
                    await _tagRepository.AssignTagAsync(objectId, tagId, ct);
                }

                await _tagRepository.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to assign tags to object {objectId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Bulk assign same tags to multiple objects (polymorphic)
        /// </summary>
        public async Task BulkAssignTagsAsync(IEnumerable<Guid> objectIds, IEnumerable<Guid> tagIds, string objectType, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectType))
                {
                    throw new ArgumentException("Object type is required", nameof(objectType));
                }

                var objectIdList = objectIds?.ToList() ?? [];
                var tagIdList = tagIds?.ToList() ?? [];

                if (objectIdList.Count == 0 || tagIdList.Count == 0)
                {
                    return;
                }

                foreach (var objectId in objectIdList)
                {
                    await AssignTagsAsync(objectId, tagIdList, objectType, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to bulk assign tags to objects ({objectType}): {ex.Message}");
                throw;
            }
        }

        #region Phase 3D: Advanced Tag Management

        /// <summary>
        /// Searches for tags by partial name match with usage counts (Phase 3D).
        /// </summary>
        public async Task<IReadOnlyList<TagSuggestionDto>> SearchTagsAsync(string query, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return [];
                }

                string lowerQuery = query.ToLowerInvariant().Trim();

                // Get all tags and their usage counts
                IReadOnlyList<Tag> allTags = await _tagRepository.ListAllAsync(ct);

                // Filter and enrich with usage counts
                List<TagSuggestionDto> suggestions = new();
                foreach (var tag in allTags)
                {
                    if (tag.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        // Count how many objects use this tag (across all types)
                        int usageCount = await _tagRepository.GetTagUsageCountAsync(tag.Id, ct);

                        suggestions.Add(new TagSuggestionDto
                        {
                            Id = tag.Id,
                            Name = tag.Name,
                            Color = tag.Color,
                            UsageCount = usageCount,
                            IsPopular = false
                        });
                    }
                }

                return suggestions.OrderBy(s => s.Name).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to search tags for query '{query}': {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets the most popular tags by usage count (Phase 3D).
        /// </summary>
        public async Task<IReadOnlyList<TagSuggestionDto>> GetPopularTagsAsync(int count, CancellationToken ct)
        {
            try
            {
                if (count <= 0)
                {
                    return [];
                }

                IReadOnlyList<Tag> allTags = await _tagRepository.ListAllAsync(ct);

                // Get usage counts for all tags
                List<(Tag tag, int count)> tagsWithCounts = new();
                foreach (var tag in allTags)
                {
                    int usageCount = await _tagRepository.GetTagUsageCountAsync(tag.Id, ct);
                    if (usageCount > 0)
                    {
                        tagsWithCounts.Add((tag, usageCount));
                    }
                }

                // Sort by usage descending and take top N
                var popularTags = tagsWithCounts
                    .OrderByDescending(t => t.count)
                    .Take(count)
                    .Select(t => new TagSuggestionDto
                    {
                        Id = t.tag.Id,
                        Name = t.tag.Name,
                        Color = t.tag.Color,
                        UsageCount = t.count,
                        IsPopular = true
                    })
                    .ToList();

                return popularTags;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get popular tags (count={count}): {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets comprehensive tag usage analytics for dashboard (Phase 3D).
        /// </summary>
        public async Task<TagAnalyticsDto> GetAnalyticsAsync(CancellationToken ct)
        {
            try
            {
                IReadOnlyList<Tag> allTags = await _tagRepository.ListAllAsync(ct);
                int totalTags = allTags.Count;

                // Calculate statistics
                List<TagStatDto> tagStats = new();
                int totalAssociations = 0;
                int tagsInUse = 0;

                foreach (var tag in allTags)
                {
                    int objectCount = await _tagRepository.GetTagUsageCountAsync(tag.Id, ct);

                    if (objectCount > 0)
                    {
                        tagsInUse++;
                    }

                    totalAssociations += objectCount;

                    DateTime? lastUsedAt = null;
                    if (objectCount > 0)
                    {
                        lastUsedAt = await _tagRepository.GetTagLastUsedAtAsync(tag.Id, ct);
                    }

                    tagStats.Add(new TagStatDto
                    {
                        Id = tag.Id,
                        Name = tag.Name,
                        ModelCount = objectCount,
                        CreatedAt = tag.CreatedAt,
                        LastUsedAt = lastUsedAt
                    });
                }

                // Calculate averages
                double averageTagsPerModel = tagsInUse > 0
                    ? (double)totalAssociations / tagsInUse
                    : 0;

                // Get top 10 tags
                var topTags = tagStats
                    .Where(t => t.ModelCount > 0)
                    .OrderByDescending(t => t.ModelCount)
                    .Take(10)
                    .ToList();

                // Get unused tags for cleanup suggestions
                var unusedTags = tagStats
                    .Where(t => t.ModelCount == 0)
                    .OrderByDescending(t => t.CreatedAt) // Newest first for cleanup priority
                    .ToList();

                return new TagAnalyticsDto
                {
                    TotalTags = totalTags,
                    TagsInUse = tagsInUse,
                    UnusedTags = totalTags - tagsInUse,
                    TotalModelTagAssociations = totalAssociations,
                    AverageTagsPerModel = averageTagsPerModel,
                    TopTags = topTags,
                    UnusedTagsList = unusedTags
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get tag analytics: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Merges a source tag into a target tag, consolidating duplicates (Phase 3D).
        /// </summary>
        public async Task MergeTagsAsync(Guid sourceTagId, Guid targetTagId, CancellationToken ct)
        {
            try
            {
                if (sourceTagId == targetTagId)
                {
                    throw new ArgumentException("Source and target tags cannot be the same", nameof(sourceTagId));
                }

                Tag? sourceTag = await _tagRepository.GetByIdAsync(sourceTagId, ct);
                if (sourceTag == null)
                {
                    throw new KeyNotFoundException($"Source tag {sourceTagId} not found");
                }

                Tag? targetTag = await _tagRepository.GetByIdAsync(targetTagId, ct);
                if (targetTag == null)
                {
                    throw new KeyNotFoundException($"Target tag {targetTagId} not found");
                }

                _logger.LogInformation($"Merging tag '{sourceTag.Name}' into '{targetTag.Name}'");

                // Get all objects using source tag
                IReadOnlyList<Guid> sourceObjectIds = await _tagRepository.GetObjectsByTagAsync(sourceTagId, string.Empty, ct);

                // Get all objects using target tag for duplicate detection
                IReadOnlyList<Guid> targetObjectIds = await _tagRepository.GetObjectsByTagAsync(targetTagId, string.Empty, ct);
                var objectsInTarget = new HashSet<Guid>(targetObjectIds);

                // Reassign objects from source to target (skip duplicates)
                foreach (var objectId in sourceObjectIds)
                {
                    if (!objectsInTarget.Contains(objectId))
                    {
                        // Use skip-navigation to assign directly (object-agnostic)
                        await _tagRepository.AssignTagAsync(objectId, targetTagId, ct);
                    }
                }

                // Remove all objects from source tag
                await _tagRepository.RemoveAllObjectsFromTagAsync(sourceTagId, ct);

                // Delete source tag
                await _tagRepository.RemoveAsync(sourceTag, ct);
                await _tagRepository.SaveChangesAsync(ct);

                _logger.LogInformation($"Successfully merged tag '{sourceTag.Name}' into '{targetTag.Name}'");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to merge tag {sourceTagId} into {targetTagId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Filters objects by tag criteria (include/exclude with AND/OR logic) for a specific object type (Phase 3D).
        /// </summary>
        public async Task<IReadOnlyList<Guid>> FilterModelsByTagsAsync(
            IEnumerable<Guid>? includeTags,
            IEnumerable<Guid>? excludeTags,
            string objectType,
            bool requireAllTags,
            CancellationToken ct)
        {
            try
            {
                List<Guid> includeTagList = includeTags?.ToList() ?? [];
                List<Guid> excludeTagList = excludeTags?.ToList() ?? [];

                _logger.LogInformation($"Filtering {objectType} objects - IncludeTags: {includeTagList.Count}, ExcludeTags: {excludeTagList.Count}, RequireAllTags: {requireAllTags}");

                // Get objects for include tags
                HashSet<Guid> objectSet;

                if (includeTagList.Count > 0)
                {
                    if (requireAllTags)
                    {
                        // Require ALL tags: start with first tag, then intersect with others
                        objectSet = new HashSet<Guid>();
                        for (int i = 0; i < includeTagList.Count; i++)
                        {
                            IReadOnlyList<Guid> objectIds =
                                await _tagRepository.GetObjectsByTagAsync(includeTagList[i], objectType, ct);
                            var objIdSet = new HashSet<Guid>(objectIds);

                            if (i == 0)
                            {
                                objectSet = objIdSet;
                            }
                            else
                            {
                                objectSet.IntersectWith(objIdSet);
                            }
                        }
                    }
                    else
                    {
                        // Require ANY tag: union all objects from all tags
                        objectSet = new HashSet<Guid>();
                        foreach (var tagId in includeTagList)
                        {
                            IReadOnlyList<Guid> objectIds =
                                await _tagRepository.GetObjectsByTagAsync(tagId, objectType, ct);
                            foreach (var objectId in objectIds)
                            {
                                objectSet.Add(objectId);
                            }
                        }
                    }
                }
                else
                {
                    // No include tags - start with all objects of type
                    IReadOnlyList<Guid> allObjectIds = await _tagRepository.GetAllObjectsOfTypeAsync(objectType, ct);
                    objectSet = new HashSet<Guid>(allObjectIds);
                }

                // Remove objects with exclude tags
                foreach (var tagId in excludeTagList)
                {
                    IReadOnlyList<Guid> objectIds =
                        await _tagRepository.GetObjectsByTagAsync(tagId, objectType, ct);
                    foreach (var objectId in objectIds)
                    {
                        objectSet.Remove(objectId);
                    }
                }

                var results = objectSet.ToList();
                _logger.LogInformation($"Filter returned {results.Count} {objectType} objects");
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to filter {objectType} objects by tags: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets tag suggestions for autocomplete/input (Phase 3D).
        /// </summary>
        public async Task<IReadOnlyList<TagSuggestionDto>> GetTagSuggestionsAsync(
            string partialName,
            int limit,
            CancellationToken ct)
        {
            try
            {
                if (limit <= 0)
                {
                    limit = 10;
                }

                // Get search results
                IReadOnlyList<TagSuggestionDto> searchResults = await SearchTagsAsync(partialName, ct);

                // Get popular tags
                IReadOnlyList<TagSuggestionDto> popularTags = await GetPopularTagsAsync(limit, ct);

                // Combine and deduplicate: prioritize exact/prefix matches, then popular
                var suggestions = searchResults
                    .Take(limit)
                    .Concat(
                        popularTags.Where(p => !searchResults.Any(s => s.Id == p.Id))
                    )
                    .Take(limit)
                    .ToList();

                return suggestions;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get tag suggestions for '{partialName}': {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets objects that have all specified tags (polymorphic - supports any object type).
        /// </summary>
        public async Task<IReadOnlyCollection<Guid>> GetObjectsWithAllTagsAsync(
            string objectType,
            IEnumerable<Guid> tagIds,
            CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectType))
                {
                    throw new ArgumentException("Object type is required", nameof(objectType));
                }

                var tagIdList = tagIds.ToList();
                if (tagIdList.Count == 0)
                {
                    // No tags specified - return all objects of type
                    IReadOnlyList<Guid> allObjectIds = await _tagRepository.GetAllObjectsOfTypeAsync(objectType, ct);
                    return allObjectIds;
                }

                return await FilterObjectsByTagsAsync(objectType, tagIdList, null, requireAllTags: true, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get objects with all tags ({objectType}): {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets objects that have any of the specified tags (polymorphic).
        /// </summary>
        public async Task<IReadOnlyCollection<Guid>> GetObjectsWithAnyTagAsync(
            string objectType,
            IEnumerable<Guid> tagIds,
            CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectType))
                {
                    throw new ArgumentException("Object type is required", nameof(objectType));
                }

                var tagIdList = tagIds.ToList();
                return tagIdList.Count == 0 ? [] : await FilterObjectsByTagsAsync(objectType, tagIdList, null, requireAllTags: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get objects with any tag ({objectType}): {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets objects that exclude specific tags (polymorphic).
        /// </summary>
        public async Task<IReadOnlyCollection<Guid>> GetObjectsExcludingTagsAsync(
            string objectType,
            IEnumerable<Guid> tagIds,
            CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectType))
                {
                    throw new ArgumentException("Object type is required", nameof(objectType));
                }

                var tagIdList = tagIds.ToList();
                if (tagIdList.Count == 0)
                {
                    // No tags to exclude - return all objects
                    IReadOnlyList<Guid> allObjectIds = await _tagRepository.GetAllObjectsOfTypeAsync(objectType, ct);
                    return allObjectIds;
                }

                return await FilterObjectsByTagsAsync(objectType, null, tagIdList, requireAllTags: true, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get objects excluding tags ({objectType}): {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Complex filtering with include/exclude rules (polymorphic).
        /// </summary>
        public async Task<IReadOnlyCollection<Guid>> GetObjectsWithComplexFilterAsync(
            string objectType,
            IEnumerable<Guid> includeAllTagIds,
            IEnumerable<Guid> includeAnyTagIds,
            IEnumerable<Guid> excludeTagIds,
            CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectType))
                {
                    throw new ArgumentException("Object type is required", nameof(objectType));
                }

                var includeAllList = includeAllTagIds.ToList();
                var includeAnyList = includeAnyTagIds.ToList();
                var excludeList = excludeTagIds.ToList();

                // Start with all objects if no include filters
                IReadOnlyCollection<Guid> resultObjects;

                if (includeAllList.Count > 0)
                {
                    // Start with objects that have ALL required tags
                    resultObjects = await FilterObjectsByTagsAsync(objectType, includeAllList, null, requireAllTags: true, ct);
                }
                else if (includeAnyList.Count > 0)
                {
                    // Start with objects that have ANY of the tags
                    resultObjects = await FilterObjectsByTagsAsync(objectType, includeAnyList, null, requireAllTags: false, ct);
                }
                else
                {
                    // No include filters - start with all objects
                    IReadOnlyList<Guid> allObjectIds = await _tagRepository.GetAllObjectsOfTypeAsync(objectType, ct);
                    resultObjects = allObjectIds;
                }

                // Apply exclusion filter
                if (excludeList.Count > 0 && resultObjects.Count > 0)
                {
                    var excludedObjects = await FilterObjectsByTagsAsync(objectType, excludeList, null, requireAllTags: false, ct);
                    var excludedSet = new HashSet<Guid>(excludedObjects);
                    resultObjects = resultObjects.Where(m => !excludedSet.Contains(m)).ToList();
                }

                _logger.LogDebug(
                    $"Complex filter returned {resultObjects.Count} {objectType} objects " +
                    $"(includeAll: {includeAllList.Count}, includeAny: {includeAnyList.Count}, exclude: {excludeList.Count})");

                return resultObjects;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to apply complex filter to {objectType} objects: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Filters objects by tag criteria (include/exclude with AND/OR logic) - polymorphic version.
        /// </summary>
        public async Task<IReadOnlyList<Guid>> FilterObjectsByTagsAsync(
            string objectType,
            IEnumerable<Guid>? includeTags,
            IEnumerable<Guid>? excludeTags,
            bool requireAllTags,
            CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectType))
                {
                    throw new ArgumentException("Object type is required", nameof(objectType));
                }

                List<Guid> includeTagList = includeTags?.ToList() ?? [];
                List<Guid> excludeTagList = excludeTags?.ToList() ?? [];

                _logger.LogInformation($"Filtering {objectType} objects - IncludeTags: {includeTagList.Count}, ExcludeTags: {excludeTagList.Count}, RequireAllTags: {requireAllTags}");

                // Get objects for include tags
                HashSet<Guid> objectSet;

                if (includeTagList.Count > 0)
                {
                    if (requireAllTags)
                    {
                        // Require ALL tags: start with first tag, then intersect with others
                        objectSet = new HashSet<Guid>();
                        for (int i = 0; i < includeTagList.Count; i++)
                        {
                            IReadOnlyList<Guid> objectIds =
                                await _tagRepository.GetObjectsByTagAsync(includeTagList[i], objectType, ct);
                            var objIdSet = new HashSet<Guid>(objectIds);

                            if (i == 0)
                            {
                                objectSet = objIdSet;
                            }
                            else
                            {
                                objectSet.IntersectWith(objIdSet);
                            }
                        }
                    }
                    else
                    {
                        // Require ANY tag: union all objects from all tags
                        objectSet = new HashSet<Guid>();
                        foreach (var tagId in includeTagList)
                        {
                            IReadOnlyList<Guid> objectIds =
                                await _tagRepository.GetObjectsByTagAsync(tagId, objectType, ct);
                            foreach (var objectId in objectIds)
                            {
                                objectSet.Add(objectId);
                            }
                        }
                    }
                }
                else
                {
                    // No include tags - start with all objects of type
                    IReadOnlyList<Guid> allObjectIds = await _tagRepository.GetAllObjectsOfTypeAsync(objectType, ct);
                    objectSet = new HashSet<Guid>(allObjectIds);
                }

                // Remove objects with exclude tags
                foreach (var tagId in excludeTagList)
                {
                    IReadOnlyList<Guid> objectIds =
                        await _tagRepository.GetObjectsByTagAsync(tagId, objectType, ct);
                    foreach (var objectId in objectIds)
                    {
                        objectSet.Remove(objectId);
                    }
                }

                var results = objectSet.ToList();
                _logger.LogInformation($"Filter returned {results.Count} {objectType} objects");
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to filter {objectType} objects by tags: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Converts a string to PascalCase format for tag name normalization.
        /// </summary>
        private static string ToPascalCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return input;
            }

            // First, convert to lowercase to normalize
            string lowered = input.ToLowerInvariant();

            string[] words = lowered.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

            // Handle case where input was only delimiters
            if (words.Length == 0)
            {
                return input;
            }

            IEnumerable<string> pascalWords = words.Select(word =>
            {
                // Safety check in case word is somehow empty
                return string.IsNullOrEmpty(word) ? "" : char.ToUpperInvariant(word[0]) + (word.Length > 1 ? word.Substring(1) : "");
            });

            return string.Concat(pascalWords);
        }

        #endregion
    }
}

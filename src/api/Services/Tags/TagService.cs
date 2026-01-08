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
    /// Service for managing 3D model tags with automatic name normalization.
    /// </summary>
    /// <remarks>
    /// This service provides tag management capabilities including:
    /// - CRUD operations for tags (create, read, delete)
    /// - Automatic PascalCase normalization of tag names for consistency
    /// - Tag-to-model associations (assign, remove, bulk operations)
    /// - Duplicate tag handling via normalization ("my tag" → "MyTag")
    /// Tag names are normalized to PascalCase to prevent duplicates with different casing.
    /// See TAG_NORMALIZATION_IMPLEMENTATION.md for complete details.
    /// </remarks>
    public class TagService : ITagService
    {
        private readonly ITagRepository _tagRepository;
        private readonly IModelTagMappingRepository _mappingRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IUnifiedLoggingService _logger;

        public TagService(
            ITagRepository tagRepository,
            IModelTagMappingRepository mappingRepository,
            IUnitOfWork unitOfWork,
            IUnifiedLoggingService logger)
        {
            _tagRepository = tagRepository ?? throw new ArgumentNullException(nameof(tagRepository));
            _mappingRepository = mappingRepository ?? throw new ArgumentNullException(nameof(mappingRepository));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Retrieves all tags in the system.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of all tag DTOs with ID, name, color, and description</returns>
        /// <exception cref="Exception">Propagated from repository layer if database access fails</exception>
        public async Task<IReadOnlyList<Model3DTagDto>> GetAllTagsAsync(CancellationToken ct)
        {
            try
            {
                IReadOnlyList<Model3DTag> tags = await _tagRepository.ListAllAsync(ct);
                return tags.Select(t => new Model3DTagDto
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
        /// <param name="tagId">Unique tag identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Tag DTO with details, or null if tag not found</returns>
        /// <exception cref="Exception">Propagated from repository layer if database access fails</exception>
        public async Task<Model3DTagDto?> GetTagByIdAsync(Guid tagId, CancellationToken ct)
        {
            try
            {
                Model3DTag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
                if (tag == null)
                {
                    return null;
                }

                return new Model3DTagDto
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
        /// <param name="dto">Tag creation DTO containing name, color, and description</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Created tag DTO, or existing tag if normalized name already exists</returns>
        /// <exception cref="ArgumentNullException">Thrown when dto is null</exception>
        /// <exception cref="ArgumentException">Thrown when tag name is null, empty, or whitespace</exception>
        /// <remarks>
        /// Tag names are normalized to PascalCase before storage:
        /// - "my tag" → "MyTag"
        /// - "MY_TAG" → "MyTag"
        /// - "my-tag" → "MyTag"
        /// If a tag with the normalized name already exists, returns the existing tag instead of creating a duplicate.
        /// </remarks>
        public async Task<Model3DTagDto> CreateTagAsync(CreateModel3DTagDto dto, CancellationToken ct)
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
                Model3DTag? existing = await _tagRepository.GetByNameAsync(normalizedName, ct);
                if (existing != null)
                {
                    // Return the existing tag
                    return new Model3DTagDto
                    {
                        Id = existing.Id,
                        Name = existing.Name,
                        Color = existing.Color,
                        Description = existing.Description
                    };
                }

                Model3DTag tag = new Model3DTag
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
                    Model3DTag? existingTag = await _tagRepository.GetByNameAsync(normalizedName, ct);
                    if (existingTag != null)
                    {
                        return new Model3DTagDto
                        {
                            Id = existingTag.Id,
                            Name = existingTag.Name,
                            Color = existingTag.Color,
                            Description = existingTag.Description
                        };
                    }
                    throw;
                }

                return new Model3DTagDto
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

        #region Phase 3D: Advanced Tag Management

        /// <summary>
        /// Searches for tags by partial name match with usage counts (Phase 3D).
        /// </summary>
        /// <param name="query">Search query to match against tag names (case-insensitive)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of matching tags with usage counts, sorted by name</returns>
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
                IReadOnlyList<Model3DTag> allTags = await _tagRepository.ListAllAsync(ct);

                // Filter and enrich with usage counts
                List<TagSuggestionDto> suggestions = new();
                foreach (var tag in allTags)
                {
                    if (tag.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        // Count how many models use this tag
                        IReadOnlyList<Model3DTagMapping> mappings = 
                            await _mappingRepository.GetByTagIdAsync(tag.Id, ct);
                        int usageCount = mappings.Count;

                        suggestions.Add(new TagSuggestionDto
                        {
                            Id = tag.Id,
                            Name = tag.Name,
                            Color = tag.Color,
                            UsageCount = usageCount,
                            IsPopular = false // Will be determined in GetPopularTagsAsync
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
        /// <param name="count">Maximum number of tags to return</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Top N most-used tags sorted by usage descending</returns>
        public async Task<IReadOnlyList<TagSuggestionDto>> GetPopularTagsAsync(int count, CancellationToken ct)
        {
            try
            {
                if (count <= 0)
                {
                    return [];
                }

                IReadOnlyList<Model3DTag> allTags = await _tagRepository.ListAllAsync(ct);

                // Get usage counts for all tags
                List<(Model3DTag tag, int count)> tagsWithCounts = new();
                foreach (var tag in allTags)
                {
                    IReadOnlyList<Model3DTagMapping> mappings = 
                        await _mappingRepository.GetByTagIdAsync(tag.Id, ct);
                    if (mappings.Count > 0)
                    {
                        tagsWithCounts.Add((tag, mappings.Count));
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
        /// <param name="ct">Cancellation token</param>
        /// <returns>Analytics DTO with usage statistics, top tags, and unused tags</returns>
        public async Task<TagAnalyticsDto> GetAnalyticsAsync(CancellationToken ct)
        {
            try
            {
                IReadOnlyList<Model3DTag> allTags = await _tagRepository.ListAllAsync(ct);
                int totalTags = allTags.Count;

                // Calculate statistics
                List<TagStatDto> tagStats = new();
                int totalAssociations = 0;
                int tagsInUse = 0;

                foreach (var tag in allTags)
                {
                    IReadOnlyList<Model3DTagMapping> mappings = 
                        await _mappingRepository.GetByTagIdAsync(tag.Id, ct);
                    int modelCount = mappings.Count;

                    if (modelCount > 0)
                    {
                        tagsInUse++;
                    }

                    totalAssociations += modelCount;

                    tagStats.Add(new TagStatDto
                    {
                        Id = tag.Id,
                        Name = tag.Name,
                        ModelCount = modelCount,
                        CreatedAt = tag.CreatedAt,
                        LastUsedAt = mappings.Count > 0 
                            ? mappings.Max(m => m.TaggedAt) 
                            : null
                    });
                }

                // Calculate averages
                double averageTagsPerModel = totalTags > 0 
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
        /// <param name="sourceTagId">Tag to merge FROM (will be deleted)</param>
        /// <param name="targetTagId">Tag to merge INTO (will retain models)</param>
        /// <param name="ct">Cancellation token</param>
        /// <exception cref="KeyNotFoundException">Thrown if either tag not found</exception>
        /// <remarks>
        /// Reassigns all models from source tag to target tag, removes duplicates,
        /// then deletes the source tag.
        /// </remarks>
        public async Task MergeTagsAsync(Guid sourceTagId, Guid targetTagId, CancellationToken ct)
        {
            try
            {
                if (sourceTagId == targetTagId)
                {
                    throw new ArgumentException("Source and target tags cannot be the same", nameof(sourceTagId));
                }

                Model3DTag? sourceTag = await _tagRepository.GetByIdAsync(sourceTagId, ct);
                if (sourceTag == null)
                {
                    throw new KeyNotFoundException($"Source tag {sourceTagId} not found");
                }

                Model3DTag? targetTag = await _tagRepository.GetByIdAsync(targetTagId, ct);
                if (targetTag == null)
                {
                    throw new KeyNotFoundException($"Target tag {targetTagId} not found");
                }

                _logger.LogInformation($"Merging tag '{sourceTag.Name}' into '{targetTag.Name}'");

                // Get all models using source tag
                IReadOnlyList<Model3DTagMapping> sourceMappings = 
                    await _mappingRepository.GetByTagIdAsync(sourceTagId, ct);

                // Get all models using target tag for duplicate detection
                IReadOnlyList<Model3DTagMapping> targetMappings = 
                    await _mappingRepository.GetByTagIdAsync(targetTagId, ct);

                HashSet<Guid> modelsInTarget = new(targetMappings.Select(m => m.Model3DId));

                // Reassign models from source to target (skip duplicates)
                foreach (var sourceMapping in sourceMappings)
                {
                    if (!modelsInTarget.Contains(sourceMapping.Model3DId))
                    {
                        // Create new mapping for target tag
                        Model3DTagMapping newMapping = new Model3DTagMapping
                        {
                            Id = Guid.NewGuid(),
                            Model3DId = sourceMapping.Model3DId,
                            TagId = targetTagId,
                            TaggedAt = DateTime.UtcNow
                        };
                        await _mappingRepository.AddAsync(newMapping, ct);
                    }
                }

                // Remove all source mappings
                await _mappingRepository.RemoveByTagIdAsync(sourceTagId, ct);

                // Delete source tag
                await _tagRepository.RemoveAsync(sourceTag, ct);

                // Save all changes
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
        /// Filters models by tag criteria (include/exclude with AND/OR logic) (Phase 3D).
        /// </summary>
        /// <param name="includeTags">Tags to include in results</param>
        /// <param name="excludeTags">Tags to exclude from results</param>
        /// <param name="requireAllTags">If true, require ALL include tags (AND); if false, ANY tag (OR)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of matching model IDs</returns>
        public async Task<IReadOnlyList<Guid>> FilterModelsByTagsAsync(
            IEnumerable<Guid>? includeTags,
            IEnumerable<Guid>? excludeTags,
            bool requireAllTags,
            CancellationToken ct)
        {
            try
            {
                List<Guid> includeTagList = includeTags?.ToList() ?? [];
                List<Guid> excludeTagList = excludeTags?.ToList() ?? [];

                _logger.LogInformation($"Filtering models - IncludeTags: {includeTagList.Count}, ExcludeTags: {excludeTagList.Count}, RequireAllTags: {requireAllTags}");

                // Get models for include tags
                HashSet<Guid> modelSet;

                if (includeTagList.Count > 0)
                {
                    if (requireAllTags)
                    {
                        // Require ALL tags: start with first tag, then intersect with others
                        modelSet = new HashSet<Guid>();
                        for (int i = 0; i < includeTagList.Count; i++)
                        {
                            IReadOnlyList<Model3DTagMapping> mappings = 
                                await _mappingRepository.GetByTagIdAsync(includeTagList[i], ct);
                            var modelIds = new HashSet<Guid>(mappings.Select(m => m.Model3DId));

                            if (i == 0)
                            {
                                modelSet = modelIds;
                            }
                            else if (modelSet != null)
                            {
                                modelSet.IntersectWith(modelIds);
                            }
                        }
                    }
                    else
                    {
                        // Require ANY tag: union all models from all tags
                        modelSet = new HashSet<Guid>();
                        foreach (var tagId in includeTagList)
                        {
                            IReadOnlyList<Model3DTagMapping> mappings = 
                                await _mappingRepository.GetByTagIdAsync(tagId, ct);
                            foreach (var modelId in mappings.Select(m => m.Model3DId))
                            {
                                modelSet.Add(modelId);
                            }
                        }
                    }
                }
                else
                {
                    // No include tags - start with all models
                    modelSet = new HashSet<Guid>();
                    // TODO: Get all model IDs from database
                }

                // Remove models with exclude tags
                if (modelSet != null)
                {
                    foreach (var tagId in excludeTagList)
                    {
                        IReadOnlyList<Model3DTagMapping> mappings = 
                            await _mappingRepository.GetByTagIdAsync(tagId, ct);
                        foreach (var modelId in mappings.Select(m => m.Model3DId))
                        {
                            modelSet.Remove(modelId);
                        }
                    }
                }

                var results = (modelSet ?? new HashSet<Guid>()).ToList();
                _logger.LogInformation($"Filter returned {results.Count} models");
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to filter models by tags: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets tag suggestions for autocomplete/input (Phase 3D).
        /// </summary>
        /// <param name="partialName">Partial name to match</param>
        /// <param name="limit">Maximum number of suggestions (default 10)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Matching tags with usage counts and popular flag</returns>
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

        #endregion

        #region Helper Methods

        /// <summary>
        /// Converts a string to PascalCase format for tag name normalization.
        /// </summary>
        /// <param name="input">Input string to normalize</param>
        /// <returns>PascalCase formatted string (e.g., "my tag" → "MyTag")</returns>
        /// <remarks>
        /// Normalization strategy:
        /// - Splits on spaces, hyphens, underscores, and camelCase boundaries
        /// - Capitalizes first letter of each word
        /// - Removes non-alphanumeric characters except those used for splitting
        /// - Joins words without separators
        /// Example: "MY_TAG", "my-tag", "my tag" all normalize to "MyTag"
        /// </remarks>
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
                if (string.IsNullOrEmpty(word))
                {
                    return "";
                }

                return char.ToUpperInvariant(word[0]) + (word.Length > 1 ? word.Substring(1) : "");
            });

            return string.Concat(pascalWords);
        }

        /// <summary>
        /// Deletes a tag and all its associations with models.
        /// </summary>
        /// <param name="tagId">Unique tag identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <exception cref="KeyNotFoundException">Thrown when tag with specified ID does not exist</exception>
        /// <remarks>
        /// Removes tag entity and cascades to delete all ModelTagMapping associations.
        /// Uses Entity Framework change tracking for cascade deletes.
        /// </remarks>
        public async Task DeleteTagAsync(Guid tagId, CancellationToken ct)
        {
            try
            {
                Model3DTag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
                if (tag == null)
                {
                    throw new KeyNotFoundException($"Tag {tagId} not found");
                }

                // Tag deletion cascade handled by EF Core configuration
                await _tagRepository.RemoveAsync(tag, ct);
                await _tagRepository.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete tag {tagId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Assigns multiple tags to a 3D model.
        /// </summary>
        /// <param name="modelId">Unique model identifier (GUID)</param>
        /// <param name="tagIds">Collection of tag identifiers to assign</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <exception cref="ArgumentNullException">Thrown when tagIds collection is null</exception>
        /// <exception cref="KeyNotFoundException">Thrown when model does not exist</exception>
        /// <remarks>
        /// Skips tags that are already assigned to prevent duplicate mappings.
        /// Only creates new mappings for tags not yet associated with the model.
        /// </remarks>
        public async Task AssignTagsToModelAsync(Guid modelId, IEnumerable<Guid> tagIds, CancellationToken ct)
        {
            try
            {
                List<Guid> tagIdList = tagIds?.ToList() ?? new List<Guid>();
                _logger.LogInformation($"Assigning {tagIdList.Count} tags to model {modelId}");

                // Verify model exists
                Model3D? model = await _unitOfWork.Model3dFiles.GetByIdAsync(modelId, ct);
                if (model == null)
                {
                    _logger.LogError($"Model {modelId} not found");
                    throw new KeyNotFoundException($"Model {modelId} not found");
                }
                _logger.LogInformation($"Model {modelId} found, proceeding with tag assignment");

                // Remove existing tag mappings
                _logger.LogInformation($"Removing existing tag mappings for model {modelId}");
                await _mappingRepository.RemoveByModelIdAsync(modelId, ct);

                // Add new tag mappings
                _logger.LogInformation($"Adding {tagIdList.Count} new tag mappings");
                foreach (Guid tagId in tagIdList)
                {
                    Model3DTag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
                    if (tag != null)
                    {
                        Model3DTagMapping mapping = new Model3DTagMapping
                        {
                            Id = Guid.NewGuid(),
                            Model3DId = modelId,
                            TagId = tagId,
                            TaggedAt = DateTime.UtcNow
                        };
                        _logger.LogInformation($"Adding tag mapping: Model={modelId}, Tag={tagId}");
                        await _mappingRepository.AddAsync(mapping, ct);
                    }
                    else
                    {
                        _logger.LogWarning($"Tag {tagId} not found");
                    }
                }

                _logger.LogInformation($"Saving changes to database");
                await _mappingRepository.SaveChangesAsync(ct);
                _logger.LogInformation($"Successfully assigned tags to model {modelId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to assign tags to model {modelId}: {ex.GetType().Name} - {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Removes a tag assignment from a 3D model.
        /// </summary>
        /// <param name="modelId">Unique model identifier (GUID)</param>
        /// <param name="tagId">Unique tag identifier (GUID) to remove</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <remarks>
        /// Silently succeeds if mapping does not exist (idempotent operation).
        /// </remarks>
        public async Task RemoveTagFromModelAsync(Guid modelId, Guid tagId, CancellationToken ct)
        {
            try
            {
                Model3DTagMapping? mapping = await _mappingRepository.GetMappingAsync(modelId, tagId, ct);
                if (mapping == null)
                {
                    throw new KeyNotFoundException($"Tag {tagId} not assigned to model {modelId}");
                }

                await _mappingRepository.RemoveAsync(mapping, ct);
                await _mappingRepository.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to remove tag {tagId} from model {modelId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Retrieves all tags assigned to a specific 3D model.
        /// </summary>
        /// <param name="modelId">Unique model identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of tag DTOs associated with the model</returns>
        /// <exception cref="KeyNotFoundException">Thrown when model does not exist</exception>
        public async Task<IReadOnlyList<Model3DTagDto>> GetModelTagsAsync(Guid modelId, CancellationToken ct)
        {
            try
            {
                IReadOnlyList<Model3DTagMapping> mappings = await _mappingRepository.GetByModelIdAsync(modelId, ct);
                List<Guid> tagIds = mappings.Select(m => m.TagId).ToList();

                List<Model3DTagDto> tags = new List<Model3DTagDto>();
                foreach (Guid tagId in tagIds)
                {
                    Model3DTag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
                    if (tag != null)
                    {
                        tags.Add(new Model3DTagDto
                        {
                            Id = tag.Id,
                            Name = tag.Name,
                            Color = tag.Color,
                            Description = tag.Description
                        });
                    }
                }

                return tags;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get tags for model {modelId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Assigns multiple tags to multiple 3D models in a single operation.
        /// </summary>
        /// <param name="modelIds">Collection of model identifiers to assign tags to</param>
        /// <param name="tagIds">Collection of tag identifiers to assign</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <exception cref="ArgumentNullException">Thrown when modelIds or tagIds is null</exception>
        /// <remarks>
        /// Creates mappings for all model-tag combinations that don't already exist.
        /// Skips existing mappings to prevent duplicates.
        /// All operations performed in a single transaction via Unit of Work.
        /// </remarks>
        public async Task BulkAssignTagsAsync(IEnumerable<Guid> modelIds, IEnumerable<Guid> tagIds, CancellationToken ct)
        {
            try
            {
                List<Guid> modelIdList = modelIds?.ToList() ?? new List<Guid>();
                List<Guid> tagIdList = tagIds?.ToList() ?? new List<Guid>();

                foreach (Guid modelId in modelIdList)
                {
                    await AssignTagsToModelAsync(modelId, tagIdList, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to bulk assign tags: {ex.Message}");
                throw;
            }
        }

        #endregion
    }
}

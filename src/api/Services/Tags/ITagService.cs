using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Tags
{
    public interface ITagService
    {
        /// <summary>
        /// Get all available tags
        /// </summary>
        Task<IReadOnlyList<Model3DTagDto>> GetAllTagsAsync(CancellationToken ct);

        /// <summary>
        /// Get a tag by ID
        /// </summary>
        Task<Model3DTagDto?> GetTagByIdAsync(Guid tagId, CancellationToken ct);

        /// <summary>
        /// Create a new tag
        /// </summary>
        Task<Model3DTagDto> CreateTagAsync(CreateModel3DTagDto dto, CancellationToken ct);

        /// <summary>
        /// Delete a tag
        /// </summary>
        Task DeleteTagAsync(Guid tagId, CancellationToken ct);

        /// <summary>
        /// Assign tags to a model (replaces existing tags)
        /// </summary>
        Task AssignTagsToModelAsync(Guid modelId, IEnumerable<Guid> tagIds, CancellationToken ct);

        /// <summary>
        /// Remove a specific tag from a model
        /// </summary>
        Task RemoveTagFromModelAsync(Guid modelId, Guid tagId, CancellationToken ct);

        /// <summary>
        /// Get all tags assigned to a model
        /// </summary>
        Task<IReadOnlyList<Model3DTagDto>> GetModelTagsAsync(Guid modelId, CancellationToken ct);

        /// <summary>
        /// Bulk assign same tags to multiple models
        /// </summary>
        Task BulkAssignTagsAsync(IEnumerable<Guid> modelIds, IEnumerable<Guid> tagIds, CancellationToken ct);

        /// <summary>
        /// Search for tags by name (Phase 3D)
        /// </summary>
        /// <param name="query">Search query string</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of matching tags with usage counts</returns>
        Task<IReadOnlyList<TagSuggestionDto>> SearchTagsAsync(string query, CancellationToken ct);

        /// <summary>
        /// Get most popular tags by usage (Phase 3D)
        /// </summary>
        /// <param name="count">Number of tags to return</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of most used tags</returns>
        Task<IReadOnlyList<TagSuggestionDto>> GetPopularTagsAsync(int count, CancellationToken ct);

        /// <summary>
        /// Get tag usage statistics for analytics (Phase 3D)
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Analytics data with tag usage statistics</returns>
        Task<TagAnalyticsDto> GetAnalyticsAsync(CancellationToken ct);

        /// <summary>
        /// Merge two tags (consolidate duplicates) (Phase 3D)
        /// </summary>
        /// <param name="sourceTagId">Tag to merge from</param>
        /// <param name="targetTagId">Tag to merge into</param>
        /// <param name="ct">Cancellation token</param>
        Task MergeTagsAsync(Guid sourceTagId, Guid targetTagId, CancellationToken ct);

        /// <summary>
        /// Filter models by tags (include or exclude) (Phase 3D)
        /// </summary>
        /// <param name="includeTags">Tags to include (ANY match)</param>
        /// <param name="excludeTags">Tags to exclude</param>
        /// <param name="requireAllTags">If true, require ALL tags (AND); if false, ANY tag (OR)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of matching model IDs</returns>
        Task<IReadOnlyList<Guid>> FilterModelsByTagsAsync(
            IEnumerable<Guid>? includeTags,
            IEnumerable<Guid>? excludeTags,
            bool requireAllTags,
            CancellationToken ct);

        /// <summary>
        /// Get tag names by partial match for autocomplete (Phase 3D)
        /// </summary>
        /// <param name="partialName">Partial tag name to match</param>
        /// <param name="limit">Maximum number of suggestions</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of matching tag names and suggestions</returns>
        Task<IReadOnlyList<TagSuggestionDto>> GetTagSuggestionsAsync(
            string partialName,
            int limit,
            CancellationToken ct);

        /// <summary>
        /// Gets models that have all specified tags (require all).
        /// </summary>
        /// <param name="tagIds">Collection of tag identifiers that models must have</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Collection of model IDs that have all specified tags</returns>
        Task<IReadOnlyCollection<Guid>> GetModelsWithAllTagsAsync(IEnumerable<Guid> tagIds, CancellationToken ct);

        /// <summary>
        /// Gets models that have any of the specified tags (require any).
        /// </summary>
        /// <param name="tagIds">Collection of tag identifiers - models matching any will be returned</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Collection of model IDs that have any of the specified tags</returns>
        Task<IReadOnlyCollection<Guid>> GetModelsWithAnyTagAsync(IEnumerable<Guid> tagIds, CancellationToken ct);

        /// <summary>
        /// Gets models that exclude specific tags.
        /// </summary>
        /// <param name="tagIds">Collection of tag identifiers to exclude</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Collection of model IDs that do NOT have any of the specified tags</returns>
        Task<IReadOnlyCollection<Guid>> GetModelsExcludingTagsAsync(IEnumerable<Guid> tagIds, CancellationToken ct);

        /// <summary>
        /// Complex filtering with include/exclude rules.
        /// </summary>
        /// <param name="includeAllTagIds">Models must have ALL of these tags (required)</param>
        /// <param name="includeAnyTagIds">Models must have ANY of these tags (optional - only if specified)</param>
        /// <param name="excludeTagIds">Models must NOT have any of these tags</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Collection of model IDs matching the complex filter criteria</returns>
        Task<IReadOnlyCollection<Guid>> GetModelsWithComplexFilterAsync(
            IEnumerable<Guid> includeAllTagIds,
            IEnumerable<Guid> includeAnyTagIds,
            IEnumerable<Guid> excludeTagIds,
            CancellationToken ct);
    }
}

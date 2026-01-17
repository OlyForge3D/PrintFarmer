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
        Task<IReadOnlyList<TagDto>> GetAllTagsAsync(CancellationToken ct);

        /// <summary>
        /// Get a tag by ID
        /// </summary>
        Task<TagDto?> GetTagByIdAsync(Guid tagId, CancellationToken ct);

        /// <summary>
        /// Create a new tag
        /// </summary>
        Task<TagDto> CreateTagAsync(CreateTagDto dto, CancellationToken ct);

        /// <summary>
        /// Delete a tag
        /// </summary>
        Task DeleteTagAsync(Guid tagId, CancellationToken ct);

        /// <summary>
        /// Assign a tag to an object (polymorphic - supports any object type)
        /// </summary>
        /// <param name="objectId">The ID of the object being tagged</param>
        /// <param name="tagId">The ID of the tag to assign</param>
        /// <param name="objectType">The type of object being tagged (e.g., "Model3D", "GcodeFile")</param>
        /// <param name="ct">Cancellation token</param>
        Task AssignTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct);

        /// <summary>
        /// Remove a tag from an object (polymorphic - supports any object type)
        /// </summary>
        /// <param name="objectId">The ID of the object to remove tag from</param>
        /// <param name="tagId">The ID of the tag to remove</param>
        /// <param name="objectType">The type of object (e.g., "Model3D", "GcodeFile")</param>
        /// <param name="ct">Cancellation token</param>
        Task RemoveTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct);

        /// <summary>
        /// Get all tags assigned to an object (polymorphic)
        /// </summary>
        /// <param name="objectId">The ID of the object</param>
        /// <param name="objectType">The type of object (e.g., "Model3D", "GcodeFile")</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of tags assigned to the object</returns>
        Task<IReadOnlyList<TagDto>> GetObjectTagsAsync(Guid objectId, string objectType, CancellationToken ct);

        /// <summary>
        /// Assign tags to an object (replaces existing tags - polymorphic)
        /// </summary>
        /// <param name="objectId">The ID of the object</param>
        /// <param name="tagIds">The tags to assign</param>
        /// <param name="objectType">The type of object (e.g., "Model3D", "GcodeFile")</param>
        /// <param name="ct">Cancellation token</param>
        Task AssignTagsAsync(Guid objectId, IEnumerable<Guid> tagIds, string objectType, CancellationToken ct);

        /// <summary>
        /// Bulk assign same tags to multiple objects (polymorphic)
        /// </summary>
        /// <param name="objectIds">The IDs of objects to tag</param>
        /// <param name="tagIds">The tags to assign</param>
        /// <param name="objectType">The type of objects (e.g., "Model3D", "GcodeFile")</param>
        /// <param name="ct">Cancellation token</param>
        Task BulkAssignTagsAsync(IEnumerable<Guid> objectIds, IEnumerable<Guid> tagIds, string objectType, CancellationToken ct);

        /// <summary>
        /// Search for tags by name
        /// </summary>
        /// <param name="query">Search query string</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of matching tags with usage counts</returns>
        Task<IReadOnlyList<TagSuggestionDto>> SearchTagsAsync(string query, CancellationToken ct);

        /// <summary>
        /// Get most popular tags by usage
        /// </summary>
        /// <param name="count">Number of tags to return</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of most used tags</returns>
        Task<IReadOnlyList<TagSuggestionDto>> GetPopularTagsAsync(int count, CancellationToken ct);

        /// <summary>
        /// Get tag usage statistics for analytics
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Analytics data with tag usage statistics</returns>
        Task<TagAnalyticsDto> GetAnalyticsAsync(CancellationToken ct);

        /// <summary>
        /// Merge two tags (consolidate duplicates)
        /// </summary>
        /// <param name="sourceTagId">Tag to merge from</param>
        /// <param name="targetTagId">Tag to merge into</param>
        /// <param name="ct">Cancellation token</param>
        Task MergeTagsAsync(Guid sourceTagId, Guid targetTagId, CancellationToken ct);

        /// <summary>
        /// Filter objects by tags (polymorphic)
        /// </summary>
        /// <param name="objectType">The type of objects to filter (e.g., "Model3D", "GcodeFile")</param>
        /// <param name="includeTags">Tags to include (ANY match)</param>
        /// <param name="excludeTags">Tags to exclude</param>
        /// <param name="requireAllTags">If true, require ALL tags (AND); if false, ANY tag (OR)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of matching object IDs</returns>
        Task<IReadOnlyList<Guid>> FilterObjectsByTagsAsync(
            string objectType,
            IEnumerable<Guid>? includeTags,
            IEnumerable<Guid>? excludeTags,
            bool requireAllTags,
            CancellationToken ct);

        /// <summary>
        /// Get tag names by partial match for autocomplete
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
        /// Gets objects that have all specified tags (polymorphic)
        /// </summary>
        /// <param name="objectType">The type of objects to search (e.g., "Model3D")</param>
        /// <param name="tagIds">Collection of tag identifiers that objects must have</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Collection of object IDs that have all specified tags</returns>
        Task<IReadOnlyCollection<Guid>> GetObjectsWithAllTagsAsync(string objectType, IEnumerable<Guid> tagIds, CancellationToken ct);

        /// <summary>
        /// Gets objects that have any of the specified tags (polymorphic)
        /// </summary>
        /// <param name="objectType">The type of objects to search</param>
        /// <param name="tagIds">Collection of tag identifiers - objects matching any will be returned</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Collection of object IDs that have any of the specified tags</returns>
        Task<IReadOnlyCollection<Guid>> GetObjectsWithAnyTagAsync(string objectType, IEnumerable<Guid> tagIds, CancellationToken ct);

        /// <summary>
        /// Gets objects that exclude specific tags (polymorphic)
        /// </summary>
        /// <param name="objectType">The type of objects to search</param>
        /// <param name="tagIds">Collection of tag identifiers to exclude</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Collection of object IDs that do NOT have any of the specified tags</returns>
        Task<IReadOnlyCollection<Guid>> GetObjectsExcludingTagsAsync(string objectType, IEnumerable<Guid> tagIds, CancellationToken ct);

        /// <summary>
        /// Complex filtering with include/exclude rules (polymorphic)
        /// </summary>
        /// <param name="objectType">The type of objects to search</param>
        /// <param name="includeAllTagIds">Objects must have ALL of these tags (required)</param>
        /// <param name="includeAnyTagIds">Objects must have ANY of these tags (optional - only if specified)</param>
        /// <param name="excludeTagIds">Objects must NOT have any of these tags</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Collection of object IDs matching the complex filter criteria</returns>
        Task<IReadOnlyCollection<Guid>> GetObjectsWithComplexFilterAsync(
            string objectType,
            IEnumerable<Guid> includeAllTagIds,
            IEnumerable<Guid> includeAnyTagIds,
            IEnumerable<Guid> excludeTagIds,
            CancellationToken ct);
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

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
    }
}

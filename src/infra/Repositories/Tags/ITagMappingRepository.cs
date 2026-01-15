using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Tags
{
    /// <summary>
    /// Generic repository for polymorphic tag-to-object mappings.
    /// Supports tagging any object type (Model3D, GcodeFile, Printer, etc.) using ObjectType discriminator.
    /// </summary>
    public interface ITagMappingRepository
    {
        /// <summary>
        /// Gets a specific mapping by ID
        /// </summary>
        Task<TagMapping?> GetByIdAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Gets all mappings for a specific object (polymorphic)
        /// </summary>
        /// <param name="objectId">The object ID being queried</param>
        /// <param name="objectType">The type of object (e.g., "Model3D", "GcodeFile")</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>All tag mappings for this object</returns>
        Task<IReadOnlyList<TagMapping>> GetByObjectAsync(Guid objectId, string objectType, CancellationToken ct);

        /// <summary>
        /// Gets all mappings for a specific tag (across all object types)
        /// </summary>
        /// <param name="tagId">The tag ID</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>All mappings using this tag</returns>
        Task<IReadOnlyList<TagMapping>> GetByTagIdAsync(Guid tagId, CancellationToken ct);

        /// <summary>
        /// Gets all mappings for a specific tag filtered by object type
        /// </summary>
        /// <param name="tagId">The tag ID</param>
        /// <param name="objectType">Filter by object type (e.g., "Model3D")</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Mappings for this tag and object type</returns>
        Task<IReadOnlyList<TagMapping>> GetByTagIdAndObjectTypeAsync(Guid tagId, string objectType, CancellationToken ct);

        /// <summary>
        /// Gets a specific mapping for an object-tag pair
        /// </summary>
        /// <param name="objectId">The object ID</param>
        /// <param name="tagId">The tag ID</param>
        /// <param name="objectType">The type of object</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The mapping if found, null otherwise</returns>
        Task<TagMapping?> GetMappingAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct);

        /// <summary>
        /// Gets objects by tag (with AND/OR logic for multiple tags)
        /// </summary>
        Task<IReadOnlyList<TagMapping>> GetObjectsWithTagsAsync(IEnumerable<Guid> tagIds, string objectType, bool requireAllTags, CancellationToken ct);

        /// <summary>
        /// Gets objects excluding specific tags
        /// </summary>
        Task<IReadOnlyList<TagMapping>> GetObjectsExcludingTagsAsync(IEnumerable<Guid> tagIds, string objectType, CancellationToken ct);

        /// <summary>
        /// Gets all objects of a specific type
        /// </summary>
        Task<IReadOnlyList<TagMapping>> GetAllObjectsOfTypeAsync(string objectType, CancellationToken ct);

        /// <summary>
        /// Gets objects for a specific tag and object type
        /// </summary>
        Task<IReadOnlyList<TagMapping>> GetObjectsByTagAsync(Guid tagId, string objectType, CancellationToken ct);

        /// <summary>
        /// Adds a new mapping
        /// </summary>
        Task AddAsync(TagMapping mapping, CancellationToken ct);

        /// <summary>
        /// Removes a mapping
        /// </summary>
        Task RemoveAsync(TagMapping mapping, CancellationToken ct);

        /// <summary>
        /// Removes all mappings for a specific object
        /// </summary>
        Task RemoveByObjectAsync(string objectType, Guid objectId, CancellationToken ct);

        /// <summary>
        /// Removes all mappings for a specific tag
        /// </summary>
        Task RemoveByTagAsync(Guid tagId, CancellationToken ct);

        /// <summary>
        /// Removes a specific mapping for an object-tag pair
        /// </summary>
        Task RemoveByObjectAndTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct);

        /// <summary>
        /// Persist all changes
        /// </summary>
        Task SaveChangesAsync(CancellationToken ct);
    }
}

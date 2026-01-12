using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Tags
{
    /// <summary>
    /// Generic repository for polymorphic tag mappings.
    /// Supports tagging any object type (gcode files, models, printers, etc.) via ObjectType discriminator.
    /// </summary>
    public interface ITagMappingRepository
    {
        /// <summary>
        /// Get a mapping by ID
        /// </summary>
        Task<TagMapping?> GetByIdAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Get a specific mapping by object type, object ID, and tag ID
        /// </summary>
        Task<TagMapping?> GetMappingAsync(string objectType, Guid objectId, Guid tagId, CancellationToken ct);

        /// <summary>
        /// Get all mappings for a specific object (by object type and ID)
        /// </summary>
        Task<IReadOnlyList<TagMapping>> GetMappingsByObjectAsync(string objectType, Guid objectId, CancellationToken ct);

        /// <summary>
        /// Get all mappings for a specific tag across all object types
        /// </summary>
        Task<IReadOnlyList<TagMapping>> GetMappingsByTagAsync(Guid tagId, CancellationToken ct);

        /// <summary>
        /// Add a new mapping
        /// </summary>
        Task AddAsync(TagMapping mapping, CancellationToken ct);

        /// <summary>
        /// Remove a mapping
        /// </summary>
        Task RemoveAsync(TagMapping mapping, CancellationToken ct);

        /// <summary>
        /// Remove all mappings for a specific object
        /// </summary>
        Task RemoveByObjectAsync(string objectType, Guid objectId, CancellationToken ct);

        /// <summary>
        /// Remove all mappings for a specific tag
        /// </summary>
        Task RemoveByTagAsync(Guid tagId, CancellationToken ct);

        /// <summary>
        /// Persist all changes
        /// </summary>
        Task SaveChangesAsync(CancellationToken ct);
    }
}

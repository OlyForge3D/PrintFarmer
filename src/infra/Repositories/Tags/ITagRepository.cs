using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Tags;

/// <summary>
/// Repository interface for tag management and object-tag associations.
/// Provides CRUD operations for tags and object-agnostic tag assignment.
/// </summary>
public interface ITagRepository
{
    /// <summary>
    /// Gets a tag by its unique identifier.
    /// </summary>
    /// <param name="id">The tag's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tag if found, otherwise null.</returns>
    Task<Tag?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Gets a tag by its name (case-sensitive).
    /// </summary>
    /// <param name="name">The tag name to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tag if found, otherwise null.</returns>
    Task<Tag?> GetByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Gets all tags in the system.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only list of all tags.</returns>
    Task<IReadOnlyList<Tag>> ListAllAsync(CancellationToken ct);

    /// <summary>
    /// Adds a new tag to the repository.
    /// </summary>
    /// <param name="tag">The tag entity to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(Tag tag, CancellationToken ct);

    /// <summary>
    /// Removes a tag from the repository.
    /// </summary>
    /// <param name="tag">The tag entity to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(Tag tag, CancellationToken ct);

    /// <summary>
    /// Persists pending changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Check if an object has a specific tag (object-agnostic).
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="tagId">The unique identifier of the tag.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> HasTagAsync(Guid objectId, Guid tagId, CancellationToken ct);

    /// <summary>
    /// Assign a tag to an object (object-agnostic).
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="tagId">The unique identifier of the tag to assign.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AssignTagAsync(Guid objectId, Guid tagId, CancellationToken ct);

    /// <summary>
    /// Remove a tag from an object (object-agnostic).
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="tagId">The unique identifier of the tag to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveTagAsync(Guid objectId, Guid tagId, CancellationToken ct);

    /// <summary>
    /// Get all tags for an object (object-agnostic).
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Tag>> GetTagsByObjectAsync(Guid objectId, CancellationToken ct);

    /// <summary>
    /// Remove all tags from an object (object-agnostic).
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAllTagsFromObjectAsync(Guid objectId, CancellationToken ct);

    /// <summary>
    /// Remove all object associations with a specific tag.
    /// </summary>
    /// <param name="tagId">The unique identifier of the tag.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAllObjectsFromTagAsync(Guid tagId, CancellationToken ct);

    // Type-filtered queries (optional when you need objects of specific type)

    /// <summary>
    /// Get all objects of a specific type that have a tag.
    /// </summary>
    /// <param name="tagId">The unique identifier of the tag.</param>
    /// <param name="objectType">The type of objects to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Guid>> GetObjectsByTagAsync(Guid tagId, string objectType, CancellationToken ct);

    /// <summary>
    /// Get all objects of a specific type that have ALL of the given tags (AND logic).
    /// </summary>
    /// <param name="tagIds">The collection of tag identifiers to match.</param>
    /// <param name="objectType">The type of objects to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Guid>> GetObjectsWithAllTagsAsync(IEnumerable<Guid> tagIds, string objectType, CancellationToken ct);

    /// <summary>
    /// Get all objects of a specific type that have ANY of the given tags (OR logic).
    /// </summary>
    /// <param name="tagIds">The collection of tag identifiers to match.</param>
    /// <param name="objectType">The type of objects to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Guid>> GetObjectsWithAnyTagsAsync(IEnumerable<Guid> tagIds, string objectType, CancellationToken ct);

    /// <summary>
    /// Get all objects of a specific type.
    /// </summary>
    /// <param name="objectType">The type of objects to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Guid>> GetAllObjectsOfTypeAsync(string objectType, CancellationToken ct);

    /// <summary>
    /// Gets the count of objects associated with a tag.
    /// </summary>
    /// <param name="tagId">The tag's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of objects using this tag.</returns>
    Task<int> GetTagUsageCountAsync(Guid tagId, CancellationToken ct);

    /// <summary>
    /// Gets the timestamp when a tag was last assigned to an object.
    /// </summary>
    /// <param name="tagId">The tag's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The last usage timestamp, or null if never used.</returns>
    Task<DateTime?> GetTagLastUsedAtAsync(Guid tagId, CancellationToken ct);
}

using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.Collections;

/// <summary>
/// Application service for managing model collections and their membership. Enforces
/// owner/administrator authorization and validates model references through the model
/// query abstraction. Callers supply their user id and administrator flag; the service
/// is the single authorization boundary for collection operations.
/// </summary>
public interface IModelCollectionService
{
    /// <summary>
    /// Lists collections visible to the caller. Administrators see all collections;
    /// other users see collections they own plus any shared collection.
    /// </summary>
    Task<IReadOnlyList<ModelCollectionDto>> ListCollectionsAsync(Guid callerUserId, bool callerIsAdmin, CancellationToken ct);

    /// <summary>
    /// Gets a single collection the caller is allowed to read.
    /// </summary>
    /// <returns>The collection, or <c>null</c> when it does not exist.</returns>
    /// <exception cref="Farm.Infrastructure.Exceptions.CollectionAccessDeniedException">
    /// The collection exists but the caller may not read it.</exception>
    Task<ModelCollectionDto?> GetCollectionAsync(Guid collectionId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);

    /// <summary>Creates a new collection owned by the caller.</summary>
    Task<ModelCollectionDto> CreateCollectionAsync(CreateModelCollectionDto dto, Guid callerUserId, CancellationToken ct);

    /// <summary>Updates a collection's metadata (owner or administrator only).</summary>
    Task<ModelCollectionDto> UpdateCollectionAsync(Guid collectionId, UpdateModelCollectionDto dto, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);

    /// <summary>Deletes a collection and its memberships (owner or administrator only).</summary>
    Task DeleteCollectionAsync(Guid collectionId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);

    /// <summary>Shares or unshares a collection (owner or administrator only).</summary>
    Task<ModelCollectionDto> SetSharedAsync(Guid collectionId, bool shared, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);

    /// <summary>Lists the memberships of a collection the caller is allowed to read.</summary>
    Task<IReadOnlyList<ModelCollectionMembershipDto>> ListMembersAsync(Guid collectionId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);

    /// <summary>
    /// Adds a model to a collection (owner or administrator only). Validates that the
    /// model exists via the model query abstraction. Adding an already-present model is
    /// idempotent and returns the existing membership.
    /// </summary>
    Task<ModelCollectionMembershipDto> AddMemberAsync(Guid collectionId, Guid modelId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);

    /// <summary>
    /// Removes a model from a collection (owner or administrator only). Removing an absent
    /// model is idempotent.
    /// </summary>
    Task RemoveMemberAsync(Guid collectionId, Guid modelId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);

    /// <summary>
    /// Replaces the entire membership set of a collection (owner or administrator only).
    /// Validates all supplied model ids exist before applying changes atomically.
    /// </summary>
    Task<ModelCollectionDto> ReplaceMembersAsync(Guid collectionId, IEnumerable<Guid> modelIds, Guid callerUserId, bool callerIsAdmin, CancellationToken ct);
}

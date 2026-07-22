using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;

namespace Farm.Infrastructure.Services.Collections;

/// <summary>
/// Application service for user-owned, shareable model collections and their membership. Enforces
/// owner/admin authorization and validates model references via the model query abstraction.
/// </summary>
/// <remarks>
/// All mutating members accept a <see cref="CollectionCaller"/> so authorization is enforced in one
/// place and the contract can be extended by the library-sync epic (change journaling, cursor sync)
/// without breaking callers.
/// </remarks>
public interface IModelCollectionService
{
    /// <summary>Lists the collections visible to the caller (owned plus shared; all for admins).</summary>
    Task<IReadOnlyList<ModelCollectionDto>> ListAsync(CollectionCaller caller, CancellationToken ct);

    /// <summary>Gets a single collection the caller is permitted to read.</summary>
    /// <exception cref="CollectionNotFoundException">The collection does not exist.</exception>
    /// <exception cref="CollectionAccessDeniedException">The caller may not read the collection.</exception>
    Task<ModelCollectionDto> GetAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct);

    /// <summary>Creates a new collection owned by the caller.</summary>
    /// <exception cref="ArgumentException">The supplied name is empty.</exception>
    Task<ModelCollectionDto> CreateAsync(CollectionCaller caller, CreateModelCollectionDto dto, CancellationToken ct);

    /// <summary>Updates a collection's mutable metadata.</summary>
    /// <exception cref="CollectionNotFoundException">The collection does not exist.</exception>
    /// <exception cref="CollectionAccessDeniedException">The caller is not the owner or an admin.</exception>
    /// <exception cref="ArgumentException">The supplied name is empty.</exception>
    Task<ModelCollectionDto> UpdateAsync(CollectionCaller caller, Guid collectionId, UpdateModelCollectionDto dto, CancellationToken ct);

    /// <summary>Deletes a collection and its membership rows.</summary>
    /// <exception cref="CollectionNotFoundException">The collection does not exist.</exception>
    /// <exception cref="CollectionAccessDeniedException">The caller is not the owner or an admin.</exception>
    Task DeleteAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct);

    /// <summary>Marks a collection as shared (readable by any authenticated user).</summary>
    /// <exception cref="CollectionNotFoundException">The collection does not exist.</exception>
    /// <exception cref="CollectionAccessDeniedException">The caller is not the owner or an admin.</exception>
    Task<ModelCollectionDto> ShareAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct);

    /// <summary>Marks a collection as private (readable only by owner and admins).</summary>
    /// <exception cref="CollectionNotFoundException">The collection does not exist.</exception>
    /// <exception cref="CollectionAccessDeniedException">The caller is not the owner or an admin.</exception>
    Task<ModelCollectionDto> UnshareAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct);

    /// <summary>Lists the model memberships of a collection the caller may read.</summary>
    /// <exception cref="CollectionNotFoundException">The collection does not exist.</exception>
    /// <exception cref="CollectionAccessDeniedException">The caller may not read the collection.</exception>
    Task<IReadOnlyList<ModelCollectionMembershipDto>> ListMembersAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct);

    /// <summary>Adds a model to a collection. Idempotent: adding an existing member returns it unchanged.</summary>
    /// <exception cref="CollectionNotFoundException">The collection does not exist.</exception>
    /// <exception cref="CollectionAccessDeniedException">The caller is not the owner or an admin.</exception>
    /// <exception cref="CollectionModelNotFoundException">The model does not exist.</exception>
    Task<ModelCollectionMembershipDto> AddMemberAsync(CollectionCaller caller, Guid collectionId, Guid modelId, CancellationToken ct);

    /// <summary>Removes a model from a collection. Idempotent: removing an absent member is a no-op.</summary>
    /// <exception cref="CollectionNotFoundException">The collection does not exist.</exception>
    /// <exception cref="CollectionAccessDeniedException">The caller is not the owner or an admin.</exception>
    Task RemoveMemberAsync(CollectionCaller caller, Guid collectionId, Guid modelId, CancellationToken ct);

    /// <summary>Replaces the full membership of a collection with the supplied set of models.</summary>
    /// <exception cref="CollectionNotFoundException">The collection does not exist.</exception>
    /// <exception cref="CollectionAccessDeniedException">The caller is not the owner or an admin.</exception>
    /// <exception cref="CollectionModelNotFoundException">One of the supplied models does not exist.</exception>
    Task<IReadOnlyList<ModelCollectionMembershipDto>> ReplaceMembersAsync(CollectionCaller caller, Guid collectionId, IReadOnlyCollection<Guid> modelIds, CancellationToken ct);
}

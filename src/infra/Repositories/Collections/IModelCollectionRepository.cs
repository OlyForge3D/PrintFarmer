using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Collections;

/// <summary>
/// Persistence abstraction for <see cref="ModelCollection"/> aggregates and their
/// <see cref="ModelCollectionMembership"/> rows. Model ids are cross-context soft
/// references (no FK); existence validation is performed by the service layer.
/// </summary>
public interface IModelCollectionRepository
{
    /// <summary>Gets a collection by id, optionally including its memberships.</summary>
    Task<ModelCollection?> GetByIdAsync(Guid id, bool includeMemberships, CancellationToken ct);

    /// <summary>Lists collections owned by the given user, ordered by name.</summary>
    Task<IReadOnlyList<ModelCollection>> ListByOwnerAsync(Guid ownerUserId, CancellationToken ct);

    /// <summary>
    /// Lists collections visible to the given user: owned collections plus any shared
    /// collection, ordered by name.
    /// </summary>
    Task<IReadOnlyList<ModelCollection>> ListVisibleAsync(Guid userId, CancellationToken ct);

    /// <summary>Lists all collections (administrator scope), ordered by name.</summary>
    Task<IReadOnlyList<ModelCollection>> ListAllAsync(CancellationToken ct);

    /// <summary>Adds a new collection.</summary>
    Task AddAsync(ModelCollection collection, CancellationToken ct);

    /// <summary>Marks a collection for removal (cascades to memberships).</summary>
    void Remove(ModelCollection collection);

    /// <summary>Lists memberships for a collection, ordered by creation time.</summary>
    Task<IReadOnlyList<ModelCollectionMembership>> ListMembershipsAsync(Guid collectionId, CancellationToken ct);

    /// <summary>Gets a single membership row, or null when absent.</summary>
    Task<ModelCollectionMembership?> GetMembershipAsync(Guid collectionId, Guid modelId, CancellationToken ct);

    /// <summary>Adds a membership row.</summary>
    Task AddMembershipAsync(ModelCollectionMembership membership, CancellationToken ct);

    /// <summary>Removes a membership row.</summary>
    void RemoveMembership(ModelCollectionMembership membership);

    /// <summary>Persists all pending changes in a single transaction.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}

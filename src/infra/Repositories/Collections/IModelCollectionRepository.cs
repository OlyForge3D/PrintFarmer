using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Collections;

/// <summary>
/// Data-access abstraction for <see cref="ModelCollection"/> aggregates and their
/// <see cref="ModelCollectionMembership"/> rows. Contains no authorization logic; callers are
/// expected to enforce owner/admin rules at the service layer.
/// </summary>
public interface IModelCollectionRepository
{
    /// <summary>Gets a tracked collection by id, or null when it does not exist.</summary>
    Task<ModelCollection?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Gets a tracked collection with its memberships eagerly loaded.</summary>
    Task<ModelCollection?> GetByIdWithMembershipsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Lists collections visible to the given user: administrators see every collection; other
    /// users see collections they own plus any collection with shared visibility.
    /// </summary>
    Task<IReadOnlyList<ModelCollection>> ListVisibleToAsync(Guid userId, bool isAdmin, CancellationToken ct);

    /// <summary>Adds a new collection.</summary>
    Task AddAsync(ModelCollection collection, CancellationToken ct);

    /// <summary>Removes a collection (cascade-deletes its memberships).</summary>
    Task RemoveAsync(ModelCollection collection, CancellationToken ct);

    /// <summary>Lists the memberships of a collection ordered by <see cref="ModelCollectionMembership.AddedAt"/>.</summary>
    Task<IReadOnlyList<ModelCollectionMembership>> ListMembershipsAsync(Guid collectionId, CancellationToken ct);

    /// <summary>Gets a single membership by collection and model id, or null when absent.</summary>
    Task<ModelCollectionMembership?> GetMembershipAsync(Guid collectionId, Guid modelId, CancellationToken ct);

    /// <summary>Adds a membership row.</summary>
    Task AddMembershipAsync(ModelCollectionMembership membership, CancellationToken ct);

    /// <summary>Removes a membership row.</summary>
    Task RemoveMembershipAsync(ModelCollectionMembership membership, CancellationToken ct);

    /// <summary>Removes a set of membership rows.</summary>
    Task RemoveMembershipsAsync(IEnumerable<ModelCollectionMembership> memberships, CancellationToken ct);

    /// <summary>Counts the memberships of a collection.</summary>
    Task<int> CountMembershipsAsync(Guid collectionId, CancellationToken ct);

    /// <summary>Persists pending changes.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}

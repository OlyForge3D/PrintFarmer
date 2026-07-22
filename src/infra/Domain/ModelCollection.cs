using System.Collections.Generic;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// A user-owned, optionally shareable collection of 3D models. Model membership is tracked via
/// <see cref="ModelCollectionMembership"/>. Model identifiers are cross-context references (no EF
/// foreign key) and are validated for existence through the model query abstraction, mirroring the
/// tag boundary precedent.
/// </summary>
/// <remarks>
/// Contracts around this entity are designed so the later library-sync work (revisioned change
/// journal, cursor-based pull/apply) can extend them without breaking changes: the entity carries
/// stable identity, owner, visibility and audit timestamps that a journaling layer can observe.
/// </remarks>
public class ModelCollection
{
    /// <summary>Stable unique identifier for the collection.</summary>
    public Guid Id { get; set; }

    /// <summary>Identifier of the user that owns the collection.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Human-readable collection name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional free-text description.</summary>
    public string? Description { get; set; }

    /// <summary>Controls who may read the collection. Mutation is always owner/admin only.</summary>
    public ModelCollectionVisibility Visibility { get; set; } = ModelCollectionVisibility.Private;

    /// <summary>UTC timestamp when the collection was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent mutation to the collection or its membership.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Membership rows linking models to this collection.</summary>
    public ICollection<ModelCollectionMembership> Memberships { get; set; } = new List<ModelCollectionMembership>();
}

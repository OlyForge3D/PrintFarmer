namespace Farm.Infrastructure.Domain.Sync;

/// <summary>
/// Identifies the kind of library entity a <see cref="LibrarySyncChange"/> refers to.
/// Serialized as a string (see <c>JsonStringEnumConverter</c>) to keep the sync contract
/// stable and human-readable for the desktop client.
/// </summary>
public enum SyncEntityType
{
    /// <summary>A <see cref="ModelCollection"/> aggregate root.</summary>
    ModelCollection,

    /// <summary>A <see cref="ModelCollectionMembership"/> join row.</summary>
    ModelCollectionMembership,

    /// <summary>A <see cref="Tag"/> row.</summary>
    Tag
}

/// <summary>
/// The mutation kind recorded by a <see cref="LibrarySyncChange"/>. <see cref="Delete"/>
/// entries are durable tombstones: they persist in the journal after the underlying row
/// has been hard-deleted so pullers can propagate removals.
/// </summary>
public enum SyncOperation
{
    /// <summary>The entity was created.</summary>
    Create,

    /// <summary>The entity's metadata or membership changed.</summary>
    Update,

    /// <summary>The entity was hard-deleted; this journal row is a tombstone.</summary>
    Delete
}

/// <summary>
/// Visibility of the entity at the time of the change, so pullers can scope a change to
/// the correct audience without re-reading (possibly already deleted) entity state.
/// </summary>
public enum SyncVisibility
{
    /// <summary>Visible only to the owner and administrators.</summary>
    Private,

    /// <summary>Visible to all authenticated users (shared).</summary>
    Shared
}

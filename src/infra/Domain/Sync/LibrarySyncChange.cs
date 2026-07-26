namespace Farm.Infrastructure.Domain.Sync;

/// <summary>
/// An append-only journal row describing a single library mutation for bi-directional sync
/// (epic #835). The journal captures collection and membership create/update/delete events
/// with enough metadata (owner, visibility, actor, timestamp) that a client can pull an
/// ordered stream of changes without re-reading entity state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Revision"/> is a store-generated, strictly increasing identity value. It is
/// provider-safe and monotonic across all supported backends (PostgreSQL identity, SQL Server
/// <c>IDENTITY</c>, and SQLite <c>AUTOINCREMENT</c>) and forms the cursor that #845's pull
/// endpoint will page over.
/// </para>
/// <para>
/// Rows are never mutated or deleted. A <see cref="SyncOperation.Delete"/> row is a durable
/// tombstone: it intentionally has no foreign key to the entity it references so it survives
/// the hard delete of that entity.
/// </para>
/// </remarks>
public class LibrarySyncChange
{
    /// <summary>
    /// Monotonic, store-generated revision. Doubles as the primary key and the global sync
    /// cursor. Do not assign manually — it is allocated by the database on insert.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>The kind of entity that changed.</summary>
    public SyncEntityType EntityType { get; set; }

    /// <summary>
    /// Identifier of the changed entity. This is a soft reference (no foreign key) so the
    /// row remains valid as a tombstone after the entity is deleted.
    /// </summary>
    public Guid EntityId { get; set; }

    /// <summary>The mutation kind (create, update, or delete/tombstone).</summary>
    public SyncOperation Operation { get; set; }

    /// <summary>
    /// Owner of the entity at the time of the change, when the entity is owned. Null for
    /// owner-less entities.
    /// </summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>Visibility of the entity at the time of the change.</summary>
    public SyncVisibility Visibility { get; set; }

    /// <summary>The user who performed the change (actor identity).</summary>
    public Guid ActorUserId { get; set; }

    /// <summary>UTC timestamp when the change was recorded.</summary>
    public DateTime Timestamp { get; set; }
}

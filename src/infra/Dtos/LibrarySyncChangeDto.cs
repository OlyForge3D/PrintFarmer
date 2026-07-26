using Farm.Infrastructure.Domain.Sync;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// API representation of a <see cref="LibrarySyncChange"/> journal entry. Property names
/// serialize to camelCase and enum values serialize as strings to match the client contract.
/// This DTO is the wire shape #845's pull endpoint will return; it is defined now so the
/// contract is stable ahead of that work.
/// </summary>
public class LibrarySyncChangeDto
{
    /// <summary>Monotonic revision / global sync cursor for this change.</summary>
    public long Revision { get; set; }

    /// <summary>Kind of entity that changed.</summary>
    public SyncEntityType EntityType { get; set; }

    /// <summary>Identifier of the changed entity.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Mutation kind; <see cref="SyncOperation.Delete"/> is a tombstone.</summary>
    public SyncOperation Operation { get; set; }

    /// <summary>Owner at the time of the change, or null when owner-less.</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>Visibility at the time of the change.</summary>
    public SyncVisibility Visibility { get; set; }

    /// <summary>User who performed the change.</summary>
    public Guid ActorUserId { get; set; }

    /// <summary>UTC timestamp of the change.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Projects a domain journal row onto its API DTO.</summary>
    public static LibrarySyncChangeDto FromDomain(LibrarySyncChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new LibrarySyncChangeDto
        {
            Revision = change.Revision,
            EntityType = change.EntityType,
            EntityId = change.EntityId,
            Operation = change.Operation,
            OwnerUserId = change.OwnerUserId,
            Visibility = change.Visibility,
            ActorUserId = change.ActorUserId,
            Timestamp = change.Timestamp
        };
    }
}

using Farm.Infrastructure.Domain.Sync;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// A batch of client mutations to apply transactionally (#845). Either every operation is
/// applied and journaled under a single unit of work, or — when one or more operations
/// conflict — nothing is persisted and the server responds with the full conflict set.
/// </summary>
public class ApplySyncRequestDto
{
    /// <summary>The ordered operations to apply. Processed as an all-or-nothing batch.</summary>
    public IReadOnlyList<ApplySyncOperationDto> Operations { get; set; } = [];
}

/// <summary>
/// A single mutation within an <see cref="ApplySyncRequestDto"/>. The combination of
/// <see cref="EntityType"/> and <see cref="Operation"/> selects the handler; the remaining
/// fields are interpreted per operation. Enum values are transmitted as strings.
/// </summary>
public class ApplySyncOperationDto
{
    /// <summary>The kind of entity this operation targets.</summary>
    public SyncEntityType EntityType { get; set; }

    /// <summary>The mutation kind (create, update, or delete).</summary>
    public SyncOperation Operation { get; set; }

    /// <summary>
    /// Target entity id. For collection operations this is the collection id (client-supplied
    /// on create so the client can reconcile). Ignored for membership operations, which key on
    /// <see cref="CollectionId"/> + <see cref="ModelId"/>.
    /// </summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// Expected base revision for optimistic concurrency on collection update/delete. When
    /// supplied it must equal the stored revision or the operation conflicts.
    /// </summary>
    public long? BaseRevision { get; set; }

    /// <summary>
    /// Expected concurrency token (ETag) for collection update/delete. When supplied it must
    /// equal the stored token or the operation conflicts. Either this or
    /// <see cref="BaseRevision"/> is required for a collection update/delete.
    /// </summary>
    public Guid? ConcurrencyToken { get; set; }

    /// <summary>Owning collection id for membership add/remove.</summary>
    public Guid? CollectionId { get; set; }

    /// <summary>Model reference id for membership add/remove.</summary>
    public Guid? ModelId { get; set; }

    /// <summary>Collection name for create/update.</summary>
    public string? Name { get; set; }

    /// <summary>Collection description for create/update.</summary>
    public string? Description { get; set; }

    /// <summary>Collection shared flag for create/update.</summary>
    public bool? IsShared { get; set; }
}

/// <summary>
/// Successful result of applying a sync batch. Reports the per-operation outcome and the new
/// server revision (global head) after the batch committed.
/// </summary>
public class ApplySyncResultDto
{
    /// <summary>Per-operation outcomes, in request order.</summary>
    public IReadOnlyList<AppliedSyncOperationDto> Applied { get; set; } = [];

    /// <summary>The highest journal revision after the batch committed.</summary>
    public long ServerRevision { get; set; }
}

/// <summary>The outcome of a single applied operation.</summary>
public class AppliedSyncOperationDto
{
    /// <summary>The kind of entity the operation targeted.</summary>
    public SyncEntityType EntityType { get; set; }

    /// <summary>The mutation kind that was applied.</summary>
    public SyncOperation Operation { get; set; }

    /// <summary>The affected entity id (collection id, or membership id for membership ops).</summary>
    public Guid EntityId { get; set; }

    /// <summary>The affected entity's revision after applying, or 0 when not applicable.</summary>
    public long Revision { get; set; }

    /// <summary>
    /// True when the operation was an idempotent no-op that converged with existing server
    /// state (e.g. adding a member that already exists, or deleting one already gone). Such
    /// genuinely independent membership changes auto-merge instead of conflicting.
    /// </summary>
    public bool Merged { get; set; }
}

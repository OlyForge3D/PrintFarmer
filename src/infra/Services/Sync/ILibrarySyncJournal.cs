using Farm.Infrastructure.Domain.Sync;

namespace Farm.Infrastructure.Services.Sync;

/// <summary>
/// Records and queries the append-only library sync journal (#844). Recording enlists a
/// journal row into the current unit of work without saving, so callers commit the entity
/// mutation and its journal entry with a single <c>SaveChangesAsync</c> — guaranteeing state
/// and journal cannot diverge. Query methods back the cursor-based pull that #845 will expose.
/// </summary>
public interface ILibrarySyncJournal
{
    /// <summary>
    /// Enlists a change into the current unit of work. The row is added to the shared
    /// <c>AppDbContext</c> change tracker but not persisted; the caller's subsequent
    /// <c>SaveChangesAsync</c> commits the entity mutation and this journal entry atomically.
    /// The store assigns the monotonic <see cref="LibrarySyncChange.Revision"/> on save.
    /// </summary>
    /// <param name="entityType">Kind of entity that changed.</param>
    /// <param name="entityId">Identifier of the changed entity (soft reference).</param>
    /// <param name="operation">Mutation kind; <see cref="SyncOperation.Delete"/> is a tombstone.</param>
    /// <param name="ownerUserId">Owner at the time of the change, or null when owner-less.</param>
    /// <param name="visibility">Visibility at the time of the change.</param>
    /// <param name="actorUserId">User who performed the change.</param>
    /// <param name="timestamp">UTC timestamp of the change.</param>
    void Record(
        SyncEntityType entityType,
        Guid entityId,
        SyncOperation operation,
        Guid? ownerUserId,
        SyncVisibility visibility,
        Guid actorUserId,
        DateTime timestamp);

    /// <summary>
    /// Returns journal entries with a revision strictly greater than <paramref name="afterRevision"/>,
    /// ordered by ascending revision. Designed as the pull cursor for #845.
    /// </summary>
    /// <param name="afterRevision">Exclusive lower bound; pass 0 to read from the beginning.</param>
    /// <param name="maxCount">Maximum number of entries to return (batch size).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<LibrarySyncChange>> GetChangesSinceAsync(long afterRevision, int maxCount, CancellationToken ct);

    /// <summary>
    /// Returns all journal entries for a single entity, ordered by ascending revision
    /// (including any tombstone). Useful for history and conflict diagnostics.
    /// </summary>
    Task<IReadOnlyList<LibrarySyncChange>> GetChangesForEntityAsync(SyncEntityType entityType, Guid entityId, CancellationToken ct);

    /// <summary>
    /// Returns the highest revision currently in the journal, or 0 when the journal is empty.
    /// </summary>
    Task<long> GetLatestRevisionAsync(CancellationToken ct);
}

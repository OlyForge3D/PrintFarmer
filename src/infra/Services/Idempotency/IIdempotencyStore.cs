using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Idempotency;

/// <summary>
/// Result of a store lookup/upsert for an Idempotency-Key triple.
/// Exactly one <see cref="Outcome"/> is meaningful per instance.
/// </summary>
/// <param name="Outcome">The lifecycle interpretation the caller should apply.</param>
/// <param name="Record">The existing or newly-inserted record; <c>null</c> when <see cref="IdempotencyLookupOutcome.Bypassed"/>.</param>
public sealed record IdempotencyLookupResult(
    IdempotencyLookupOutcome Outcome,
    IdempotencyRecord? Record);

/// <summary>
/// Outcome of a store lookup for an incoming Idempotency-Key request.
/// </summary>
public enum IdempotencyLookupOutcome
{
    /// <summary>
    /// No matching record was found and one has been freshly inserted in
    /// <see cref="IdempotencyRecordStatus.Processing"/>. The caller must execute
    /// the mutation, then call <c>CompleteAsync</c>.
    /// </summary>
    Inserted,

    /// <summary>
    /// A prior request with the same key/hash has already completed. The caller
    /// must replay the stored response instead of executing the mutation.
    /// </summary>
    ReplayCompleted,

    /// <summary>
    /// A prior request with the same key is still in-flight. The caller must
    /// return a <c>409 Conflict</c> (in-progress).
    /// </summary>
    InProgress,

    /// <summary>
    /// A prior request with the same key exists but its request hash differs.
    /// The caller must return a <c>409 Conflict</c> (hash mismatch).
    /// </summary>
    HashConflict,

    /// <summary>
    /// The store bypassed persistence (feature disabled or malformed key). The
    /// caller executes the mutation normally without replay support.
    /// </summary>
    Bypassed,
}

/// <summary>
/// Persistent store for <see cref="IdempotencyRecord"/> entries backing the
/// <c>Idempotency-Key</c> header on gated write endpoints (issue #715).
///
/// <para>
/// The store is authoritative only when the caller has verified the
/// <c>offlineWriteReplayEnabled</c> operator feature flag is on. Callers are
/// responsible for that gate; a disabled feature must never reach the store.
/// </para>
///
/// <para>
/// Retention: entries older than <see cref="RetentionWindow"/> from their
/// immutable <see cref="IdempotencyRecord.CreatedAt"/> value are treated as
/// non-existent by <see cref="TryBeginAsync"/> and are pruned in the background
/// by the cleanup hosted service. Read-side filtering ensures an expired record
/// is never mistaken for a valid replay even before it is deleted.
/// </para>
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>Retention window measured from <see cref="IdempotencyRecord.CreatedAt"/>.</summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Look up or insert a record for (<paramref name="userId"/>, <paramref name="routeKey"/>,
    /// <paramref name="idempotencyKey"/>). Uses the composite unique index to serialize
    /// concurrent first-requests: exactly one racing caller receives
    /// <see cref="IdempotencyLookupOutcome.Inserted"/>; every other caller receives
    /// either a replay, in-progress, or hash-conflict outcome.
    /// </summary>
    Task<IdempotencyLookupResult> TryBeginAsync(
        string userId,
        string routeKey,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct);

    /// <summary>
    /// Persist the captured response for a previously inserted record, transitioning
    /// it to <see cref="IdempotencyRecordStatus.Completed"/>. Idempotent: repeat
    /// invocations are silently ignored so callers do not need to distinguish
    /// concurrent completion attempts.
    /// </summary>
    Task CompleteAsync(
        Guid recordId,
        int statusCode,
        string? contentType,
        byte[] responseBody,
        CancellationToken ct);

    /// <summary>
    /// Removes a <see cref="IdempotencyRecordStatus.Processing"/> record on failure
    /// so a client retry can execute normally rather than being blocked as
    /// in-progress until the entry ages out. Safe to call on already-completed
    /// records — completed rows are left in place.
    /// </summary>
    Task AbandonProcessingAsync(Guid recordId, CancellationToken ct);

    /// <summary>
    /// Delete all records older than the retention window from
    /// <paramref name="now"/>. Concurrency-safe: bulk delete by predicate so
    /// overlapping cleanup runs never conflict on individual rows. Returns the
    /// number of rows removed.
    /// </summary>
    Task<int> PruneExpiredAsync(DateTime now, CancellationToken ct);
}

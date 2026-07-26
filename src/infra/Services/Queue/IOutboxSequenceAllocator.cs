namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Allocates monotonically increasing sequence numbers for <c>QueueDispatchOutbox</c>
/// events within the current process lifetime.
///
/// Provides a process-local transactionally fenced allocator: the counter is seeded
/// from the database maximum on first use (so post-crash restarts continue after the
/// last persisted value), and each subsequent call returns a strictly increasing value
/// via <see cref="System.Threading.Interlocked.Increment(ref long)"/>.
///
/// This guarantees uniqueness within a single-process deployment (the typical PrintFarmer
/// web-server configuration). A unique index on <c>QueueDispatchOutbox.Sequence</c> enforces
/// database-level uniqueness and will surface any collision as a constraint violation.
/// </summary>
public interface IOutboxSequenceAllocator
{
    /// <summary>
    /// Seeds the allocator from the current database maximum sequence.
    /// Must be called once during application startup before any events are written.
    /// Safe to call multiple times — subsequent calls are no-ops if already initialized.
    /// </summary>
    /// <param name="currentDatabaseMax">
    /// The current maximum <c>Sequence</c> value in the outbox table (or <c>0</c> if empty).
    /// </param>
    void Seed(long currentDatabaseMax);

    /// <summary>
    /// Returns the next unique monotonically increasing sequence number.
    /// Thread-safe; multiple concurrent callers each receive a distinct value.
    /// </summary>
    long Next();
}

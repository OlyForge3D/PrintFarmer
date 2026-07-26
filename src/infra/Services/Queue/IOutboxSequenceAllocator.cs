using Farm.Infrastructure.Data;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Allocates monotonically increasing sequence numbers for <c>QueueDispatchOutbox</c>
/// events using a database-backed counter row committed atomically in the same transaction
/// as the outbox event write.
///
/// This is the authoritative cross-process allocator. Unlike a process-local Interlocked
/// counter, this allocator uses a single <c>OutboxSequenceState</c> row protected by an
/// optimistic concurrency token: only one concurrent writer can commit per sequence slot,
/// ensuring uniqueness across multiple API instances without relying on application-level
/// state or startup seeding.
/// </summary>
public interface IDbOutboxSequenceAllocator
{
    /// <summary>
    /// Increments the shared <c>OutboxSequenceState</c> counter row within
    /// <paramref name="db"/> and returns the next sequence value.
    ///
    /// The increment is tracked by <paramref name="db"/> and MUST be committed by
    /// the caller in the same <see cref="AppDbContext.SaveChangesAsync"/> call as
    /// the outbox event insert. If two concurrent writers race on the same slot,
    /// exactly one succeeds; the other receives a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
    /// which the caller's existing exception handler maps to a typed conflict result.
    /// </summary>
    /// <param name="db">The ambient DbContext whose transaction will commit the increment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The next allocated sequence value.</returns>
    Task<long> AllocateAsync(AppDbContext db, CancellationToken ct = default);
}

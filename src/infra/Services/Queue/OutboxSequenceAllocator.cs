using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Database-backed cross-process monotonic sequence allocator.
///
/// Each call to <see cref="AllocateAsync"/> loads the single <see cref="OutboxSequenceState"/>
/// row via the caller's <see cref="AppDbContext"/>, increments <c>NextSequence</c> in-memory,
/// and returns the new value. Because the entity is tracked by the same context, the increment
/// is committed atomically when the caller calls <c>SaveChangesAsync()</c> — in the same
/// database transaction as the outbox event insert.
///
/// The <c>RowVersion</c> optimistic concurrency token on <see cref="OutboxSequenceState"/>
/// ensures that two concurrent writers race-detect each other: the loser's
/// <c>SaveChangesAsync()</c> throws <c>DbUpdateConcurrencyException</c> and the caller's
/// existing exception handler maps this to a typed conflict response.
///
/// This replaces the former process-local <c>Interlocked.Increment</c> approach which
/// could produce duplicate sequences when multiple API instances observed the same
/// database maximum on startup.
/// </summary>
public sealed class DbOutboxSequenceAllocator : IDbOutboxSequenceAllocator
{
    /// <inheritdoc />
    public async Task<long> AllocateAsync(AppDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Load the single counter row (Id = 1). If already tracked in this context
        // (e.g., a second allocation in the same unit of work), EF Core returns the
        // cached — already-incremented — entity, so each call within a transaction
        // produces a unique monotonically increasing value.
        OutboxSequenceState state = await db.OutboxSequenceStates.SingleAsync(ct);
        state.NextSequence++;

        // Return the new value; the actual DB commit happens when the caller calls
        // SaveChangesAsync() — together with the outbox event write.
        return state.NextSequence;
    }
}

using Farm.Infrastructure.Data;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Allocates monotonically increasing sequence numbers for <c>QueueDispatchOutbox</c>
/// events with a provider-native atomic database statement.
/// </summary>
public interface IDbOutboxSequenceAllocator
{
    /// <summary>
    /// Atomically increments the shared counter and returns the next sequence.
    /// Relational callers must own a transaction that also inserts the outbox event.
    /// This prevents a later sequence from becoming visible before an earlier allocated
    /// event and being skipped by cursor consumers.
    /// </summary>
    /// <param name="db">The ambient DbContext whose transaction will commit the increment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The next allocated sequence value.</returns>
    Task<long> AllocateAsync(AppDbContext db, CancellationToken ct = default);
}

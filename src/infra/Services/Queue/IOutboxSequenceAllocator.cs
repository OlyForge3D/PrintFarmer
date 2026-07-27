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
    /// When the caller already owns a relational transaction, allocation participates
    /// in that transaction; otherwise sequence gaps are permitted if the later event
    /// write rolls back, but duplicate or regressing values are not.
    /// </summary>
    /// <param name="db">The ambient DbContext whose transaction will commit the increment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The next allocated sequence value.</returns>
    Task<long> AllocateAsync(AppDbContext db, CancellationToken ct = default);
}

using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Process-local transactionally fenced sequence allocator for the durable queue outbox.
///
/// Seeded from <c>MAX(Sequence)</c> in <c>QueueDispatchOutbox</c> at startup via
/// <see cref="OutboxSequenceSeedService"/>, then incremented atomically with
/// <see cref="System.Threading.Interlocked.Increment(ref long)"/> for each new event.
///
/// A unique database index on <c>Sequence</c> enforces correctness: any collision
/// (e.g., from a multi-process deployment without shared state) surfaces as a constraint
/// violation rather than silent duplicate ordering.
/// </summary>
public sealed class OutboxSequenceAllocator : IOutboxSequenceAllocator
{
    private long _counter;
    private bool _seeded;
    private readonly object _seedLock = new();

    /// <inheritdoc />
    public void Seed(long currentDatabaseMax)
    {
        lock (_seedLock)
        {
            if (_seeded)
            {
                return;
            }

            // Start one above the current DB maximum so post-crash restarts
            // never re-use a sequence value that was already persisted.
            _counter = Math.Max(currentDatabaseMax, 0);
            _seeded = true;
        }
    }

    /// <inheritdoc />
    public long Next()
    {
        return System.Threading.Interlocked.Increment(ref _counter);
    }
}

/// <summary>
/// Startup service that seeds the <see cref="OutboxSequenceAllocator"/> singleton from the
/// current database maximum before any outbox events are written.
/// Runs exactly once as a hosted service at application start.
/// </summary>
public sealed class OutboxSequenceSeedService(
    IOutboxSequenceAllocator allocator,
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxSequenceSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            long maxSeq = await db.QueueDispatchOutbox
                .AsNoTracking()
                .MaxAsync(e => (long?)e.Sequence, cancellationToken) ?? 0L;

            allocator.Seed(maxSeq);

            logger.LogInformation(
                "[OutboxSequence] Sequence allocator seeded from DB max={Max}; next will be {Next}",
                maxSeq,
                maxSeq + 1);
        }
        catch (Exception ex)
        {
            // If the DB is unavailable on startup (first run), seed from 0.
            // The unique index will catch any collisions.
            logger.LogWarning(
                ex,
                "[OutboxSequence] Could not query DB max sequence; seeding from 0. If this is a fresh database, this is expected.");
            allocator.Seed(0);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Idempotency;

/// <summary>
/// EF Core implementation of <see cref="IIdempotencyStore"/>. Uses
/// <see cref="IDbContextFactory{TContext}"/> so it can be resolved outside a
/// request scope (hosted cleanup service, background workers).
///
/// <para>
/// Concurrency model: the composite unique index on
/// <c>(UserId, RouteKey, IdempotencyKey)</c> serializes racing first-requests
/// at the database. The "insert-then-catch-unique-violation-then-reload" pattern
/// is used because it lets the store handle two racing callers atomically without
/// requiring provider-specific upsert syntax.
/// </para>
///
/// <para>
/// Expired records: an existing row with <c>CreatedAt &lt; now - RetentionWindow</c>
/// is deleted before a fresh insert is attempted. Read-side interpretation always
/// ignores expired rows so a stale row never masquerades as a replay even before
/// the cleanup service prunes it.
/// </para>
/// </summary>
public sealed class IdempotencyStore(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<IdempotencyStore> logger) : IIdempotencyStore
{
    /// <inheritdoc />
    public async Task<IdempotencyLookupResult> TryBeginAsync(
        string userId,
        string routeKey,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        DateTime now = DateTime.UtcNow;
        DateTime cutoff = now - IIdempotencyStore.RetentionWindow;

        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);

        IdempotencyRecord? existing = await db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.UserId == userId
                    && r.RouteKey == routeKey
                    && r.IdempotencyKey == idempotencyKey,
                ct);

        if (existing is not null)
        {
            if (existing.CreatedAt < cutoff)
            {
                // Expired: purge before attempting a fresh insert. If a concurrent
                // caller beat us to the delete or the row is already gone, ExecuteDeleteAsync
                // simply returns 0 — no error.
                _ = await db.IdempotencyRecords
                    .Where(r => r.Id == existing.Id)
                    .ExecuteDeleteAsync(ct);
            }
            else if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return new IdempotencyLookupResult(IdempotencyLookupOutcome.HashConflict, existing);
            }
            else if (existing.Status == IdempotencyRecordStatus.Completed)
            {
                return new IdempotencyLookupResult(IdempotencyLookupOutcome.ReplayCompleted, existing);
            }
            else
            {
                return new IdempotencyLookupResult(IdempotencyLookupOutcome.InProgress, existing);
            }
        }

        IdempotencyRecord record = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RouteKey = routeKey,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            Status = IdempotencyRecordStatus.Processing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ = db.IdempotencyRecords.Add(record);

        try
        {
            _ = await db.SaveChangesAsync(ct);
            return new IdempotencyLookupResult(IdempotencyLookupOutcome.Inserted, record);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent first-request won the race. Reload the winning row and
            // interpret it exactly as we would in the initial-read path.
            logger.LogInformation(
                ex,
                "Idempotency-Key race resolved by unique index for route={RouteKey} user={UserId}; reloading winner.",
                routeKey,
                userId);

            await using AppDbContext readDb = await dbFactory.CreateDbContextAsync(ct);
            IdempotencyRecord? winner = await readDb.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.UserId == userId
                        && r.RouteKey == routeKey
                        && r.IdempotencyKey == idempotencyKey,
                    ct);
            if (winner is null || winner.CreatedAt < cutoff)
            {
                // The winning row vanished or is itself expired. Rather than looping
                // (which risks livelock under sustained contention) we fall back to
                // Bypassed so the caller executes the mutation normally. A subsequent
                // retry with the same key will find a stable state.
                return new IdempotencyLookupResult(IdempotencyLookupOutcome.Bypassed, null);
            }

            if (!string.Equals(winner.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return new IdempotencyLookupResult(IdempotencyLookupOutcome.HashConflict, winner);
            }

            return winner.Status == IdempotencyRecordStatus.Completed
                ? new IdempotencyLookupResult(IdempotencyLookupOutcome.ReplayCompleted, winner)
                : new IdempotencyLookupResult(IdempotencyLookupOutcome.InProgress, winner);
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        Guid recordId,
        int statusCode,
        string? contentType,
        byte[] responseBody,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(responseBody);

        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        IdempotencyRecord? record = await db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
        {
            // Record was pruned or abandoned between begin and complete — leave it
            // gone; the client will retry and go through the normal insert path.
            return;
        }

        if (record.Status == IdempotencyRecordStatus.Completed)
        {
            // Already completed by a racing observer; leave as-is to preserve
            // the first-writer response bytes.
            return;
        }

        record.Status = IdempotencyRecordStatus.Completed;
        record.ResponseStatusCode = statusCode;
        record.ResponseContentType = contentType;
        record.ResponseBody = responseBody;
        record.UpdatedAt = DateTime.UtcNow;
        _ = await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task AbandonProcessingAsync(Guid recordId, CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        _ = await db.IdempotencyRecords
            .Where(r => r.Id == recordId && r.Status == IdempotencyRecordStatus.Processing)
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> PruneExpiredAsync(DateTime now, CancellationToken ct)
    {
        DateTime cutoff = now - IIdempotencyStore.RetentionWindow;
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Bulk delete-by-predicate: concurrency-safe because it does not enumerate
        // and each row is deleted in a single statement using the same predicate.
        int removed = await db.IdempotencyRecords
            .Where(r => r.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            logger.LogInformation("Pruned {Count} expired idempotency records older than {Cutoff:O}.", removed, cutoff);
        }

        return removed;
    }

    /// <summary>
    /// True when the exception represents a unique-constraint violation across
    /// the supported providers. Matches the same heuristic as the printed-parts
    /// service so the store stays provider-neutral.
    /// </summary>
    /// <remarks>
    /// The <c>constraint failed</c> match covers SQLite (error code 19,
    /// <c>SQLITE_CONSTRAINT</c>), whose default message does not include the
    /// literal string <c>UNIQUE</c> when raised from a batched insert. This is
    /// safe here because <c>IdempotencyRecords</c> only has the composite
    /// unique index — the table has no CHECK constraints or foreign keys that
    /// could produce a competing constraint failure.
    /// </remarks>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        Exception? inner = ex.InnerException;
        while (inner is not null)
        {
            string message = inner.Message ?? string.Empty;
            if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("23505", StringComparison.Ordinal)
                || message.Contains("2601", StringComparison.Ordinal)
                || message.Contains("2627", StringComparison.Ordinal))
            {
                return true;
            }

            inner = inner.InnerException;
        }

        return false;
    }
}

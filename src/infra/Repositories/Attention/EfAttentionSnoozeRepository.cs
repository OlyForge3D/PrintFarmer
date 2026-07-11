using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Attention;

/// <summary>
/// EF Core implementation of <see cref="IAttentionSnoozeRepository"/>.
/// </summary>
public sealed class EfAttentionSnoozeRepository(AppDbContext dbContext) : IAttentionSnoozeRepository
{
    private readonly AppDbContext _db = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionSnooze>> GetActiveForUserAsync(
        Guid userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        List<AttentionSnooze> rows = await _db.AttentionSnoozes
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.SnoozedUntilUtc > nowUtc)
            .ToListAsync(cancellationToken);
        return rows;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The <c>IX_AttentionSnoozes_UserId_AttentionItemId</c> unique index (see
    /// <c>AttentionSnoozeConfiguration</c>) makes racing inserts by the same user for the
    /// same item collide at commit time. Only that unique/primary-key violation is caught
    /// (via <see cref="IsUniqueViolation"/>, mirroring the provider-agnostic detection used
    /// by <c>TagService</c>); we then retry once as a read-modify-save so exactly one
    /// logical snooze survives. Any other <see cref="DbUpdateException"/> (NOT NULL, foreign
    /// key, connection failure, …) propagates unchanged.
    /// </remarks>
    public async Task<AttentionSnooze> UpsertAsync(
        Guid userId,
        string attentionItemId,
        DateTime snoozedUntilUtc,
        DateTime nowUtc,
        DateTime? attentionItemAnchorAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attentionItemId);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            AttentionSnooze? existing = await _db.AttentionSnoozes
                .FirstOrDefaultAsync(
                    s => s.UserId == userId && s.AttentionItemId == attentionItemId,
                    cancellationToken);

            if (existing is null)
            {
                AttentionSnooze inserted = new()
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    AttentionItemId = attentionItemId,
                    SnoozedUntilUtc = snoozedUntilUtc,
                    CreatedAtUtc = nowUtc,
                    AttentionItemAnchorAtUtc = attentionItemAnchorAtUtc,
                };
                _ = _db.AttentionSnoozes.Add(inserted);
                try
                {
                    _ = await _db.SaveChangesAsync(cancellationToken);
                    return inserted;
                }
                catch (DbUpdateException ex) when (attempt == 0 && IsUniqueViolation(ex))
                {
                    // A concurrent insert by the same (user, item) won the unique index.
                    // Detach our tentative row and loop so the second pass reads the winner
                    // and updates it. Non-unique failures fall through and propagate.
                    _db.Entry(inserted).State = EntityState.Detached;
                    continue;
                }
            }

            // Update path: a concurrent update cannot violate the unique index, so no retry
            // is needed here. Optimistic/other failures propagate to the caller.
            existing.SnoozedUntilUtc = snoozedUntilUtc;
            existing.AttentionItemAnchorAtUtc = attentionItemAnchorAtUtc;
            _ = await _db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        // Unreachable: attempt 0 either returns, updates, or rethrows a non-unique failure;
        // attempt 1 always finds the winning row and returns via the update path.
        throw new InvalidOperationException("AttentionSnooze upsert failed after a unique-violation retry.");
    }

    /// <summary>
    /// Provider-agnostic detection of a unique/primary-key constraint violation, matching the
    /// established pattern in <c>Farm.Infrastructure.Services.Tags.TagService</c> (SQLite,
    /// SQL Server, and PostgreSQL wordings).
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        string? message = ex.InnerException?.Message;
        return message is not null
            && (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Violation of PRIMARY KEY", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Violation of UNIQUE KEY", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        Guid userId,
        string attentionItemId,
        CancellationToken cancellationToken = default)
    {
        AttentionSnooze? existing = await _db.AttentionSnoozes
            .FirstOrDefaultAsync(
                s => s.UserId == userId && s.AttentionItemId == attentionItemId,
                cancellationToken);
        if (existing is null)
        {
            return false;
        }

        _ = _db.AttentionSnoozes.Remove(existing);
        try
        {
            _ = await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent request already deleted this row between our read and save. The
            // desired end state (no snooze) is achieved, so this DELETE is idempotently
            // successful and consistent with the endpoint's 204 contract (issue #707, R5).
            // Detach the phantom entity so the tracker is left in a safe state; other
            // DbUpdateExceptions are NOT caught and continue to propagate.
            _db.Entry(existing).State = EntityState.Detached;
            return true;
        }
    }
}

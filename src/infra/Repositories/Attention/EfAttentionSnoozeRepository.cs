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
    /// The <c>UX_AttentionSnooze_User_Item</c> unique index (see
    /// <c>AttentionSnoozeConfiguration</c>) makes racing inserts by the same user for the
    /// same item collide at commit time. We handle that by retrying once as an update:
    /// on the retry pass the other transaction has already written the row, so
    /// <c>FirstOrDefault</c> returns it and the second caller updates the winning row.
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
                catch (DbUpdateException) when (attempt == 0)
                {
                    // Racing insert from another request won. Detach our tentative entity
                    // and re-fetch so the second pass sees the winning row and updates it.
                    _db.ChangeTracker.Clear();
                    continue;
                }
            }

            existing.SnoozedUntilUtc = snoozedUntilUtc;
            existing.AttentionItemAnchorAtUtc = attentionItemAnchorAtUtc;
            try
            {
                _ = await _db.SaveChangesAsync(cancellationToken);
                return existing;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                _db.ChangeTracker.Clear();
                continue;
            }
        }

        // Unreachable: the loop either returns or throws on the second attempt.
        throw new InvalidOperationException("AttentionSnooze upsert failed after retry.");
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
        _ = await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

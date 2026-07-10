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
    public async Task<AttentionSnooze> UpsertAsync(
        Guid userId,
        string attentionItemId,
        DateTime snoozedUntilUtc,
        DateTime nowUtc,
        DateTime? attentionItemAnchorAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attentionItemId);

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
            _ = await _db.SaveChangesAsync(cancellationToken);
            return inserted;
        }

        existing.SnoozedUntilUtc = snoozedUntilUtc;
        existing.AttentionItemAnchorAtUtc = attentionItemAnchorAtUtc;
        _ = await _db.SaveChangesAsync(cancellationToken);
        return existing;
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

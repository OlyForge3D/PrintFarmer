using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Sync;

/// <summary>
/// Default <see cref="ILibrarySyncJournal"/> backed by <see cref="AppDbContext"/>. It shares
/// the scoped context with the collection repository, so journal rows recorded here are
/// committed in the same transaction as the entity mutation that produced them.
/// </summary>
public class LibrarySyncJournal(AppDbContext dbContext) : ILibrarySyncJournal
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private DbSet<LibrarySyncChange> Changes => _dbContext.Set<LibrarySyncChange>();

    /// <inheritdoc/>
    public void Record(
        SyncEntityType entityType,
        Guid entityId,
        SyncOperation operation,
        Guid? ownerUserId,
        SyncVisibility visibility,
        Guid actorUserId,
        DateTime timestamp)
    {
        // Revision is intentionally left at 0: it is store-generated (identity) and assigned
        // by the database on save, keeping revisions monotonic across concurrent writers.
        _ = Changes.Add(new LibrarySyncChange
        {
            EntityType = entityType,
            EntityId = entityId,
            Operation = operation,
            OwnerUserId = ownerUserId,
            Visibility = visibility,
            ActorUserId = actorUserId,
            Timestamp = timestamp
        });
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LibrarySyncChange>> GetChangesSinceAsync(long afterRevision, int maxCount, CancellationToken ct)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        return await Changes
            .AsNoTracking()
            .Where(c => c.Revision > afterRevision)
            .OrderBy(c => c.Revision)
            .Take(maxCount)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LibrarySyncChange>> GetVisibleChangesSinceAsync(
        long afterRevision,
        Guid callerUserId,
        bool callerIsAdmin,
        int maxCount,
        CancellationToken ct)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        IQueryable<LibrarySyncChange> query = Changes
            .AsNoTracking()
            .Where(c => c.Revision > afterRevision);

        if (!callerIsAdmin)
        {
            // Regular callers see only what they may observe: their own changes, shared
            // changes, and owner-less changes. Applied in-store so no out-of-scope row is
            // ever materialized, keeping the cursor leak-proof across users.
            query = query.Where(c =>
                c.OwnerUserId == callerUserId
                || c.Visibility == SyncVisibility.Shared
                || c.OwnerUserId == null);
        }

        return await query
            .OrderBy(c => c.Revision)
            .Take(maxCount)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LibrarySyncChange>> GetChangesForEntityAsync(SyncEntityType entityType, Guid entityId, CancellationToken ct)
    {
        return await Changes
            .AsNoTracking()
            .Where(c => c.EntityType == entityType && c.EntityId == entityId)
            .OrderBy(c => c.Revision)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<long> GetLatestRevisionAsync(CancellationToken ct)
    {
        return await Changes
            .AsNoTracking()
            .OrderByDescending(c => c.Revision)
            .Select(c => c.Revision)
            .FirstOrDefaultAsync(ct);
    }
}

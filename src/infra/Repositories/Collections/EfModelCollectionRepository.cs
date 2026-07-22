using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Collections;

/// <summary>
/// Entity Framework implementation of <see cref="IModelCollectionRepository"/> backed by
/// <see cref="AppDbContext"/>.
/// </summary>
public class EfModelCollectionRepository(AppDbContext dbContext) : IModelCollectionRepository
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private DbSet<ModelCollection> Collections => _dbContext.Set<ModelCollection>();

    private DbSet<ModelCollectionMembership> Memberships => _dbContext.Set<ModelCollectionMembership>();

    /// <inheritdoc/>
    public async Task<ModelCollection?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await Collections.FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <inheritdoc/>
    public async Task<ModelCollection?> GetByIdWithMembershipsAsync(Guid id, CancellationToken ct)
    {
        return await Collections
            .Include(c => c.Memberships)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollection>> ListVisibleToAsync(Guid userId, bool isAdmin, CancellationToken ct)
    {
        IQueryable<ModelCollection> query = Collections
            .AsNoTracking()
            .Include(c => c.Memberships);

        if (!isAdmin)
        {
            query = query.Where(c => c.OwnerUserId == userId || c.Visibility == ModelCollectionVisibility.Shared);
        }

        return await query
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task AddAsync(ModelCollection collection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _ = await Collections.AddAsync(collection, ct);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(ModelCollection collection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _ = Collections.Remove(collection);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollectionMembership>> ListMembershipsAsync(Guid collectionId, CancellationToken ct)
    {
        return await Memberships
            .AsNoTracking()
            .Where(m => m.CollectionId == collectionId)
            .OrderBy(m => m.AddedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionMembership?> GetMembershipAsync(Guid collectionId, Guid modelId, CancellationToken ct)
    {
        return await Memberships
            .FirstOrDefaultAsync(m => m.CollectionId == collectionId && m.ModelId == modelId, ct);
    }

    /// <inheritdoc/>
    public async Task AddMembershipAsync(ModelCollectionMembership membership, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(membership);
        _ = await Memberships.AddAsync(membership, ct);
    }

    /// <inheritdoc/>
    public Task RemoveMembershipAsync(ModelCollectionMembership membership, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(membership);
        _ = Memberships.Remove(membership);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveMembershipsAsync(IEnumerable<ModelCollectionMembership> memberships, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        Memberships.RemoveRange(memberships);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<int> CountMembershipsAsync(Guid collectionId, CancellationToken ct)
    {
        return await Memberships.CountAsync(m => m.CollectionId == collectionId, ct);
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        _ = await _dbContext.SaveChangesAsync(ct);
    }
}

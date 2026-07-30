using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Collections;

/// <summary>
/// Entity Framework implementation of <see cref="IModelCollectionRepository"/> backed by
/// <see cref="AppDbContext"/>. Model ids are stored as cross-context soft references with
/// no foreign key, consistent with the tag/context-boundary precedent.
/// </summary>
public class EfModelCollectionRepository(AppDbContext dbContext) : IModelCollectionRepository
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private DbSet<ModelCollection> Collections => _dbContext.Set<ModelCollection>();

    private DbSet<ModelCollectionMembership> Memberships => _dbContext.Set<ModelCollectionMembership>();

    /// <inheritdoc/>
    public async Task<ModelCollection?> GetByIdAsync(Guid id, bool includeMemberships, CancellationToken ct)
    {
        IQueryable<ModelCollection> query = Collections;
        if (includeMemberships)
        {
            query = query.Include(c => c.Memberships);
        }

        return await query.FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollection>> ListByOwnerAsync(Guid ownerUserId, CancellationToken ct)
    {
        return await Collections
            .Where(c => c.OwnerUserId == ownerUserId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollection>> ListVisibleAsync(Guid userId, CancellationToken ct)
    {
        return await Collections
            .Where(c => c.OwnerUserId == userId || c.IsShared)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollection>> ListAllAsync(CancellationToken ct)
    {
        return await Collections
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task AddAsync(ModelCollection collection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _ = await Collections.AddAsync(collection, ct);
    }

    /// <inheritdoc/>
    public void Remove(ModelCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _ = Collections.Remove(collection);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollectionMembership>> ListMembershipsAsync(Guid collectionId, CancellationToken ct)
    {
        return await Memberships
            .Where(m => m.CollectionId == collectionId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
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
    public void RemoveMembership(ModelCollectionMembership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        _ = Memberships.Remove(membership);
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        _ = await _dbContext.SaveChangesAsync(ct);
    }
}

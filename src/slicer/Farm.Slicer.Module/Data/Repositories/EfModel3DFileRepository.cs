using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IModel3DFileRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
/// <remarks>
/// Unlike the core-domain equivalent this implementation does not join on FolderNode or
/// Model3DTag navigation properties. Folder filtering uses <see cref="StoredFileBase.FolderId"/>
/// directly. Tag filtering is delegated to the service layer via cross-context queries.
/// </remarks>
public class EfModel3DFileRepository(SlicerDbContext db) : IModel3DFileRepository
{
    private readonly SlicerDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc/>
    public async Task AddAsync(Model3D model, CancellationToken ct)
    {
        _ = await _db.Models3D.AddAsync(model, ct);
    }

    /// <inheritdoc/>
    public async Task<Model3D?> GetByHashAsync(string fileHash, CancellationToken ct)
    {
        return await _db.Models3D.FirstOrDefaultAsync(m => m.FileHash == fileHash, ct);
    }

    /// <inheritdoc/>
    public async Task<Model3D?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await _db.Models3D.FirstOrDefaultAsync(m => m.Id == id && m.IsValid, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Model3D>> ListValidAsync(CancellationToken ct)
    {
        return await _db.Models3D
            .Where(m => m.IsValid)
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<List<Model3D>> ListValidByFolderAsync(Guid folderId, CancellationToken ct)
    {
        return await _db.Models3D
            .Where(m => m.IsValid && m.FolderId == folderId)
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> CountValidAsync(CancellationToken ct)
    {
        return await _db.Models3D.Where(m => m.IsValid).CountAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<(List<Model3D> Models, int TotalCount)> QueryModelsAsync(
        Guid[]? folderIds,
        string? search,
        string? sortBy,
        string? sortOrder,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        IQueryable<Model3D> query = _db.Models3D.Where(m => m.IsValid);

        // Filter by folder IDs (resolved externally at service layer)
        if (folderIds is { Length: > 0 })
        {
            query = query.Where(m => folderIds.Contains(m.FolderId));
        }

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(m => m.FileName.Contains(search));
        }

        int totalCount = await query.CountAsync(ct);

        query = (sortBy?.ToLower(), sortOrder?.ToLower()) switch
        {
            ("size", "desc") => query.OrderByDescending(m => m.FileSizeBytes),
            ("size", _) => query.OrderBy(m => m.FileSizeBytes),
            ("date", "desc") => query.OrderByDescending(m => m.UploadedAt),
            ("date", _) => query.OrderBy(m => m.UploadedAt),
            ("name", "desc") => query.OrderByDescending(m => m.FileName),
            _ => query.OrderBy(m => m.FileName)
        };

        int skip = (page - 1) * pageSize;
        List<Model3D> models = await query
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);

        return (models, totalCount);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Model3D>> SearchAsync(string? query, string sortBy, bool descending, int skip, int take, CancellationToken ct)
    {
        IQueryable<Model3D> dbQuery = _db.Models3D.Where(m => m.IsValid);

        if (!string.IsNullOrWhiteSpace(query))
        {
            string searchTerm = query.ToLowerInvariant();
            dbQuery = dbQuery.Where(m =>
                m.FileName.Contains(searchTerm) ||
                (m.Description != null && m.Description.Contains(searchTerm)));
        }

        dbQuery = sortBy?.ToLower() switch
        {
            "name" => descending ? dbQuery.OrderByDescending(m => m.FileName) : dbQuery.OrderBy(m => m.FileName),
            "size" => descending ? dbQuery.OrderByDescending(m => m.FileSizeBytes) : dbQuery.OrderBy(m => m.FileSizeBytes),
            _ => descending ? dbQuery.OrderByDescending(m => m.UploadedAt) : dbQuery.OrderBy(m => m.UploadedAt)
        };

        return await dbQuery
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(Model3D model, CancellationToken ct)
    {
        _ = _db.Models3D.Remove(model);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateAsync(Model3D model, CancellationToken ct)
    {
        _ = _db.Models3D.Update(model);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        _ = await _db.SaveChangesAsync(ct);
    }
}

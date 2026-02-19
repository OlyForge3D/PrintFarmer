using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Api.Repositories;

/// <summary>
/// Entity Framework implementation for file audit repository.
/// Provides read access to files and write access to audit results.
/// Uses AppDbContext for GcodeFile/FileHealthAudit (core domain) and
/// SlicerDbContext for Model3D (slicer module domain).
/// </summary>
public class EfFileAuditRepository(
    IDbContextFactory<AppDbContext> dbFactory,
    IDbContextFactory<SlicerDbContext>? slicerDbFactory = null) : IFileAuditRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    private readonly IDbContextFactory<SlicerDbContext>? _slicerDbFactory = slicerDbFactory;

    public async Task<IReadOnlyList<Model3D>> GetAllModel3DFilesAsync(CancellationToken ct = default)
    {
        if (_slicerDbFactory is null)
        {
            return [];
        }

        using SlicerDbContext db = _slicerDbFactory.CreateDbContext();
        return await db.Set<Model3D>().AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GcodeFile>> GetAllGcodeFilesAsync(CancellationToken ct = default)
    {
        using AppDbContext db = _dbFactory.CreateDbContext();
        return await db.Set<GcodeFile>().AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetAllModel3DPathsAsync(CancellationToken ct = default)
    {
        if (_slicerDbFactory is null)
        {
            return [];
        }

        using SlicerDbContext db = _slicerDbFactory.CreateDbContext();
        return await db.Set<Model3D>()
            .AsNoTracking()
            .Select(m => m.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetAllGcodePathsAsync(CancellationToken ct = default)
    {
        using AppDbContext db = _dbFactory.CreateDbContext();
        return await db.Set<GcodeFile>()
            .AsNoTracking()
            .Select(g => g.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToListAsync(ct);
    }

    public async Task SaveAuditResultAsync(FileHealthAudit auditResult, CancellationToken ct = default)
    {
        using AppDbContext db = _dbFactory.CreateDbContext();
        _ = db.Set<FileHealthAudit>().Add(auditResult);
        _ = await db.SaveChangesAsync(ct);
    }
}

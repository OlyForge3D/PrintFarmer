using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.FileConsistency;

/// <summary>
/// Entity Framework implementation for file audit repository.
/// Provides read access to files and write access to audit results.
/// </summary>
public class EfFileAuditRepository : IFileAuditRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public EfFileAuditRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    public async Task<IReadOnlyList<Model3D>> GetAllModel3DFilesAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.Set<Model3D>().AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GcodeFile>> GetAllGcodeFilesAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.Set<GcodeFile>().AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetAllModel3DPathsAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.Set<Model3D>()
            .AsNoTracking()
            .Select(m => m.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetAllGcodePathsAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.Set<GcodeFile>()
            .AsNoTracking()
            .Select(g => g.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToListAsync(ct);
    }

    public async Task SaveAuditResultAsync(FileHealthAudit auditResult, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        _ = db.Set<FileHealthAudit>().Add(auditResult);
        _ = await db.SaveChangesAsync(ct);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.FileConsistency;

public class EfFileConsistencyRepository(AppDbContext db) : IFileConsistencyRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<int> CountModel3DFilesAsync(CancellationToken ct)
        => await _db.Models3D.CountAsync(ct);

    public async Task<int> CountHealthyModel3DFilesAsync(CancellationToken ct)
        => await _db.Models3D.CountAsync(m => m.HealthStatus == FileHealthStatus.Healthy, ct);

    public async Task<int> CountMissingModel3DFilesAsync(CancellationToken ct)
        => await _db.Models3D.CountAsync(m => m.HealthStatus == FileHealthStatus.Missing, ct);

    public async Task<int> CountCorruptedModel3DFilesAsync(CancellationToken ct)
        => await _db.Models3D.CountAsync(m => m.HealthStatus == FileHealthStatus.Corrupted, ct);

    public async Task<int> CountGcodeFilesAsync(CancellationToken ct)
        => await _db.GcodeFiles.CountAsync(ct);

    public async Task<int> CountHealthyGcodeFilesAsync(CancellationToken ct)
        => await _db.GcodeFiles.CountAsync(g => g.HealthStatus == FileHealthStatus.Healthy, ct);

    public async Task<int> CountMissingGcodeFilesAsync(CancellationToken ct)
        => await _db.GcodeFiles.CountAsync(g => g.HealthStatus == FileHealthStatus.Missing, ct);

    public async Task<int> CountCorruptedGcodeFilesAsync(CancellationToken ct)
        => await _db.GcodeFiles.CountAsync(g => g.HealthStatus == FileHealthStatus.Corrupted, ct);

    public async Task<IReadOnlyList<Model3D>> GetModel3DFilesWithIssueAsync(FileHealthStatus status, CancellationToken ct)
        => await _db.Models3D
            .Where(m => m.HealthStatus == status)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<GcodeFile>> GetGcodeFilesWithIssueAsync(FileHealthStatus status, CancellationToken ct)
        => await _db.GcodeFiles
            .Where(g => g.HealthStatus == status)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<FileHealthAudit>> GetRecentAuditsAsync(int pageSize, CancellationToken ct)
        => await _db.FileHealthAudits
            .OrderByDescending(a => a.AuditDate)
            .Take(pageSize)
            .ToListAsync(ct);

    public async Task<FileHealthAudit?> GetMostRecentHealthyAuditAsync(CancellationToken ct)
        => await _db.FileHealthAudits
            .Where(a => a.HasIssues == false)
            .OrderByDescending(a => a.AuditDate)
            .FirstOrDefaultAsync(ct);

    public async Task<Model3D?> GetModel3DWithHealthDetailsAsync(Guid modelId, CancellationToken ct)
        => await _db.Models3D
            .Where(m => m.Id == modelId)
            .FirstOrDefaultAsync(ct);

    public async Task<GcodeFile?> GetGcodeFileWithHealthDetailsAsync(Guid gcodeId, CancellationToken ct)
        => await _db.GcodeFiles
            .Where(g => g.Id == gcodeId)
            .FirstOrDefaultAsync(ct);
}

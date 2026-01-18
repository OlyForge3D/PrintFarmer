using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.FileConsistency;

public interface IFileConsistencyRepository
{
    // Health Statistics
    Task<int> CountModel3DFilesAsync(CancellationToken ct);

    Task<int> CountHealthyModel3DFilesAsync(CancellationToken ct);

    Task<int> CountMissingModel3DFilesAsync(CancellationToken ct);

    Task<int> CountCorruptedModel3DFilesAsync(CancellationToken ct);

    Task<int> CountGcodeFilesAsync(CancellationToken ct);

    Task<int> CountHealthyGcodeFilesAsync(CancellationToken ct);

    Task<int> CountMissingGcodeFilesAsync(CancellationToken ct);

    Task<int> CountCorruptedGcodeFilesAsync(CancellationToken ct);

    // File Issues
    Task<IReadOnlyList<Model3D>> GetModel3DFilesWithIssueAsync(FileHealthStatus status, CancellationToken ct);

    Task<IReadOnlyList<GcodeFile>> GetGcodeFilesWithIssueAsync(FileHealthStatus status, CancellationToken ct);

    // Audit History
    Task<IReadOnlyList<FileHealthAudit>> GetRecentAuditsAsync(int pageSize, CancellationToken ct);

    Task<FileHealthAudit?> GetMostRecentHealthyAuditAsync(CancellationToken ct);

    // Individual File Details
    Task<Model3D?> GetModel3DWithHealthDetailsAsync(Guid modelId, CancellationToken ct);

    Task<GcodeFile?> GetGcodeFileWithHealthDetailsAsync(Guid gcodeId, CancellationToken ct);
}

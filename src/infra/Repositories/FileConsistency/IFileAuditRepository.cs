using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.FileConsistency;

/// <summary>
/// Repository for file audit operations (reading files and writing audit results).
/// </summary>
public interface IFileAuditRepository
{
    /// <summary>
    /// Gets all Model3D files for audit checking.
    /// </summary>
    Task<IReadOnlyList<Model3D>> GetAllModel3DFilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all GcodeFile files for audit checking.
    /// </summary>
    Task<IReadOnlyList<GcodeFile>> GetAllGcodeFilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all Model3D file paths (for orphaned file detection).
    /// </summary>
    Task<IReadOnlyList<string>> GetAllModel3DPathsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all GcodeFile file paths (for orphaned file detection).
    /// </summary>
    Task<IReadOnlyList<string>> GetAllGcodePathsAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves file health audit results to the database.
    /// </summary>
    Task SaveAuditResultAsync(FileHealthAudit auditResult, CancellationToken ct = default);
}

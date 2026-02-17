using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;

namespace Farm.Infrastructure.Repositories.FileConsistency;

/// <summary>
/// Repository for file consistency statistics and health issue queries.
/// Provides access to file health status counts and files with specific issues.
/// </summary>
public interface IFileConsistencyRepository
{
    // Health Statistics

    /// <summary>Counts total Model3D files.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountModel3DFilesAsync(CancellationToken ct);

    /// <summary>Counts Model3D files with healthy status.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountHealthyModel3DFilesAsync(CancellationToken ct);

    /// <summary>Counts Model3D files with missing file status.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountMissingModel3DFilesAsync(CancellationToken ct);

    /// <summary>Counts Model3D files with corrupted status.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountCorruptedModel3DFilesAsync(CancellationToken ct);

    /// <summary>Counts total G-code files.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountGcodeFilesAsync(CancellationToken ct);

    /// <summary>Counts G-code files with healthy status.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountHealthyGcodeFilesAsync(CancellationToken ct);

    /// <summary>Counts G-code files with missing file status.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountMissingGcodeFilesAsync(CancellationToken ct);

    /// <summary>Counts G-code files with corrupted status.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountCorruptedGcodeFilesAsync(CancellationToken ct);

    // File Issues

    /// <summary>Gets Model3D files with the specified health status.</summary>
    /// <param name="status">The health status to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Model3D>> GetModel3DFilesWithIssueAsync(FileHealthStatus status, CancellationToken ct);

    /// <summary>Gets G-code files with the specified health status.</summary>
    /// <param name="status">The health status to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<GcodeFile>> GetGcodeFilesWithIssueAsync(FileHealthStatus status, CancellationToken ct);

    // Audit History

    /// <summary>Gets recent file health audit records.</summary>
    /// <param name="pageSize">Maximum number of audits to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FileHealthAudit>> GetRecentAuditsAsync(int pageSize, CancellationToken ct);

    /// <summary>Gets the most recent audit with all files healthy.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<FileHealthAudit?> GetMostRecentHealthyAuditAsync(CancellationToken ct);

    // Individual File Details

    /// <summary>Gets a Model3D with health status details.</summary>
    /// <param name="modelId">The model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Model3D?> GetModel3DWithHealthDetailsAsync(Guid modelId, CancellationToken ct);

    /// <summary>Gets a G-code file with health status details.</summary>
    /// <param name="gcodeId">The G-code file identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GcodeFile?> GetGcodeFileWithHealthDetailsAsync(Guid gcodeId, CancellationToken ct);
}

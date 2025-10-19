using System;

namespace Farm.Web.Api.DTOs.Artifacts;

/// <summary>
/// Canonical API representation of a stored artifact (metadata only; not file bytes).
/// </summary>
/// <param name="Id">Unique identifier for this artifact.</param>
/// <param name="JobId">The slice job that produced this artifact.</param>
/// <param name="WorkerId">The worker that generated this artifact (optional).</param>
/// <param name="Kind">Artifact classification (e.g., 'gcode', 'thumbnail', 'log').</param>
/// <param name="FileName">Original filename as uploaded.</param>
/// <param name="RelativePath">Storage path relative to artifact root directory.</param>
/// <param name="ContentType">MIME type of the artifact (e.g., 'application/x-gcode', 'image/png').</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Sha256">SHA-256 hash of the file content for integrity verification.</param>
/// <param name="CreatedAt">Timestamp when the artifact was uploaded and stored.</param>
/// <param name="DownloadUrl">API endpoint URL to download the artifact bytes (e.g., '/api/artifacts/{id}/download').</param>
/// <param name="PublicUrl">Optional static URL for direct access if artifact is served via static file middleware (future enhancement).</param>
public sealed record ArtifactDto(
    Guid Id,
    Guid JobId,
    Guid? WorkerId,
    string Kind,
    string FileName,
    string RelativePath,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTime CreatedAt,
    string DownloadUrl,
    string? PublicUrl
);

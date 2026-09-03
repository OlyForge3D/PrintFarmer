namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Artifact metadata returned by GET /api/artifacts/job/{jobId}.
/// </summary>
/// <param name="Id">Artifact primary identifier.</param>
/// <param name="JobId">Associated slice job identifier.</param>
/// <param name="FileName">Original filename of the artifact.</param>
/// <param name="ContentType">MIME content type.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="DownloadUrl">URL to download the artifact file.</param>
/// <param name="CreatedAt">UTC timestamp when the artifact was persisted.</param>
/// <param name="IsPrimary">
/// Whether this is the job's authoritative primary G-code artifact. When no valid primary G-code
/// can be identified, every item is false and callers must explicitly select an artifact.
/// </param>
public sealed record ArtifactListItemDto(
    Guid Id,
    Guid JobId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string DownloadUrl,
    DateTime CreatedAt,
    bool IsPrimary);

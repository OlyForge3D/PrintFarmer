namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Artifact metadata returned by GET /api/artifacts/{id}/metadata.
/// </summary>
/// <param name="Id">Artifact primary identifier.</param>
/// <param name="FileName">Original filename of the artifact.</param>
/// <param name="ContentType">MIME content type.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="DownloadUrl">URL to download the artifact file.</param>
/// <param name="CreatedAt">UTC timestamp when the artifact was persisted.</param>
/// <param name="SliceJobId">Associated slice job identifier.</param>
public record ArtifactMetadataDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string DownloadUrl,
    DateTime CreatedAt,
    Guid SliceJobId);

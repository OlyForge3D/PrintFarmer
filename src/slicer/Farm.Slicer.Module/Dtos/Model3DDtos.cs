using Farm.Infrastructure.Dtos;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Data transfer object for a 3D model file.
/// </summary>
#pragma warning disable SA1402
public class Model3DDto
{
    /// <summary>Gets or sets the unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the GUID-based filename for internal storage.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the original filename uploaded by user (for display).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Gets or sets the file type (stl, 3mf, obj, ply).</summary>
    public string FileType { get; set; } = string.Empty;

    /// <summary>Gets or sets the upload timestamp.</summary>
    public DateTime UploadedAt { get; set; }

    /// <summary>Gets the download URL.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Gets or sets the thumbnail URL.</summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the X dimension in millimeters.</summary>
    public double? DimensionX { get; set; }

    /// <summary>Gets or sets the Y dimension in millimeters.</summary>
    public double? DimensionY { get; set; }

    /// <summary>Gets or sets the Z dimension in millimeters.</summary>
    public double? DimensionZ { get; set; }

    /// <summary>Gets or sets the triangle count.</summary>
    public int? TriangleCount { get; set; }

    /// <summary>Gets or sets whether the model passed validation.</summary>
    public bool IsValid { get; set; } = true;

    /// <summary>Gets or sets validation error details.</summary>
    public string? ValidationErrors { get; set; }

    /// <summary>Gets or sets the associated tags.</summary>
    public TagDto[]? Tags { get; set; }
}

/// <summary>
/// Entry in a hierarchical model file listing (file or directory).
/// </summary>
public record Model3DEntryDto(
    string Path,
    string FileName,
    long FileSize,
    DateTime UploadedAt,
    bool IsDirectory,
    string? ThumbnailUrl = null,
    string? Id = null,
    string? DirectoryId = null,
    string? Name = null,
    string? FileType = null);

/// <summary>
/// Response envelope for hierarchical model file listing.
/// </summary>
public record Model3DListResponse(
    IReadOnlyList<Model3DEntryDto> Models,
    int TotalCount,
    long TotalSize,
    int Page,
    int PageSize,
    int TotalPages,
    int TotalItems);

/// <summary>
/// Result of uploading a 3D model file.
/// </summary>
public class Model3DUploadResultDto
{
    /// <summary>Gets or sets the model identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the storage filename.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Gets or sets the file type.</summary>
    public string FileType { get; set; } = string.Empty;

    /// <summary>Gets or sets the upload timestamp.</summary>
    public DateTime UploadedAt { get; set; }

    /// <summary>Gets or sets the download URL.</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Validation result for a 3D model file.
/// </summary>
public class Model3DValidationResultDto
{
    /// <summary>Gets or sets whether the model is valid.</summary>
    public bool Valid { get; set; }

    /// <summary>Gets or sets validation issue descriptions.</summary>
    public string[]? Issues { get; set; }
}

/// <summary>
/// Result of uploading raw geometry (e.g., from the Cut Model tool).
/// Lightweight alternative to <see cref="Model3DUploadResultDto"/> that skips
/// thumbnail generation, model analysis, and deduplication.
/// </summary>
public class GeometryUploadResultDto
{
    /// <summary>Gets or sets the model identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the storage filename.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Gets or sets the server-accessible download URL usable by the slicer worker.</summary>
    public string FileUrl { get; set; } = string.Empty;
}
#pragma warning restore SA1402

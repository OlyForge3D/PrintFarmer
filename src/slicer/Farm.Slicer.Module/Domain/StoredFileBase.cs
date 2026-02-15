using System.ComponentModel.DataAnnotations;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Slicer-local base class for stored files (mirrors core StoredFile properties).
/// The original StoredFile in Farm.Infrastructure.Domain remains the base for GcodeFile;
/// this copy is used exclusively by Model3D within the slicer module.
/// Cross-domain navigation properties (FolderNode, Tags) are replaced by Guid-only soft refs.
/// </summary>
public abstract class StoredFileBase
{
    public Guid Id { get; set; }

    /// <summary>Concurrency token for optimistic locking during metadata updates.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>Original filename for display.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>GUID-based filename on disk.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Soft reference to FolderNode (no FK constraint — folder lives in core).</summary>
    public Guid FolderId { get; set; }

    /// <summary>Directory path where file is stored.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Thumbnail filename (stored in same directory as file).</summary>
    public string? ThumbnailFileName { get; set; }

    public long FileSizeBytes { get; set; }

    /// <summary>SHA256 for deduplication.</summary>
    public string FileHash { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // File health status (populated by FileConsistencyAuditService)
    public DateTime? LastHealthCheckDate { get; set; }

    public FileHealthStatus HealthStatus { get; set; } = FileHealthStatus.Unknown;

    public string? LastVerificationResult { get; set; } // JSON object with verification details
}

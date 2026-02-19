using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

// G-code Library System

/// <summary>
/// Abstract base class for all stored files (GCode and 3D Models).
/// Consolidates common file storage and management properties.
/// </summary>
public abstract class StoredFile
{
    public Guid Id { get; set; }

    /// <summary>Concurrency token for optimistic locking during metadata updates from multiple sources.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public string Name { get; set; } = string.Empty; // Original filename for display

    public string FileName { get; set; } = string.Empty; // GUID-based filename on disk

    public Guid FolderId { get; set; } // Foreign key to FolderNode entity - REQUIRED

    public FolderNode? Folder { get; set; } // Navigation property to FolderNode

    public string FilePath { get; set; } = string.Empty; // Directory path where file is stored

    public string? ThumbnailFileName { get; set; } // Just the thumbnail filename (stored in same directory as file)

    public long FileSizeBytes { get; set; }

    public string FileHash { get; set; } = string.Empty; // SHA256 for deduplication

    public DateTime UploadedAt { get; set; }

    public string? Description { get; set; }

    public virtual ICollection<Tag> Tags { get; set; } = new List<Tag>(); // Skip-navigation collection for modern EF Core

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // File health status (populated by FileConsistencyAuditService)
    public DateTime? LastHealthCheckDate { get; set; }

    public FileHealthStatus HealthStatus { get; set; } = FileHealthStatus.Unknown;

    public string? LastVerificationResult { get; set; } // JSON object with verification details
}

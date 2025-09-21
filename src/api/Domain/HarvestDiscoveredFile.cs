using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Farm.Web.Api.Domain;

public class HarvestDiscoveredFile
{
    [Key]
    public Guid Id { get; set; }
    public Guid HarvestOperationId { get; set; }
    public string FilePath { get; set; } = string.Empty; // Path on printer
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? ThumbnailUrl { get; set; }
    public HarvestFileStatus Status { get; set; } = HarvestFileStatus.Pending;
    public string? Error { get; set; }
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Additional fields for compatibility and metadata
    public bool AlreadyInLibrary { get; set; } = false;
    public string? FileHash { get; set; }
    public double? ExtractedNozzleDiameter { get; set; }
    public string? ExtractedMaterial { get; set; }
    public double? ExtractedPrintTime { get; set; }
    public double? ExtractedFilamentLength { get; set; }
    public string? ExtractedSlicerName { get; set; }
    public string? ExtractedSlicerVersion { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

public enum HarvestFileStatus
{
    Pending = 0,
    InProgress = 1,
    Complete = 2,
    Failed = 3,
    Cancelled = 4,
    Skipped = 5
}

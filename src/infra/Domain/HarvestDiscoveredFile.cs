using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class HarvestDiscoveredFile
{
    [Key]
    public Guid Id { get; set; }

    public Guid HarvestOperationId { get; set; }

    public GcodeHarvestOperation? HarvestOperation { get; set; } // Navigation property to parent operation

    public string FilePath { get; set; } = string.Empty; // Path on printer

    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }

    public string? ThumbnailUrl { get; set; }

    public HarvestFileStatus Status { get; set; } = HarvestFileStatus.Pending;

    public string? Error { get; set; }

    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public bool AlreadyInLibrary { get; set; } = false;

    public string? FileHash { get; set; }

    public double? ExtractedNozzleDiameter { get; set; }

    public string? ExtractedMaterial { get; set; }

    public double? ExtractedPrintTime { get; set; }

    public double? ExtractedFilamentLength { get; set; }

    public string? ExtractedSlicerName { get; set; }

    public string? ExtractedSlicerVersion { get; set; }

    public DateTime? ModifiedAt { get; set; }

    // Navigation property to harvest file to gcode file mappings
    // Protected by Restrict delete behavior - prevents accidental deletion when cleaning up harvest operations
    public ICollection<HarvestFileGcodeFileMapping> GcodeFileMappings { get; set; } = new List<HarvestFileGcodeFileMapping>();
}

using System;
using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a slicing job request that will be processed by a slicer worker
/// </summary>
public class SliceJob
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User who requested this slicing job
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Target printer for this sliced output (optional)
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// URL or path to the 3D model file to slice (STL, OBJ, 3MF, etc.)
    /// </summary>
    [Required]
    [MaxLength(2048)]
    public string ModelFileUrl { get; set; } = string.Empty;

    /// <summary>
    /// Original filename of the model
    /// </summary>
    [MaxLength(512)]
    public string ModelFileName { get; set; } = string.Empty;

    /// <summary>
    /// Slicer engine to use (OrcaSlicer, PrusaSlicer, etc.)
    /// Maps to SlicerEngineType enum
    /// </summary>
    public int SlicerEngine { get; set; }

    /// <summary>
    /// Serialized slicer profile/settings (JSON)
    /// </summary>
    public string? SlicerProfileJson { get; set; }

    /// <summary>
    /// Required capabilities for this job (JSON array)
    /// Example: ["orcaslicer", "multi-material", "variable-layer-height"]
    /// Workers must match these capabilities to claim the job
    /// </summary>
    public string? RequiredCapabilitiesJson { get; set; }

    /// <summary>
    /// Job status: Queued, Processing, Completed, Failed, Cancelled
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Queued";

    /// <summary>
    /// Job priority: Low=0, Normal=1, High=2, Critical=3
    /// Maps to SlicingJobPriority enum
    /// </summary>
    public int Priority { get; set; } = 1; // Normal

    /// <summary>
    /// When the job was submitted to the queue
    /// </summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When a worker started processing this job
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the job finished (successfully or with error)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// URL to the resulting G-code file (populated on success)
    /// </summary>
    [MaxLength(2048)]
    public string? ResultFileUrl { get; set; }

    /// <summary>
    /// Error message if job failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Current progress percentage (0-100)
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Current progress message (e.g., "Slicing in progress...", "Uploading G-code")
    /// </summary>
    [MaxLength(512)]
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// Estimated print time in seconds (populated from G-code metadata)
    /// </summary>
    public int? EstimatedPrintTimeSeconds { get; set; }

    /// <summary>
    /// Estimated filament usage in grams (populated from G-code metadata)
    /// </summary>
    public decimal? FilamentUsedGrams { get; set; }

    /// <summary>
    /// ID of the worker that processed/is processing this job
    /// </summary>
    public Guid? WorkerId { get; set; }

    /// <summary>
    /// Timestamp when this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when this record was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Job status constants
/// </summary>
public static class SliceJobStatus
{
    public const string Queued = "Queued";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

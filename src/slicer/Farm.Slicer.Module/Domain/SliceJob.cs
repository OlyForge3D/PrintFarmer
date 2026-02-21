using System.ComponentModel.DataAnnotations;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Represents a slicing job request that will be processed by a slicer worker.
/// </summary>
public class SliceJob
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User who requested this slicing job (soft ref — no FK constraint).
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Target printer for this sliced output (soft ref — no FK constraint).
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// URL or path to the 3D model file to slice (STL, OBJ, 3MF, etc.)
    /// </summary>
    [Required]
    [MaxLength(2048)]
    public string ModelFileUrl { get; set; } = string.Empty;

    /// <summary>
    /// Original filename of the model.
    /// </summary>
    [MaxLength(512)]
    public string ModelFileName { get; set; } = string.Empty;

    /// <summary>
    /// Slicer engine to use (OrcaSlicer, PrusaSlicer, etc.).
    /// Maps to SlicerEngineType enum.
    /// </summary>
    public int SlicerEngine { get; set; }

    /// <summary>
    /// Serialized slicer profile/settings (JSON).
    /// </summary>
    public string? SlicerProfileJson { get; set; }

    /// <summary>
    /// Optional reference to a stored ProcessProfile entity used for this job.
    /// When provided, SlicerProfileJson is populated from the profile's RawJson snapshot at submit time
    /// to ensure immutability if the profile later changes.
    /// </summary>
    public Guid? SlicerProfileId { get; set; }

    public ProcessProfile? SlicerProfile { get; set; }

    /// <summary>
    /// Required capabilities for this job (JSON array).
    /// Workers must match these capabilities to claim the job.
    /// </summary>
    public string? RequiredCapabilitiesJson { get; set; }

    /// <summary>
    /// Job status: Queued, Processing, Completed, Failed, Cancelled.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Queued";

    /// <summary>
    /// Job priority: Low=0, Normal=1, High=2, Critical=3.
    /// </summary>
    public int Priority { get; set; } = 1;

    /// <summary>
    /// When the job was submitted to the queue.
    /// </summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Correlation ID for idempotent job submission / message envelope tracking.
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Optional checksum/hash for idempotency and deduplication.
    /// </summary>
    [MaxLength(128)]
    public string? Checksum { get; set; }

    /// <summary>
    /// When a worker started processing this job.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the job finished (successfully or with error).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// URL to the resulting G-code file (populated on success).
    /// </summary>
    [MaxLength(2048)]
    public string? ResultFileUrl { get; set; }

    /// <summary>
    /// Error message if job failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Current progress percentage (0-100).
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Current progress message.
    /// </summary>
    [MaxLength(512)]
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// Estimated print time in seconds (populated from G-code metadata).
    /// </summary>
    public int? EstimatedPrintTimeSeconds { get; set; }

    /// <summary>
    /// Estimated filament usage in grams (populated from G-code metadata).
    /// </summary>
    public decimal? FilamentUsedGrams { get; set; }

    /// <summary>
    /// ID of the worker that processed/is processing this job.
    /// </summary>
    public Guid? WorkerId { get; set; }

    /// <summary>
    /// When the job was claimed by a worker (pull model).
    /// </summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>
    /// When the job lease expires (pull model with timeout).
    /// </summary>
    public DateTime? LeaseExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Comma-separated list of artifact IDs associated with this job at completion.
    /// </summary>
    public string? ArtifactIdsCsv { get; set; }

    /// <summary>
    /// Total number of artifacts produced for this job.
    /// </summary>
    public int? ArtifactsCount { get; set; }

    /// <summary>
    /// Aggregate size in bytes of all artifacts for this job.
    /// </summary>
    public long? ArtifactsTotalBytes { get; set; }

    /// <summary>
    /// Number of times this job has been retried after timing out or failing.
    /// </summary>
    public int RetryCount { get; set; }
}

/// <summary>
/// Job status constants.
/// </summary>
public static class SliceJobStatus
{
    public const string Queued = "Queued";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

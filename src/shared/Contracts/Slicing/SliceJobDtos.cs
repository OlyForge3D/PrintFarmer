using System;
using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Shared.Contracts.Slicing;

/// <summary>
/// Request to submit a new slicing job
/// </summary>
public class SubmitSliceJobRequest
{
    /// <summary>
    /// ID of the user submitting the job
    /// </summary>
    [Required]
    public Guid UserId { get; set; }

    /// <summary>
    /// Optional target printer ID
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// URL or path to the 3D model file
    /// </summary>
    [Required]
    public string ModelFileUrl { get; set; } = string.Empty;

    /// <summary>
    /// Original filename of the model
    /// </summary>
    [Required]
    public string ModelFileName { get; set; } = string.Empty;

    /// <summary>
    /// Slicer engine to use (0=OrcaSlicer, 1=PrusaSlicer, etc.)
    /// </summary>
    [Required]
    public int SlicerEngine { get; set; }

    /// <summary>
    /// Serialized slicer profile/settings (JSON)
    /// </summary>
    public string? SlicerProfileJson { get; set; }

    /// <summary>
    /// Optional reference to a stored slicer profile. If provided the API will
    /// resolve the profile, copy its RawJson snapshot into SlicerProfileJson for
    /// immutable processing, and record the SlicerProfileId on the SliceJob.
    /// Either SlicerProfileId or SlicerProfileJson may be supplied. If both are
    /// set the referenced profile takes precedence.
    /// </summary>
    public Guid? SlicerProfileId { get; set; }

    /// <summary>
    /// Required capabilities (JSON array of strings)
    /// </summary>
    public string? RequiredCapabilitiesJson { get; set; }

    /// <summary>
    /// Job priority (0=Low, 1=Normal, 2=High, 3=Critical)
    /// </summary>
    public int Priority { get; set; } = 1;
}

/// <summary>
/// Response after submitting a slicing job
/// </summary>
public class SubmitSliceJobResponse
{
    /// <summary>
    /// ID of the created job
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Current status of the job
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// When the job was queued
    /// </summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>
    /// Estimated position in queue (if applicable)
    /// </summary>
    public int? QueuePosition { get; set; }
}

/// <summary>
/// Response for getting job status
/// </summary>
public class SliceJobStatusResponse
{
    /// <summary>
    /// Job ID
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Current status
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Progress percentage (0-100)
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Current progress message
    /// </summary>
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// When the job was queued
    /// </summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>
    /// When processing started (if started)
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the job completed (if completed)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// URL to result file (if completed)
    /// </summary>
    public string? ResultFileUrl { get; set; }

    /// <summary>
    /// Error message (if failed)
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Estimated print time in seconds (if completed)
    /// </summary>
    public int? EstimatedPrintTimeSeconds { get; set; }

    /// <summary>
    /// Estimated filament usage in grams (if completed)
    /// </summary>
    public decimal? FilamentUsedGrams { get; set; }

    /// <summary>
    /// Worker ID that processed/is processing this job
    /// </summary>
    public Guid? WorkerId { get; set; }

    // Extended fields needed by workers to actually perform slicing (added for BLOCKER 1 bridging)
    public string ModelFileUrl { get; set; } = string.Empty;
    public string ModelFileName { get; set; } = string.Empty;
    public int SlicerEngine { get; set; } // matches SliceJob.SlicerEngine (enum int)
    public string? SlicerProfileJson { get; set; }
}

/// <summary>
/// Request to mark a slice job as completed and associate produced artifacts.
/// </summary>
public class CompleteSliceJobRequest
{
    /// <summary>
    /// Primary G-code artifact identifier (required). This artifact's URL becomes the job's ResultFileUrl.
    /// </summary>
    [Required]
    public Guid PrimaryArtifactId { get; set; }

    /// <summary>
    /// Optional additional artifact identifiers (thumbnails, previews, logs) related to this job.
    /// </summary>
    public Guid[]? AdditionalArtifactIds { get; set; }

    /// <summary>
    /// Estimated print time in seconds produced by the slicer (optional).
    /// </summary>
    public int? EstimatedPrintTimeSeconds { get; set; }

    /// <summary>
    /// Estimated filament usage in grams (optional).
    /// </summary>
    public decimal? FilamentUsedGrams { get; set; }

    /// <summary>
    /// Optional raw slicer log text. If provided and no separate log artifact was uploaded, the API will persist it
    /// as a 'log' artifact named 'slicer-log.txt' and include its ID in the completion response.
    /// </summary>
    public string? LogText { get; set; }
}

/// <summary>
/// Request to update in-flight job progress (worker emitted).
/// </summary>
public class SliceJobProgressUpdateRequest
{
    /// <summary>
    /// Progress percentage (0-100 inclusive).
    /// </summary>
    [Range(0, 100)]
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Optional human-readable progress message.
    /// </summary>
    [MaxLength(256)]
    public string? ProgressMessage { get; set; }
}

/// <summary>
/// Response after successful job completion.
/// </summary>
public class CompleteSliceJobResponse
{
    /// <summary>
    /// Job identifier.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Final status ("Completed").
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// When the job was marked completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Canonical URL to the primary G-code artifact.
    /// </summary>
    public string? ResultFileUrl { get; set; }

    /// <summary>
    /// All artifact identifiers associated with this completion (primary + additional).
    /// </summary>
    public Guid[] ArtifactIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Optional slicer-generated metrics.
    /// </summary>
    public int? EstimatedPrintTimeSeconds { get; set; }
    public decimal? FilamentUsedGrams { get; set; }

    /// <summary>
    /// If a log text payload was provided and converted into an artifact, this contains that artifact's ID; otherwise null.
    /// </summary>
    public Guid? LogArtifactId { get; set; }

    /// <summary>
    /// Total artifact count snapshot at completion.
    /// </summary>
    public int? ArtifactsCount { get; set; }

    /// <summary>
    /// Aggregate bytes of all artifacts at completion.
    /// </summary>
    public long? ArtifactsTotalBytes { get; set; }
}

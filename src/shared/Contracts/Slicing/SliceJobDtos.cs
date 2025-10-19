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
}

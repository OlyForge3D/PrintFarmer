using System.ComponentModel.DataAnnotations;

namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// Request to submit a new slicing job.
/// </summary>
public class SubmitSliceJobRequest
{
    [Required]
    public Guid UserId { get; set; }

    public Guid? PrinterId { get; set; }

    [Required]
    public string ModelFileUrl { get; set; } = string.Empty;

    [Required]
    public string ModelFileName { get; set; } = string.Empty;

    [Required]
    public int SlicerEngine { get; set; }

    public string? SlicerProfileJson { get; set; }

    public Guid? SlicerProfileId { get; set; }

    public string? RequiredCapabilitiesJson { get; set; }

    public int Priority { get; set; } = 1;

    /// <summary>
    /// JSON-serialized model transform (rotation/scale) from the UI workspace.
    /// Format: {"rotation":[rx,ry,rz],"scale":[sx,sy,sz]} (radians, Y-up).
    /// </summary>
    public string? ModelTransformJson { get; set; }
}

/// <summary>
/// Response after submitting a slicing job.
/// </summary>
public class SubmitSliceJobResponse
{
    public Guid JobId { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime QueuedAt { get; set; }

    public int? QueuePosition { get; set; }
}

/// <summary>
/// Response for getting job status.
/// </summary>
public class SliceJobStatusResponse
{
    public Guid Id { get; set; }

    public string Status { get; set; } = string.Empty;

    public int ProgressPercent { get; set; }

    public string? ProgressMessage { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ResultFileUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public int? EstimatedPrintTimeSeconds { get; set; }

    public decimal? FilamentUsedGrams { get; set; }

    public Guid? WorkerId { get; set; }

    // Extended fields needed by workers to perform slicing
    public string ModelFileUrl { get; set; } = string.Empty;

    public string ModelFileName { get; set; } = string.Empty;

    public int SlicerEngine { get; set; }

    public string? SlicerProfileJson { get; set; }

    /// <summary>
    /// JSON-serialized model transform (rotation/scale) from the UI workspace.
    /// </summary>
    public string? ModelTransformJson { get; set; }
}

/// <summary>
/// Request to mark a slice job as completed and associate produced artifacts.
/// </summary>
public class CompleteSliceJobRequest
{
    [Required]
    public Guid PrimaryArtifactId { get; set; }

    public Guid[]? AdditionalArtifactIds { get; set; }

    public int? EstimatedPrintTimeSeconds { get; set; }

    public decimal? FilamentUsedGrams { get; set; }

    public string? LogText { get; set; }
}

/// <summary>
/// Request to update in-flight job progress.
/// </summary>
public class SliceJobProgressUpdateRequest
{
    [Range(0, 100)]
    public int ProgressPercent { get; set; }

    [MaxLength(256)]
    public string? ProgressMessage { get; set; }
}

/// <summary>
/// Response after successful job completion.
/// </summary>
public class CompleteSliceJobResponse
{
    public Guid JobId { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime? CompletedAt { get; set; }

    public string? ResultFileUrl { get; set; }

    public Guid[] ArtifactIds { get; set; } = Array.Empty<Guid>();

    public int? EstimatedPrintTimeSeconds { get; set; }

    public decimal? FilamentUsedGrams { get; set; }

    public Guid? LogArtifactId { get; set; }

    public int? ArtifactsCount { get; set; }

    public long? ArtifactsTotalBytes { get; set; }
}

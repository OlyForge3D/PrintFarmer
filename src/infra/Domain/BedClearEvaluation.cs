using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

public enum BedClearDetectorVerdict
{
    Unknown,
    Clear,
    Occupied
}

public enum BedClearGroundTruth
{
    Clear,
    Occupied
}

public enum BedClearEvaluationState
{
    Pending,
    Processing,
    Complete
}

/// <summary>
/// Shadow-mode detector evidence captured for one operator bed-clear acknowledgement.
/// A sample is not a trusted reference until a different operator independently labels it Clear.
/// </summary>
public sealed class BedClearEvaluation
{
    public Guid Id { get; set; }

    public Guid PrinterId { get; set; }

    public Guid? CameraId { get; set; }

    public Guid? PrintJobId { get; set; }

    public Guid? BedTypeId { get; set; }

    /// <summary>Stable identity of the acknowledgement that produced this sample.</summary>
    public Guid? SourceAcknowledgementId { get; set; }

    [MaxLength(256)]
    public string AcknowledgedBySubject { get; set; } = string.Empty;

    public DateTime AcknowledgedAtUtc { get; set; }

    [MaxLength(500)]
    public string? ImageFilePath { get; set; }

    public DateTime? CapturedAtUtc { get; set; }

    public DateTime? CameraConfigurationAtUtc { get; set; }

    public bool IsReferenceEligible { get; set; }

    public DateTime? ProcessingStartedAtUtc { get; set; }

    public double? ToolheadX { get; set; }

    public double? ToolheadY { get; set; }

    public double? ToolheadZ { get; set; }

    public BedClearEvaluationState State { get; set; } = BedClearEvaluationState.Pending;

    public BedClearDetectorVerdict Verdict { get; set; } = BedClearDetectorVerdict.Unknown;

    public double? Score { get; set; }

    [MaxLength(128)]
    public string? ReasonCode { get; set; }

    public BedClearGroundTruth? GroundTruth { get; set; }

    [MaxLength(256)]
    public string? GroundTruthBySubject { get; set; }

    public DateTime? GroundTruthAtUtc { get; set; }

    /// <summary>Only independently reviewed Clear samples are eligible for comparison.</summary>
    public bool IsActiveReference { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Classifies the purpose of a <see cref="PrintJob"/>.
/// Standard jobs are backward-compatible; FilamentCalibration jobs carry
/// additional immutable provenance and compatibility fields.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobKind
{
    /// <summary>Normal user-submitted print job.</summary>
    Standard = 0,

    /// <summary>
    /// Generated calibration G-code job linked to a calibration project/attempt.
    /// Requires explicit idempotency key and full compatibility tuple at creation.
    /// </summary>
    FilamentCalibration = 1,
}

/// <summary>
/// Typed reason a calibration job is blocked without consuming its acknowledgement.
/// The value is stored as a string in <see cref="PrintJob.BlockedReasonCode"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobBlockedReasonCode
{
    /// <summary>No blocked reason (job is dispatchable).</summary>
    None = 0,

    /// <summary>Explicit Klipper firmware family not matched.</summary>
    FirmwareFamilyMismatch = 1,

    /// <summary>Explicit Klipper G-code dialect not matched.</summary>
    GcodeDialectMismatch = 2,

    /// <summary>Upstream OrcaSlicer version/digest mismatch.</summary>
    SlicerTupleMismatch = 3,

    /// <summary>One or more specification/profile/G-code content hashes changed.</summary>
    ContentHashMismatch = 4,

    /// <summary>Printer configuration revision has advanced beyond the pinned value.</summary>
    PrinterConfigRevisionStale = 5,

    /// <summary>Hard material/nozzle/toolhead/model/build compatibility failed.</summary>
    HardCompatibilityFailure = 6,

    /// <summary>Calibration records invalid or missing for this job.</summary>
    CalibrationRecordInvalid = 7,

    /// <summary>Filament hard gate (material/SKU/spool sufficiency) failed.</summary>
    FilamentCheckFailed = 8,

    /// <summary>Required capability not advertised by the target printer.</summary>
    MissingRequiredCapability = 9,
}

/// <summary>
/// Outcome of a single dispatch attempt as recorded in <see cref="QueueDispatchAttempt"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DispatchAttemptOutcome
{
    /// <summary>Attempt is in progress.</summary>
    InProgress = 0,

    /// <summary>Backend accepted the job and confirmed it started.</summary>
    Accepted = 1,

    /// <summary>Backend explicitly rejected the job before start.</summary>
    Rejected = 2,

    /// <summary>Known failure before the backend I/O was attempted.</summary>
    FailedBeforeStart = 3,

    /// <summary>
    /// Network or protocol error during backend I/O; outcome unknown.
    /// The job remains in Starting and must be reconciled against backend state.
    /// </summary>
    Unknown = 4,
}

/// <summary>
/// Status of a durable <see cref="QueueDispatchOutbox"/> event.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueOutboxEventStatus
{
    /// <summary>Event is waiting to be picked up by the publisher.</summary>
    Pending = 0,

    /// <summary>Publisher has claimed and is processing this event.</summary>
    Processing = 1,

    /// <summary>Event was successfully published.</summary>
    Published = 2,

    /// <summary>All retry attempts exhausted; event is dead-lettered.</summary>
    DeadLettered = 3,
}

/// <summary>Status of a durable bed-clear command idempotency record.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BedClearCommandStatus
{
    Pending = 0,
    Claimed = 1,
    Accepted = 2,
    Rejected = 3,
    Unknown = 4,
    Expired = 5,
}

/// <summary>Durable phase of a dispatch attempt around backend network I/O.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DispatchBackendCallPhase
{
    Claimed = 0,
    InvokingBackend = 1,
    AwaitingReconciliation = 2,
    ResponseReceived = 3,
    Reconciled = 4,
    Terminal = 5,
}

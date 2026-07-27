using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Durable actor/resource/operation/outcome audit row for safety-sensitive queue and
/// dispatch operations (issue #900).
///
/// Rows are written inside the SAME database transaction as the operation they describe
/// (claim, bed-clear acknowledgement, cancel, abort, safety override, reconciliation) so
/// an audit entry can never be lost while its effect is persisted, nor persisted for an
/// effect that rolled back.
///
/// The payload is deliberately redacted: it carries public identifiers, typed codes and
/// revision tokens only — never credentials, private URLs or filesystem paths.
/// </summary>
public sealed class QueueOperationAudit
{
    /// <summary>Unique audit row identity.</summary>
    public Guid Id { get; set; }

    /// <summary>UTC timestamp when the audited operation committed.</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Actor (user subject or system identity) that performed the operation.</summary>
    [MaxLength(256)]
    public string ActorSubject { get; set; } = string.Empty;

    /// <summary>
    /// Kind of resource the operation targeted (e.g., <c>PrintJob</c>, <c>Printer</c>).
    /// </summary>
    [MaxLength(64)]
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>Identity of the targeted resource.</summary>
    public Guid? ResourceId { get; set; }

    /// <summary>Printer involved in the operation, when applicable.</summary>
    public Guid? PrinterId { get; set; }

    /// <summary>Print job involved in the operation, when applicable.</summary>
    public Guid? PrintJobId { get; set; }

    /// <summary>Dispatch attempt involved in the operation, when applicable.</summary>
    public Guid? DispatchAttemptId { get; set; }

    /// <summary>
    /// Typed operation name — one of <see cref="QueueAuditOperations"/>.
    /// </summary>
    [MaxLength(64)]
    public string Operation { get; set; } = string.Empty;

    /// <summary>Typed outcome — one of <see cref="QueueAuditOutcomes"/>.</summary>
    [MaxLength(32)]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Typed failure/reason code on non-success outcomes.</summary>
    [MaxLength(128)]
    public string? ReasonCode { get; set; }

    /// <summary>Job row version observed when the operation committed.</summary>
    public byte[]? JobRowVersion { get; set; }

    /// <summary>Dispatch-state row version observed when the operation committed.</summary>
    public byte[]? DispatchStateRowVersion { get; set; }

    /// <summary>SHA-256 fingerprint of the idempotency key, when applicable.</summary>
    [MaxLength(512)]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Redacted structured detail (public identifiers and typed codes only).
    /// </summary>
    [MaxLength(2048)]
    public string? DetailJson { get; set; }
}

/// <summary>Typed operation names written to <see cref="QueueOperationAudit.Operation"/>.</summary>
public static class QueueAuditOperations
{
    /// <summary>An atomic dispatch claim was attempted.</summary>
    public const string DispatchClaim = "dispatch.claim";

    /// <summary>An ad-hoc (non-queue) printer start was attempted.</summary>
    public const string AdHocStart = "dispatch.adhoc_start";

    /// <summary>A claim was released after a known pre-start failure.</summary>
    public const string DispatchRelease = "dispatch.release";

    /// <summary>The backend confirmed acceptance of a dispatch.</summary>
    public const string DispatchAccepted = "dispatch.accepted";

    /// <summary>A dispatch produced an unknown outcome requiring reconciliation.</summary>
    public const string DispatchUnknown = "dispatch.unknown";

    /// <summary>A reconciliation pass classified a dispatch attempt.</summary>
    public const string Reconciliation = "dispatch.reconcile";

    /// <summary>A bed-clear acknowledgement was issued.</summary>
    public const string BedClearAcknowledge = "queue.bed_clear_ack";

    /// <summary>A job was cancelled.</summary>
    public const string JobCancel = "queue.cancel";

    /// <summary>An in-flight print was aborted.</summary>
    public const string JobAbort = "queue.abort";

    /// <summary>An in-flight print was paused.</summary>
    public const string JobPause = "queue.pause";

    /// <summary>A paused print was resumed.</summary>
    public const string JobResume = "queue.resume";

    /// <summary>A safety gate was overridden by an operator.</summary>
    public const string SafetyOverride = "queue.safety_override";

    /// <summary>A queue job was deleted.</summary>
    public const string JobDelete = "queue.delete";

    /// <summary>A queue job's mutable fields were updated.</summary>
    public const string JobUpdate = "queue.update";
}

/// <summary>Typed outcomes written to <see cref="QueueOperationAudit.Outcome"/>.</summary>
public static class QueueAuditOutcomes
{
    /// <summary>Operation completed successfully.</summary>
    public const string Success = "success";

    /// <summary>Operation was rejected by a guard or precondition.</summary>
    public const string Denied = "denied";

    /// <summary>Operation failed with a known error.</summary>
    public const string Failed = "failed";

    /// <summary>Operation outcome could not be determined and requires reconciliation.</summary>
    public const string Unknown = "unknown";

    /// <summary>Operation was an idempotent replay of a previously recorded result.</summary>
    public const string Replayed = "replayed";
}

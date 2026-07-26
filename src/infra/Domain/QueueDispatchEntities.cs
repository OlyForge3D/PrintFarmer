using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Durable scheduling outbox event. Exactly one is written in the same
/// <c>AppDbContext</c> transaction as the winning idempotency write so that
/// neither the write nor the event can be lost across process crashes.
/// The in-memory channel is only a wake-up optimization; startup/periodic
/// reconciliation recovers any events that were written but not yet published.
/// </summary>
public sealed class QueueDispatchOutbox
{
    /// <summary>Durable event identity — also used as the idempotency key for the publisher.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Monotonically increasing sequence used for ordered, gap-free cursor reads.
    /// Assigned by the database (SEQUENCE in PostgreSQL/SQL Server, autoincrement in SQLite).
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>Aggregate root type this event belongs to (e.g., <c>PrintJob</c>).</summary>
    [MaxLength(128)]
    public string AggregateType { get; set; } = string.Empty;

    /// <summary>Aggregate instance (the print job).</summary>
    public Guid AggregateId { get; set; }

    /// <summary>Snapshot of the print-job row version at the time of the write (for fencing).</summary>
    public byte[]? AggregateRowVersion { get; set; }

    /// <summary>Printer the job was dispatched to.</summary>
    public Guid? PrinterId { get; set; }

    /// <summary>Printer configuration revision pinned in the job at creation time.</summary>
    public long? PrinterConfigRevision { get; set; }

    /// <summary>
    /// Fully-qualified event type name (e.g., <c>PrintFarmer.Queue.CalibrationJobQueued.v1</c>).
    /// </summary>
    [MaxLength(256)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>Schema version of the payload (e.g., <c>1</c>).</summary>
    [MaxLength(16)]
    public string SchemaVersion { get; set; } = "1";

    /// <summary>
    /// Serialized payload; contains only public identifiers and no credentials,
    /// private URLs, or filesystem paths. Must be sufficient to reconstruct the
    /// event without re-reading the source row.
    /// </summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Current processing status of this outbox event.</summary>
    public QueueOutboxEventStatus Status { get; set; } = QueueOutboxEventStatus.Pending;

    /// <summary>Number of publish attempts so far.</summary>
    public int AttemptCount { get; set; }

    /// <summary>UTC timestamp of the most recent publish attempt.</summary>
    public DateTime? LastAttemptedAtUtc { get; set; }

    /// <summary>UTC timestamp before which the next attempt must not start (exponential back-off).</summary>
    public DateTime? RetryAfterUtc { get; set; }

    /// <summary>Last error message recorded against this event.</summary>
    [MaxLength(2048)]
    public string? LastError { get; set; }

    /// <summary>UTC timestamp when this event was persisted.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>UTC timestamp when this event reached a terminal status (Published or DeadLettered).</summary>
    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>
/// Records each database-backed atomic dispatch claim and its outcome.
/// One row is written per start path invocation so that unknown outcomes
/// can be reconciled and known failures release the lease cleanly.
/// </summary>
public sealed class QueueDispatchAttempt
{
    /// <summary>Unique attempt identity.</summary>
    public Guid Id { get; set; }

    /// <summary>The print job this attempt belongs to.</summary>
    public Guid PrintJobId { get; set; }

    /// <summary>Navigation property back to the print job.</summary>
    public PrintJob? PrintJob { get; set; }

    /// <summary>The printer that was claimed for this attempt.</summary>
    public Guid PrinterId { get; set; }

    /// <summary>Printer configuration revision current at claim time.</summary>
    public long PrinterConfigRevision { get; set; }

    /// <summary>
    /// Monotonic sequence number scoped to the print job (1-based).
    /// Used to distinguish multiple dispatch attempts on the same job.
    /// </summary>
    public int AttemptNumber { get; set; }

    /// <summary>Actor (user subject or system identity) that triggered this attempt.</summary>
    [MaxLength(256)]
    public string ActorSubject { get; set; } = string.Empty;

    /// <summary>How the start path was invoked (e.g., <c>Manual</c>, <c>Auto</c>, <c>BedClear</c>).</summary>
    [MaxLength(64)]
    public string StartPathKind { get; set; } = string.Empty;

    /// <summary>Idempotency key of the bed-clear acknowledgement consumed by this attempt (if any).</summary>
    [MaxLength(256)]
    public string? AcknowledgementIdempotencyKey { get; set; }

    /// <summary>UTC timestamp when the claim transaction committed.</summary>
    public DateTime ClaimedAtUtc { get; set; }

    /// <summary>UTC timestamp when the backend accepted the job (null until confirmed).</summary>
    public DateTime? BackendAcceptedAtUtc { get; set; }

    /// <summary>Outcome of this attempt.</summary>
    public DispatchAttemptOutcome Outcome { get; set; } = DispatchAttemptOutcome.InProgress;

    /// <summary>Typed error code on non-Accepted outcomes.</summary>
    [MaxLength(128)]
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable error detail (no credentials or paths).</summary>
    [MaxLength(2048)]
    public string? ErrorDetail { get; set; }

    /// <summary>Whether this attempt can be safely retried.</summary>
    public bool IsRetryable { get; set; }

    /// <summary>Whether backend state must be reconciled before the job can proceed.</summary>
    public bool RequiresReconciliation { get; set; }

    /// <summary>
    /// External job identifier assigned by the printer backend, if known.
    /// Preserved for reconciliation even on unknown outcomes.
    /// </summary>
    [MaxLength(512)]
    public string? BackendJobId { get; set; }

    /// <summary>PrintJob.RowVersion at the time the claim was committed.</summary>
    public byte[]? JobRowVersionAtClaim { get; set; }

    /// <summary>PrinterDispatchState.RowVersion at the time the claim was committed.</summary>
    public byte[]? DispatchStateRowVersionAtClaim { get; set; }

    /// <summary>UTC timestamp when this attempt record was last updated.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Single-row sequence counter table for the durable outbox.
/// Atomically incremented (within the same <c>AppDbContext</c> transaction as each
/// outbox event insert) to provide a truly cross-process monotonic sequence.
///
/// The <see cref="RowVersion"/> optimistic concurrency token ensures that two concurrent
/// API instances racing on the same sequence slot both cannot commit: the loser receives
/// a <c>DbUpdateConcurrencyException</c> that rolls back its entire unit of work.
/// There is always exactly one row in this table (Id = 1).
/// </summary>
public sealed class OutboxSequenceState
{
    /// <summary>Always 1 — there is exactly one row.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The most recently allocated sequence number.
    /// Incremented by 1 in the same transaction as each outbox event insert.
    /// </summary>
    public long NextSequence { get; set; }

    /// <summary>
    /// Optimistic concurrency token (application-managed on SQLite/PostgreSQL;
    /// native ROWVERSION on SQL Server). Prevents two concurrent writers from
    /// both succeeding with the same sequence value.
    /// </summary>
    public byte[]? RowVersion { get; set; }
}

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
    /// Allocated by <see cref="Farm.Infrastructure.Services.Queue.IDbOutboxSequenceAllocator"/>
    /// within the same transaction as the outbox event write.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Optimistic concurrency token used for atomic lease acquisition by the durable command
    /// consumer. Prevents two concurrent consumer instances from double-executing the same event.
    /// Application-managed on SQLite/PostgreSQL; native ROWVERSION on SQL Server.
    /// </summary>
    public byte[]? RowVersion { get; set; }

    /// <summary>Aggregate root type this event belongs to (e.g., <c>PrintJob</c>).</summary>
    [MaxLength(128)]
    public string AggregateType { get; set; } = string.Empty;

    /// <summary>Aggregate instance (the print job).</summary>
    public Guid AggregateId { get; set; }

    /// <summary>Snapshot of the print-job row version at the time of the write (for fencing).</summary>
    public byte[]? AggregateRowVersion { get; set; }

    /// <summary>Resulting logical print-job revision for this event.</summary>
    public long? JobRevision { get; set; }

    /// <summary>Printer the job was dispatched to.</summary>
    public Guid? PrinterId { get; set; }

    /// <summary>Calibration or print project scope, when present.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Print-job status captured when the event was written.</summary>
    [MaxLength(32)]
    public string? JobStatus { get; set; }

    /// <summary>Print-job kind captured when the event was written.</summary>
    [MaxLength(32)]
    public string? JobKind { get; set; }

    /// <summary>Printer configuration revision pinned in the job at creation time.</summary>
    public long? PrinterConfigRevision { get; set; }

    /// <summary>
    /// Snapshot of <see cref="PrinterDispatchState.RowVersion"/> at write time so consumers can
    /// detect dispatch-state drift without re-reading the row.
    /// </summary>
    public byte[]? DispatchStateRowVersion { get; set; }

    /// <summary>Resulting logical dispatch-state revision for this event.</summary>
    public long? DispatchStateRevision { get; set; }

    /// <summary>Dispatch attempt this event belongs to, when the event was produced by a claim.</summary>
    public Guid? AttemptId { get; set; }

    /// <summary>Monotonic number of the dispatch attempt within its job.</summary>
    public int? AttemptNumber { get; set; }

    /// <summary>Typed dispatch-attempt outcome at event write time.</summary>
    [MaxLength(32)]
    public string? AttemptOutcome { get; set; }

    /// <summary>
    /// Bed-clear acknowledgement state at write time
    /// (<c>None</c>, <c>Acknowledged</c>, <c>Consumed</c>, <c>Invalidated</c>).
    /// </summary>
    [MaxLength(32)]
    public string? BedClearState { get; set; }

    /// <summary>Durable bed-clear command identity, when the event represents that lifecycle.</summary>
    public Guid? BedClearCommandId { get; set; }

    /// <summary>Expiry of the exact-job acknowledgement represented by the event.</summary>
    public DateTime? BedClearExpiresAtUtc { get; set; }

    /// <summary>Typed failure code carried by terminal/failure events (never free-form text).</summary>
    [MaxLength(128)]
    public string? FailureCode { get; set; }

    /// <summary>Whether the typed failure may be retried without an operator mutation.</summary>
    public bool? FailureRetryable { get; set; }

    /// <summary>Whether the failure requires authoritative backend reconciliation.</summary>
    public bool? FailureRequiresReconciliation { get; set; }

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

    /// <summary>Optimistic concurrency token fencing concurrent outcome/reconciliation writers.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>The print job this attempt belongs to. Null for ad-hoc (non-queue) starts.</summary>
    public Guid? PrintJobId { get; set; }

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

    /// <summary>
    /// Provider file/path identity returned by upload or observed in current/history state.
    /// This is deliberately distinct from <see cref="BackendJobId"/> and must never be sent
    /// to a provider endpoint that expects a history UID.
    /// </summary>
    [MaxLength(512)]
    public string? BackendFileIdentity { get; set; }

    /// <summary>
    /// Stable identity of the command sent to the backend for this attempt.
    /// Written BEFORE any network I/O so reconciliation can correlate the attempt with
    /// backend state even when the response was never observed.
    /// </summary>
    [MaxLength(128)]
    public string? BackendCommandId { get; set; }

    /// <summary>
    /// File name presented to the printer backend for this attempt.
    /// Written BEFORE the upload so an unmatched printing backend can be recognised.
    /// </summary>
    [MaxLength(512)]
    public string? BackendFileName { get; set; }

    /// <summary>Durable phase around the backend call and reconciliation.</summary>
    public DispatchBackendCallPhase BackendCallPhase { get; set; }

    /// <summary>Stable correlation transmitted in the backend-visible filename.</summary>
    [MaxLength(128)]
    public string? BackendCorrelationId { get; set; }

    public DateTime? BackendCallStartedAtUtc { get; set; }

    public DateTime? BackendResponseAtUtc { get; set; }

    public int ReconciliationCount { get; set; }

    public DateTime? LastReconciledAtUtc { get; set; }

    public DateTime? TerminalAtUtc { get; set; }

    /// <summary>PrintJob.RowVersion at the time the claim was committed.</summary>
    public byte[]? JobRowVersionAtClaim { get; set; }

    /// <summary>PrinterDispatchState.RowVersion at the time the claim was committed.</summary>
    public byte[]? DispatchStateRowVersionAtClaim { get; set; }

    /// <summary>UTC timestamp when this attempt record was last updated.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Durable one-use idempotency record for an exact job/printer/queue-generation
/// bed-clear command. Records remain after acknowledgement consumption.
/// </summary>
public sealed class BedClearCommandRecord
{
    public Guid Id { get; set; }

    public Guid PrinterId { get; set; }

    public Guid JobId { get; set; }

    [MaxLength(512)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(64)]
    public string RequestSha256 { get; set; } = string.Empty;

    [MaxLength(256)]
    public string ActorSubject { get; set; } = string.Empty;

    public byte[] JobRowVersion { get; set; } = [];

    public byte[] DispatchStateRowVersion { get; set; } = [];

    public long QueueRevision { get; set; }

    public long PrinterConfigRevision { get; set; }

    public BedClearCommandStatus Status { get; set; }

    public Guid OutboxEventId { get; set; }

    public Guid? DispatchAttemptId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// Provider-native monotonic queue-position counter scoped to one printer.
/// <see cref="Guid.Empty"/> is the global scope for unassigned jobs.
/// </summary>
public sealed class QueuePositionState
{
    public Guid ScopeId { get; set; }

    public int NextPosition { get; set; }
}

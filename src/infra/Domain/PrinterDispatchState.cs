using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Dispatch-related state for a printer, stored separately from the Printer entity
/// to avoid RowVersion contention between user edits and background dispatch operations.
/// AutoDispatchService writes these fields ~14× per cycle; isolating them in their own
/// table with their own RowVersion prevents DbUpdateConcurrencyException on the Printer row.
/// </summary>
public class PrinterDispatchState : IRevisionedEntity
{
    /// <summary>
    /// PK and FK — mirrors <see cref="Printer.Id"/> (1:1 relationship).
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Navigation back to the parent printer.
    /// </summary>
    public Printer Printer { get; set; } = null!;

    /// <summary>
    /// Current auto-dispatch ready-gate workflow state.
    /// </summary>
    public AutoDispatchState AutoDispatchState { get; set; } = AutoDispatchState.None;

    /// <summary>
    /// Indicates the operator has pre-confirmed the bed is clear.
    /// </summary>
    public bool BedPreConfirmed { get; set; }

    /// <summary>
    /// Opaque compatibility token derived from <see cref="Revision"/>.
    /// </summary>
    [NotMapped]
    public byte[]? RowVersion => Revision > 0 ? RevisionETag.EncodeBytes(Revision) : null;

    /// <summary>Provider-independent logical revision incremented on every mutation.</summary>
    public long Revision { get; set; } = 1;

    /// <summary>
    /// Monotonic per-printer queue generation. Every insertion, reorder, reassignment,
    /// cancellation, or safety-relevant configuration mutation increments this value.
    /// </summary>
    public long QueueRevision { get; set; }

    // =========================================================================
    // Issue #900: Exact-job bed-clear acknowledgement (one-use, expiring)
    // =========================================================================

    /// <summary>
    /// The exact print job this acknowledgement was issued for.
    /// A new acknowledgement is required if the job changes, a different job is
    /// inserted ahead, or the acknowledgement expires.
    /// </summary>
    public Guid? AcknowledgedJobId { get; set; }

    /// <summary>
    /// UTC timestamp when the acknowledgement was persisted.
    /// </summary>
    public DateTime? AcknowledgedAtUtc { get; set; }

    /// <summary>
    /// Subject of the operator who issued the acknowledgement.
    /// </summary>
    [MaxLength(256)]
    public string? AcknowledgedBySubject { get; set; }

    /// <summary>
    /// Stable caller-supplied idempotency key for the acknowledge request,
    /// kept for exact-replay detection (returns 200 instead of re-consuming).
    /// </summary>
    [MaxLength(512)]
    public string? AcknowledgementIdempotencyKey { get; set; }

    /// <summary>
    /// UTC timestamp after which this acknowledgement must not be consumed.
    /// Computed at write time as a configurable offset from <see cref="AcknowledgedAtUtc"/>.
    /// </summary>
    public DateTime? AcknowledgementExpiresAtUtc { get; set; }

    /// <summary>Job revision the operator observed when acknowledging bed clear.</summary>
    public byte[]? AcknowledgedJobRowVersion { get; set; }

    /// <summary>Queue generation the acknowledged job was the urgent-first head of.</summary>
    public long? AcknowledgedQueueRevision { get; set; }

    /// <summary>Printer configuration revision observed by the operator.</summary>
    public long? AcknowledgedPrinterConfigRevision { get; set; }

    // =========================================================================
    // Issue #900: Active job / dispatch-attempt tracking
    // =========================================================================

    /// <summary>
    /// The print job currently Starting/Printing on this printer.
    /// Written atomically by the dispatch claim transaction.
    /// </summary>
    public Guid? ActiveJobId { get; set; }

    /// <summary>
    /// The dispatch-attempt record for the currently active start.
    /// Null when no job is Starting/Printing.
    /// </summary>
    public Guid? ActiveDispatchAttemptId { get; set; }

    /// <summary>
    /// Durable physical-I/O barrier. While present, no new dispatch claim may be acquired
    /// for this printer. The barrier is acquired before a backend control call and released
    /// only after its outcome is known or authoritatively reconciled.
    /// </summary>
    public Guid? PhysicalControlCommandId { get; set; }

    /// <summary>The dispatch attempt whose physical state the control command may affect.</summary>
    public Guid? PhysicalControlAttemptId { get; set; }

    /// <summary>Typed physical operation protected by <see cref="PhysicalControlCommandId"/>.</summary>
    [MaxLength(64)]
    public string? PhysicalControlOperation { get; set; }

    /// <summary>Actor that acquired the physical-I/O barrier.</summary>
    [MaxLength(256)]
    public string? PhysicalControlActorSubject { get; set; }

    /// <summary>UTC timestamp when the physical-I/O barrier was acquired.</summary>
    public DateTime? PhysicalControlStartedAtUtc { get; set; }

    /// <summary>
    /// True when a response was lost and the barrier must remain until reconciliation or
    /// an explicit operator decision proves that releasing it is safe.
    /// </summary>
    public bool PhysicalControlRequiresReconciliation { get; set; }
}

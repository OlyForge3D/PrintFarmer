// <copyright file="QueueLifecycleEventWriter.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Public constants and helper for writing durable lifecycle outbox events.
///
/// Lifecycle events are written as <see cref="QueueOutboxEventStatus.Pending"/> rows so the
/// <see cref="QueueOutboxPublisherService"/> picks them up and broadcasts them via SignalR to
/// authorized groups. They are committed atomically in the SAME <c>SaveChangesAsync()</c>
/// call as the associated state/lease mutation.
///
/// The event type constants are public so that higher-layer services outside
/// <c>Farm.Infrastructure</c> (e.g., <c>Farm.Api</c>) can emit the correct event type without
/// taking a dependency on <see cref="Queue.Dispatch.DispatchClaimService"/>.
/// </summary>
public static class QueueLifecycleEventWriter
{
    // ── Terminal / dispatch outcome events ──────────────────────────────────────

    /// <summary>Dispatch attempt rejected due to a known pre-start failure.</summary>
    public const string EventTypeKnownFailure = Dispatch.DispatchClaimService.EventTypeKnownFailure;

    /// <summary>Backend confirmed it accepted the job (job transitions to Printing).</summary>
    public const string EventTypeBackendAccepted = Dispatch.DispatchClaimService.EventTypeBackendAccepted;

    /// <summary>Backend outcome unknown; reconciliation required.</summary>
    public const string EventTypeUnknownOutcome = Dispatch.DispatchClaimService.EventTypeUnknownOutcome;

    // ── Reconciliation events ───────────────────────────────────────────────────

    /// <summary>Reconciliation scan confirmed the backend is actively printing.</summary>
    public const string EventTypeReconciliationAccepted = Dispatch.DispatchClaimService.EventTypeReconciliationAccepted;

    /// <summary>Reconciliation scan found the job absent from the backend.</summary>
    public const string EventTypeReconciliationAbsent = Dispatch.DispatchClaimService.EventTypeReconciliationAbsent;

    /// <summary>Reconciliation scan could not determine backend state.</summary>
    public const string EventTypeReconciliationIndeterminate = Dispatch.DispatchClaimService.EventTypeReconciliationIndeterminate;

    // ── Job lifecycle events ────────────────────────────────────────────────────

    /// <summary>
    /// Job entered the queue (transitions to Queued). Membership-changing: this is when a
    /// job first appears in <c>GetSubscriptionResourcesAsync</c>'s active jobIds/projectIds
    /// snapshot (see #1731 PR #1741 review, Bishop).
    /// </summary>
    public const string EventTypeJobQueued = "PrintFarmer.Queue.JobQueued.v1";

    /// <summary>Calibration job entered the queue. Membership-changing, same as <see cref="EventTypeJobQueued"/>.</summary>
    public const string EventTypeCalibrationJobQueued = "PrintFarmer.Queue.CalibrationJobQueued.v1";

    /// <summary>Job completed (all copies finished successfully).</summary>
    public const string EventTypeJobCompleted = Dispatch.DispatchClaimService.EventTypeJobCompleted;

    /// <summary>
    /// One copy of a multi-copy job finished but the job requeued for the next copy (still
    /// active; not membership-changing). See #1731 PR #1741 review (Bishop).
    /// </summary>
    public const string EventTypeJobCopyCompleted = Dispatch.DispatchClaimService.EventTypeJobCopyCompleted;

    /// <summary>Job transitioned to Failed.</summary>
    public const string EventTypeJobFailed = Dispatch.DispatchClaimService.EventTypeJobFailed;

    /// <summary>Orphaned Starting/Printing job synced to a terminal state.</summary>
    public const string EventTypeJobOrphanSynced = Dispatch.DispatchClaimService.EventTypeJobOrphanSynced;

    /// <summary>Job cancelled (terminal; removed from active queue).</summary>
    public const string EventTypeJobCancelled = Dispatch.DispatchClaimService.EventTypeJobCancelled;

    /// <summary>Job's current print attempt aborted (job returns to queued).</summary>
    public const string EventTypeJobAborted = Dispatch.DispatchClaimService.EventTypeJobAborted;

    /// <summary>Backend confirmed the active print was paused.</summary>
    public const string EventTypeJobPaused = "PrintFarmer.Queue.JobPaused.v1";

    /// <summary>Backend confirmed the paused print was resumed.</summary>
    public const string EventTypeJobResumed = "PrintFarmer.Queue.JobResumed.v1";

    public const string EventTypeBedClearAcknowledged =
        "PrintFarmer.Queue.BedClearAcknowledged.v1";

    public const string EventTypeBedClearConsumed =
        "PrintFarmer.Queue.BedClearConsumed.v1";

    public const string EventTypeBedClearExpired =
        "PrintFarmer.Queue.BedClearExpired.v1";

    public const string EventTypeBedClearInvalidated =
        "PrintFarmer.Queue.BedClearInvalidated.v1";

    public const string EventTypeControlRejected =
        "PrintFarmer.Queue.BackendControlRejected.v1";

    public const string EventTypeControlUnknown =
        "PrintFarmer.Queue.BackendControlUnknown.v1";

    // ── Shared writer ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a durable lifecycle outbox event to <paramref name="db"/> and allocates a monotonic
    /// sequence using <paramref name="sequenceAllocator"/>. The event is committed atomically
    /// when the caller calls <c>SaveChangesAsync()</c>.
    ///
    /// This public overload delegates to the internal
    /// <see cref="Dispatch.DispatchClaimService.AddLifecycleOutboxEventAsync"/> so higher-layer
    /// services outside <c>Farm.Infrastructure</c> can use the shared write pattern without a
    /// direct reference to the dispatch service.
    /// </summary>
    public static Task AddEventAsync(
        AppDbContext db,
        IDbOutboxSequenceAllocator sequenceAllocator,
        string eventType,
        Guid aggregateId,
        Guid? printerId,
        Guid? attemptId,
        byte[]? aggregateRowVersion,
        string? failureCode,
        string payloadJson,
        CancellationToken ct = default) =>
        AddEventAsync(
            db,
            sequenceAllocator,
            eventType,
            aggregateId,
            printerId,
            attemptId,
            aggregateRowVersion,
            failureCode,
            payloadJson,
            bedClearState: null,
            bedClearCommandId: null,
            bedClearExpiresAtUtc: null,
            failureRetryable: null,
            failureRequiresReconciliation: null,
            ct: ct);

    public static Task AddEventAsync(
        AppDbContext db,
        IDbOutboxSequenceAllocator sequenceAllocator,
        string eventType,
        Guid aggregateId,
        Guid? printerId,
        Guid? attemptId,
        byte[]? aggregateRowVersion,
        string? failureCode,
        string payloadJson,
        string? bedClearState = null,
        Guid? bedClearCommandId = null,
        DateTime? bedClearExpiresAtUtc = null,
        bool? failureRetryable = null,
        bool? failureRequiresReconciliation = null,
        CancellationToken ct = default) =>
        Dispatch.DispatchClaimService.AddLifecycleOutboxEventAsync(
            db,
            sequenceAllocator,
            eventType,
            aggregateId,
            printerId,
            attemptId,
            aggregateRowVersion,
            failureCode,
            payloadJson,
            ct,
            bedClearState: bedClearState,
            bedClearCommandId: bedClearCommandId,
            bedClearExpiresAtUtc: bedClearExpiresAtUtc,
            failureRetryable: failureRetryable,
            failureRequiresReconciliation: failureRequiresReconciliation);

    /// <summary>
    /// Builds a minimal canonical lifecycle payload JSON string.
    /// Returns only public identifiers; never includes credentials, filesystem paths, or free-form reasons.
    /// </summary>
    public static string BuildTerminalPayload(
        Guid jobId,
        Guid? printerId,
        Guid? attemptId,
        string jobStatus,
        string jobKind,
        string? failureCode) =>
        JsonSerializer.Serialize(new
        {
            jobId,
            printerId,
            attemptId,
            jobStatus,
            jobKind,
            failureCode,
        });
}

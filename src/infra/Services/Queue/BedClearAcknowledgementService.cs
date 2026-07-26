using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Manages exact-job, one-use, expiring bed-clear acknowledgements.
/// Each acknowledgement is scoped to the specific job at the front of the
/// printer queue; reorder, job insertion, cancellation, changed compatibility
/// data, or expiry all invalidate the acknowledgement.
///
/// <strong>Durability guarantee:</strong> the acknowledgement fields and the durable
/// <see cref="QueueDispatchOutbox"/> backend-start command are written in a SINGLE
/// <see cref="AppDbContext.SaveChangesAsync"/> call. A process crash between the
/// HTTP return and the adapter orchestrator picking up the command cannot lose the
/// work — startup polling will re-discover and execute it.
/// The actual claim (Job.Status = Starting, QueueDispatchAttempt) is acquired by
/// <see cref="Farm.Infrastructure.Services.Queue.Dispatch.IDispatchClaimService"/> when the
/// backend-start command is processed, using the persisted acknowledgement key.
/// </summary>
public sealed class BedClearAcknowledgementService(
    AppDbContext db,
    IDbOutboxSequenceAllocator sequenceAllocator,
    IPrinterStatusSnapshotReader statusReader,
    ILogger<BedClearAcknowledgementService> logger) : IBedClearAcknowledgementService
{
    /// <summary>
    /// Default acknowledgement validity window.
    /// Operators are expected to start the job within this window.
    /// </summary>
    private static readonly TimeSpan DefaultAcknowledgementTtl = TimeSpan.FromMinutes(15);

    /// <summary>Maximum age of a telemetry snapshot accepted when issuing an acknowledgement.</summary>
    private static readonly TimeSpan TelemetryFreshnessLimit = TimeSpan.FromMinutes(5);

    /// <summary>Event type string for the durable backend-start command written to the outbox.</summary>
    internal const string BackendStartCommandEventType = "PrintFarmer.Queue.BackendStartCommand.v1";

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IDbOutboxSequenceAllocator _sequenceAllocator = sequenceAllocator ?? throw new ArgumentNullException(nameof(sequenceAllocator));
    private readonly IPrinterStatusSnapshotReader _statusReader = statusReader ?? throw new ArgumentNullException(nameof(statusReader));
    private readonly ILogger<BedClearAcknowledgementService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<AcknowledgeBedClearResult> AcknowledgeAsync(
        AcknowledgeBedClearRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PreconditionRequired,
                null, null,
                "Idempotency-Key header is required for bed-clear acknowledgements.");
        }

        // --- Load job and dispatch state ---
        PrintJob? job = await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .FirstOrDefaultAsync(j => j.Id == request.JobId, ct);

        if (job is null)
        {
            return new AcknowledgeBedClearResult(BedClearAckOutcome.JobNotFound, null, null,
                $"Job {request.JobId} not found.");
        }

        // Verify the job is assigned to the requested printer.
        if (job.AssignedPrinterId != request.PrinterId)
        {
            return new AcknowledgeBedClearResult(BedClearAckOutcome.WrongJob, null, null,
                "Job is not assigned to the specified printer.");
        }

        // Short-circuit: job is already Starting or Printing — treat as success.
        if (job.Status is PrintJobStatus.Starting or PrintJobStatus.Printing)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.AlreadyStartingOrPrinting,
                job.RowVersion, null, null);
        }

        // Job must be in a dispatchable state.
        if (job.Status is not (PrintJobStatus.Queued or PrintJobStatus.Assigned))
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.JobNotDispatchable,
                job.RowVersion,
                null,
                $"Job is in state {job.Status} and cannot be acknowledged.");
        }

        if (job.GcodeFile is null)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.CalibrationJobIncompatible,
                job.RowVersion, null,
                "Job is missing its G-code artifact.");
        }

        PrinterDispatchState? dispatchState = await _db.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == request.PrinterId, ct);

        if (dispatchState is null)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterOfflineOrStale,
                null,
                null,
                $"Printer dispatch state for {request.PrinterId} not found.");
        }

        // If-Match precondition check.
        if (request.IfMatchDispatchState is null)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PreconditionRequired,
                job.RowVersion, dispatchState.RowVersion,
                "If-Match header is required for bed-clear acknowledgements.");
        }

        if (!request.IfMatchDispatchState.SequenceEqual(dispatchState.RowVersion ?? []))
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.DispatchRevisionConflict,
                job.RowVersion, dispatchState.RowVersion,
                "Dispatch state has changed since the request was prepared. Re-fetch and retry.");
        }

        // Check whether the printer is already occupied.
        if (dispatchState.ActiveJobId.HasValue && dispatchState.ActiveJobId != request.JobId)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterBusy,
                job.RowVersion,
                dispatchState.RowVersion,
                $"Printer {request.PrinterId} is busy with job {dispatchState.ActiveJobId}.");
        }

        // --- Exact-replay detection ---
        if (dispatchState.AcknowledgedJobId == request.JobId &&
            dispatchState.AcknowledgementIdempotencyKey == request.IdempotencyKey)
        {
            // Idempotent re-acknowledgement of the same request.
            _logger.LogDebug(
                "Bed-clear acknowledgement replayed: Job={JobId} Key={Key}",
                request.JobId, request.IdempotencyKey);

            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.Replayed,
                job.RowVersion, dispatchState.RowVersion, null);
        }

        // Conflict: same key, different job.
        if (dispatchState.AcknowledgementIdempotencyKey == request.IdempotencyKey &&
            dispatchState.AcknowledgedJobId != request.JobId)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.IdempotencyMismatch,
                job.RowVersion, dispatchState.RowVersion,
                "Idempotency key was previously used for a different job.");
        }

        // For calibration jobs, verify blocked-reason is clear.
        if (job.JobKind == JobKind.FilamentCalibration && job.BlockedReasonCode.HasValue &&
            job.BlockedReasonCode != JobBlockedReasonCode.None)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.CalibrationJobIncompatible,
                job.RowVersion, dispatchState.RowVersion,
                $"Calibration job blocked: {job.BlockedReasonCode}");
        }

        // =========================================================================
        // Printer revision is REQUIRED — an acknowledgement issued without pinning the
        // configuration the operator actually saw can be consumed after a config change.
        // =========================================================================
        if (!request.ExpectedPrinterConfigRevision.HasValue)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PreconditionRequired,
                job.RowVersion, dispatchState.RowVersion,
                "expectedPrinterConfigRevision is required for bed-clear acknowledgements.");
        }

        Printer? printer = await _db.Printers
            .Include(p => p.Toolheads)
            .FirstOrDefaultAsync(p => p.Id == request.PrinterId, ct);

        if (printer is null)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterOfflineOrStale,
                job.RowVersion, dispatchState.RowVersion,
                $"Printer {request.PrinterId} not found.");
        }

        if (printer.ConfigurationRevision != request.ExpectedPrinterConfigRevision.Value)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.CalibrationJobIncompatible,
                job.RowVersion, dispatchState.RowVersion,
                $"Printer configuration revision {printer.ConfigurationRevision} does not match expected {request.ExpectedPrinterConfigRevision}.");
        }

        // =========================================================================
        // Fresh telemetry — an acknowledgement must reflect a bed the operator can
        // actually see right now, on a printer that is online and not printing.
        // =========================================================================
        PrinterStatusSnapshot? snapshot = _statusReader.GetStatusSnapshot(request.PrinterId);
        if (snapshot is null)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterOfflineOrStale,
                job.RowVersion, dispatchState.RowVersion,
                "Fresh telemetry is required to acknowledge bed-clear. No snapshot is available for this printer.");
        }

        DateTime? observedAtUtc = snapshot.ObservedAtUtc ?? snapshot.LastSeenAtUtc;
        if (!observedAtUtc.HasValue || (DateTime.UtcNow - observedAtUtc.Value) > TelemetryFreshnessLimit)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterOfflineOrStale,
                job.RowVersion, dispatchState.RowVersion,
                $"Printer telemetry is older than {TelemetryFreshnessLimit.TotalMinutes:F0} minutes; bed-clear cannot be acknowledged.");
        }

        if (!snapshot.Status.IsOnline)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterOfflineOrStale,
                job.RowVersion, dispatchState.RowVersion,
                "Printer is offline per telemetry; bed-clear cannot be acknowledged.");
        }

        if (snapshot.Status.State is "printing" or "starting" or "paused")
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterBusy,
                job.RowVersion, dispatchState.RowVersion,
                $"Printer is in state '{snapshot.Status.State}' per telemetry; bed-clear cannot be acknowledged.");
        }

        // =========================================================================
        // Hard filament / spool gate — evaluated with the SAME shared rules the claim
        // uses so an acknowledgement can never be issued for a job the claim will reject.
        // =========================================================================
        DispatchClaimResult? filamentFailure = DispatchSafetyGates.EvaluateFilament(job, printer);
        if (filamentFailure is not null)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.FilamentCheckFailed,
                job.RowVersion, dispatchState.RowVersion,
                filamentFailure.ErrorDetail);
        }

        // =========================================================================
        // Complete compatibility tuple, hash and lineage validation for calibration jobs,
        // plus shared hardware gates for every job kind.
        // =========================================================================
        DispatchClaimResult? hardwareFailure = DispatchSafetyGates.EvaluateHardware(job, printer);
        if (hardwareFailure is not null)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.CalibrationJobIncompatible,
                job.RowVersion, dispatchState.RowVersion,
                hardwareFailure.ErrorDetail);
        }

        if (job.JobKind == JobKind.FilamentCalibration)
        {
            if (!job.GcodeFile.IsImmutable || job.GcodeFile.PromotedAtUtc is null ||
                !QueueJobClassifier.IsCalibrationArtifact(job.GcodeFile))
            {
                return new AcknowledgeBedClearResult(
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    job.RowVersion, dispatchState.RowVersion,
                    "The calibration job does not reference a promoted immutable calibration artifact.");
            }

            if (string.IsNullOrWhiteSpace(job.GcodeContentSha256))
            {
                return new AcknowledgeBedClearResult(
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    job.RowVersion, dispatchState.RowVersion,
                    "The calibration job has no pinned G-code content hash.");
            }

            string? authoritativeHash = !string.IsNullOrWhiteSpace(job.GcodeFile.ContentSha256)
                ? job.GcodeFile.ContentSha256
                : job.GcodeFile.FileHash;

            if (string.IsNullOrWhiteSpace(authoritativeHash) ||
                !string.Equals(job.GcodeContentSha256, authoritativeHash, StringComparison.OrdinalIgnoreCase))
            {
                const string HashMismatchDetail =
                    "The calibration artifact's content hash does not match the job's pinned hash. " +
                    "A new job and idempotency key are required.";
                return new AcknowledgeBedClearResult(
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    job.RowVersion, dispatchState.RowVersion, HashMismatchDetail);
            }

            DispatchClaimResult? calibrationFailure =
                DispatchSafetyGates.EvaluateCalibrationCompatibility(job, printer);

            if (calibrationFailure is not null)
            {
                return new AcknowledgeBedClearResult(
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    job.RowVersion, dispatchState.RowVersion,
                    calibrationFailure.ErrorDetail);
            }
        }

        // =========================================================================
        // ATOMIC WRITE: acknowledgement fields + durable backend-start command.
        // The actual claim (Job.Status = Starting, QueueDispatchAttempt) is acquired
        // by IDispatchClaimService when the adapter orchestrator processes the outbox
        // BackendStartCommand event. This two-phase approach ensures the shared claim
        // path is used for every start, while keeping ack persistence atomic.
        //
        // Crash recovery: the outbox publisher rediscovers Pending BackendStartCommand
        // events on its next poll cycle and re-invokes the adapter orchestrator.
        //
        // Bounded retry: if the only concurrency conflict is on the OutboxSequenceState
        // row (sequence allocation contention), we reload the counter and retry up to
        // MaxSequenceRetries times so every legitimate producer persists its own event.
        // =========================================================================
        DateTime now = DateTime.UtcNow;

        // Persist the acknowledgement on dispatch state.
        dispatchState.AcknowledgedJobId = request.JobId;
        dispatchState.AcknowledgedAtUtc = now;
        dispatchState.AcknowledgedBySubject = request.ActorSubject;
        dispatchState.AcknowledgementIdempotencyKey = request.IdempotencyKey;
        dispatchState.AcknowledgementExpiresAtUtc = now + DefaultAcknowledgementTtl;

        // Write a durable backend-start command to the outbox.
        // Payload has everything the adapter orchestrator needs: jobId, printerId,
        // actorSubject, and the acknowledgement key to pass to AcquireClaimAsync.
        var startCommand = new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = 0, // Allocated inside the retry loop below.
            AggregateType = nameof(PrintJob),
            AggregateId = request.JobId,
            AggregateRowVersion = job.RowVersion,
            DispatchStateRowVersion = dispatchState.RowVersion,
            BedClearState = "Acknowledged",
            PrinterId = request.PrinterId,
            PrinterConfigRevision = job.PinnedPrinterConfigRevision,
            EventType = BackendStartCommandEventType,
            SchemaVersion = "1",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                jobId = request.JobId,
                printerId = request.PrinterId,
                actorSubject = request.ActorSubject,
                acknowledgementKey = request.IdempotencyKey,
            }),
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = now,
        };

        _ = _db.QueueDispatchOutbox.Add(startCommand);

        // Durable audit — committed in the SAME transaction as the acknowledgement.
        _ = QueueAuditWriter.Add(
            _db,
            request.ActorSubject,
            QueueAuditOperations.BedClearAcknowledge,
            QueueAuditOutcomes.Success,
            nameof(PrintJob),
            resourceId: request.JobId,
            printerId: request.PrinterId,
            printJobId: request.JobId,
            jobRowVersion: job.RowVersion,
            dispatchStateRowVersion: dispatchState.RowVersion,
            idempotencyKey: request.IdempotencyKey,
            detail: new
            {
                jobKind = job.JobKind?.ToString() ?? nameof(JobKind.Standard),
                expectedPrinterConfigRevision = request.ExpectedPrinterConfigRevision,
                commandId = startCommand.Id,
            });

        // Bounded retry loop: up to MaxSequenceRetries attempts on sequence-only conflicts.
        // Any other conflict (e.g., the dispatch-state If-Match already caught above) surfaces
        // as DispatchRevisionConflict without retrying.
        const int MaxSequenceRetries = 5;
        bool saved = false;
        DbUpdateConcurrencyException? lastConflict = null;

        for (int seqRetry = 0; seqRetry < MaxSequenceRetries && !saved; seqRetry++)
        {
            startCommand.Sequence = await _sequenceAllocator.AllocateAsync(_db, ct);

            try
            {
                await _db.SaveChangesAsync(ct);
                saved = true;
                lastConflict = null;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                lastConflict = ex;

                // Only retry when the sole conflicting entity is the sequence counter.
                // A conflict on the dispatch state or job means a genuine race that the
                // client must resolve by re-fetching.
                bool isSequenceConflictOnly = ex.Entries.Count > 0 &&
                    ex.Entries.All(e => e.Entity is OutboxSequenceState);

                if (!isSequenceConflictOnly || seqRetry >= MaxSequenceRetries - 1)
                {
                    // Give up — not a sequence conflict or max retries exhausted.
                    break;
                }

                _logger.LogWarning(
                    ex,
                    "[BedClearAck] Outbox sequence contention (retry {Retry}/{Max}); reloading counter for Job={JobId}",
                    seqRetry + 1, MaxSequenceRetries, request.JobId);

                // Reload the sequence-state row so the next AllocateAsync call sees
                // the winner's committed NextSequence value and increments from there.
                OutboxSequenceState? seqState = _db.OutboxSequenceStates.Local.SingleOrDefault();
                if (seqState is not null)
                {
                    await _db.Entry(seqState).ReloadAsync(ct);
                }
            }
        }

        if (!saved)
        {
            _logger.LogWarning(
                lastConflict,
                "[BedClearAck] Concurrency conflict persisting bed-clear acknowledgement for Job={JobId}",
                request.JobId);

            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.DispatchRevisionConflict,
                null, null,
                "A concurrent operation modified the dispatch state. Re-fetch and retry.");
        }

        _logger.LogInformation(
            "Bed-clear acknowledged and durable backend-start command queued: " +
            "Job={JobId} Printer={PrinterId} Actor={Actor} Key={Key} Command={CommandId}",
            request.JobId, request.PrinterId, request.ActorSubject,
            request.IdempotencyKey, startCommand.Id);

        return new AcknowledgeBedClearResult(
            BedClearAckOutcome.Accepted,
            job.RowVersion, dispatchState.RowVersion, null);
    }

    /// <inheritdoc />
    public async Task InvalidateStaleAcknowledgementsAsync(
        Guid printerId,
        CancellationToken ct = default)
    {
        PrinterDispatchState? dispatchState = await _db.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId, ct);

        if (dispatchState is null || !dispatchState.AcknowledgedJobId.HasValue)
        {
            return;
        }

        Guid acknowledgedJobId = dispatchState.AcknowledgedJobId.Value;

        // Verify the acknowledged job is still the front-of-queue for this printer,
        // using the SINGLE shared ordering selector (Urgent first).
        PrintJob? frontJob = await _db.PrintJobs
            .Where(j =>
                j.AssignedPrinterId == printerId &&
                (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned))
            .OrderByPriorityDescending()
            .FirstOrDefaultAsync(ct);

        bool isStale = frontJob is null || frontJob.Id != acknowledgedJobId;

        if (isStale)
        {
            dispatchState.AcknowledgedJobId = null;
            dispatchState.AcknowledgedAtUtc = null;
            dispatchState.AcknowledgedBySubject = null;
            dispatchState.AcknowledgementIdempotencyKey = null;
            dispatchState.AcknowledgementExpiresAtUtc = null;

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Invalidated stale bed-clear acknowledgement for Printer={PrinterId} (was for Job={JobId})",
                printerId, acknowledgedJobId);
        }
    }
}

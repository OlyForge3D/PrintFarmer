using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
    ILogger<BedClearAcknowledgementService> logger) : IBedClearAcknowledgementService
{
    /// <summary>
    /// Default acknowledgement validity window.
    /// Operators are expected to start the job within this window.
    /// </summary>
    private static readonly TimeSpan DefaultAcknowledgementTtl = TimeSpan.FromMinutes(15);

    /// <summary>Event type string for the durable backend-start command written to the outbox.</summary>
    internal const string BackendStartCommandEventType = "PrintFarmer.Queue.BackendStartCommand.v1";

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IDbOutboxSequenceAllocator _sequenceAllocator = sequenceAllocator ?? throw new ArgumentNullException(nameof(sequenceAllocator));
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

        // Verify printer configuration revision has not advanced beyond the pinned value.
        if (request.ExpectedPrinterConfigRevision.HasValue)
        {
            Printer? printer = await _db.Printers
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.PrinterId, ct);

            if (printer is not null &&
                printer.ConfigurationRevision != request.ExpectedPrinterConfigRevision.Value)
            {
                return new AcknowledgeBedClearResult(
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    job.RowVersion, dispatchState.RowVersion,
                    $"Printer configuration revision {printer.ConfigurationRevision} does not match expected {request.ExpectedPrinterConfigRevision}.");
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
            Sequence = await _sequenceAllocator.AllocateAsync(_db, ct),
            AggregateType = nameof(PrintJob),
            AggregateId = request.JobId,
            AggregateRowVersion = job.RowVersion,
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

        _db.QueueDispatchOutbox.Add(startCommand);

        try
        {
            // Single SaveChangesAsync: ack fields + durable command in one transaction.
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Bed-clear acknowledged and durable backend-start command queued: " +
                "Job={JobId} Printer={PrinterId} Actor={Actor} Key={Key} Command={CommandId}",
                request.JobId, request.PrinterId, request.ActorSubject,
                request.IdempotencyKey, startCommand.Id);

            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.Accepted,
                job.RowVersion, dispatchState.RowVersion, null);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(
                ex,
                "Concurrency conflict persisting bed-clear acknowledgement for Job={JobId}",
                request.JobId);

            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.DispatchRevisionConflict,
                null, null,
                "A concurrent operation modified the dispatch state. Re-fetch and retry.");
        }
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

        // Verify the acknowledged job is still the front-of-queue for this printer.
        PrintJob? frontJob = await _db.PrintJobs
            .Where(j =>
                j.AssignedPrinterId == printerId &&
                (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.QueuePosition)
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

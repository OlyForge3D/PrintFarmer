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
/// </summary>
public sealed class BedClearAcknowledgementService(
    AppDbContext db,
    ILogger<BedClearAcknowledgementService> logger) : IBedClearAcknowledgementService
{
    /// <summary>
    /// Default acknowledgement validity window.
    /// Operators are expected to start the job within this window.
    /// </summary>
    private static readonly TimeSpan DefaultAcknowledgementTtl = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
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

        // --- Persist the acknowledgement ---
        DateTime now = DateTime.UtcNow;
        dispatchState.AcknowledgedJobId = request.JobId;
        dispatchState.AcknowledgedAtUtc = now;
        dispatchState.AcknowledgedBySubject = request.ActorSubject;
        dispatchState.AcknowledgementIdempotencyKey = request.IdempotencyKey;
        dispatchState.AcknowledgementExpiresAtUtc = now + DefaultAcknowledgementTtl;

        try
        {
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Bed-clear acknowledged: Job={JobId} Printer={PrinterId} Actor={Actor} Key={Key} Expires={Expires:u}",
                request.JobId, request.PrinterId, request.ActorSubject,
                request.IdempotencyKey, dispatchState.AcknowledgementExpiresAtUtc);

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

using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Database-backed atomic dispatch claim service.
/// Every start path (manual, auto, scored, batch, rerun, bed-clear) must acquire a
/// claim through this service before calling any printer upload/start adapter.
/// The transaction commits before any network I/O so a process crash cannot leave
/// a printer in an inconsistent state without a corresponding database record.
/// </summary>
public sealed class DispatchClaimService(
    AppDbContext db,
    ILogger<DispatchClaimService> logger) : IDispatchClaimService
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger<DispatchClaimService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<DispatchClaimResult> AcquireClaimAsync(
        DispatchClaimRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Load job + dispatch state in a single round-trip.
        PrintJob? job = await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .FirstOrDefaultAsync(j => j.Id == request.JobId, ct);

        if (job is null)
        {
            return DispatchClaimResult.Fail("job_not_found", $"Print job {request.JobId} not found.");
        }

        PrinterDispatchState? dispatchState = await _db.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == request.PrinterId, ct);

        if (dispatchState is null)
        {
            return DispatchClaimResult.Fail("printer_not_found", $"Printer dispatch state for {request.PrinterId} not found.");
        }

        // --- Pre-claim validations ---

        // Job must be in a Queued or Assigned state (not already Starting/Printing/terminal).
        if (job.Status is not (PrintJobStatus.Queued or PrintJobStatus.Assigned))
        {
            return DispatchClaimResult.Fail(
                "job_not_dispatchable",
                $"Job {request.JobId} is in state {job.Status}, which cannot be dispatched.");
        }

        // Job must be assigned to the claimed printer.
        if (job.AssignedPrinterId != request.PrinterId)
        {
            return DispatchClaimResult.Fail(
                "printer_mismatch",
                $"Job {request.JobId} is assigned to printer {job.AssignedPrinterId}, not {request.PrinterId}.");
        }

        // Verify no other active job is already claiming this printer.
        if (dispatchState.ActiveJobId.HasValue && dispatchState.ActiveJobId != request.JobId)
        {
            return DispatchClaimResult.Fail(
                "printer_busy",
                $"Printer {request.PrinterId} already has an active job {dispatchState.ActiveJobId}.");
        }

        // For calibration jobs, validate the acknowledgement idempotency key when required.
        if (job.JobKind == JobKind.FilamentCalibration &&
            request.AcknowledgementIdempotencyKey is not null)
        {
            // Verify the stored acknowledgement matches this exact job and has not expired.
            if (dispatchState.AcknowledgedJobId != request.JobId)
            {
                return DispatchClaimResult.Fail(
                    "wrong_acknowledgement_job",
                    $"Acknowledgement was for job {dispatchState.AcknowledgedJobId}, not {request.JobId}.");
            }

            if (dispatchState.AcknowledgementExpiresAtUtc.HasValue &&
                dispatchState.AcknowledgementExpiresAtUtc < DateTime.UtcNow)
            {
                return DispatchClaimResult.Fail(
                    "acknowledgement_expired",
                    $"Bed-clear acknowledgement for job {request.JobId} has expired.");
            }

            if (dispatchState.AcknowledgementIdempotencyKey != request.AcknowledgementIdempotencyKey)
            {
                return DispatchClaimResult.Fail(
                    "acknowledgement_key_mismatch",
                    "Acknowledgement idempotency key does not match the persisted value.");
            }
        }

        // --- Compute next attempt number ---
        int attemptNumber = await _db.QueueDispatchAttempts
            .Where(a => a.PrintJobId == request.JobId)
            .CountAsync(ct) + 1;

        var attempt = new QueueDispatchAttempt
        {
            Id = Guid.NewGuid(),
            PrintJobId = request.JobId,
            PrinterId = request.PrinterId,
            PrinterConfigRevision = job.PinnedPrinterConfigRevision ?? 0,
            AttemptNumber = attemptNumber,
            ActorSubject = request.ActorSubject,
            StartPathKind = request.StartPathKind,
            AcknowledgementIdempotencyKey = request.AcknowledgementIdempotencyKey,
            ClaimedAtUtc = DateTime.UtcNow,
            Outcome = DispatchAttemptOutcome.InProgress,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        // Write outbox event in the same transaction.
        var outboxEvent = new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            AggregateType = nameof(PrintJob),
            AggregateId = request.JobId,
            AggregateRowVersion = job.RowVersion,
            PrinterId = request.PrinterId,
            PrinterConfigRevision = job.PinnedPrinterConfigRevision,
            EventType = "PrintFarmer.Queue.JobDispatchStarted.v1",
            SchemaVersion = "1",
            PayloadJson = BuildOutboxPayload(job, attempt),
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };

        // --- Atomic state transition ---
        job.Status = PrintJobStatus.Starting;
        job.ActualStartTime = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        // Consume the acknowledgement by clearing it.
        if (request.AcknowledgementIdempotencyKey is not null)
        {
            dispatchState.AcknowledgedJobId = null;
            dispatchState.AcknowledgedAtUtc = null;
            dispatchState.AcknowledgedBySubject = null;
            dispatchState.AcknowledgementIdempotencyKey = null;
            dispatchState.AcknowledgementExpiresAtUtc = null;
        }

        dispatchState.ActiveJobId = request.JobId;
        dispatchState.ActiveDispatchAttemptId = attempt.Id;

        attempt.JobRowVersionAtClaim = job.RowVersion;
        attempt.DispatchStateRowVersionAtClaim = dispatchState.RowVersion;

        _db.QueueDispatchAttempts.Add(attempt);
        _db.QueueDispatchOutbox.Add(outboxEvent);

        try
        {
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Dispatch claim acquired: Job={JobId} Printer={PrinterId} Attempt={AttemptId} StartPath={StartPath}",
                request.JobId, request.PrinterId, attempt.Id, request.StartPathKind);

            return DispatchClaimResult.Ok(attempt);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(
                ex,
                "Concurrency conflict acquiring dispatch claim for Job={JobId} Printer={PrinterId}",
                request.JobId, request.PrinterId);

            return DispatchClaimResult.Fail(
                "concurrency_conflict",
                "A concurrent operation modified the job or dispatch state. Retry with the latest ETag.");
        }
    }

    /// <inheritdoc />
    public async Task ReleaseClaimOnKnownFailureAsync(
        Guid attemptId,
        string errorCode,
        string errorDetail,
        CancellationToken ct = default)
    {
        QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
            .Include(a => a.PrintJob)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);

        if (attempt is null)
        {
            _logger.LogWarning("ReleaseClaimOnKnownFailure: Attempt {AttemptId} not found.", attemptId);
            return;
        }

        PrinterDispatchState? dispatchState = await _db.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == attempt.PrinterId, ct);

        // Update attempt.
        attempt.Outcome = DispatchAttemptOutcome.FailedBeforeStart;
        attempt.ErrorCode = errorCode;
        attempt.ErrorDetail = errorDetail;
        attempt.IsRetryable = true;
        attempt.UpdatedAtUtc = DateTime.UtcNow;

        // Return job to Assigned so it can be re-dispatched.
        if (attempt.PrintJob is not null && attempt.PrintJob.Status == PrintJobStatus.Starting)
        {
            attempt.PrintJob.Status = PrintJobStatus.Assigned;
            attempt.PrintJob.ActualStartTime = null;
            attempt.PrintJob.UpdatedAt = DateTime.UtcNow;
        }

        // Release printer active job reference.
        if (dispatchState is not null && dispatchState.ActiveDispatchAttemptId == attemptId)
        {
            dispatchState.ActiveJobId = null;
            dispatchState.ActiveDispatchAttemptId = null;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Dispatch claim released (known failure): Attempt={AttemptId} Code={ErrorCode}",
            attemptId, errorCode);
    }

    /// <inheritdoc />
    public async Task RecordBackendAcceptedAsync(
        Guid attemptId,
        string? backendJobId,
        CancellationToken ct = default)
    {
        QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
            .Include(a => a.PrintJob)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);

        if (attempt is null)
        {
            _logger.LogWarning("RecordBackendAccepted: Attempt {AttemptId} not found.", attemptId);
            return;
        }

        attempt.Outcome = DispatchAttemptOutcome.Accepted;
        attempt.BackendAcceptedAtUtc = DateTime.UtcNow;
        attempt.BackendJobId = backendJobId;
        attempt.UpdatedAtUtc = DateTime.UtcNow;

        if (attempt.PrintJob is not null && attempt.PrintJob.Status == PrintJobStatus.Starting)
        {
            attempt.PrintJob.Status = PrintJobStatus.Printing;
            attempt.PrintJob.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Backend accepted dispatch: Attempt={AttemptId} BackendJobId={BackendJobId}",
            attemptId, backendJobId ?? "(none)");
    }

    /// <inheritdoc />
    public async Task RecordUnknownOutcomeAsync(
        Guid attemptId,
        string errorDetail,
        CancellationToken ct = default)
    {
        QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);

        if (attempt is null)
        {
            _logger.LogWarning("RecordUnknownOutcome: Attempt {AttemptId} not found.", attemptId);
            return;
        }

        attempt.Outcome = DispatchAttemptOutcome.Unknown;
        attempt.ErrorDetail = errorDetail;
        attempt.IsRetryable = false;
        attempt.RequiresReconciliation = true;
        attempt.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Dispatch outcome unknown — reconciliation required: Attempt={AttemptId}",
            attemptId);
    }

    /// <summary>
    /// Builds a minimal, credential-free JSON payload for the outbox event.
    /// </summary>
    private static string BuildOutboxPayload(PrintJob job, QueueDispatchAttempt attempt)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            jobKind = job.JobKind?.ToString() ?? "Standard",
            printerId = attempt.PrinterId,
            attemptId = attempt.Id,
            attemptNumber = attempt.AttemptNumber,
            startPathKind = attempt.StartPathKind,
            actorSubject = attempt.ActorSubject,
            calibrationProjectId = job.CalibrationProjectId,
            calibrationAttemptId = job.CalibrationAttemptId,
            claimedAtUtc = attempt.ClaimedAtUtc,
        });
    }
}

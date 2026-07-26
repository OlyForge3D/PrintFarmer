using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
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
    IPrinterStatusSnapshotReader statusReader,
    IOutboxSequenceAllocator sequenceAllocator,
    ILogger<DispatchClaimService> logger) : IDispatchClaimService
{
    /// <summary>Maximum age of a telemetry snapshot for calibration dispatch.</summary>
    private static readonly TimeSpan TelemetryFreshnessLimit = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IPrinterStatusSnapshotReader _statusReader = statusReader ?? throw new ArgumentNullException(nameof(statusReader));
    private readonly IOutboxSequenceAllocator _sequenceAllocator = sequenceAllocator ?? throw new ArgumentNullException(nameof(sequenceAllocator));
    private readonly ILogger<DispatchClaimService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<DispatchClaimResult> AcquireClaimAsync(
        DispatchClaimRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        PrintJob? job = await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .FirstOrDefaultAsync(j => j.Id == request.JobId, ct);

        if (job is null)
        {
            return DispatchClaimResult.Fail("job_not_found", $"Print job {request.JobId} not found.");
        }

        Printer? printer = await _db.Printers
            .FirstOrDefaultAsync(p => p.Id == request.PrinterId, ct);

        if (printer is null)
        {
            return DispatchClaimResult.Fail("printer_not_found", $"Printer {request.PrinterId} not found.");
        }

        PrinterDispatchState? dispatchState = await _db.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == request.PrinterId, ct);

        if (dispatchState is null)
        {
            return DispatchClaimResult.Fail("printer_not_found", $"Printer dispatch state for {request.PrinterId} not found.");
        }

        if (!printer.IsEnabled)
        {
            return DispatchClaimResult.Fail("printer_disabled", $"Printer {request.PrinterId} is disabled.");
        }

        if (printer.InMaintenance)
        {
            return DispatchClaimResult.Fail("printer_in_maintenance", $"Printer {request.PrinterId} is in maintenance.");
        }

        if (job.Status is not (PrintJobStatus.Queued or PrintJobStatus.Assigned))
        {
            return DispatchClaimResult.Fail(
                "job_not_dispatchable",
                $"Job {request.JobId} is in state {job.Status}, which cannot be dispatched.");
        }

        if (job.AssignedPrinterId != request.PrinterId)
        {
            return DispatchClaimResult.Fail(
                "printer_mismatch",
                $"Job {request.JobId} is assigned to printer {job.AssignedPrinterId}, not {request.PrinterId}.");
        }

        if (job.GcodeFile is null)
        {
            return DispatchClaimResult.Fail(
                "gcode_missing",
                $"Job {request.JobId} is missing its G-code artifact.");
        }

        if (dispatchState.ActiveJobId.HasValue && dispatchState.ActiveJobId != request.JobId)
        {
            return DispatchClaimResult.Fail(
                "printer_busy_active",
                $"Printer {request.PrinterId} already has an active job {dispatchState.ActiveJobId}.");
        }

        // --- Telemetry freshness and online/idle check ---
        // Calibration jobs FAIL CLOSED: fresh telemetry is mandatory.
        // Standard jobs pass through if no telemetry is available.
        PrinterStatusSnapshot? snapshot = _statusReader.GetStatusSnapshot(request.PrinterId);

        if (job.JobKind == JobKind.FilamentCalibration)
        {
            // Must have a telemetry snapshot — null means no data, which is a hard gate.
            if (snapshot is null)
            {
                return DispatchClaimResult.Fail(
                    "telemetry_unavailable",
                    $"Fresh telemetry is required for calibration dispatch. No snapshot is available for printer {request.PrinterId}.");
            }

            DateTime? observedAt = snapshot.ObservedAtUtc ?? snapshot.LastSeenAtUtc;
            bool isFresh = observedAt.HasValue &&
                           (DateTime.UtcNow - observedAt.Value) <= TelemetryFreshnessLimit;

            if (!isFresh)
            {
                return DispatchClaimResult.Fail(
                    "telemetry_stale",
                    $"Printer telemetry is older than {TelemetryFreshnessLimit.TotalMinutes:F0} minutes. Calibration requires fresh online+idle status.");
            }

            if (!snapshot.Status.IsOnline)
            {
                return DispatchClaimResult.Fail(
                    "printer_offline",
                    $"Printer {request.PrinterId} is not online per telemetry.");
            }

            string? state = snapshot.Status.State;
            if (state is "printing" or "starting" or "paused")
            {
                return DispatchClaimResult.Fail(
                    "printer_busy_telemetry",
                    $"Printer {request.PrinterId} is in state '{state}' per telemetry; cannot start a calibration job.");
            }
        }
        else if (snapshot is not null)
        {
            // Standard jobs: check online/idle status only when telemetry is available.
            if (!snapshot.Status.IsOnline)
            {
                return DispatchClaimResult.Fail(
                    "printer_offline",
                    $"Printer {request.PrinterId} is not online per telemetry.");
            }

            string? state = snapshot.Status.State;
            if (state is "printing" or "starting" or "paused")
            {
                return DispatchClaimResult.Fail(
                    "printer_busy_telemetry",
                    $"Printer {request.PrinterId} is in state '{state}' per telemetry; cannot start a new job.");
            }
        }

        // --- Calibration-specific compatibility checks ---
        // All compatibility fields must be explicitly set (non-null, non-Unknown).
        // Null fields are not inferred from manufacturer/model/backend — they fail closed.
        if (job.JobKind == JobKind.FilamentCalibration)
        {
            // Required firmware family must be explicitly Klipper (not null or Unknown).
            if (!job.RequiredFirmwareFamily.HasValue || job.RequiredFirmwareFamily == PrinterFirmwareFamily.Unknown)
            {
                return DispatchClaimResult.Fail(
                    "compatibility_incomplete",
                    "Calibration job is missing required firmware family. Null or Unknown compatibility fields are not permitted.");
            }

            if (!job.RequiredGcodeDialect.HasValue || job.RequiredGcodeDialect == PrinterGcodeDialect.Unknown)
            {
                return DispatchClaimResult.Fail(
                    "compatibility_incomplete",
                    "Calibration job is missing required G-code dialect. Null or Unknown compatibility fields are not permitted.");
            }

            if (string.IsNullOrWhiteSpace(job.RequiredSlicerEngine))
            {
                return DispatchClaimResult.Fail(
                    "compatibility_incomplete",
                    "Calibration job is missing required slicer engine.");
            }

            if (string.IsNullOrWhiteSpace(job.RequiredSlicerDistribution))
            {
                return DispatchClaimResult.Fail(
                    "compatibility_incomplete",
                    "Calibration job is missing required slicer distribution.");
            }

            if (string.IsNullOrWhiteSpace(job.RequiredSlicerVersion))
            {
                return DispatchClaimResult.Fail(
                    "compatibility_incomplete",
                    "Calibration job is missing required slicer version.");
            }

            // Validate actual compatibility against printer configuration.
            if (printer.FirmwareFamily != job.RequiredFirmwareFamily)
            {
                return DispatchClaimResult.Fail(
                    "firmware_family_mismatch",
                    $"Job requires firmware family '{job.RequiredFirmwareFamily}' but printer has '{printer.FirmwareFamily}'.");
            }

            if (printer.GcodeDialect != job.RequiredGcodeDialect)
            {
                return DispatchClaimResult.Fail(
                    "gcode_dialect_mismatch",
                    $"Job requires G-code dialect '{job.RequiredGcodeDialect}' but printer has '{printer.GcodeDialect}'.");
            }

            if (!string.Equals(printer.CalibrationSlicerEngine, job.RequiredSlicerEngine, StringComparison.OrdinalIgnoreCase))
            {
                return DispatchClaimResult.Fail(
                    "slicer_tuple_mismatch",
                    $"Job requires slicer engine '{job.RequiredSlicerEngine}' but printer is configured for '{printer.CalibrationSlicerEngine}'.");
            }

            if (!string.Equals(printer.CalibrationSlicerDistribution, job.RequiredSlicerDistribution, StringComparison.OrdinalIgnoreCase))
            {
                return DispatchClaimResult.Fail(
                    "slicer_tuple_mismatch",
                    $"Job requires slicer distribution '{job.RequiredSlicerDistribution}' but printer is configured for '{printer.CalibrationSlicerDistribution}'.");
            }

            if (!string.Equals(printer.CalibrationSlicerVersion, job.RequiredSlicerVersion, StringComparison.OrdinalIgnoreCase))
            {
                return DispatchClaimResult.Fail(
                    "slicer_tuple_mismatch",
                    $"Job requires slicer version '{job.RequiredSlicerVersion}' but printer is configured for '{printer.CalibrationSlicerVersion}'.");
            }

            if (job.PinnedPrinterConfigRevision.HasValue &&
                printer.ConfigurationRevision != job.PinnedPrinterConfigRevision.Value)
            {
                return DispatchClaimResult.Fail(
                    "printer_config_revision_stale",
                    $"Printer configuration revision {printer.ConfigurationRevision} does not match the pinned revision {job.PinnedPrinterConfigRevision}.");
            }

            // Ack key is required in the claim request.
            if (string.IsNullOrWhiteSpace(request.AcknowledgementIdempotencyKey))
            {
                return DispatchClaimResult.Fail(
                    "acknowledgement_required",
                    "Calibration jobs require a valid bed-clear acknowledgement idempotency key.");
            }

            // A persisted ack MUST exist — fail closed. An ack key in the request with no
            // persisted counterpart is a programming error or a replay attack.
            if (!dispatchState.AcknowledgedJobId.HasValue)
            {
                return DispatchClaimResult.Fail(
                    "acknowledgement_missing",
                    "No persisted bed-clear acknowledgement found for this printer. The operator must acknowledge bed-clear before calibration dispatch.");
            }

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

        var outboxEvent = new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = _sequenceAllocator.Next(), // Monotonic: process-local atomic counter seeded from DB max.
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

        job.Status = PrintJobStatus.Starting;
        job.ActualStartTime = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        if (request.AcknowledgementIdempotencyKey is not null &&
            dispatchState.AcknowledgedJobId == request.JobId)
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

        attempt.Outcome = DispatchAttemptOutcome.FailedBeforeStart;
        attempt.ErrorCode = errorCode;
        attempt.ErrorDetail = errorDetail;
        attempt.IsRetryable = true;
        attempt.UpdatedAtUtc = DateTime.UtcNow;

        if (attempt.PrintJob is not null && attempt.PrintJob.Status == PrintJobStatus.Starting)
        {
            attempt.PrintJob.Status = PrintJobStatus.Assigned;
            attempt.PrintJob.ActualStartTime = null;
            attempt.PrintJob.UpdatedAt = DateTime.UtcNow;
        }

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
            "Dispatch outcome unknown - reconciliation required: Attempt={AttemptId}",
            attemptId);
    }

    private static string BuildOutboxPayload(PrintJob job, QueueDispatchAttempt attempt) =>
        System.Text.Json.JsonSerializer.Serialize(new
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

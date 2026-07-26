using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Database-backed atomic dispatch claim service.
/// Every start path (manual, auto, scored, batch, rerun, bed-clear, slice bridge, printer
/// file start) must acquire a claim through this service before calling any printer
/// upload/start adapter. The transaction commits before any network I/O so a process crash
/// cannot leave a printer in an inconsistent state without a corresponding database record.
/// </summary>
public sealed class DispatchClaimService(
    AppDbContext db,
    IPrinterStatusSnapshotReader statusReader,
    IDbOutboxSequenceAllocator sequenceAllocator,
    ILogger<DispatchClaimService> logger) : IDispatchClaimService
{
    /// <summary>Maximum age of a telemetry snapshot for calibration dispatch.</summary>
    internal static readonly TimeSpan TelemetryFreshnessLimit = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IPrinterStatusSnapshotReader _statusReader = statusReader ?? throw new ArgumentNullException(nameof(statusReader));
    private readonly IDbOutboxSequenceAllocator _sequenceAllocator = sequenceAllocator ?? throw new ArgumentNullException(nameof(sequenceAllocator));
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
            .Include(p => p.Toolheads)
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

        // =====================================================================
        // Caller-supplied revision preconditions (If-Match). Both inputs are wired:
        // a stale token is a 412 precondition failure, never a silent overwrite.
        // =====================================================================
        if (request.ExpectedJobRowVersion is { Length: > 0 } &&
            !request.ExpectedJobRowVersion.SequenceEqual(job.RowVersion ?? []))
        {
            return DispatchClaimResult.PreconditionFailed(
                "job_revision_conflict",
                "The job has changed since the request was prepared. Re-fetch the job ETag and retry.");
        }

        if (request.ExpectedDispatchStateRowVersion is { Length: > 0 } &&
            !request.ExpectedDispatchStateRowVersion.SequenceEqual(dispatchState.RowVersion ?? []))
        {
            return DispatchClaimResult.PreconditionFailed(
                "dispatch_revision_conflict",
                "The printer dispatch state has changed since the request was prepared. Re-fetch and retry.");
        }

        DispatchClaimResult? printerGate = EvaluatePrinterGates(printer, dispatchState, request.PrinterId, request.JobId);
        if (printerGate is not null)
        {
            await WriteDeniedAuditAsync(request, printerGate, job, dispatchState, ct);
            return printerGate;
        }

        if (job.Status is not (PrintJobStatus.Queued or PrintJobStatus.Assigned))
        {
            DispatchClaimResult notDispatchable = DispatchClaimResult.Fail(
                "job_not_dispatchable",
                $"Job {request.JobId} is in state {job.Status}, which cannot be dispatched.");
            await WriteDeniedAuditAsync(request, notDispatchable, job, dispatchState, ct);
            return notDispatchable;
        }

        if (job.AssignedPrinterId != request.PrinterId)
        {
            DispatchClaimResult mismatch = DispatchClaimResult.Fail(
                "printer_mismatch",
                $"Job {request.JobId} is assigned to printer {job.AssignedPrinterId}, not {request.PrinterId}.");
            await WriteDeniedAuditAsync(request, mismatch, job, dispatchState, ct);
            return mismatch;
        }

        DispatchClaimResult? artifactGate = EvaluateArtifactGates(job, request.JobId);
        if (artifactGate is not null)
        {
            await WriteDeniedAuditAsync(request, artifactGate, job, dispatchState, ct);
            return artifactGate;
        }

        // --- Telemetry freshness and online/idle check ---
        PrinterStatusSnapshot? snapshot = _statusReader.GetStatusSnapshot(request.PrinterId);
        DispatchClaimResult? telemetryGate = EvaluateTelemetryGates(job, snapshot, request.PrinterId);
        if (telemetryGate is not null)
        {
            await WriteDeniedAuditAsync(request, telemetryGate, job, dispatchState, ct);
            return telemetryGate;
        }

        // --- Calibration-specific compatibility checks ---
        // Evaluated BEFORE the physical hardware/filament gates so an incompletely
        // specified job reports the precise definition defect rather than a downstream
        // symptom of that incompleteness.
        if (job.JobKind == JobKind.FilamentCalibration)
        {
            DispatchClaimResult? calibrationGate =
                DispatchSafetyGates.EvaluateCalibrationCompatibility(job, printer) ??
                EvaluateAcknowledgementGates(job, dispatchState, request);

            if (calibrationGate is not null)
            {
                await WriteDeniedAuditAsync(request, calibrationGate, job, dispatchState, ct);
                return calibrationGate;
            }
        }

        // --- Hard capability, nozzle/model/build and filament gates (all job kinds) ---
        DispatchClaimResult? hardwareGate = DispatchSafetyGates.EvaluateHardware(job, printer);
        if (hardwareGate is not null)
        {
            await WriteDeniedAuditAsync(request, hardwareGate, job, dispatchState, ct);
            return hardwareGate;
        }

        DispatchClaimResult? filamentGate = DispatchSafetyGates.EvaluateFilament(job, printer);
        if (filamentGate is not null)
        {
            await WriteDeniedAuditAsync(request, filamentGate, job, dispatchState, ct);
            return filamentGate;
        }

        int attemptNumber = await _db.QueueDispatchAttempts
            .Where(a => a.PrintJobId == request.JobId)
            .CountAsync(ct) + 1;

        DateTime nowUtc = DateTime.UtcNow;
        PrintJobStatus previousStatus = job.Status;

        var attempt = new QueueDispatchAttempt
        {
            Id = Guid.NewGuid(),
            PrintJobId = request.JobId,
            PrinterId = request.PrinterId,
            PrinterConfigRevision = job.PinnedPrinterConfigRevision ?? printer.ConfigurationRevision,
            AttemptNumber = attemptNumber,
            ActorSubject = request.ActorSubject,
            StartPathKind = request.StartPathKind,
            AcknowledgementIdempotencyKey = request.AcknowledgementIdempotencyKey,
            ClaimedAtUtc = nowUtc,
            Outcome = DispatchAttemptOutcome.InProgress,
            UpdatedAtUtc = nowUtc,

            // Backend identity is persisted BEFORE any network I/O so reconciliation can
            // correlate an unmatched printing backend with this attempt.
            BackendCommandId = Guid.NewGuid().ToString("N"),
            BackendFileName = job.GcodeFile?.Name,
        };

        bool consumesAcknowledgement =
            request.AcknowledgementIdempotencyKey is not null &&
            dispatchState.AcknowledgedJobId == request.JobId;

        var outboxEvent = new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = 0, // Allocated in the retry loop below.
            AggregateType = nameof(PrintJob),
            AggregateId = request.JobId,
            AggregateRowVersion = job.RowVersion,
            DispatchStateRowVersion = dispatchState.RowVersion,
            AttemptId = attempt.Id,
            BedClearState = consumesAcknowledgement ? "Consumed" : "None",
            PrinterId = request.PrinterId,
            PrinterConfigRevision = job.PinnedPrinterConfigRevision,
            EventType = "PrintFarmer.Queue.JobDispatchStarted.v1",
            SchemaVersion = "1",
            PayloadJson = BuildOutboxPayload(job, attempt),
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = nowUtc,
        };

        job.Status = PrintJobStatus.Starting;
        job.ActualStartTime = nowUtc;
        job.UpdatedAt = nowUtc;

        if (consumesAcknowledgement)
        {
            ClearAcknowledgement(dispatchState);
        }

        dispatchState.ActiveJobId = request.JobId;
        dispatchState.ActiveDispatchAttemptId = attempt.Id;

        attempt.JobRowVersionAtClaim = job.RowVersion;
        attempt.DispatchStateRowVersionAtClaim = dispatchState.RowVersion;

        _ = _db.QueueDispatchAttempts.Add(attempt);
        _ = _db.QueueDispatchOutbox.Add(outboxEvent);

        // Durable job state history — written in the SAME transaction as the claim.
        _ = _db.JobStateHistories.Add(new JobStateHistory
        {
            Id = Guid.NewGuid(),
            JobId = request.JobId,
            FromState = previousStatus.ToString(),
            ToState = PrintJobStatus.Starting.ToString(),
            TransitionedAtUtc = nowUtc,
            CreatedAt = nowUtc,
            Notes = $"Dispatch claim {attempt.Id:N} via {request.StartPathKind}",
        });

        // Durable audit — written in the SAME transaction as the claim.
        _ = QueueAuditWriter.Add(
            _db,
            request.ActorSubject,
            QueueAuditOperations.DispatchClaim,
            QueueAuditOutcomes.Success,
            nameof(PrintJob),
            resourceId: request.JobId,
            printerId: request.PrinterId,
            printJobId: request.JobId,
            dispatchAttemptId: attempt.Id,
            jobRowVersion: job.RowVersion,
            dispatchStateRowVersion: dispatchState.RowVersion,
            idempotencyKey: request.AcknowledgementIdempotencyKey,
            detail: new
            {
                startPathKind = request.StartPathKind,
                attemptNumber,
                jobKind = job.JobKind?.ToString() ?? nameof(JobKind.Standard),
                backendCommandId = attempt.BackendCommandId,
                acknowledgementConsumed = consumesAcknowledgement,
            });

        // Bounded retry: on sequence-only concurrency conflicts, reload the counter and retry.
        // Conflicts on PrintJob or PrinterDispatchState are genuine races and surface immediately.
        const int MaxSequenceRetries = 5;
        bool claimed = false;
        DbUpdateConcurrencyException? lastConflict = null;

        for (int seqRetry = 0; seqRetry < MaxSequenceRetries && !claimed; seqRetry++)
        {
            outboxEvent.Sequence = await _sequenceAllocator.AllocateAsync(_db, ct);

            try
            {
                _ = await _db.SaveChangesAsync(ct);
                claimed = true;
                lastConflict = null;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                lastConflict = ex;

                bool isSequenceConflictOnly = ex.Entries.Count > 0 &&
                    ex.Entries.All(e => e.Entity is OutboxSequenceState);

                if (!isSequenceConflictOnly || seqRetry >= MaxSequenceRetries - 1)
                {
                    break;
                }

                _logger.LogWarning(
                    ex,
                    "[Claim] Sequence contention (retry {Retry}/{Max}) for Job={JobId} Printer={PrinterId}",
                    seqRetry + 1, MaxSequenceRetries, request.JobId, request.PrinterId);

                OutboxSequenceState? seqState = _db.OutboxSequenceStates.Local.SingleOrDefault();
                if (seqState is not null)
                {
                    await _db.Entry(seqState).ReloadAsync(ct);
                }
            }
        }

        if (!claimed)
        {
            _logger.LogWarning(
                lastConflict,
                "Concurrency conflict acquiring dispatch claim for Job={JobId} Printer={PrinterId}",
                request.JobId, request.PrinterId);

            return DispatchClaimResult.Fail(
                "concurrency_conflict",
                "A concurrent operation modified the job or dispatch state. Retry with the latest ETag.");
        }

        _logger.LogInformation(
            "Dispatch claim acquired: Job={JobId} Printer={PrinterId} Attempt={AttemptId} StartPath={StartPath}",
            request.JobId, request.PrinterId, attempt.Id, request.StartPathKind);

        return DispatchClaimResult.Ok(attempt);
    }

    /// <inheritdoc />
    public async Task<DispatchClaimResult> AcquireAdHocClaimAsync(
        AdHocDispatchClaimRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
            return DispatchClaimResult.Fail(
                "printer_not_found",
                $"Printer dispatch state for {request.PrinterId} not found.");
        }

        DispatchClaimResult? printerGate = EvaluatePrinterGates(printer, dispatchState, request.PrinterId, jobId: null);
        if (printerGate is not null)
        {
            _ = QueueAuditWriter.Add(
                _db,
                request.ActorSubject,
                QueueAuditOperations.AdHocStart,
                QueueAuditOutcomes.Denied,
                nameof(Printer),
                resourceId: request.PrinterId,
                printerId: request.PrinterId,
                reasonCode: printerGate.ErrorCode,
                dispatchStateRowVersion: dispatchState.RowVersion,
                detail: new { startPathKind = request.StartPathKind });
            _ = await _db.SaveChangesAsync(ct);
            return printerGate;
        }

        // An ad-hoc start must never race a physically printing backend.
        PrinterStatusSnapshot? snapshot = _statusReader.GetStatusSnapshot(request.PrinterId);
        if (snapshot is not null)
        {
            if (!snapshot.Status.IsOnline)
            {
                return DispatchClaimResult.Fail(
                    "printer_offline",
                    $"Printer {request.PrinterId} is not online per telemetry.");
            }

            if (snapshot.Status.State is "printing" or "starting" or "paused")
            {
                return DispatchClaimResult.Fail(
                    "printer_busy_telemetry",
                    $"Printer {request.PrinterId} is in state '{snapshot.Status.State}' per telemetry; cannot start a new job.");
            }
        }

        DateTime nowUtc = DateTime.UtcNow;
        int attemptNumber = await _db.QueueDispatchAttempts
            .Where(a => a.PrinterId == request.PrinterId && a.PrintJobId == null)
            .CountAsync(ct) + 1;

        var attempt = new QueueDispatchAttempt
        {
            Id = Guid.NewGuid(),
            PrintJobId = null,
            PrinterId = request.PrinterId,
            PrinterConfigRevision = printer.ConfigurationRevision,
            AttemptNumber = attemptNumber,
            ActorSubject = request.ActorSubject,
            StartPathKind = request.StartPathKind,
            ClaimedAtUtc = nowUtc,
            Outcome = DispatchAttemptOutcome.InProgress,
            UpdatedAtUtc = nowUtc,
            BackendCommandId = Guid.NewGuid().ToString("N"),
            BackendFileName = request.BackendFileName,
            DispatchStateRowVersionAtClaim = dispatchState.RowVersion,
        };

        dispatchState.ActiveDispatchAttemptId = attempt.Id;

        _ = _db.QueueDispatchAttempts.Add(attempt);

        _ = QueueAuditWriter.Add(
            _db,
            request.ActorSubject,
            QueueAuditOperations.AdHocStart,
            QueueAuditOutcomes.Success,
            nameof(Printer),
            resourceId: request.PrinterId,
            printerId: request.PrinterId,
            dispatchAttemptId: attempt.Id,
            dispatchStateRowVersion: dispatchState.RowVersion,
            detail: new
            {
                startPathKind = request.StartPathKind,
                backendCommandId = attempt.BackendCommandId,
            });

        try
        {
            _ = await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(
                ex,
                "Concurrency conflict acquiring ad-hoc dispatch claim for Printer={PrinterId}",
                request.PrinterId);

            return DispatchClaimResult.Fail(
                "concurrency_conflict",
                "A concurrent operation modified the printer dispatch state. Retry.");
        }

        _logger.LogInformation(
            "Ad-hoc dispatch claim acquired: Printer={PrinterId} Attempt={AttemptId} StartPath={StartPath}",
            request.PrinterId, attempt.Id, request.StartPathKind);

        return DispatchClaimResult.Ok(attempt);
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

        DateTime nowUtc = DateTime.UtcNow;

        attempt.Outcome = DispatchAttemptOutcome.FailedBeforeStart;
        attempt.ErrorCode = errorCode;
        attempt.ErrorDetail = errorDetail;
        attempt.IsRetryable = true;
        attempt.UpdatedAtUtc = nowUtc;

        if (attempt.PrintJob is not null && attempt.PrintJob.Status == PrintJobStatus.Starting)
        {
            attempt.PrintJob.Status = PrintJobStatus.Assigned;
            attempt.PrintJob.ActualStartTime = null;
            attempt.PrintJob.UpdatedAt = nowUtc;

            _ = _db.JobStateHistories.Add(new JobStateHistory
            {
                Id = Guid.NewGuid(),
                JobId = attempt.PrintJob.Id,
                FromState = PrintJobStatus.Starting.ToString(),
                ToState = PrintJobStatus.Assigned.ToString(),
                TransitionedAtUtc = nowUtc,
                CreatedAt = nowUtc,
                Notes = $"Dispatch released: {errorCode}",
            });
        }

        if (dispatchState is not null && dispatchState.ActiveDispatchAttemptId == attemptId)
        {
            dispatchState.ActiveJobId = null;
            dispatchState.ActiveDispatchAttemptId = null;
        }

        _ = QueueAuditWriter.Add(
            _db,
            attempt.ActorSubject,
            QueueAuditOperations.DispatchRelease,
            QueueAuditOutcomes.Failed,
            nameof(PrintJob),
            resourceId: attempt.PrintJobId,
            printerId: attempt.PrinterId,
            printJobId: attempt.PrintJobId,
            dispatchAttemptId: attemptId,
            reasonCode: errorCode,
            jobRowVersion: attempt.PrintJob?.RowVersion,
            dispatchStateRowVersion: dispatchState?.RowVersion,
            detail: new { startPathKind = attempt.StartPathKind });

        _ = await _db.SaveChangesAsync(ct);

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

        DateTime nowUtc = DateTime.UtcNow;

        attempt.Outcome = DispatchAttemptOutcome.Accepted;
        attempt.BackendAcceptedAtUtc = nowUtc;

        // Never overwrite a persisted backend identity with null: the identity written
        // before the network call remains the reconciliation key.
        if (!string.IsNullOrWhiteSpace(backendJobId))
        {
            attempt.BackendJobId = backendJobId;
        }

        attempt.UpdatedAtUtc = nowUtc;

        if (attempt.PrintJob is not null && attempt.PrintJob.Status == PrintJobStatus.Starting)
        {
            attempt.PrintJob.Status = PrintJobStatus.Printing;
            attempt.PrintJob.UpdatedAt = nowUtc;

            _ = _db.JobStateHistories.Add(new JobStateHistory
            {
                Id = Guid.NewGuid(),
                JobId = attempt.PrintJob.Id,
                FromState = PrintJobStatus.Starting.ToString(),
                ToState = PrintJobStatus.Printing.ToString(),
                TransitionedAtUtc = nowUtc,
                CreatedAt = nowUtc,
                Notes = "Backend accepted dispatch",
            });
        }

        if (attempt.PrintJobId is null)
        {
            // Ad-hoc start completed — release the printer lease.
            PrinterDispatchState? adHocState = await _db.PrinterDispatchStates
                .FirstOrDefaultAsync(s => s.PrinterId == attempt.PrinterId, ct);

            if (adHocState is not null && adHocState.ActiveDispatchAttemptId == attemptId)
            {
                adHocState.ActiveDispatchAttemptId = null;
            }
        }

        _ = QueueAuditWriter.Add(
            _db,
            attempt.ActorSubject,
            QueueAuditOperations.DispatchAccepted,
            QueueAuditOutcomes.Success,
            attempt.PrintJobId is null ? nameof(Printer) : nameof(PrintJob),
            resourceId: attempt.PrintJobId ?? attempt.PrinterId,
            printerId: attempt.PrinterId,
            printJobId: attempt.PrintJobId,
            dispatchAttemptId: attemptId,
            jobRowVersion: attempt.PrintJob?.RowVersion,
            detail: new
            {
                startPathKind = attempt.StartPathKind,
                backendCommandId = attempt.BackendCommandId,
                hasBackendJobId = !string.IsNullOrWhiteSpace(attempt.BackendJobId),
            });

        _ = await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Backend accepted dispatch: Attempt={AttemptId} BackendJobId={BackendJobId}",
            attemptId, attempt.BackendJobId ?? "(none)");
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

        _ = QueueAuditWriter.Add(
            _db,
            attempt.ActorSubject,
            QueueAuditOperations.DispatchUnknown,
            QueueAuditOutcomes.Unknown,
            attempt.PrintJobId is null ? nameof(Printer) : nameof(PrintJob),
            resourceId: attempt.PrintJobId ?? attempt.PrinterId,
            printerId: attempt.PrinterId,
            printJobId: attempt.PrintJobId,
            dispatchAttemptId: attemptId,
            reasonCode: "backend_outcome_unknown",
            detail: new
            {
                startPathKind = attempt.StartPathKind,
                backendCommandId = attempt.BackendCommandId,
            });

        _ = await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Dispatch outcome unknown - reconciliation required: Attempt={AttemptId}",
            attemptId);
    }

    // ===== Gate evaluation helpers =====
    private static DispatchClaimResult? EvaluatePrinterGates(
        Printer printer,
        PrinterDispatchState dispatchState,
        Guid printerId,
        Guid? jobId)
    {
        if (!printer.IsEnabled)
        {
            return DispatchClaimResult.Fail("printer_disabled", $"Printer {printerId} is disabled.");
        }

        if (printer.InMaintenance)
        {
            return DispatchClaimResult.Fail("printer_in_maintenance", $"Printer {printerId} is in maintenance.");
        }

        // IsAvailable is a hard gate — the printer may be explicitly marked unavailable
        // by an operator even if it is enabled and not in formal maintenance.
        if (!printer.IsAvailable)
        {
            return DispatchClaimResult.Fail("printer_unavailable", $"Printer {printerId} is not available.");
        }

        if (dispatchState.ActiveJobId.HasValue && dispatchState.ActiveJobId != jobId)
        {
            return DispatchClaimResult.Fail(
                "printer_busy_active",
                $"Printer {printerId} already has an active job {dispatchState.ActiveJobId}.");
        }

        if (jobId is null && dispatchState.ActiveDispatchAttemptId.HasValue)
        {
            return DispatchClaimResult.Fail(
                "printer_busy_active",
                $"Printer {printerId} already has an in-flight dispatch attempt {dispatchState.ActiveDispatchAttemptId}.");
        }

        return null;
    }

    private static DispatchClaimResult? EvaluateArtifactGates(PrintJob job, Guid jobId)
    {
        if (job.GcodeFile is null)
        {
            return DispatchClaimResult.Fail(
                "gcode_missing",
                $"Job {jobId} is missing its G-code artifact.");
        }

        // Calibration jobs must print an authoritative, promoted, immutable artifact.
        if (job.JobKind == JobKind.FilamentCalibration)
        {
            if (!job.GcodeFile.IsImmutable || job.GcodeFile.PromotedAtUtc is null)
            {
                return DispatchClaimResult.Fail(
                    "gcode_not_promoted",
                    $"Job {jobId} references a G-code artifact that is not a promoted immutable calibration output.");
            }

            if (!QueueJobClassifier.IsCalibrationArtifact(job.GcodeFile))
            {
                return DispatchClaimResult.Fail(
                    "gcode_lineage_mismatch",
                    $"Job {jobId} is classified as a calibration job but its artifact carries no calibration lineage.");
            }
        }

        // FAIL-CLOSED G-code hash verification: a pinned hash with no authoritative
        // counterpart to compare against must never be treated as "verified".
        if (!string.IsNullOrWhiteSpace(job.GcodeContentSha256))
        {
            string? authoritative = !string.IsNullOrWhiteSpace(job.GcodeFile.ContentSha256)
                ? job.GcodeFile.ContentSha256
                : job.GcodeFile.FileHash;

            if (string.IsNullOrWhiteSpace(authoritative))
            {
                string unverifiableDetail =
                    $"Job {jobId}: the G-code artifact has no authoritative content hash to verify " +
                    "the pinned GcodeContentSha256 against. Dispatch fails closed.";
                return DispatchClaimResult.Fail("gcode_hash_unverifiable", unverifiableDetail);
            }

            if (!string.Equals(job.GcodeContentSha256, authoritative, StringComparison.OrdinalIgnoreCase))
            {
                string mismatchDetail =
                    $"Job {jobId}: GcodeContentSha256 does not match the authoritative G-code file hash. " +
                    "The artifact may have been replaced — a new job and idempotency key are required.";
                return DispatchClaimResult.Fail("gcode_hash_mismatch", mismatchDetail);
            }
        }
        else if (job.JobKind == JobKind.FilamentCalibration)
        {
            return DispatchClaimResult.Fail(
                "gcode_hash_missing",
                $"Job {jobId}: calibration dispatch requires a pinned GcodeContentSha256.");
        }

        return null;
    }

    private static DispatchClaimResult? EvaluateTelemetryGates(
        PrintJob job,
        PrinterStatusSnapshot? snapshot,
        Guid printerId)
    {
        bool isCalibration = job.JobKind == JobKind.FilamentCalibration;

        // Calibration FAILS CLOSED: fresh telemetry is mandatory, no snapshot is a hard stop.
        if (snapshot is null)
        {
            return isCalibration
                ? DispatchClaimResult.Fail(
                    "telemetry_unavailable",
                    $"Fresh telemetry is required for calibration dispatch. No snapshot is available for printer {printerId}.")
                : null;
        }

        // Capability-advertised freshness: once a backend HAS advertised telemetry (a snapshot
        // exists), a stale observation is never accepted for any job kind — a printer that has
        // stopped reporting must not be treated as idle.
        DateTime? observedAt = snapshot.ObservedAtUtc ?? snapshot.LastSeenAtUtc;
        bool isFresh = observedAt.HasValue &&
                       (DateTime.UtcNow - observedAt.Value) <= TelemetryFreshnessLimit;

        if (!isFresh)
        {
            string staleDetail =
                $"Printer telemetry is older than {TelemetryFreshnessLimit.TotalMinutes:F0} minutes. " +
                "Dispatch requires a fresh online+idle observation.";
            return DispatchClaimResult.Fail("telemetry_stale", staleDetail);
        }

        if (!snapshot.Status.IsOnline)
        {
            return DispatchClaimResult.Fail(
                "printer_offline",
                $"Printer {printerId} is not online per telemetry.");
        }

        if (snapshot.Status.State is "printing" or "starting" or "paused")
        {
            return DispatchClaimResult.Fail(
                "printer_busy_telemetry",
                $"Printer {printerId} is in state '{snapshot.Status.State}' per telemetry; cannot start a new job.");
        }

        return null;
    }

    private static DispatchClaimResult? EvaluateAcknowledgementGates(
        PrintJob job,
        PrinterDispatchState dispatchState,
        DispatchClaimRequest request)
    {
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
            const string MissingAckDetail =
                "No persisted bed-clear acknowledgement found for this printer. " +
                "The operator must acknowledge bed-clear before calibration dispatch.";
            return DispatchClaimResult.Fail("acknowledgement_missing", MissingAckDetail);
        }

        if (dispatchState.AcknowledgedJobId != job.Id)
        {
            return DispatchClaimResult.Fail(
                "wrong_acknowledgement_job",
                $"Acknowledgement was for job {dispatchState.AcknowledgedJobId}, not {job.Id}.");
        }

        if (dispatchState.AcknowledgementExpiresAtUtc.HasValue &&
            dispatchState.AcknowledgementExpiresAtUtc < DateTime.UtcNow)
        {
            return DispatchClaimResult.Fail(
                "acknowledgement_expired",
                $"Bed-clear acknowledgement for job {job.Id} has expired.");
        }

        if (dispatchState.AcknowledgementIdempotencyKey != request.AcknowledgementIdempotencyKey)
        {
            return DispatchClaimResult.Fail(
                "acknowledgement_key_mismatch",
                "Acknowledgement idempotency key does not match the persisted value.");
        }

        return null;
    }

    private static void ClearAcknowledgement(PrinterDispatchState dispatchState)
    {
        dispatchState.AcknowledgedJobId = null;
        dispatchState.AcknowledgedAtUtc = null;
        dispatchState.AcknowledgedBySubject = null;
        dispatchState.AcknowledgementIdempotencyKey = null;
        dispatchState.AcknowledgementExpiresAtUtc = null;
    }

    private async Task WriteDeniedAuditAsync(
        DispatchClaimRequest request,
        DispatchClaimResult denial,
        PrintJob job,
        PrinterDispatchState dispatchState,
        CancellationToken ct)
    {
        _ = QueueAuditWriter.Add(
            _db,
            request.ActorSubject,
            QueueAuditOperations.DispatchClaim,
            QueueAuditOutcomes.Denied,
            nameof(PrintJob),
            resourceId: request.JobId,
            printerId: request.PrinterId,
            printJobId: request.JobId,
            reasonCode: denial.ErrorCode,
            jobRowVersion: job.RowVersion,
            dispatchStateRowVersion: dispatchState.RowVersion,
            idempotencyKey: request.AcknowledgementIdempotencyKey,
            detail: new
            {
                startPathKind = request.StartPathKind,
                jobKind = job.JobKind?.ToString() ?? nameof(JobKind.Standard),
            });

        try
        {
            _ = await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Audit must never mask the original denial reason.
            _logger.LogWarning(ex, "[Claim] Failed to persist denial audit for Job={JobId}", request.JobId);
        }
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
            backendCommandId = attempt.BackendCommandId,
            calibrationProjectId = job.CalibrationProjectId,
            calibrationAttemptId = job.CalibrationAttemptId,
            claimedAtUtc = attempt.ClaimedAtUtc,
        });
}

using System.Security.Cryptography;
using System.Text;
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
    ILogger<DispatchClaimService> logger,
    IPrinterTelemetryFreshnessPolicy telemetryFreshnessPolicy,
    IStoredGcodeIntegrityVerifier? integrityVerifier = null,
    IQueueResourceAuthorizationService? resourceAuthorization = null) : IDispatchClaimService
{
    private static readonly HashSet<string> ExplicitIdleStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "idle",
        "ready",
        "standby",
        "operational",
    };

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IPrinterStatusSnapshotReader _statusReader = statusReader ?? throw new ArgumentNullException(nameof(statusReader));
    private readonly IDbOutboxSequenceAllocator _sequenceAllocator = sequenceAllocator ?? throw new ArgumentNullException(nameof(sequenceAllocator));
    private readonly ILogger<DispatchClaimService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IPrinterTelemetryFreshnessPolicy _telemetryFreshnessPolicy =
        telemetryFreshnessPolicy ??
        throw new ArgumentNullException(nameof(telemetryFreshnessPolicy));

    private readonly IStoredGcodeIntegrityVerifier? _integrityVerifier = integrityVerifier;
    private readonly IQueueResourceAuthorizationService? _resourceAuthorization =
        resourceAuthorization;

    /// <inheritdoc />
    public async Task<DispatchClaimResult> AcquireClaimAsync(
        DispatchClaimRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_resourceAuthorization is not null &&
            (!await _resourceAuthorization.CanActorAccessJobAsync(
                 request.ActorSubject,
                 request.JobId,
                 PrinterGroupAccessLevel.Submit,
                 ct) ||
             !await _resourceAuthorization.CanActorAccessPrinterAsync(
                 request.ActorSubject,
                 request.PrinterId,
                 PrinterGroupAccessLevel.Submit,
                 ct)))
        {
            return DispatchClaimResult.Fail(
                "job_not_found",
                "The queue job was not found.");
        }

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

        DispatchClaimResult? printerGate = EvaluatePrinterGates(printer, dispatchState, request.PrinterId);
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

        bool hasDatabaseActiveJob = await _db.PrintJobs
            .AsNoTracking()
            .AnyAsync(
                candidate =>
                    candidate.AssignedPrinterId == request.PrinterId &&
                    candidate.Id != request.JobId &&
                    (candidate.Status == PrintJobStatus.Starting ||
                     candidate.Status == PrintJobStatus.Printing ||
                     candidate.Status == PrintJobStatus.Paused),
                ct);
        if (hasDatabaseActiveJob)
        {
            DispatchClaimResult busy = DispatchClaimResult.Fail(
                "printer_busy_database",
                $"Printer {request.PrinterId} has another Starting, Printing, or Paused job in the database.");
            await WriteDeniedAuditAsync(request, busy, job, dispatchState, ct);
            return busy;
        }

        DispatchClaimResult? artifactGate = EvaluateArtifactGates(job, request.JobId);
        if (artifactGate is not null)
        {
            await WriteDeniedAuditAsync(request, artifactGate, job, dispatchState, ct);
            return artifactGate;
        }

        if (job.JobKind == JobKind.FilamentCalibration)
        {
            if (_integrityVerifier is null)
            {
                DispatchClaimResult unavailable = DispatchClaimResult.Fail(
                    "gcode_byte_verifier_unavailable",
                    "Stored-byte integrity verification is unavailable; calibration dispatch fails closed.");
                await WriteDeniedAuditAsync(request, unavailable, job, dispatchState, ct);
                return unavailable;
            }

            StoredGcodeIntegrityResult integrity = await _integrityVerifier.VerifyAsync(
                job.GcodeFile!,
                job.GcodeContentSha256!,
                job.PinnedGcodeFileSizeBytes,
                ct);
            if (!integrity.Success)
            {
                DispatchClaimResult tampered = DispatchClaimResult.Fail(
                    integrity.ErrorCode ?? "gcode_byte_hash_mismatch",
                    integrity.ErrorDetail ?? "Stored G-code byte integrity verification failed.");
                await WriteDeniedAuditAsync(request, tampered, job, dispatchState, ct);
                return tampered;
            }
        }

        // --- Telemetry freshness and online/idle check ---
        PrinterStatusSnapshot? snapshot = _statusReader.GetStatusSnapshot(request.PrinterId);
        if (!_telemetryFreshnessPolicy.TryGetMaximumObservationAge(
                printer.Backend,
                out TimeSpan telemetryFreshnessLimit))
        {
            return DispatchClaimResult.Fail(
                "telemetry_sla_unavailable",
                "The printer backend does not advertise a telemetry freshness SLA.");
        }

        DispatchClaimResult? telemetryGate = EvaluateTelemetryGates(
            snapshot,
            request.PrinterId,
            telemetryFreshnessLimit);
        if (telemetryGate is not null)
        {
            await WriteDeniedAuditAsync(request, telemetryGate, job, dispatchState, ct);
            return telemetryGate;
        }

        // --- Calibration-specific compatibility checks ---
        // Evaluated BEFORE the physical hardware/filament gates so an incompletely
        // specified job reports the precise definition defect rather than a downstream
        // symptom of that incompleteness.
        BedClearCommandRecord? bedClearCommand = null;
        QueueDispatchOutbox? backendStartCommand = null;
        if (job.JobKind == JobKind.FilamentCalibration)
        {
            (DispatchClaimResult? acknowledgementFailure, BedClearCommandRecord? command) =
                await EvaluateAcknowledgementGatesAsync(
                    job,
                    printer,
                    dispatchState,
                    request,
                    ct);
            bedClearCommand = command;
            DispatchClaimResult? calibrationGate =
                DispatchSafetyGates.EvaluateCalibrationCompatibility(job, printer);
            calibrationGate ??= acknowledgementFailure;
            if (calibrationGate is null)
            {
                calibrationGate =
                    await EvaluatePersistedCalibrationInputsAsync(_db, job, printer, ct);
            }

            if (calibrationGate is not null)
            {
                await WriteDeniedAuditAsync(request, calibrationGate, job, dispatchState, ct);
                return calibrationGate;
            }

            if (bedClearCommand is not null)
            {
                backendStartCommand = await _db.QueueDispatchOutbox
                    .SingleOrDefaultAsync(
                        command =>
                            command.Id == bedClearCommand.OutboxEventId &&
                            command.EventType ==
                                BedClearAcknowledgementService.BackendStartCommandEventType,
                        ct);
                if (backendStartCommand is null)
                {
                    DispatchClaimResult missingCommand = DispatchClaimResult.Fail(
                        "bed_clear_command_invalid",
                        "The durable bed-clear start command is missing.");
                    await WriteDeniedAuditAsync(
                        request,
                        missingCommand,
                        job,
                        dispatchState,
                        ct);
                    return missingCommand;
                }
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

        if (job.JobKind == JobKind.FilamentCalibration)
        {
            DispatchClaimResult? pinnedSpoolGate = await EvaluatePinnedSpoolAsync(job, printer, ct);
            if (pinnedSpoolGate is not null)
            {
                await WriteDeniedAuditAsync(request, pinnedSpoolGate, job, dispatchState, ct);
                return pinnedSpoolGate;
            }
        }

        if (job.BlockedReasonCode == JobBlockedReasonCode.FilamentCheckFailed)
        {
            job.BlockedReasonCode = null;
            job.BlockedReasonJson = null;
        }

        int attemptNumber = await _db.QueueDispatchAttempts
            .Where(a => a.PrintJobId == request.JobId)
            .CountAsync(ct) + 1;

        DateTime nowUtc = DateTime.UtcNow;
        PrintJobStatus previousStatus = job.Status;

        Guid attemptId = Guid.NewGuid();
        var attempt = new QueueDispatchAttempt
        {
            Id = attemptId,
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
            BackendCommandId = $"pf-{attemptId:N}",
            BackendFileName = BuildBackendFileName(attemptId, job.GcodeFile?.Name),
            BackendFileIdentity = BuildBackendFileName(attemptId, job.GcodeFile?.Name),
            BackendCorrelationId = $"pf-{attemptId:N}",
            BackendCallPhase = DispatchBackendCallPhase.PreCall,
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
            AttemptNumber = attempt.AttemptNumber,
            AttemptOutcome = attempt.Outcome.ToString(),
            BedClearState = consumesAcknowledgement ? "Consumed" : "None",
            BedClearCommandId = bedClearCommand?.Id,
            BedClearExpiresAtUtc = bedClearCommand?.ExpiresAtUtc,
            PrinterId = request.PrinterId,
            ProjectId = job.CalibrationProjectId ?? job.ProjectId,
            CalibrationAttemptId = job.CalibrationAttemptId,
            JobStatus = PrintJobStatus.Starting.ToString(),
            JobKind = job.JobKind?.ToString() ?? nameof(JobKind.Standard),
            PrinterConfigRevision = job.PinnedPrinterConfigRevision,
            EventType = "PrintFarmer.Queue.JobDispatchStarted.v1",
            SchemaVersion = QueueEventSchemaVersions.Current,
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
        if (bedClearCommand is not null)
        {
            bedClearCommand.Status = BedClearCommandStatus.Claimed;
            bedClearCommand.DispatchAttemptId = attempt.Id;
            bedClearCommand.UpdatedAtUtc = nowUtc;
            backendStartCommand!.AttemptId = attempt.Id;
        }

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

        try
        {
            await using QueueOutboxTransactionScope transaction =
                await QueueOutboxTransactionScope.BeginAsync(_db, ct);
            outboxEvent.Sequence = await _sequenceAllocator.AllocateAsync(_db, ct);
            if (consumesAcknowledgement && bedClearCommand is not null)
            {
                await AddLifecycleOutboxEventAsync(
                    _db,
                    _sequenceAllocator,
                    QueueLifecycleEventWriter.EventTypeBedClearConsumed,
                    aggregateId: request.JobId,
                    printerId: request.PrinterId,
                    attemptId: attempt.Id,
                    aggregateRowVersion: job.RowVersion,
                    failureCode: null,
                    payloadJson: System.Text.Json.JsonSerializer.Serialize(new
                    {
                        jobId = request.JobId,
                        printerId = request.PrinterId,
                        attemptId = attempt.Id,
                        bedClearCommandId = bedClearCommand.Id,
                        bedClearState = "Consumed",
                    }),
                    ct,
                    bedClearState: "Consumed",
                    bedClearCommandId: bedClearCommand.Id,
                    bedClearExpiresAtUtc: bedClearCommand.ExpiresAtUtc);
            }

            _ = await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            _logger.LogWarning(
                ex,
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

        if (_resourceAuthorization is not null &&
            !await _resourceAuthorization.CanActorAccessPrinterAsync(
                request.ActorSubject,
                request.PrinterId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            return DispatchClaimResult.Fail(
                "printer_not_found",
                "The printer was not found.");
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
            return DispatchClaimResult.Fail(
                "printer_not_found",
                $"Printer dispatch state for {request.PrinterId} not found.");
        }

        DispatchClaimResult? printerGate = EvaluatePrinterGates(printer, dispatchState, request.PrinterId);
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

        bool hasDatabaseActiveJob = await _db.PrintJobs
            .AsNoTracking()
            .AnyAsync(
                candidate =>
                    candidate.AssignedPrinterId == request.PrinterId &&
                    (candidate.Status == PrintJobStatus.Starting ||
                     candidate.Status == PrintJobStatus.Printing ||
                     candidate.Status == PrintJobStatus.Paused),
                ct);
        if (hasDatabaseActiveJob)
        {
            return DispatchClaimResult.Fail(
                "printer_busy_database",
                $"Printer {request.PrinterId} has a Starting, Printing, or Paused job in the database.");
        }

        // An ad-hoc start applies the same fail-closed telemetry gate as queue claims:
        // missing or stale telemetry is a hard stop (never permitted to pass on absence of data).
        if (!_telemetryFreshnessPolicy.TryGetMaximumObservationAge(
                printer.Backend,
                out TimeSpan telemetryFreshnessLimit))
        {
            return DispatchClaimResult.Fail(
                "telemetry_sla_unavailable",
                "The printer backend does not advertise a telemetry freshness SLA.");
        }

        PrinterStatusSnapshot? snapshot = _statusReader.GetStatusSnapshot(request.PrinterId);
        if (snapshot is null)
        {
            return DispatchClaimResult.Fail(
                "telemetry_unavailable",
                $"Fresh telemetry is required for ad-hoc dispatch. No snapshot is available for printer {request.PrinterId}.");
        }

        DateTime? observedAt = snapshot.ObservedAtUtc ?? snapshot.LastSeenAtUtc;
        bool isFresh = observedAt.HasValue &&
                       (DateTime.UtcNow - observedAt.Value) <= telemetryFreshnessLimit;
        if (!isFresh)
        {
            string staleMsg =
                $"Printer telemetry exceeds the backend SLA of {telemetryFreshnessLimit.TotalSeconds:F0} seconds. " +
                "Ad-hoc dispatch requires a fresh online+idle observation.";
            return DispatchClaimResult.Fail("telemetry_stale", staleMsg);
        }

        if (!snapshot.Status.IsOnline)
        {
            return DispatchClaimResult.Fail(
                "printer_offline",
                $"Printer {request.PrinterId} is not online per telemetry.");
        }

        if (!IsExplicitlyIdle(snapshot.Status.State))
        {
            string busyDetail =
                $"Printer {request.PrinterId} is not in an explicitly idle state per telemetry " +
                $"(observed '{snapshot.Status.State ?? "unknown"}').";
            return DispatchClaimResult.Fail(
                "printer_busy_telemetry",
                busyDetail);
        }

        DateTime nowUtc = DateTime.UtcNow;
        int attemptNumber = await _db.QueueDispatchAttempts
            .Where(a => a.PrinterId == request.PrinterId && a.PrintJobId == null)
            .CountAsync(ct) + 1;

        Guid attemptId = Guid.NewGuid();
        var attempt = new QueueDispatchAttempt
        {
            Id = attemptId,
            PrintJobId = null,
            PrinterId = request.PrinterId,
            PrinterConfigRevision = printer.ConfigurationRevision,
            AttemptNumber = attemptNumber,
            ActorSubject = request.ActorSubject,
            StartPathKind = request.StartPathKind,
            ClaimedAtUtc = nowUtc,
            Outcome = DispatchAttemptOutcome.InProgress,
            UpdatedAtUtc = nowUtc,
            BackendCommandId = $"pf-{attemptId:N}",
            BackendFileName = request.UseDeterministicFileName
                ? BuildBackendFileName(attemptId, request.BackendFileName)
                : request.BackendFileName,
            BackendFileIdentity = request.UseDeterministicFileName
                ? BuildBackendFileName(attemptId, request.BackendFileName)
                : request.BackendFileName,
            BackendCorrelationId = $"pf-{attemptId:N}",
            BackendCallPhase = DispatchBackendCallPhase.PreCall,
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
    public async Task<bool> RecordBackendCallStartedAsync(
        Guid attemptId,
        CancellationToken ct = default)
    {
        QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
            .FirstOrDefaultAsync(candidate => candidate.Id == attemptId, ct);
        if (attempt is null)
        {
            _logger.LogWarning(
                "Ignoring backend-call start for missing attempt {AttemptId}.",
                attemptId);
            return false;
        }

        if (attempt.BackendCallPhase != DispatchBackendCallPhase.PreCall)
        {
            _logger.LogWarning(
                "Ignoring duplicate backend-call start for attempt {AttemptId} in phase {Phase}.",
                attemptId,
                attempt.BackendCallPhase);
            return false;
        }

        PrinterDispatchState? activeState = await LoadActiveAttemptStateAsync(attempt, ct);
        if (activeState is null)
        {
            _logger.LogWarning(
                "Ignoring backend-call start for inactive attempt {AttemptId}.",
                attemptId);
            return false;
        }

        attempt.BackendCallPhase = DispatchBackendCallPhase.BackendCall;
        attempt.BackendCallStartedAtUtc = DateTime.UtcNow;
        attempt.UpdatedAtUtc = DateTime.UtcNow;
        activeState.PhysicalControlCommandId = attempt.Id;
        activeState.PhysicalControlAttemptId = attempt.Id;
        activeState.PhysicalControlOperation = "start";
        activeState.PhysicalControlActorSubject = attempt.ActorSubject;
        activeState.PhysicalControlStartedAtUtc = attempt.BackendCallStartedAtUtc;
        activeState.PhysicalControlRequiresReconciliation = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseClaimOnKnownFailureAsync(
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
            return false;
        }

        if (attempt.Outcome == DispatchAttemptOutcome.Accepted ||
            attempt.BackendCallPhase is DispatchBackendCallPhase.Accepted or
                DispatchBackendCallPhase.PostAccept)
        {
            _logger.LogWarning(
                "Ignoring known failure after acceptance for attempt {AttemptId}.",
                attemptId);
            return false;
        }

        PrinterDispatchState? dispatchState = await LoadActiveAttemptStateAsync(attempt, ct);
        if (dispatchState is null)
        {
            _logger.LogWarning(
                "Ignoring late known-failure outcome for inactive attempt {AttemptId}.",
                attemptId);
            return false;
        }

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);
        DateTime nowUtc = DateTime.UtcNow;

        attempt.Outcome = DispatchAttemptOutcome.FailedBeforeStart;
        attempt.ErrorCode = errorCode;
        attempt.ErrorDetail = RedactedFailureDetail(errorCode);
        attempt.IsRetryable = true;
        attempt.BackendCallPhase = DispatchBackendCallPhase.Terminal;
        attempt.TerminalAtUtc = nowUtc;
        attempt.UpdatedAtUtc = nowUtc;
        BedClearCommandRecord? failedCommand = await _db.BedClearCommandRecords
            .FirstOrDefaultAsync(record => record.DispatchAttemptId == attemptId, ct);
        if (failedCommand is not null)
        {
            failedCommand.Status = BedClearCommandStatus.Rejected;
            failedCommand.UpdatedAtUtc = nowUtc;
        }

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
            ClearPhysicalBarrier(dispatchState, attemptId);
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

        // Emit a durable lifecycle event so the outbox publisher broadcasts the failure to
        // authorized groups. The event is committed in the SAME SaveChangesAsync call as
        // the dispatch state changes above.
        if (attempt.PrintJobId is not null)
        {
            await AddLifecycleOutboxEventAsync(
                _db,
                _sequenceAllocator,
                EventTypeKnownFailure,
                aggregateId: attempt.PrintJobId.Value,
                printerId: attempt.PrinterId,
                attemptId: attemptId,
                aggregateRowVersion: attempt.PrintJob?.RowVersion,
                failureCode: errorCode,
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    jobId = attempt.PrintJobId,
                    printerId = attempt.PrinterId,
                    attemptId,
                    errorCode,
                    startPathKind = attempt.StartPathKind,
                }),
                ct);
        }

        _ = await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Dispatch claim released (known failure): Attempt={AttemptId} Code={ErrorCode}",
            attemptId, errorCode);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> RecordBackendAcceptedAsync(
        Guid attemptId,
        string? backendJobId,
        CancellationToken ct = default) =>
        RecordBackendAcceptedAsync(attemptId, backendJobId, null, ct);

    /// <inheritdoc />
    public async Task<bool> RecordBackendAcceptedAsync(
        Guid attemptId,
        string? backendJobId,
        string? backendFileIdentity,
        CancellationToken ct = default)
    {
        QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
            .Include(a => a.PrintJob)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);

        if (attempt is null)
        {
            _logger.LogWarning("RecordBackendAccepted: Attempt {AttemptId} not found.", attemptId);
            return false;
        }

        PrinterDispatchState? dispatchState = await LoadActiveAttemptStateAsync(attempt, ct);
        if (dispatchState is null)
        {
            _logger.LogWarning(
                "Ignoring late backend acceptance for inactive attempt {AttemptId}.",
                attemptId);
            return false;
        }

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);
        DateTime nowUtc = DateTime.UtcNow;

        attempt.Outcome = DispatchAttemptOutcome.Accepted;
        attempt.BackendAcceptedAtUtc = nowUtc;
        attempt.BackendCallPhase = DispatchBackendCallPhase.Accepted;
        attempt.BackendResponseAtUtc = nowUtc;

        // Never overwrite a persisted backend identity with null: the identity written
        // before the network call remains the reconciliation key.
        if (!string.IsNullOrWhiteSpace(backendJobId))
        {
            attempt.BackendJobId = backendJobId;
        }

        if (!string.IsNullOrWhiteSpace(backendFileIdentity))
        {
            attempt.BackendFileIdentity = backendFileIdentity;
        }

        attempt.UpdatedAtUtc = nowUtc;
        attempt.TerminalAtUtc = nowUtc;
        BedClearCommandRecord? acceptedCommand = await _db.BedClearCommandRecords
            .FirstOrDefaultAsync(record => record.DispatchAttemptId == attemptId, ct);
        if (acceptedCommand is not null)
        {
            acceptedCommand.Status = BedClearCommandStatus.Accepted;
            acceptedCommand.UpdatedAtUtc = nowUtc;
        }

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

        ClearPhysicalBarrier(dispatchState, attemptId);

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

        // Emit a durable lifecycle event for backend acceptance so the outbox publisher
        // broadcasts the Printing state transition to authorized groups.
        if (attempt.PrintJobId is not null)
        {
            await AddLifecycleOutboxEventAsync(
                _db,
                _sequenceAllocator,
                EventTypeBackendAccepted,
                aggregateId: attempt.PrintJobId.Value,
                printerId: attempt.PrinterId,
                attemptId: attemptId,
                aggregateRowVersion: attempt.PrintJob?.RowVersion,
                failureCode: null,
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    jobId = attempt.PrintJobId,
                    printerId = attempt.PrinterId,
                    attemptId,
                    backendJobId = attempt.BackendJobId,
                    backendCommandId = attempt.BackendCommandId,
                    startPathKind = attempt.StartPathKind,
                    backendAcceptedAtUtc = attempt.BackendAcceptedAtUtc,
                }),
                ct);
        }

        _ = await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Backend accepted dispatch: Attempt={AttemptId} BackendJobId={BackendJobId}",
            attemptId, attempt.BackendJobId ?? "(none)");
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RecordUnknownOutcomeAsync(
        Guid attemptId,
        string errorDetail,
        CancellationToken ct = default)
    {
        QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);

        if (attempt is null)
        {
            _logger.LogWarning("RecordUnknownOutcome: Attempt {AttemptId} not found.", attemptId);
            return false;
        }

        if (attempt.Outcome == DispatchAttemptOutcome.Accepted ||
            attempt.BackendCallPhase is DispatchBackendCallPhase.Accepted or
                DispatchBackendCallPhase.PostAccept)
        {
            _logger.LogWarning(
                "Ignoring unknown outcome after acceptance for attempt {AttemptId}.",
                attemptId);
            return false;
        }

        PrinterDispatchState? dispatchState = await LoadActiveAttemptStateAsync(attempt, ct);
        if (dispatchState is null)
        {
            _logger.LogWarning(
                "Ignoring late unknown outcome for inactive attempt {AttemptId}.",
                attemptId);
            return false;
        }

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);
        attempt.Outcome = DispatchAttemptOutcome.Unknown;
        attempt.ErrorDetail = RedactedFailureDetail("backend_outcome_unknown");
        attempt.IsRetryable = false;
        attempt.RequiresReconciliation = true;
        attempt.BackendCallPhase = DispatchBackendCallPhase.AwaitingReconciliation;
        attempt.UpdatedAtUtc = DateTime.UtcNow;
        if (dispatchState.PhysicalControlCommandId == attemptId)
        {
            dispatchState.PhysicalControlRequiresReconciliation = true;
        }

        BedClearCommandRecord? unknownCommand = await _db.BedClearCommandRecords
            .FirstOrDefaultAsync(record => record.DispatchAttemptId == attemptId, ct);
        if (unknownCommand is not null)
        {
            unknownCommand.Status = BedClearCommandStatus.Unknown;
            unknownCommand.UpdatedAtUtc = DateTime.UtcNow;
        }

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

        // Emit a durable lifecycle event for the uncertain outcome so operators can detect
        // that reconciliation is required via the event stream.
        if (attempt.PrintJobId is not null)
        {
            await AddLifecycleOutboxEventAsync(
                _db,
                _sequenceAllocator,
                EventTypeUnknownOutcome,
                aggregateId: attempt.PrintJobId.Value,
                printerId: attempt.PrinterId,
                attemptId: attemptId,
                aggregateRowVersion: null,
                failureCode: "backend_outcome_unknown",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    jobId = attempt.PrintJobId,
                    printerId = attempt.PrinterId,
                    attemptId,
                    backendCommandId = attempt.BackendCommandId,
                    startPathKind = attempt.StartPathKind,
                    requiresReconciliation = true,
                }),
                ct);
        }

        _ = await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogWarning(
            "Dispatch outcome unknown - reconciliation required: Attempt={AttemptId}",
            attemptId);
        return true;
    }

    /// <inheritdoc />
    public async Task<DispatchExceptionDisposition> RecordDispatchExceptionAsync(
        Guid attemptId,
        string failureCode,
        CancellationToken ct = default)
    {
        QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == attemptId, ct);
        if (attempt is null)
        {
            return DispatchExceptionDisposition.Superseded;
        }

        if (attempt.Outcome == DispatchAttemptOutcome.Accepted ||
            attempt.BackendCallPhase is DispatchBackendCallPhase.Accepted or
                DispatchBackendCallPhase.PostAccept)
        {
            return DispatchExceptionDisposition.Accepted;
        }

        if (attempt.BackendCallPhase == DispatchBackendCallPhase.PreCall)
        {
            bool released = await ReleaseClaimOnKnownFailureAsync(
                attemptId,
                failureCode,
                RedactedFailureDetail(failureCode),
                ct);
            return released
                ? DispatchExceptionDisposition.ReleasedBeforeStart
                : DispatchExceptionDisposition.Superseded;
        }

        if (attempt.BackendCallPhase is DispatchBackendCallPhase.BackendCall or
            DispatchBackendCallPhase.AwaitingReconciliation)
        {
            bool recorded = await RecordUnknownOutcomeAsync(
                attemptId,
                RedactedFailureDetail("backend_outcome_unknown"),
                ct);
            return recorded
                ? DispatchExceptionDisposition.AwaitingReconciliation
                : DispatchExceptionDisposition.Superseded;
        }

        return DispatchExceptionDisposition.Superseded;
    }

    /// <inheritdoc />
    public async Task<bool> RecordPostAcceptCompletedAsync(
        Guid attemptId,
        CancellationToken ct = default)
    {
        QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
            .FirstOrDefaultAsync(candidate => candidate.Id == attemptId, ct);
        if (attempt is null ||
            attempt.Outcome != DispatchAttemptOutcome.Accepted)
        {
            return false;
        }

        if (attempt.BackendCallPhase == DispatchBackendCallPhase.PostAccept)
        {
            return true;
        }

        if (attempt.BackendCallPhase != DispatchBackendCallPhase.Accepted)
        {
            return false;
        }

        attempt.BackendCallPhase = DispatchBackendCallPhase.PostAccept;
        attempt.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string RedactedFailureDetail(string errorCode) =>
        errorCode switch
        {
            "artifact_unavailable" or
            "gcode_byte_hash_mismatch" or
            "gcode_byte_size_mismatch" =>
                "The stored G-code artifact is unavailable or failed integrity validation.",
            "backend_rejected" or
            "backend_failed_before_start" =>
                "The printer rejected the request before accepting the print.",
            "backend_outcome_unknown" =>
                "The backend outcome could not be determined; reconciliation is required.",
            _ =>
                "The dispatch failed before backend acceptance.",
        };

    // ===== Gate evaluation helpers =====
    private async Task<PrinterDispatchState?> LoadActiveAttemptStateAsync(
        QueueDispatchAttempt attempt,
        CancellationToken ct)
    {
        PrinterDispatchState? state = await _db.PrinterDispatchStates
            .FirstOrDefaultAsync(candidate => candidate.PrinterId == attempt.PrinterId, ct);
        if (state is not null)
        {
            await _db.Entry(state).ReloadAsync(ct);
        }

        Guid? expectedJobId = attempt.PrintJobId;
        if (state is null ||
            state.ActiveDispatchAttemptId != attempt.Id ||
            state.ActiveJobId != expectedJobId)
        {
            return null;
        }

        // Force a concurrency-token-bound write even when the outcome does not otherwise
        // change dispatch-state fields. A competing B claim therefore makes late A fail
        // instead of mutating B's job through a stale pre-read.
        state.Revision = Math.Max(1, state.Revision) + 1;
        return state;
    }

    private static DispatchClaimResult? EvaluatePrinterGates(
        Printer printer,
        PrinterDispatchState dispatchState,
        Guid printerId)
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

        if (dispatchState.PhysicalControlCommandId.HasValue)
        {
            return DispatchClaimResult.Fail(
                "printer_physical_control_in_flight",
                $"Printer {printerId} has an in-flight physical control barrier.");
        }

        // Mutual exclusion is strict. Idempotent retries are resolved by the caller from
        // the persisted attempt; acquiring a second attempt for the same job could still
        // produce a second physical start after a lost response.
        if (dispatchState.ActiveDispatchAttemptId.HasValue)
        {
            string ownerDesc = dispatchState.ActiveJobId.HasValue
                ? $"queue job {dispatchState.ActiveJobId}"
                : "an ad-hoc start";
            return DispatchClaimResult.Fail(
                "printer_busy_active",
                $"Printer {printerId} already has an in-flight dispatch attempt for {ownerDesc}.");
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

    internal static async Task<DispatchClaimResult?> EvaluatePersistedCalibrationInputsAsync(
        AppDbContext db,
        PrintJob job,
        Printer printer,
        CancellationToken ct)
    {
        CalibrationProject? project = await db.CalibrationProjects
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == job.CalibrationProjectId, ct);
        CalibrationAttempt? attempt = await db.CalibrationAttempts
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == job.CalibrationAttemptId, ct);
        PrinterConfigurationSnapshot? snapshot = await db.PrinterConfigurationSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == job.CalibrationConfigSnapshotId, ct);
        CalibrationOrchestration? orchestration = await db.CalibrationOrchestrations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == job.CalibrationOrchestrationId, ct);
        if (project is null || attempt is null || snapshot is null || orchestration is null)
        {
            return DispatchClaimResult.Fail(
                "calibration_record_invalid",
                "An authoritative calibration project, attempt, snapshot, or orchestration record is missing.");
        }

        bool mismatch =
            project.PrinterId != printer.Id ||
            (project.LocalSpoolId ?? project.SpoolmanSpoolId) != job.PinnedSpoolId ||
            project.CurrentPrinterConfigurationSnapshotId != snapshot.Id ||
            project.SelectedToolheadId != job.PinnedToolheadId ||
            project.SelectedToolheadIndex != job.PinnedToolheadIndex ||
            !string.Equals(project.FilamentSku, job.PinnedFilamentSku, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(project.FilamentProductName, job.FilamentName, StringComparison.Ordinal) ||
            !string.Equals(project.FilamentVendor, job.FilamentVendor, StringComparison.Ordinal) ||
            !string.Equals(project.FilamentColor, job.FilamentColor, StringComparison.Ordinal) ||
            !string.Equals(
                ComputeSha256(project.FilamentSnapshotJson),
                job.FilamentSnapshotSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(project.FilamentMaterial, job.RequiredMaterialType, StringComparison.OrdinalIgnoreCase) ||
            attempt.ProjectId != project.Id ||
            attempt.PrinterConfigurationSnapshotId != snapshot.Id ||
            !string.Equals(attempt.SpecificationSha256, job.SpecificationSha256, StringComparison.OrdinalIgnoreCase) ||
            snapshot.ProjectId != project.Id ||
            snapshot.PrinterId != printer.Id ||
            snapshot.PrinterConfigurationRevision != job.PinnedPrinterConfigRevision ||
            snapshot.FirmwareFamily != job.RequiredFirmwareFamily ||
            snapshot.GcodeDialect != job.RequiredGcodeDialect ||
            !string.Equals(snapshot.SlicerEngine, job.RequiredSlicerEngine, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.SlicerDistribution, job.RequiredSlicerDistribution, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.SlicerVersion, job.RequiredSlicerVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.SlicerContainerDigest, job.RequiredSlicerContainerDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.SnapshotSha256, job.PrinterConfigSnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.MachineProfileSha256, job.MachineProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.ProcessProfileSha256, job.ProcessProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.FilamentProfileSha256, job.FilamentProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            orchestration.ProjectId != project.Id ||
            orchestration.AttemptId != attempt.Id ||
            !string.Equals(orchestration.SpecificationSha256, job.SpecificationSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(orchestration.ManifestSha256, job.CalibrationManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(orchestration.SlicerContainerDigest, job.RequiredSlicerContainerDigest, StringComparison.OrdinalIgnoreCase) ||
            orchestration.GcodeFileId != job.GcodeFileId ||
            orchestration.SliceJobId != job.SliceJobId ||
            orchestration.FinalArtifactId != job.SourceArtifactId ||
            !string.Equals(orchestration.GcodeSha256, job.GcodeContentSha256, StringComparison.OrdinalIgnoreCase) ||
            job.GcodeFile is null ||
            job.GcodeFile.PrinterModelId != job.PinnedPrinterModelId ||
            job.GcodeFile.FileSizeBytes != job.PinnedGcodeFileSizeBytes ||
            !string.Equals(job.GcodeFile.SourceModelSha256, job.SourceModelSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.GcodeFile.CalibrationManifestSha256, job.CalibrationManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.GcodeFile.SlicerEngineName, job.RequiredSlicerEngine, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.GcodeFile.SlicerDistribution, job.RequiredSlicerDistribution, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.GcodeFile.PinnedSlicerVersion, job.RequiredSlicerVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.GcodeFile.SlicerContainerDigest, job.RequiredSlicerContainerDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.GcodeFile.SpecificationSha256, job.SpecificationSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.GcodeFile.MachineProfileSha256, job.MachineProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.GcodeFile.ProcessProfileSha256, job.ProcessProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.GcodeFile.FilamentProfileSha256, job.FilamentProfileSha256, StringComparison.OrdinalIgnoreCase);
        return mismatch
            ? DispatchClaimResult.Fail(
                "calibration_record_mismatch",
                "Authoritative calibration records no longer match the immutable queue inputs.")
            : null;
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static DispatchClaimResult? EvaluateTelemetryGates(
        PrinterStatusSnapshot? snapshot,
        Guid printerId,
        TimeSpan telemetryFreshnessLimit)
    {
        // Every physical start fails closed without authoritative telemetry.
        if (snapshot is null)
        {
            return DispatchClaimResult.Fail(
                "telemetry_unavailable",
                $"Fresh telemetry is required for dispatch. No snapshot is available for printer {printerId}.");
        }

        // Capability-advertised freshness: once a backend HAS advertised telemetry (a snapshot
        // exists), a stale observation is never accepted for any job kind — a printer that has
        // stopped reporting must not be treated as idle.
        DateTime? observedAt = snapshot.ObservedAtUtc ?? snapshot.LastSeenAtUtc;
        bool isFresh = observedAt.HasValue &&
                       (DateTime.UtcNow - observedAt.Value) <= telemetryFreshnessLimit;

        if (!isFresh)
        {
            string staleDetail =
                $"Printer telemetry exceeds the backend SLA of {telemetryFreshnessLimit.TotalSeconds:F0} seconds. " +
                "Dispatch requires a fresh online+idle observation.";
            return DispatchClaimResult.Fail("telemetry_stale", staleDetail);
        }

        if (!snapshot.Status.IsOnline)
        {
            return DispatchClaimResult.Fail(
                "printer_offline",
                $"Printer {printerId} is not online per telemetry.");
        }

        if (!IsExplicitlyIdle(snapshot.Status.State))
        {
            string busyDetail =
                $"Printer {printerId} is not in an explicitly idle state per telemetry " +
                $"(observed '{snapshot.Status.State ?? "unknown"}').";
            return DispatchClaimResult.Fail(
                "printer_busy_telemetry",
                busyDetail);
        }

        return null;
    }

    private static bool IsExplicitlyIdle(string? state) =>
        !string.IsNullOrWhiteSpace(state) && ExplicitIdleStates.Contains(state.Trim());

    private static string BuildBackendFileName(Guid attemptId, string? requestedName)
    {
        string safeName = Path.GetFileName(string.IsNullOrWhiteSpace(requestedName)
            ? "print.gcode"
            : requestedName);
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalid, '_');
        }

        string prefix = $"pf-{attemptId:N}-";
        int available = Math.Max(1, 240 - prefix.Length);
        if (safeName.Length > available)
        {
            string extension = Path.GetExtension(safeName);
            int stemLength = Math.Max(1, available - extension.Length);
            string stem = Path.GetFileNameWithoutExtension(safeName);
            if (string.IsNullOrEmpty(stem))
            {
                stem = "print";
            }

            safeName = $"{stem[..Math.Min(stem.Length, stemLength)]}{extension}";
        }

        return $"{prefix}{safeName}";
    }

    private async Task<(DispatchClaimResult? Failure, BedClearCommandRecord? Command)>
        EvaluateAcknowledgementGatesAsync(
        PrintJob job,
        Printer printer,
        PrinterDispatchState dispatchState,
        DispatchClaimRequest request,
        CancellationToken ct)
    {
        // Ack key is required in the claim request.
        if (string.IsNullOrWhiteSpace(request.AcknowledgementIdempotencyKey))
        {
            return (
                DispatchClaimResult.Fail(
                    "acknowledgement_required",
                    "Calibration jobs require a valid bed-clear acknowledgement idempotency key."),
                null);
        }

        // A persisted ack MUST exist — fail closed. An ack key in the request with no
        // persisted counterpart is a programming error or a replay attack.
        if (!dispatchState.AcknowledgedJobId.HasValue)
        {
            const string MissingAckDetail =
                "No persisted bed-clear acknowledgement found for this printer. " +
                "The operator must acknowledge bed-clear before calibration dispatch.";
            return (DispatchClaimResult.Fail("acknowledgement_missing", MissingAckDetail), null);
        }

        if (dispatchState.AcknowledgedJobId != job.Id)
        {
            return (
                DispatchClaimResult.Fail(
                    "wrong_acknowledgement_job",
                    $"Acknowledgement was for job {dispatchState.AcknowledgedJobId}, not {job.Id}."),
                null);
        }

        if (dispatchState.AcknowledgementExpiresAtUtc.HasValue &&
            dispatchState.AcknowledgementExpiresAtUtc < DateTime.UtcNow)
        {
            return (
                DispatchClaimResult.Fail(
                    "acknowledgement_expired",
                    $"Bed-clear acknowledgement for job {job.Id} has expired."),
                null);
        }

        if (dispatchState.AcknowledgementIdempotencyKey != request.AcknowledgementIdempotencyKey)
        {
            return (
                DispatchClaimResult.Fail(
                    "acknowledgement_key_mismatch",
                    "Acknowledgement idempotency key does not match the persisted value."),
                null);
        }

        if (dispatchState.AcknowledgedQueueRevision != dispatchState.QueueRevision)
        {
            return (
                DispatchClaimResult.Fail(
                    "acknowledgement_queue_revision_stale",
                    "The queue changed after bed-clear acknowledgement."),
                null);
        }

        if (dispatchState.AcknowledgedPrinterConfigRevision != printer.ConfigurationRevision)
        {
            return (
                DispatchClaimResult.Fail(
                    "acknowledgement_printer_revision_stale",
                    "The printer configuration changed after bed-clear acknowledgement."),
                null);
        }

        if (dispatchState.AcknowledgedJobRowVersion is null ||
            !dispatchState.AcknowledgedJobRowVersion.SequenceEqual(job.RowVersion ?? []))
        {
            return (
                DispatchClaimResult.Fail(
                    "acknowledgement_job_revision_stale",
                    "The job changed after bed-clear acknowledgement."),
                null);
        }

        Guid? queueHeadId = await _db.PrintJobs
            .AsNoTracking()
            .Where(candidate =>
                candidate.AssignedPrinterId == printer.Id &&
                (candidate.Status == PrintJobStatus.Queued ||
                 candidate.Status == PrintJobStatus.Assigned))
            .OrderByPriorityDescending()
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(ct);
        if (queueHeadId != job.Id)
        {
            return (
                DispatchClaimResult.Fail(
                    "wrong_job",
                    "The acknowledged job is no longer the urgent-first current queue head."),
                null);
        }

        BedClearCommandRecord? command = await _db.BedClearCommandRecords
            .FirstOrDefaultAsync(
                record =>
                    record.PrinterId == printer.Id &&
                    record.IdempotencyKey == request.AcknowledgementIdempotencyKey,
                ct);
        if (command is null ||
            command.JobId != job.Id ||
            command.Status != BedClearCommandStatus.Pending ||
            command.ExpiresAtUtc <= DateTime.UtcNow ||
            command.QueueRevision != dispatchState.QueueRevision ||
            command.PrinterConfigRevision != printer.ConfigurationRevision)
        {
            return (
                DispatchClaimResult.Fail(
                    "acknowledgement_command_invalid",
                    "The durable bed-clear command is absent, consumed, expired, or stale."),
                null);
        }

        return (null, command);
    }

    private async Task<DispatchClaimResult?> EvaluatePinnedSpoolAsync(
        PrintJob job,
        Printer printer,
        CancellationToken ct)
    {
        if (!job.PinnedSpoolId.HasValue)
        {
            return DispatchClaimResult.Fail(
                "filament_spool_missing",
                "Calibration dispatch requires an exact pinned physical spool.");
        }

        Spool? spool = await _db.Spools
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == job.PinnedSpoolId.Value, ct);
        if (spool is null ||
            !spool.InUse ||
            spool.AssignedPrinterId != printer.Id)
        {
            return DispatchClaimResult.Fail(
                "filament_spool_mismatch",
                "The exact pinned physical spool is not loaded on the assigned printer.");
        }

        if (!string.Equals(
                spool.Material,
                job.RequiredMaterialType,
                StringComparison.OrdinalIgnoreCase))
        {
            return DispatchClaimResult.Fail(
                "filament_material_mismatch",
                "The exact pinned spool material no longer matches the queued job.");
        }

        if (string.IsNullOrWhiteSpace(job.PinnedFilamentSku) ||
            string.IsNullOrWhiteSpace(job.PinnedFilamentLotNumber) ||
            !string.Equals(
                spool.Sku,
                job.PinnedFilamentSku,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                spool.LotNumber,
                job.PinnedFilamentLotNumber,
                StringComparison.Ordinal))
        {
            return DispatchClaimResult.Fail(
                "filament_spool_identity_mismatch",
                "The physical spool SKU or lot no longer matches the pinned calibration identity.");
        }

        if (job.EstimatedFilamentUsage is > 0 &&
            spool.WeightGrams < job.EstimatedFilamentUsage.Value)
        {
            return DispatchClaimResult.Fail(
                "filament_insufficient",
                "The exact pinned spool no longer contains enough filament for the job.");
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
        dispatchState.AcknowledgedJobRowVersion = null;
        dispatchState.AcknowledgedQueueRevision = null;
        dispatchState.AcknowledgedPrinterConfigRevision = null;
    }

    private static void ClearPhysicalBarrier(
        PrinterDispatchState? state,
        Guid attemptId)
    {
        if (state?.PhysicalControlCommandId != attemptId)
        {
            return;
        }

        state.PhysicalControlCommandId = null;
        state.PhysicalControlAttemptId = null;
        state.PhysicalControlOperation = null;
        state.PhysicalControlActorSubject = null;
        state.PhysicalControlStartedAtUtc = null;
        state.PhysicalControlRequiresReconciliation = false;
    }

    private async Task WriteDeniedAuditAsync(
        DispatchClaimRequest request,
        DispatchClaimResult denial,
        PrintJob job,
        PrinterDispatchState dispatchState,
        CancellationToken ct)
    {
        JobBlockedReasonCode? blockedReason = job.JobKind == JobKind.FilamentCalibration
            ? DispatchSafetyGates.MapBlockedReason(denial.ErrorCode)
            : null;
        if (blockedReason.HasValue)
        {
            job.BlockedReasonCode = blockedReason;
            job.BlockedReasonJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                errorCode = denial.ErrorCode,
                detail = denial.ErrorDetail,
            });
        }

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

    // ===== Terminal lifecycle event type constants =====

    /// <summary>Event type emitted when a dispatch attempt is rejected due to a known pre-start failure.</summary>
    internal const string EventTypeKnownFailure = "PrintFarmer.Queue.DispatchKnownFailure.v1";

    /// <summary>Event type emitted when the backend confirms it accepted the job (job transitions to Printing).</summary>
    internal const string EventTypeBackendAccepted = "PrintFarmer.Queue.DispatchAccepted.v1";

    /// <summary>Event type emitted when the backend outcome is unknown and reconciliation is required.</summary>
    internal const string EventTypeUnknownOutcome = "PrintFarmer.Queue.DispatchUnknown.v1";

    /// <summary>Event type emitted when a reconciliation scan concludes the backend is actively printing.</summary>
    internal const string EventTypeReconciliationAccepted = "PrintFarmer.Queue.ReconciliationAccepted.v1";

    /// <summary>Event type emitted when a reconciliation scan finds the job absent from the backend.</summary>
    internal const string EventTypeReconciliationAbsent = "PrintFarmer.Queue.ReconciliationAbsent.v1";

    /// <summary>Event type emitted when a reconciliation scan cannot determine the backend state.</summary>
    internal const string EventTypeReconciliationIndeterminate = "PrintFarmer.Queue.ReconciliationIndeterminate.v1";

    /// <summary>Event type emitted when a job is completed (all copies finished successfully).</summary>
    internal const string EventTypeJobCompleted = "PrintFarmer.Queue.JobCompleted.v1";

    /// <summary>Event type emitted when a job transitions to Failed.</summary>
    internal const string EventTypeJobFailed = "PrintFarmer.Queue.JobFailed.v1";

    /// <summary>Event type emitted when an orphaned Starting/Printing job is synced to a terminal state.</summary>
    internal const string EventTypeJobOrphanSynced = "PrintFarmer.Queue.JobOrphanSynced.v1";

    /// <summary>Event type emitted when a job is cancelled (terminal; job removed from active queue).</summary>
    internal const string EventTypeJobCancelled = "PrintFarmer.Queue.JobCancelled.v1";

    /// <summary>Event type emitted when a job's current print attempt is aborted (job returns to queued).</summary>
    internal const string EventTypeJobAborted = "PrintFarmer.Queue.JobAborted.v1";

    // ===== Shared outbox helper =====

    /// <summary>
    /// Adds a durable lifecycle outbox event to the context and allocates a monotonic sequence.
    /// The event is committed atomically when the caller calls <c>SaveChangesAsync()</c>.
    /// Allocates a sequence via <see cref="IDbOutboxSequenceAllocator"/> — callers must not
    /// retry concurrency conflicts unless they also reload the counter.
    /// </summary>
    internal static async Task AddLifecycleOutboxEventAsync(
        AppDbContext db,
        IDbOutboxSequenceAllocator sequenceAllocator,
        string eventType,
        Guid aggregateId,
        Guid? printerId,
        Guid? attemptId,
        byte[]? aggregateRowVersion,
        string? failureCode,
        string payloadJson,
        CancellationToken ct,
        Guid? projectId = null,
        string? jobStatus = null,
        string? jobKind = null,
        string? bedClearState = null,
        Guid? bedClearCommandId = null,
        DateTime? bedClearExpiresAtUtc = null,
        bool? failureRetryable = null,
        bool? failureRequiresReconciliation = null)
    {
        DateTime nowUtc = DateTime.UtcNow;
        PrintJob? job = db.PrintJobs.Local.FirstOrDefault(candidate => candidate.Id == aggregateId)
            ?? await db.PrintJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == aggregateId, ct);
        QueueDispatchAttempt? attempt = attemptId.HasValue
            ? db.QueueDispatchAttempts.Local.FirstOrDefault(candidate => candidate.Id == attemptId.Value)
                ?? await db.QueueDispatchAttempts
                   .AsNoTracking()
                   .FirstOrDefaultAsync(candidate => candidate.Id == attemptId.Value, ct)
            : null;
        var outbox = new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = await sequenceAllocator.AllocateAsync(db, ct),
            AggregateType = nameof(PrintJob),
            AggregateId = aggregateId,
            AggregateRowVersion = aggregateRowVersion,
            PrinterId = printerId,
            ProjectId = projectId ?? job?.CalibrationProjectId ?? job?.ProjectId,
            CalibrationAttemptId = job?.CalibrationAttemptId,
            JobStatus = jobStatus ?? job?.Status.ToString(),
            JobKind = jobKind ?? job?.JobKind?.ToString() ?? nameof(JobKind.Standard),
            AttemptId = attemptId,
            AttemptNumber = attempt?.AttemptNumber,
            AttemptOutcome = attempt?.Outcome.ToString(),
            BedClearState = bedClearState,
            BedClearCommandId = bedClearCommandId,
            BedClearExpiresAtUtc = bedClearExpiresAtUtc,
            EventType = eventType,
            SchemaVersion = QueueEventSchemaVersions.Current,
            FailureCode = failureCode,
            FailureRetryable = failureRetryable ?? attempt?.IsRetryable,
            FailureRequiresReconciliation =
                failureRequiresReconciliation ?? attempt?.RequiresReconciliation,
            PayloadJson = payloadJson,
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = nowUtc,
        };
        db.QueueDispatchOutbox.Add(outbox);
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
            backendCommandId = attempt.BackendCommandId,
            calibrationProjectId = job.CalibrationProjectId,
            calibrationAttemptId = job.CalibrationAttemptId,
            claimedAtUtc = attempt.ClaimedAtUtc,
        });
}

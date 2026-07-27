using System.Security.Cryptography;
using System.Text;
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
    ILogger<BedClearAcknowledgementService> logger,
    IStoredGcodeIntegrityVerifier? integrityVerifier = null,
    IQueueResourceAuthorizationService? resourceAuthorization = null) : IBedClearAcknowledgementService
{
    /// <summary>
    /// Default acknowledgement validity window.
    /// Operators are expected to start the job within this window.
    /// </summary>
    private static readonly TimeSpan DefaultAcknowledgementTtl = TimeSpan.FromMinutes(15);

    /// <summary>Maximum age of a telemetry snapshot accepted when issuing an acknowledgement.</summary>
    private static readonly TimeSpan TelemetryFreshnessLimit = TimeSpan.FromMinutes(5);

    /// <summary>Event type string for the durable backend-start command written to the outbox.</summary>
    public const string BackendStartCommandEventType = "PrintFarmer.Queue.BackendStartCommand.v1";

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IDbOutboxSequenceAllocator _sequenceAllocator = sequenceAllocator ?? throw new ArgumentNullException(nameof(sequenceAllocator));
    private readonly IPrinterStatusSnapshotReader _statusReader = statusReader ?? throw new ArgumentNullException(nameof(statusReader));
    private readonly ILogger<BedClearAcknowledgementService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IStoredGcodeIntegrityVerifier? _integrityVerifier = integrityVerifier;
    private readonly IQueueResourceAuthorizationService? _resourceAuthorization =
        resourceAuthorization;

    /// <inheritdoc />
    public async Task<AcknowledgeBedClearResult> AcknowledgeAsync(
        AcknowledgeBedClearRequest request,
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
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.JobNotFound,
                null,
                null,
                "The queue job was not found.");
        }

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

        if (request.IfMatchJob is null || request.IfMatchDispatchState is null)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PreconditionRequired,
                job.RowVersion, dispatchState.RowVersion,
                "Both If-Match and X-Dispatch-State-If-Match are required for bed-clear acknowledgements.");
        }

        byte[] effectiveJobRevision = request.IfMatchJob;
        string requestSha256 = BuildCommandRequestSha256(request);
        BedClearCommandRecord? priorCommand = await _db.BedClearCommandRecords
            .FirstOrDefaultAsync(
                record =>
                    record.PrinterId == request.PrinterId &&
                    record.IdempotencyKey == request.IdempotencyKey,
                ct);
        if (priorCommand is not null)
        {
            if (!string.Equals(
                    priorCommand.RequestSha256,
                    requestSha256,
                    StringComparison.Ordinal))
            {
                return new AcknowledgeBedClearResult(
                    BedClearAckOutcome.IdempotencyMismatch,
                    job.RowVersion,
                    dispatchState.RowVersion,
                    "Idempotency key was previously used with different job or revision inputs.");
            }

            if (priorCommand.Status is BedClearCommandStatus.Rejected or
                BedClearCommandStatus.Expired)
            {
                return new AcknowledgeBedClearResult(
                    BedClearAckOutcome.JobNotDispatchable,
                    job.RowVersion,
                    dispatchState.RowVersion,
                    "The prior bed-clear command is terminal and cannot be replayed. Use a new idempotency key.");
            }

            if (priorCommand.Status == BedClearCommandStatus.Pending)
            {
                Guid? currentHeadId = await _db.PrintJobs
                    .AsNoTracking()
                    .Where(candidate =>
                        candidate.AssignedPrinterId == request.PrinterId &&
                        (candidate.Status == PrintJobStatus.Queued ||
                         candidate.Status == PrintJobStatus.Assigned))
                    .OrderByPriorityDescending()
                    .Select(candidate => (Guid?)candidate.Id)
                    .FirstOrDefaultAsync(ct);
                long? currentPrinterRevision = await _db.Printers
                    .AsNoTracking()
                    .Where(printer => printer.Id == request.PrinterId)
                    .Select(printer => (long?)printer.ConfigurationRevision)
                    .SingleOrDefaultAsync(ct);
                bool pendingIsStale =
                    priorCommand.ExpiresAtUtc <= DateTime.UtcNow ||
                    priorCommand.QueueRevision != dispatchState.QueueRevision ||
                    !priorCommand.JobRowVersion.SequenceEqual(job.RowVersion ?? []) ||
                    priorCommand.PrinterConfigRevision != currentPrinterRevision ||
                    currentHeadId != priorCommand.JobId;
                if (pendingIsStale)
                {
                    await PersistBedClearTerminalAsync(
                        priorCommand,
                        job,
                        dispatchState,
                        expired: priorCommand.ExpiresAtUtc <= DateTime.UtcNow,
                        "pending_inputs_changed",
                        ct);
                    return new AcknowledgeBedClearResult(
                        BedClearAckOutcome.JobNotDispatchable,
                        job.RowVersion,
                        dispatchState.RowVersion,
                        "The pending bed-clear command expired or its exact queue inputs changed. Use a new idempotency key.");
                }
            }

            BedClearAckOutcome replayOutcome =
                priorCommand.Status is BedClearCommandStatus.Claimed or
                    BedClearCommandStatus.Accepted or
                    BedClearCommandStatus.Unknown
                    ? BedClearAckOutcome.AlreadyStartingOrPrinting
                    : BedClearAckOutcome.Replayed;
            return new AcknowledgeBedClearResult(
                replayOutcome,
                job.RowVersion,
                dispatchState.RowVersion,
                null);
        }

        if (!effectiveJobRevision.SequenceEqual(job.RowVersion ?? []))
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.DispatchRevisionConflict,
                job.RowVersion,
                dispatchState.RowVersion,
                "The job changed since the request was prepared. Re-fetch both ETags and retry.");
        }

        if (!request.IfMatchDispatchState.SequenceEqual(dispatchState.RowVersion ?? []))
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.DispatchRevisionConflict,
                job.RowVersion, dispatchState.RowVersion,
                "Dispatch state has changed since the request was prepared. Re-fetch and retry.");
        }

        // Database state cannot be overridden by a stale idle telemetry snapshot.
        bool hasDatabaseActiveJob = await _db.PrintJobs
            .AsNoTracking()
            .AnyAsync(
                candidate =>
                candidate.AssignedPrinterId == request.PrinterId &&
                candidate.Id != request.JobId &&
                (candidate.Status == PrintJobStatus.Starting ||
                 candidate.Status == PrintJobStatus.Printing ||
                 candidate.Status == PrintJobStatus.Paused), ct);
        if (hasDatabaseActiveJob)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterBusy,
                job.RowVersion,
                dispatchState.RowVersion,
                "Another Starting, Printing, or Paused job owns this printer in the database.");
        }

        // Check whether the printer is already occupied.
        if (dispatchState.ActiveDispatchAttemptId.HasValue ||
            (dispatchState.ActiveJobId.HasValue && dispatchState.ActiveJobId != request.JobId))
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterBusy,
                job.RowVersion,
                dispatchState.RowVersion,
                "The printer is already owned by an active queue or ad-hoc dispatch attempt.");
        }

        // Short-circuit only after durable idempotency and revision checks.
        if (job.Status is PrintJobStatus.Starting or PrintJobStatus.Printing)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.AlreadyStartingOrPrinting,
                job.RowVersion,
                dispatchState.RowVersion,
                null);
        }

        if (job.Status is not (PrintJobStatus.Queued or PrintJobStatus.Assigned))
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.JobNotDispatchable,
                job.RowVersion,
                dispatchState.RowVersion,
                $"Job is in state {job.Status} and cannot be acknowledged.");
        }

        // Job must still be the exact urgent-first current queue head.
        Guid? queueHeadId = await _db.PrintJobs
            .AsNoTracking()
            .Where(candidate =>
                candidate.AssignedPrinterId == request.PrinterId &&
                (candidate.Status == PrintJobStatus.Queued ||
                 candidate.Status == PrintJobStatus.Assigned))
            .OrderByPriorityDescending()
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(ct);
        if (queueHeadId != request.JobId)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.WrongJob,
                job.RowVersion,
                dispatchState.RowVersion,
                "Only the urgent-first current queue head can be acknowledged.");
        }

        if (job.GcodeFile is null)
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.CalibrationJobIncompatible,
                job.RowVersion,
                dispatchState.RowVersion,
                "Job is missing its G-code artifact.");
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
            return await PersistBlockedAsync(
                request,
                job,
                dispatchState,
                BedClearAckOutcome.CalibrationJobIncompatible,
                "printer_config_revision_stale",
                $"Printer configuration revision {printer.ConfigurationRevision} does not match expected {request.ExpectedPrinterConfigRevision}.",
                ct);
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

        if (!IsExplicitlyIdle(snapshot.Status.State))
        {
            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.PrinterBusy,
                job.RowVersion, dispatchState.RowVersion,
                $"Printer is not explicitly idle (observed '{snapshot.Status.State ?? "unknown"}'); bed-clear cannot be acknowledged.");
        }

        // =========================================================================
        // Hard filament / spool gate — evaluated with the SAME shared rules the claim
        // uses so an acknowledgement can never be issued for a job the claim will reject.
        // =========================================================================
        DispatchClaimResult? filamentFailure = DispatchSafetyGates.EvaluateFilament(job, printer);
        if (filamentFailure is not null)
        {
            return await PersistBlockedAsync(
                request,
                job,
                dispatchState,
                BedClearAckOutcome.FilamentCheckFailed,
                filamentFailure.ErrorCode ?? "filament_material_mismatch",
                filamentFailure.ErrorDetail,
                ct);
        }

        if (job.JobKind == JobKind.FilamentCalibration)
        {
            Spool? pinnedSpool = job.PinnedSpoolId.HasValue
                ? await _db.Spools
                    .AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == job.PinnedSpoolId.Value, ct)
                : null;
            if (pinnedSpool is null ||
                !pinnedSpool.InUse ||
                pinnedSpool.AssignedPrinterId != printer.Id ||
                !string.Equals(
                    pinnedSpool.Material,
                    job.RequiredMaterialType,
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(job.PinnedFilamentSku) ||
                string.IsNullOrWhiteSpace(job.PinnedFilamentLotNumber) ||
                !string.Equals(
                    pinnedSpool.Sku,
                    job.PinnedFilamentSku,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    pinnedSpool.LotNumber,
                    job.PinnedFilamentLotNumber,
                    StringComparison.Ordinal) ||
                (job.EstimatedFilamentUsage is > 0 &&
                 pinnedSpool.WeightGrams < job.EstimatedFilamentUsage.Value))
            {
                return await PersistBlockedAsync(
                    request,
                    job,
                    dispatchState,
                    BedClearAckOutcome.FilamentCheckFailed,
                    "filament_spool_mismatch",
                    "The exact pinned physical spool is absent, mismatched, or insufficient.",
                    ct);
            }
        }

        // =========================================================================
        // Complete compatibility tuple, hash and lineage validation for calibration jobs,
        // plus shared hardware gates for every job kind.
        // =========================================================================
        DispatchClaimResult? hardwareFailure = DispatchSafetyGates.EvaluateHardware(job, printer);
        if (hardwareFailure is not null)
        {
            return await PersistBlockedAsync(
                request,
                job,
                dispatchState,
                BedClearAckOutcome.CalibrationJobIncompatible,
                hardwareFailure.ErrorCode ?? "compatibility_incomplete",
                hardwareFailure.ErrorDetail,
                ct);
        }

        if (job.JobKind == JobKind.FilamentCalibration)
        {
            if (!job.GcodeFile.IsImmutable || job.GcodeFile.PromotedAtUtc is null ||
                !QueueJobClassifier.IsCalibrationArtifact(job.GcodeFile))
            {
                return await PersistBlockedAsync(
                    request,
                    job,
                    dispatchState,
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    "gcode_hash_unverifiable",
                    "The calibration job does not reference a promoted immutable calibration artifact.",
                    ct);
            }

            if (string.IsNullOrWhiteSpace(job.GcodeContentSha256))
            {
                return await PersistBlockedAsync(
                    request,
                    job,
                    dispatchState,
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    "gcode_hash_missing",
                    "The calibration job has no pinned G-code content hash.",
                    ct);
            }

            if (_integrityVerifier is null)
            {
                return await PersistBlockedAsync(
                    request,
                    job,
                    dispatchState,
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    "gcode_hash_unverifiable",
                    "Stored-byte integrity verification is unavailable; acknowledgement fails closed.",
                    ct);
            }

            StoredGcodeIntegrityResult byteIntegrity = await _integrityVerifier.VerifyAsync(
                job.GcodeFile,
                job.GcodeContentSha256,
                job.PinnedGcodeFileSizeBytes,
                ct);
            if (!byteIntegrity.Success)
            {
                return await PersistBlockedAsync(
                    request,
                    job,
                    dispatchState,
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    byteIntegrity.ErrorCode ?? "gcode_byte_hash_mismatch",
                    byteIntegrity.ErrorDetail,
                    ct);
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
                return await PersistBlockedAsync(
                    request,
                    job,
                    dispatchState,
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    "gcode_hash_mismatch",
                    HashMismatchDetail,
                    ct);
            }

            DispatchClaimResult? calibrationFailure =
                DispatchSafetyGates.EvaluateCalibrationCompatibility(job, printer);

            if (calibrationFailure is not null)
            {
                return await PersistBlockedAsync(
                    request,
                    job,
                    dispatchState,
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    calibrationFailure.ErrorCode ?? "compatibility_incomplete",
                    calibrationFailure.ErrorDetail,
                    ct);
            }

            DispatchClaimResult? persistedInputFailure =
                await DispatchClaimService.EvaluatePersistedCalibrationInputsAsync(
                    _db,
                    job,
                    printer,
                    ct);
            if (persistedInputFailure is not null)
            {
                return await PersistBlockedAsync(
                    request,
                    job,
                    dispatchState,
                    BedClearAckOutcome.CalibrationJobIncompatible,
                    persistedInputFailure.ErrorCode ?? "calibration_record_mismatch",
                    persistedInputFailure.ErrorDetail,
                    ct);
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
        dispatchState.AcknowledgedJobRowVersion = effectiveJobRevision.ToArray();
        dispatchState.AcknowledgedQueueRevision = dispatchState.QueueRevision;
        dispatchState.AcknowledgedPrinterConfigRevision =
            request.ExpectedPrinterConfigRevision.Value;

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
            ProjectId = job.CalibrationProjectId ?? job.ProjectId,
            JobStatus = job.Status.ToString(),
            JobKind = job.JobKind?.ToString() ?? nameof(JobKind.Standard),
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
        var commandRecord = new BedClearCommandRecord
        {
            Id = Guid.NewGuid(),
            PrinterId = request.PrinterId,
            JobId = request.JobId,
            IdempotencyKey = request.IdempotencyKey,
            RequestSha256 = requestSha256,
            ActorSubject = request.ActorSubject,
            JobRowVersion = effectiveJobRevision.ToArray(),
            DispatchStateRowVersion = request.IfMatchDispatchState.ToArray(),
            QueueRevision = dispatchState.QueueRevision,
            PrinterConfigRevision = request.ExpectedPrinterConfigRevision.Value,
            Status = BedClearCommandStatus.Pending,
            OutboxEventId = startCommand.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ExpiresAtUtc = now + DefaultAcknowledgementTtl,
        };
        _ = _db.BedClearCommandRecords.Add(commandRecord);

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

        try
        {
            await using QueueOutboxTransactionScope transaction =
                await QueueOutboxTransactionScope.BeginAsync(_db, ct);
            startCommand.Sequence = await _sequenceAllocator.AllocateAsync(_db, ct);
            await QueueLifecycleEventWriter.AddEventAsync(
                db: _db,
                sequenceAllocator: _sequenceAllocator,
                eventType: QueueLifecycleEventWriter.EventTypeBedClearAcknowledged,
                aggregateId: job.Id,
                printerId: request.PrinterId,
                attemptId: null,
                aggregateRowVersion: job.RowVersion,
                failureCode: null,
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    jobId = job.Id,
                    printerId = request.PrinterId,
                    bedClearCommandId = commandRecord.Id,
                    bedClearState = "Acknowledged",
                    expiresAtUtc = commandRecord.ExpiresAtUtc,
                }),
                bedClearState: "Acknowledged",
                bedClearCommandId: commandRecord.Id,
                bedClearExpiresAtUtc: commandRecord.ExpiresAtUtc,
                ct: ct);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(
                ex,
                "[BedClearAck] Concurrency conflict persisting bed-clear acknowledgement for Job={JobId}",
                request.JobId);

            return new AcknowledgeBedClearResult(
                BedClearAckOutcome.DispatchRevisionConflict,
                null, null,
                "A concurrent operation modified the dispatch state. Re-fetch and retry.");
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            BedClearCommandRecord? winner = await _db.BedClearCommandRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    record =>
                        record.PrinterId == request.PrinterId &&
                        record.IdempotencyKey == request.IdempotencyKey,
                    ct);
            if (winner is not null)
            {
                bool isReplay = string.Equals(
                    winner.RequestSha256,
                    requestSha256,
                    StringComparison.Ordinal);
                string? replayError = isReplay
                    ? null
                    : "Idempotency key was concurrently used with different inputs.";
                return new AcknowledgeBedClearResult(
                    isReplay
                        ? BedClearAckOutcome.Replayed
                        : BedClearAckOutcome.IdempotencyMismatch,
                    null,
                    null,
                    replayError);
            }

            throw;
        }

        _logger.LogInformation(
            "Bed-clear acknowledged and durable backend-start command queued: " +
            "Job={JobId} Printer={PrinterId} Actor={Actor} Command={CommandId}",
            request.JobId, request.PrinterId, request.ActorSubject,
            startCommand.Id);

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

        long? printerRevision = await _db.Printers
            .AsNoTracking()
            .Where(printer => printer.Id == printerId)
            .Select(printer => (long?)printer.ConfigurationRevision)
            .SingleOrDefaultAsync(ct);
        bool isStale =
            frontJob is null ||
            frontJob.Id != acknowledgedJobId ||
            dispatchState.AcknowledgedQueueRevision != dispatchState.QueueRevision ||
            dispatchState.AcknowledgedPrinterConfigRevision != printerRevision ||
            dispatchState.AcknowledgedJobRowVersion is null ||
            !dispatchState.AcknowledgedJobRowVersion.SequenceEqual(frontJob.RowVersion ?? []);
        bool isExpired =
            dispatchState.AcknowledgementExpiresAtUtc.HasValue &&
            dispatchState.AcknowledgementExpiresAtUtc <= DateTime.UtcNow;

        if (isStale || isExpired)
        {
            PrintJob? acknowledgedJob = await _db.PrintJobs
                .FirstOrDefaultAsync(candidate => candidate.Id == acknowledgedJobId, ct);
            BedClearCommandRecord? command = await _db.BedClearCommandRecords
                .Where(candidate =>
                    candidate.PrinterId == printerId &&
                    candidate.JobId == acknowledgedJobId)
                .OrderByDescending(candidate => candidate.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (acknowledgedJob is not null && command is not null)
            {
                await PersistBedClearTerminalAsync(
                    command,
                    acknowledgedJob,
                    dispatchState,
                    isExpired,
                    isExpired ? "acknowledgement_expired" : "queue_head_changed",
                    ct);
            }
            else
            {
                ClearAcknowledgement(dispatchState);
                await _db.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
                "Invalidated stale bed-clear acknowledgement for Printer={PrinterId} (was for Job={JobId})",
                printerId, acknowledgedJobId);
        }
    }

    private async Task PersistBedClearTerminalAsync(
        BedClearCommandRecord command,
        PrintJob job,
        PrinterDispatchState state,
        bool expired,
        string reasonCode,
        CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        command.Status = expired
            ? BedClearCommandStatus.Expired
            : BedClearCommandStatus.Rejected;
        command.UpdatedAtUtc = now;
        QueueDispatchOutbox? startCommand = await _db.QueueDispatchOutbox
            .FirstOrDefaultAsync(candidate => candidate.Id == command.OutboxEventId, ct);
        if (startCommand is not null &&
            startCommand.Status is QueueOutboxEventStatus.Pending or
                QueueOutboxEventStatus.Processing)
        {
            startCommand.Status = QueueOutboxEventStatus.DeadLettered;
            startCommand.FailureCode = reasonCode;
            startCommand.LastError = "The exact-job bed-clear acknowledgement is no longer valid.";
            startCommand.CompletedAtUtc = now;
        }

        ClearAcknowledgement(state);
        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);
        string eventType = expired
            ? QueueLifecycleEventWriter.EventTypeBedClearExpired
            : QueueLifecycleEventWriter.EventTypeBedClearInvalidated;
        string stateName = expired ? "Expired" : "Invalidated";
        await QueueLifecycleEventWriter.AddEventAsync(
            _db,
            _sequenceAllocator,
            eventType,
            job.Id,
            state.PrinterId,
            command.DispatchAttemptId,
            job.RowVersion,
            reasonCode,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                jobId = job.Id,
                printerId = state.PrinterId,
                bedClearCommandId = command.Id,
                bedClearState = stateName,
                failureCode = reasonCode,
            }),
            bedClearState: stateName,
            bedClearCommandId: command.Id,
            bedClearExpiresAtUtc: command.ExpiresAtUtc,
            failureRetryable: false,
            failureRequiresReconciliation: false,
            ct: ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static void ClearAcknowledgement(PrinterDispatchState state)
    {
        state.AcknowledgedJobId = null;
        state.AcknowledgedAtUtc = null;
        state.AcknowledgedBySubject = null;
        state.AcknowledgementIdempotencyKey = null;
        state.AcknowledgementExpiresAtUtc = null;
        state.AcknowledgedJobRowVersion = null;
        state.AcknowledgedQueueRevision = null;
        state.AcknowledgedPrinterConfigRevision = null;
    }

    private static bool IsExplicitlyIdle(string? state) =>
        !string.IsNullOrWhiteSpace(state) &&
        (state.Trim().Equals("idle", StringComparison.OrdinalIgnoreCase) ||
         state.Trim().Equals("ready", StringComparison.OrdinalIgnoreCase) ||
         state.Trim().Equals("standby", StringComparison.OrdinalIgnoreCase) ||
         state.Trim().Equals("operational", StringComparison.OrdinalIgnoreCase));

    private async Task<AcknowledgeBedClearResult> PersistBlockedAsync(
        AcknowledgeBedClearRequest request,
        PrintJob job,
        PrinterDispatchState dispatchState,
        BedClearAckOutcome outcome,
        string errorCode,
        string? detail,
        CancellationToken ct)
    {
        if (job.JobKind == JobKind.FilamentCalibration)
        {
            job.BlockedReasonCode = DispatchSafetyGates.MapBlockedReason(errorCode)
                ?? JobBlockedReasonCode.HardCompatibilityFailure;
            job.BlockedReasonJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                errorCode,
                detail,
            });
            _ = QueueAuditWriter.Add(
                _db,
                request.ActorSubject,
                QueueAuditOperations.BedClearAcknowledge,
                QueueAuditOutcomes.Denied,
                nameof(PrintJob),
                resourceId: job.Id,
                printerId: request.PrinterId,
                printJobId: job.Id,
                reasonCode: errorCode,
                jobRowVersion: job.RowVersion,
                dispatchStateRowVersion: dispatchState.RowVersion,
                idempotencyKey: request.IdempotencyKey,
                detail: new { blockedReason = job.BlockedReasonCode?.ToString() });
            await _db.SaveChangesAsync(ct);
        }

        return new AcknowledgeBedClearResult(
            outcome,
            job.RowVersion,
            dispatchState.RowVersion,
            detail);
    }

    private static string BuildCommandRequestSha256(
        AcknowledgeBedClearRequest request)
    {
        string configurationRevision =
            request.ExpectedPrinterConfigRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        string canonical = string.Join(
            '\n',
            request.JobId.ToString("D"),
            request.PrinterId.ToString("D"),
            request.ActorSubject,
            configurationRevision);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

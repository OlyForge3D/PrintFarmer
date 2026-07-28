using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Background reconciliation service for dispatch attempts with unknown outcomes.
/// When a process crashes between a successful claim write and the backend API call,
/// the job remains in <see cref="PrintJobStatus.Starting"/> with a
/// <see cref="QueueDispatchAttempt.RequiresReconciliation"/> flag. This service
/// periodically probes the authoritative printer backend to distinguish:
/// <list type="bullet">
///   <item><c>Accepted/Active</c>: the backend is actively printing the job → advance to Printing.</item>
///   <item><c>Absent/Rejected</c>: the backend does not know the job → clear the lease, re-queue.</item>
///   <item><c>Unknown</c>: the backend is unreachable → leave in Starting, retry later.</item>
/// </list>
/// The service never blindly retries an uncertain attempt without first confirming the
/// backend state, and never re-sends a start command without clearing the stale lease.
/// </summary>
public sealed class QueueReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<QueueReconciliationService> logger) : BackgroundService
{
    /// <summary>How often to scan for attempts requiring reconciliation.</summary>
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(2);

    /// <summary>Attempts older than this are considered stale and eligible for forced reconciliation.</summary>
    private static readonly TimeSpan StaleAttemptAge = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[Reconciliation] Queue reconciliation service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileStaleAttemptsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Reconciliation] Error during reconciliation scan");
            }

            await Task.Delay(ReconciliationInterval, stoppingToken);
        }

        logger.LogInformation("[Reconciliation] Queue reconciliation service stopped");
    }

    internal async Task ReconcileStaleAttemptsAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IDbOutboxSequenceAllocator? sequenceAllocator = scope.ServiceProvider.GetService<IDbOutboxSequenceAllocator>();

        DateTime staleCutoff = DateTime.UtcNow - StaleAttemptAge;
        bool recoveredNullAttemptCommands =
            await RecoverNullAttemptCommandsAsync(db, ct);

        // Find InProgress attempts older than the stale threshold — these are candidates
        // for authoritative backend reconciliation.
        List<QueueDispatchAttempt> staleAttempts = await db.QueueDispatchAttempts
            .Include(a => a.PrintJob)
            .Where(a =>
                a.Outcome == DispatchAttemptOutcome.InProgress &&
                a.ClaimedAtUtc < staleCutoff &&
                a.PrintJob != null &&
                a.PrintJob.Status == PrintJobStatus.Starting)
            .Take(20)
            .ToListAsync(ct);

        // Also reconcile attempts already marked RequiresReconciliation.
        List<QueueDispatchAttempt> flaggedAttempts = await db.QueueDispatchAttempts
            .Include(a => a.PrintJob)
            .Where(a =>
                a.Outcome == DispatchAttemptOutcome.Unknown &&
                a.RequiresReconciliation &&
                a.PrintJob != null &&
                a.PrintJob.Status == PrintJobStatus.Starting)
            .Take(20)
            .ToListAsync(ct);

        // Ad-hoc attempts (PrintJobId == null) that are stale InProgress — these pin the
        // printer lease forever if never reconciled. Reconcile through adapter identity/state.
        List<QueueDispatchAttempt> staleAdHocAttempts = await db.QueueDispatchAttempts
            .Where(a =>
                a.PrintJobId == null &&
                a.Outcome == DispatchAttemptOutcome.InProgress &&
                a.ClaimedAtUtc < staleCutoff)
            .Take(20)
            .ToListAsync(ct);

        // Ad-hoc attempts already flagged for reconciliation.
        List<QueueDispatchAttempt> flaggedAdHocAttempts = await db.QueueDispatchAttempts
            .Where(a =>
                a.PrintJobId == null &&
                a.Outcome == DispatchAttemptOutcome.Unknown &&
                a.RequiresReconciliation)
            .Take(20)
            .ToListAsync(ct);

        // Confirmed ad-hoc starts retain their exclusive lease until the backend is no
        // longer active. This periodic scan releases them only after terminal evidence.
        List<QueueDispatchAttempt> acceptedAdHocAttempts = await db.QueueDispatchAttempts
            .Where(a =>
                a.PrintJobId == null &&
                a.Outcome == DispatchAttemptOutcome.Accepted &&
                a.UpdatedAtUtc < staleCutoff)
            .Take(20)
            .ToListAsync(ct);

        List<QueueDispatchAttempt> toReconcile = staleAttempts
            .Concat(flaggedAttempts)
            .Concat(staleAdHocAttempts)
            .Concat(flaggedAdHocAttempts)
            .Concat(acceptedAdHocAttempts)
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .ToList();

        if (toReconcile.Count == 0)
        {
            if (recoveredNullAttemptCommands)
            {
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        logger.LogWarning(
            "[Reconciliation] Found {Count} dispatch attempt(s) requiring backend reconciliation",
            toReconcile.Count);

        // Try to get IPrintersService for backend queries. May be unavailable in test environments.
        IPrintersService? printersSvc = scope.ServiceProvider.GetService<IPrintersService>();

        foreach (QueueDispatchAttempt attempt in toReconcile)
        {
            await ReconcileSingleAttemptAsync(db, printersSvc, sequenceAllocator, attempt, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<bool> RecoverNullAttemptCommandsAsync(
        AppDbContext db,
        CancellationToken ct)
    {
        List<QueueDispatchOutbox> commands = await db.QueueDispatchOutbox
            .Where(command =>
                command.EventType == BedClearAcknowledgementService.BackendStartCommandEventType &&
                command.Status == QueueOutboxEventStatus.Processing &&
                command.FailureCode == "backend_outcome_unknown" &&
                command.AttemptId == null)
            .Take(20)
            .ToListAsync(ct);
        bool changed = false;
        foreach (QueueDispatchOutbox command in commands)
        {
            if (!command.PrinterId.HasValue)
            {
                continue;
            }

            PrinterDispatchState? state = await db.PrinterDispatchStates
                .FirstOrDefaultAsync(
                    candidate => candidate.PrinterId == command.PrinterId.Value,
                    ct);
            PrintJob? job = await db.PrintJobs
                .FirstOrDefaultAsync(candidate => candidate.Id == command.AggregateId, ct);
            if (state?.ActiveJobId == command.AggregateId &&
                state.ActiveDispatchAttemptId.HasValue)
            {
                command.AttemptId = state.ActiveDispatchAttemptId;
                BedClearCommandRecord? record = await db.BedClearCommandRecords
                    .FirstOrDefaultAsync(
                        candidate => candidate.OutboxEventId == command.Id,
                        ct);
                if (record is not null)
                {
                    record.DispatchAttemptId = state.ActiveDispatchAttemptId;
                    record.UpdatedAtUtc = DateTime.UtcNow;
                }

                changed = true;
                continue;
            }

            if (state is not null &&
                !state.ActiveDispatchAttemptId.HasValue &&
                !state.ActiveJobId.HasValue &&
                job?.Status is PrintJobStatus.Queued or PrintJobStatus.Assigned)
            {
                // A claim is committed before any start-capable network call. With no active
                // attempt and a still-dispatchable job, the exception occurred pre-claim and
                // this command is safe to retry.
                command.Status = QueueOutboxEventStatus.Pending;
                command.FailureCode = null;
                command.LastError = "Recovered a pre-claim command with no persisted attempt.";
                command.RetryAfterUtc = DateTime.UtcNow;
                BedClearCommandRecord? record = await db.BedClearCommandRecords
                    .FirstOrDefaultAsync(
                        candidate => candidate.OutboxEventId == command.Id,
                        ct);
                if (record is not null)
                {
                    record.Status = BedClearCommandStatus.Pending;
                    record.UpdatedAtUtc = DateTime.UtcNow;
                }

                changed = true;
            }
        }

        return changed;
    }

    private async Task ReconcileSingleAttemptAsync(
        AppDbContext db,
        IPrintersService? printersSvc,
        IDbOutboxSequenceAllocator? sequenceAllocator,
        QueueDispatchAttempt attempt,
        CancellationToken ct)
    {
        logger.LogWarning(
            "[Reconciliation] Reconciling attempt {AttemptId} Job={JobId} Printer={PrinterId} " +
            "BackendJobId={BackendJobId} StartPath={Path} ClaimedAt={ClaimedAt:u}",
            attempt.Id,
            attempt.PrintJobId,
            attempt.PrinterId,
            attempt.BackendJobId ?? "(none)",
            attempt.StartPathKind,
            attempt.ClaimedAtUtc);

        PrinterDispatchState? activeState = await db.PrinterDispatchStates
            .FirstOrDefaultAsync(
                state => state.PrinterId == attempt.PrinterId,
                ct);
        if (activeState is null ||
            activeState.ActiveDispatchAttemptId != attempt.Id ||
            activeState.ActiveJobId != attempt.PrintJobId)
        {
            attempt.Outcome = DispatchAttemptOutcome.FailedBeforeStart;
            attempt.ErrorCode = "attempt_superseded";
            attempt.ErrorDetail = "A newer dispatch attempt owns the printer.";
            attempt.IsRetryable = false;
            attempt.RequiresReconciliation = false;
            attempt.BackendCallPhase = DispatchBackendCallPhase.Terminal;
            attempt.TerminalAtUtc = DateTime.UtcNow;
            attempt.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        bool unresolvedStartMayReconcile =
            string.Equals(
                activeState.PhysicalControlOperation,
                "start",
                StringComparison.Ordinal) &&
            attempt.BackendCallPhase == DispatchBackendCallPhase.AwaitingReconciliation;
        bool hasOutstandingControl =
            (activeState.PhysicalControlCommandId.HasValue &&
             activeState.PhysicalControlAttemptId == attempt.Id &&
             !unresolvedStartMayReconcile) ||
            await db.QueueDispatchOutbox
                .AsNoTracking()
                .AnyAsync(
                    command =>
                        command.EventType == BackendControlCommandConsumerService.EventType &&
                        command.AttemptId == attempt.Id &&
                        (command.Status == QueueOutboxEventStatus.Pending ||
                         command.Status == QueueOutboxEventStatus.Processing ||
                         (command.Status == QueueOutboxEventStatus.DeadLettered &&
                          command.FailureCode == "manual_control_reconciliation_required")),
                    ct);
        if (hasOutstandingControl)
        {
            logger.LogDebug(
                "[Reconciliation] Attempt {AttemptId} has an outstanding physical control; lease retained",
                attempt.Id);
            return;
        }

        // If no backend service is available, flag for manual reconciliation.
        if (printersSvc is null)
        {
            MarkRequiresReconciliation(
                attempt,
                "IPrintersService unavailable — manual reconciliation required.");
            return;
        }

        // Query the authoritative backend for the current printer state.
        BackendReconciliationOutcome outcome = await QueryBackendOutcomeAsync(
            printersSvc, attempt, ct);
        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(db, ct);
        activeState.Revision = Math.Max(1, activeState.Revision) + 1;
        attempt.ReconciliationCount++;
        attempt.LastReconciledAtUtc = DateTime.UtcNow;

        switch (outcome)
        {
            case BackendReconciliationOutcome.ActiveOnBackend:
                // The backend is actively printing — advance to Printing for queue jobs.
                // Accepted ad-hoc starts retain the lease while the backend is active.
                attempt.Outcome = DispatchAttemptOutcome.Accepted;
                attempt.BackendAcceptedAtUtc ??= DateTime.UtcNow;
                attempt.RequiresReconciliation = false;
                attempt.UpdatedAtUtc = DateTime.UtcNow;
                attempt.BackendCallPhase = DispatchBackendCallPhase.PostAccept;
                ClearStartBarrier(activeState, attempt.Id);

                if (attempt.PrintJob is not null && attempt.PrintJob.Status == PrintJobStatus.Starting)
                {
                    attempt.PrintJob.Status = PrintJobStatus.Printing;
                    attempt.PrintJob.UpdatedAt = DateTime.UtcNow;
                    AddHistory(
                        db,
                        attempt.PrintJob.Id,
                        PrintJobStatus.Starting,
                        PrintJobStatus.Printing,
                        "Backend reconciliation proved acceptance.");
                }

                await SetBedClearCommandStatusAsync(
                    db,
                    attempt.Id,
                    BedClearCommandStatus.Accepted,
                    ct);
                _ = QueueAuditWriter.Add(
                    db,
                    attempt.ActorSubject,
                    QueueAuditOperations.Reconciliation,
                    QueueAuditOutcomes.Success,
                    attempt.PrintJobId is null ? nameof(Printer) : nameof(PrintJob),
                    resourceId: attempt.PrintJobId ?? (Guid?)attempt.PrinterId,
                    printerId: attempt.PrinterId,
                    printJobId: attempt.PrintJobId,
                    dispatchAttemptId: attempt.Id,
                    reasonCode: "reconciliation_active",
                    detail: new { startPathKind = attempt.StartPathKind });

                // Emit a durable lifecycle event so the outbox publisher broadcasts the
                // reconciliation result to authorized groups.
                if (attempt.PrintJobId is not null && sequenceAllocator is not null)
                {
                    await DispatchClaimService.AddLifecycleOutboxEventAsync(
                        db,
                        sequenceAllocator,
                        DispatchClaimService.EventTypeReconciliationAccepted,
                        aggregateId: attempt.PrintJobId.Value,
                        printerId: attempt.PrinterId,
                        attemptId: attempt.Id,
                        aggregateRowVersion: attempt.PrintJob?.RowVersion,
                        failureCode: null,
                        payloadJson: JsonSerializer.Serialize(new
                        {
                            jobId = attempt.PrintJobId,
                            printerId = attempt.PrinterId,
                            attemptId = attempt.Id,
                            startPathKind = attempt.StartPathKind,
                        }),
                        ct);
                }

                await FinalizeBackendStartCommandAsync(
                    db,
                    attempt,
                    QueueOutboxEventStatus.Published,
                    failureCode: null,
                    lastError: null,
                    ct);

                logger.LogInformation(
                    "[Reconciliation] Attempt {AttemptId} confirmed active on backend → advanced to Printing",
                    attempt.Id);
                break;
            case BackendReconciliationOutcome.CompletedOnBackend:
                attempt.Outcome = DispatchAttemptOutcome.Accepted;
                attempt.BackendAcceptedAtUtc ??= DateTime.UtcNow;
                attempt.RequiresReconciliation = false;
                attempt.UpdatedAtUtc = DateTime.UtcNow;
                attempt.BackendCallPhase = DispatchBackendCallPhase.Terminal;
                attempt.TerminalAtUtc = DateTime.UtcNow;
                ClearStartBarrier(activeState, attempt.Id);

                if (attempt.PrintJob is not null &&
                    attempt.PrintJob.Status is PrintJobStatus.Starting or PrintJobStatus.Printing)
                {
                    PrintJobStatus fromStatus = attempt.PrintJob.Status;
                    attempt.PrintJob.Status = PrintJobStatus.Completed;
                    attempt.PrintJob.ActualEndTime ??= DateTime.UtcNow;
                    attempt.PrintJob.UpdatedAt = DateTime.UtcNow;
                    AddHistory(
                        db,
                        attempt.PrintJob.Id,
                        fromStatus,
                        PrintJobStatus.Completed,
                        "Backend history proved completion.");
                }

                if (activeState.ActiveDispatchAttemptId == attempt.Id)
                {
                    activeState.ActiveJobId = null;
                    activeState.ActiveDispatchAttemptId = null;
                    activeState.QueueRevision++;
                }

                await SetBedClearCommandStatusAsync(
                    db,
                    attempt.Id,
                    BedClearCommandStatus.Accepted,
                    ct);

                _ = QueueAuditWriter.Add(
                    db,
                    attempt.ActorSubject,
                    QueueAuditOperations.Reconciliation,
                    QueueAuditOutcomes.Success,
                    attempt.PrintJobId is null ? nameof(Printer) : nameof(PrintJob),
                    resourceId: attempt.PrintJobId ?? (Guid?)attempt.PrinterId,
                    printerId: attempt.PrinterId,
                    printJobId: attempt.PrintJobId,
                    dispatchAttemptId: attempt.Id,
                    reasonCode: "reconciliation_completed",
                    detail: new { startPathKind = attempt.StartPathKind });

                await FinalizeBackendStartCommandAsync(
                    db,
                    attempt,
                    QueueOutboxEventStatus.Published,
                    failureCode: null,
                    lastError: null,
                    ct);

                if (attempt.PrintJobId is not null && sequenceAllocator is not null)
                {
                    await DispatchClaimService.AddLifecycleOutboxEventAsync(
                        db,
                        sequenceAllocator,
                        DispatchClaimService.EventTypeJobCompleted,
                        aggregateId: attempt.PrintJobId.Value,
                        printerId: attempt.PrinterId,
                        attemptId: attempt.Id,
                        aggregateRowVersion: attempt.PrintJob?.RowVersion,
                        failureCode: null,
                        payloadJson: QueueLifecycleEventWriter.BuildTerminalPayload(
                            attempt.PrintJobId.Value,
                            attempt.PrinterId,
                            attempt.Id,
                            PrintJobStatus.Completed.ToString(),
                            attempt.PrintJob?.JobKind?.ToString() ?? nameof(JobKind.Standard),
                            failureCode: null),
                        ct);
                }

                logger.LogInformation(
                    "[Reconciliation] Attempt {AttemptId} found in backend history after idle → terminal lease released",
                    attempt.Id);
                break;

            case BackendReconciliationOutcome.AbsentFromBackend:
                // The backend has no record of this job/start — it failed or was never accepted.
                // Release the lease so the job can be re-dispatched (queue) or the printer is freed (ad-hoc).
                attempt.Outcome = DispatchAttemptOutcome.FailedBeforeStart;
                attempt.ErrorCode = "reconciliation_absent";
                attempt.ErrorDetail = "Backend reconciliation found no active or historical record of this job.";
                attempt.IsRetryable = attempt.PrintJobId is not null; // queue jobs are retryable; ad-hoc are not
                attempt.RequiresReconciliation = false;
                attempt.UpdatedAtUtc = DateTime.UtcNow;
                attempt.BackendCallPhase = DispatchBackendCallPhase.Terminal;
                attempt.TerminalAtUtc = DateTime.UtcNow;
                ClearStartBarrier(activeState, attempt.Id);

                if (attempt.PrintJob is not null && attempt.PrintJob.Status == PrintJobStatus.Starting)
                {
                    attempt.PrintJob.Status = PrintJobStatus.Assigned;
                    attempt.PrintJob.ActualStartTime = null;
                    attempt.PrintJob.UpdatedAt = DateTime.UtcNow;
                    AddHistory(
                        db,
                        attempt.PrintJob.Id,
                        PrintJobStatus.Starting,
                        PrintJobStatus.Assigned,
                        "Backend reconciliation proved the start absent.");
                }

                // Clear the active lease on the printer dispatch state (queue and ad-hoc).
                if (activeState.ActiveDispatchAttemptId == attempt.Id)
                {
                    activeState.ActiveJobId = null;
                    activeState.ActiveDispatchAttemptId = null;
                    activeState.QueueRevision++;
                }

                await SetBedClearCommandStatusAsync(
                    db,
                    attempt.Id,
                    BedClearCommandStatus.Rejected,
                    ct);

                _ = QueueAuditWriter.Add(
                    db,
                    attempt.ActorSubject,
                    QueueAuditOperations.Reconciliation,
                    QueueAuditOutcomes.Failed,
                    attempt.PrintJobId is null ? nameof(Printer) : nameof(PrintJob),
                    resourceId: attempt.PrintJobId ?? (Guid?)attempt.PrinterId,
                    printerId: attempt.PrinterId,
                    printJobId: attempt.PrintJobId,
                    dispatchAttemptId: attempt.Id,
                    reasonCode: "reconciliation_absent",
                    detail: new { startPathKind = attempt.StartPathKind });

                // Emit a durable lifecycle event for the absent-from-backend reconciliation
                // so the outbox publisher broadcasts the lease release to authorized groups.
                if (attempt.PrintJobId is not null && sequenceAllocator is not null)
                {
                    await DispatchClaimService.AddLifecycleOutboxEventAsync(
                        db,
                        sequenceAllocator,
                        DispatchClaimService.EventTypeReconciliationAbsent,
                        aggregateId: attempt.PrintJobId.Value,
                        printerId: attempt.PrinterId,
                        attemptId: attempt.Id,
                        aggregateRowVersion: attempt.PrintJob?.RowVersion,
                        failureCode: "reconciliation_absent",
                        payloadJson: JsonSerializer.Serialize(new
                        {
                            jobId = attempt.PrintJobId,
                            printerId = attempt.PrinterId,
                            attemptId = attempt.Id,
                            startPathKind = attempt.StartPathKind,
                        }),
                        ct);
                }

                await FinalizeBackendStartCommandAsync(
                    db,
                    attempt,
                    QueueOutboxEventStatus.DeadLettered,
                    failureCode: "reconciliation_absent",
                    lastError: "Backend reconciliation proved that the start command was not accepted.",
                    ct);

                logger.LogWarning(
                    "[Reconciliation] Attempt {AttemptId} absent from backend → lease released, job re-queued",
                    attempt.Id);
                break;
            default:
                // Backend is unreachable OR is printing something we cannot positively
                // match. Either way the state is INDETERMINATE — leave the lease intact
                // and retry. An unmatched printing backend must never be treated as absent.
                MarkRequiresReconciliation(
                    attempt,
                    "Backend state indeterminate during reconciliation — lease retained, will retry.");

                _ = QueueAuditWriter.Add(
                    db,
                    attempt.ActorSubject,
                    QueueAuditOperations.Reconciliation,
                    QueueAuditOutcomes.Unknown,
                    attempt.PrintJobId is null ? nameof(Printer) : nameof(PrintJob),
                    resourceId: attempt.PrintJobId ?? (Guid?)attempt.PrinterId,
                    printerId: attempt.PrinterId,
                    printJobId: attempt.PrintJobId,
                    dispatchAttemptId: attempt.Id,
                    reasonCode: "reconciliation_indeterminate",
                    detail: new { startPathKind = attempt.StartPathKind });

                // Emit a durable lifecycle event for the indeterminate reconciliation state.
                if (attempt.PrintJobId is not null && sequenceAllocator is not null)
                {
                    await DispatchClaimService.AddLifecycleOutboxEventAsync(
                        db,
                        sequenceAllocator,
                        DispatchClaimService.EventTypeReconciliationIndeterminate,
                        aggregateId: attempt.PrintJobId.Value,
                        printerId: attempt.PrinterId,
                        attemptId: attempt.Id,
                        aggregateRowVersion: null,
                        failureCode: "reconciliation_indeterminate",
                        payloadJson: JsonSerializer.Serialize(new
                        {
                            jobId = attempt.PrintJobId,
                            printerId = attempt.PrinterId,
                            attemptId = attempt.Id,
                            startPathKind = attempt.StartPathKind,
                            requiresReconciliation = true,
                        }),
                        ct);
                }

                break;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static void MarkRequiresReconciliation(QueueDispatchAttempt attempt, string reason)
    {
        attempt.Outcome = DispatchAttemptOutcome.Unknown;
        attempt.RequiresReconciliation = true;
        attempt.IsRetryable = false;
        attempt.ErrorDetail = reason;
        attempt.UpdatedAtUtc = DateTime.UtcNow;
        attempt.BackendCallPhase = DispatchBackendCallPhase.AwaitingReconciliation;
    }

    private static void ClearStartBarrier(
        PrinterDispatchState state,
        Guid attemptId)
    {
        if (state.PhysicalControlCommandId != attemptId ||
            !string.Equals(
                state.PhysicalControlOperation,
                "start",
                StringComparison.Ordinal))
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

    private static void AddHistory(
        AppDbContext db,
        Guid jobId,
        PrintJobStatus fromState,
        PrintJobStatus toState,
        string notes)
    {
        DateTime now = DateTime.UtcNow;
        db.JobStateHistories.Add(new JobStateHistory
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            FromState = fromState.ToString(),
            ToState = toState.ToString(),
            TransitionedAtUtc = now,
            CreatedAt = now,
            Notes = notes,
        });
    }

    private static async Task SetBedClearCommandStatusAsync(
        AppDbContext db,
        Guid attemptId,
        BedClearCommandStatus status,
        CancellationToken ct)
    {
        BedClearCommandRecord? command = await db.BedClearCommandRecords
            .FirstOrDefaultAsync(record => record.DispatchAttemptId == attemptId, ct);
        if (command is not null)
        {
            command.Status = status;
            command.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static async Task FinalizeBackendStartCommandAsync(
        AppDbContext db,
        QueueDispatchAttempt attempt,
        QueueOutboxEventStatus status,
        string? failureCode,
        string? lastError,
        CancellationToken ct)
    {
        if (attempt.PrintJobId is null)
        {
            return;
        }

        List<QueueDispatchOutbox> commands = await db.QueueDispatchOutbox
            .Where(command =>
                command.EventType == BedClearAcknowledgementService.BackendStartCommandEventType &&
                command.Status == QueueOutboxEventStatus.Processing &&
                (command.AttemptId == attempt.Id ||
                 (command.AttemptId == null && command.AggregateId == attempt.PrintJobId.Value)))
            .ToListAsync(ct);

        foreach (QueueDispatchOutbox command in commands)
        {
            command.AttemptId = attempt.Id;
            command.Status = status;
            command.FailureCode = failureCode;
            command.LastError = lastError;
            command.CompletedAtUtc = DateTime.UtcNow;
            command.RetryAfterUtc = null;
        }
    }

    private async Task<BackendReconciliationOutcome> QueryBackendOutcomeAsync(
        IPrintersService printersSvc,
        QueueDispatchAttempt attempt,
        CancellationToken ct)
    {
        try
        {
            // Query the current authoritative printer status from the backend.
            PrinterStatusDto status = await printersSvc.GetStatusDtoAsync(attempt.PrinterId, ct);

            if (!status.IsOnline)
            {
                logger.LogDebug(
                    "[Reconciliation] Printer {PrinterId} is offline — cannot reconcile attempt {AttemptId}",
                    attempt.PrinterId, attempt.Id);
                return BackendReconciliationOutcome.BackendIndeterminate;
            }

            string? currentFile = status.FileName ?? status.JobName;

            // If the backend is printing, try to positively match it against the identity
            // persisted BEFORE the network call (backend job id, command id, or file name).
            if (IsActiveState(status.State))
            {
                if (MatchesPersistedIdentity(attempt, currentFile))
                {
                    return BackendReconciliationOutcome.ActiveOnBackend;
                }

                // CRITICAL: a physically printing backend that we cannot positively match
                // is INDETERMINATE, never absent. Clearing the lease here would allow a
                // duplicate start on a printer that is already running a job.
                logger.LogWarning(
                    "[Reconciliation] Printer {PrinterId} is printing '{CurrentFile}' which does not match " +
                    "attempt {AttemptId} (backendJobId='{BackendJobId}', commandId='{CommandId}', file='{File}') — " +
                    "treating as INDETERMINATE; the lease is retained to prevent a duplicate start",
                    attempt.PrinterId,
                    currentFile ?? "(unknown)",
                    attempt.Id,
                    attempt.BackendJobId ?? "(none)",
                    attempt.BackendCommandId ?? "(none)",
                    attempt.BackendFileName ?? "(none)");

                return BackendReconciliationOutcome.BackendIndeterminate;
            }

            if (!IsExplicitlyIdleOrTerminalState(status.State))
            {
                logger.LogWarning(
                    "[Reconciliation] Printer {PrinterId} reported unknown online state '{State}' for attempt {AttemptId}; " +
                    "absence cannot be proven and every dispatch fence is retained",
                    attempt.PrinterId,
                    status.State ?? "(null)",
                    attempt.Id);
                return BackendReconciliationOutcome.BackendIndeterminate;
            }

            // The backend is explicitly idle/terminal. Both the exact provider-id probe and
            // the list probe must complete authoritatively before absence is destructive.
            logger.LogDebug(
                "[Reconciliation] Printer {PrinterId} is quiescent (state='{State}') — probing history for attempt {AttemptId}",
                attempt.PrinterId, status.State, attempt.Id);

            string? backendJobId = attempt.BackendJobId;
            bool hasBackendJobId = !string.IsNullOrWhiteSpace(backendJobId);
            if (hasBackendJobId)
            {
                string authoritativeBackendJobId = backendJobId!;
                try
                {
                    HistoryJob? historyJob = await printersSvc.GetHistoryJobAsync(
                        attempt.PrinterId,
                        authoritativeBackendJobId,
                        ct);
                    if (historyJob is null)
                    {
                        logger.LogWarning(
                            "[Reconciliation] Provider-id probe returned null for '{Identity}' on printer {PrinterId}; " +
                            "the result is unavailable, not authoritative absence",
                            authoritativeBackendJobId,
                            attempt.PrinterId);
                        return BackendReconciliationOutcome.BackendIndeterminate;
                    }

                    attempt.BackendFileIdentity = historyJob.Filename;
                    logger.LogInformation(
                        "[Reconciliation] Attempt {AttemptId} found by provider history id '{Identity}'",
                        attempt.Id,
                        authoritativeBackendJobId);
                    return BackendReconciliationOutcome.CompletedOnBackend;
                }
                catch (KeyNotFoundException)
                {
                    logger.LogDebug(
                        "[Reconciliation] Provider history id '{Identity}' is authoritatively absent on printer {PrinterId}",
                        authoritativeBackendJobId,
                        attempt.PrinterId);
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException)
                {
                    logger.LogDebug(
                        histEx,
                        "[Reconciliation] Provider-id probe failed for '{Identity}' on printer {PrinterId}",
                        authoritativeBackendJobId,
                        attempt.PrinterId);
                    return BackendReconciliationOutcome.BackendIndeterminate;
                }
            }

            HistoryListProbeResult? historyProbe =
                await printersSvc.ProbeHistoryListAsync(
                    attempt.PrinterId,
                    limit: 100,
                    start: null,
                    since: attempt.ClaimedAtUtc.AddMinutes(-5),
                    before: null,
                    order: "desc",
                    ct);
            if (historyProbe?.Status != HistoryProbeStatus.Authoritative ||
                historyProbe.History is null)
            {
                logger.LogWarning(
                    "[Reconciliation] History-list probe for attempt {AttemptId} was {Status}; " +
                    "absence cannot be proven and every dispatch fence is retained",
                    attempt.Id,
                    historyProbe?.Status.ToString() ?? "null");
                return BackendReconciliationOutcome.BackendIndeterminate;
            }

            HistoryJob? exactFileMatch = historyProbe.History.Jobs.FirstOrDefault(
                candidate => MatchesHistoryIdentity(attempt, candidate));
            if (exactFileMatch is not null)
            {
                if (!string.IsNullOrWhiteSpace(exactFileMatch.JobId))
                {
                    attempt.BackendJobId = exactFileMatch.JobId;
                }

                attempt.BackendFileIdentity = exactFileMatch.Filename;
                logger.LogInformation(
                    "[Reconciliation] Attempt {AttemptId} matched provider history file '{FileName}' as id '{HistoryId}'",
                    attempt.Id,
                    exactFileMatch.Filename,
                    exactFileMatch.JobId);
                return BackendReconciliationOutcome.CompletedOnBackend;
            }

            if (!hasBackendJobId)
            {
                logger.LogWarning(
                    "[Reconciliation] Attempt {AttemptId} has no authoritative backend job id and no exact history match; " +
                    "absence cannot be proven and every dispatch fence is retained",
                    attempt.Id);
                return BackendReconciliationOutcome.BackendIndeterminate;
            }

            // Idle backend, every exact identity was authoritatively absent.
            return BackendReconciliationOutcome.AbsentFromBackend;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[Reconciliation] Backend query failed for printer {PrinterId} during reconciliation of attempt {AttemptId}",
                attempt.PrinterId, attempt.Id);
            return BackendReconciliationOutcome.BackendIndeterminate;
        }
    }

    /// <summary>
    /// Positively matches a printing backend against the identity persisted for this attempt
    /// BEFORE the network call. Any of the backend job id, backend command id or the exact
    /// backend file name is sufficient.
    /// </summary>
    private static bool MatchesPersistedIdentity(QueueDispatchAttempt attempt, string? currentFile)
    {
        if (string.IsNullOrWhiteSpace(currentFile))
        {
            return false;
        }

        string normalizedCurrent = NormalizeBackendIdentity(currentFile);
        string currentName = Path.GetFileName(normalizedCurrent);
        foreach (string identity in EnumerateBackendFileIdentities(attempt))
        {
            string normalizedIdentity = NormalizeBackendIdentity(identity);
            if (string.Equals(normalizedCurrent, normalizedIdentity, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currentName, Path.GetFileName(normalizedIdentity), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsActiveState(string? state) =>
        !string.IsNullOrWhiteSpace(state) &&
        state.Trim() is var normalized &&
        (normalized.Equals("printing", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("starting", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("paused", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("heating", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("pausing", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("resuming", StringComparison.OrdinalIgnoreCase));

    private static bool IsExplicitlyIdleOrTerminalState(string? state) =>
        !string.IsNullOrWhiteSpace(state) &&
        state.Trim() is var normalized &&
        (normalized.Equals("idle", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("ready", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("standby", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("operational", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("finished", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("canceled", StringComparison.OrdinalIgnoreCase) ||
         normalized.Equals("failed", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeBackendIdentity(string value) =>
        value.Trim().Replace('\\', '/').TrimStart('/');

    private static bool MatchesHistoryIdentity(
        QueueDispatchAttempt attempt,
        HistoryJob history)
    {
        if (!string.IsNullOrWhiteSpace(attempt.BackendJobId) &&
            string.Equals(
                history.JobId,
                attempt.BackendJobId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return EnumerateBackendFileIdentities(attempt).Any(identity =>
            string.Equals(
                Path.GetFileName(NormalizeBackendIdentity(history.Filename)),
                Path.GetFileName(NormalizeBackendIdentity(identity)),
                StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateBackendFileIdentities(
        QueueDispatchAttempt attempt)
    {
        if (!string.IsNullOrWhiteSpace(attempt.BackendFileIdentity))
        {
            yield return attempt.BackendFileIdentity;
        }

        if (!string.IsNullOrWhiteSpace(attempt.BackendFileName))
        {
            yield return attempt.BackendFileName;
        }

        if (!string.IsNullOrWhiteSpace(attempt.BackendCorrelationId))
        {
            yield return attempt.BackendCorrelationId;
        }
    }

    private enum BackendReconciliationOutcome
    {
        /// <summary>The backend is actively printing (or historically ran) the reconciled job.</summary>
        ActiveOnBackend,

        /// <summary>The backend is idle and exact history proves the job ran.</summary>
        CompletedOnBackend,

        /// <summary>The backend is idle and has no record of the job across all known identities.</summary>
        AbsentFromBackend,

        /// <summary>
        /// The backend is unreachable, errored, or is printing something that cannot be
        /// positively matched. The lease MUST be retained.
        /// </summary>
        BackendIndeterminate,
    }
}

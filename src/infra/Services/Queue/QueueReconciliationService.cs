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

    private async Task ReconcileStaleAttemptsAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IDbOutboxSequenceAllocator? sequenceAllocator = scope.ServiceProvider.GetService<IDbOutboxSequenceAllocator>();

        DateTime staleCutoff = DateTime.UtcNow - StaleAttemptAge;

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
                attempt.BackendCallPhase = DispatchBackendCallPhase.Reconciled;

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

                PrinterDispatchState? completedState = await db.PrinterDispatchStates
                    .FirstOrDefaultAsync(s => s.PrinterId == attempt.PrinterId, ct);
                if (completedState is not null && completedState.ActiveDispatchAttemptId == attempt.Id)
                {
                    completedState.ActiveJobId = null;
                    completedState.ActiveDispatchAttemptId = null;
                    completedState.QueueRevision++;
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
                PrinterDispatchState? dispatchState = await db.PrinterDispatchStates
                    .FirstOrDefaultAsync(s => s.PrinterId == attempt.PrinterId, ct);

                if (dispatchState is not null && dispatchState.ActiveDispatchAttemptId == attempt.Id)
                {
                    dispatchState.ActiveJobId = null;
                    dispatchState.ActiveDispatchAttemptId = null;
                    dispatchState.QueueRevision++;
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

                await ExpireIndeterminateBackendStartCommandAsync(db, attempt, ct);

                break;
        }
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

    private static async Task ExpireIndeterminateBackendStartCommandAsync(
        AppDbContext db,
        QueueDispatchAttempt attempt,
        CancellationToken ct)
    {
        if (attempt.PrintJobId is null)
        {
            return;
        }

        DateTime manualReviewCutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        List<QueueDispatchOutbox> commands = await db.QueueDispatchOutbox
            .Where(command =>
                command.EventType == BedClearAcknowledgementService.BackendStartCommandEventType &&
                command.Status == QueueOutboxEventStatus.Processing &&
                command.CreatedAtUtc <= manualReviewCutoff &&
                (command.AttemptId == attempt.Id ||
                 (command.AttemptId == null && command.AggregateId == attempt.PrintJobId.Value)))
            .ToListAsync(ct);

        foreach (QueueDispatchOutbox command in commands)
        {
            command.AttemptId = attempt.Id;
            command.Status = QueueOutboxEventStatus.DeadLettered;
            command.FailureCode = "manual_reconciliation_required";
            command.LastError =
                "Backend outcome remained indeterminate for 24 hours. The dispatch lease remains fenced for manual review.";
            command.CompletedAtUtc = DateTime.UtcNow;
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

            // Backend is idle. Confirm via the backend job/command history APIs before
            // declaring absence.
            logger.LogDebug(
                "[Reconciliation] Printer {PrinterId} is idle (state='{State}') — probing history for attempt {AttemptId}",
                attempt.PrinterId, status.State, attempt.Id);

            bool historyProbeFailed = false;
            foreach (string identity in EnumerateBackendIdentities(attempt))
            {
                try
                {
                    HistoryJob historyJob = await printersSvc.GetHistoryJobAsync(
                        attempt.PrinterId, identity, ct);

                    if (historyJob is not null)
                    {
                        logger.LogInformation(
                            "[Reconciliation] Attempt {AttemptId} found in backend history via '{Identity}' — treating as Accepted",
                            attempt.Id, identity);
                        return BackendReconciliationOutcome.CompletedOnBackend;
                    }
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException)
                {
                    logger.LogDebug(
                        histEx,
                        "[Reconciliation] History probe failed for identity '{Identity}' on printer {PrinterId}",
                        identity, attempt.PrinterId);

                    // A failed probe is not evidence of absence. Continue trying every
                    // persisted identity in case another one can prove completion.
                    historyProbeFailed = true;
                }
            }

            if (historyProbeFailed)
            {
                return BackendReconciliationOutcome.BackendIndeterminate;
            }

            // Idle backend, all history probes returned nothing: the job is genuinely absent.
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
        foreach (string identity in EnumerateBackendIdentities(attempt))
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

    private static string NormalizeBackendIdentity(string value) =>
        value.Trim().Replace('\\', '/').TrimStart('/');

    private static IEnumerable<string> EnumerateBackendIdentities(QueueDispatchAttempt attempt)
    {
        if (!string.IsNullOrWhiteSpace(attempt.BackendJobId))
        {
            yield return attempt.BackendJobId;
        }

        if (!string.IsNullOrWhiteSpace(attempt.BackendCommandId))
        {
            yield return attempt.BackendCommandId;
        }

        if (!string.IsNullOrWhiteSpace(attempt.BackendFileName))
        {
            yield return attempt.BackendFileName;
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

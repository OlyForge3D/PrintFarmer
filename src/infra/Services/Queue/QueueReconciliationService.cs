using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
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

        List<QueueDispatchAttempt> toReconcile = staleAttempts
            .Concat(flaggedAttempts)
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
            await ReconcileSingleAttemptAsync(db, printersSvc, attempt, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileSingleAttemptAsync(
        AppDbContext db,
        IPrintersService? printersSvc,
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

        switch (outcome)
        {
            case BackendReconciliationOutcome.ActiveOnBackend:
                // The backend is actively printing this job — advance to Printing.
                attempt.Outcome = DispatchAttemptOutcome.Accepted;
                attempt.BackendAcceptedAtUtc ??= DateTime.UtcNow;
                attempt.RequiresReconciliation = false;
                attempt.UpdatedAtUtc = DateTime.UtcNow;

                if (attempt.PrintJob is not null && attempt.PrintJob.Status == PrintJobStatus.Starting)
                {
                    attempt.PrintJob.Status = PrintJobStatus.Printing;
                    attempt.PrintJob.UpdatedAt = DateTime.UtcNow;
                }

                _ = QueueAuditWriter.Add(
                    db,
                    attempt.ActorSubject,
                    QueueAuditOperations.Reconciliation,
                    QueueAuditOutcomes.Success,
                    nameof(PrintJob),
                    resourceId: attempt.PrintJobId,
                    printerId: attempt.PrinterId,
                    printJobId: attempt.PrintJobId,
                    dispatchAttemptId: attempt.Id,
                    reasonCode: "reconciliation_active",
                    detail: new { startPathKind = attempt.StartPathKind });

                logger.LogInformation(
                    "[Reconciliation] Attempt {AttemptId} confirmed active on backend → advanced to Printing",
                    attempt.Id);
                break;

            case BackendReconciliationOutcome.AbsentFromBackend:
                // The backend has no record of this job — it failed or was never accepted.
                // Release the lease so the job can be re-dispatched.
                attempt.Outcome = DispatchAttemptOutcome.FailedBeforeStart;
                attempt.ErrorCode = "reconciliation_absent";
                attempt.ErrorDetail = "Backend reconciliation found no active or historical record of this job.";
                attempt.IsRetryable = true;
                attempt.RequiresReconciliation = false;
                attempt.UpdatedAtUtc = DateTime.UtcNow;

                if (attempt.PrintJob is not null && attempt.PrintJob.Status == PrintJobStatus.Starting)
                {
                    attempt.PrintJob.Status = PrintJobStatus.Assigned;
                    attempt.PrintJob.ActualStartTime = null;
                    attempt.PrintJob.UpdatedAt = DateTime.UtcNow;
                }

                // Clear the active lease on the printer dispatch state.
                PrinterDispatchState? dispatchState = await db.PrinterDispatchStates
                    .FirstOrDefaultAsync(s => s.PrinterId == attempt.PrinterId, ct);

                if (dispatchState is not null && dispatchState.ActiveDispatchAttemptId == attempt.Id)
                {
                    dispatchState.ActiveJobId = null;
                    dispatchState.ActiveDispatchAttemptId = null;
                }

                _ = QueueAuditWriter.Add(
                    db,
                    attempt.ActorSubject,
                    QueueAuditOperations.Reconciliation,
                    QueueAuditOutcomes.Failed,
                    nameof(PrintJob),
                    resourceId: attempt.PrintJobId,
                    printerId: attempt.PrinterId,
                    printJobId: attempt.PrintJobId,
                    dispatchAttemptId: attempt.Id,
                    reasonCode: "reconciliation_absent",
                    detail: new { startPathKind = attempt.StartPathKind });

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
                    nameof(PrintJob),
                    resourceId: attempt.PrintJobId,
                    printerId: attempt.PrinterId,
                    printJobId: attempt.PrintJobId,
                    dispatchAttemptId: attempt.Id,
                    reasonCode: "reconciliation_indeterminate",
                    detail: new { startPathKind = attempt.StartPathKind });
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
            if (status.State is "printing" or "starting" or "paused")
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
                        return BackendReconciliationOutcome.ActiveOnBackend;
                    }
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException)
                {
                    logger.LogDebug(
                        histEx,
                        "[Reconciliation] History probe failed for identity '{Identity}' on printer {PrinterId}",
                        identity, attempt.PrinterId);

                    // A failed probe is not evidence of absence.
                    return BackendReconciliationOutcome.BackendIndeterminate;
                }
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

        foreach (string identity in EnumerateBackendIdentities(attempt))
        {
            if (currentFile.Contains(identity, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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

        /// <summary>The backend is idle and has no record of the job across all known identities.</summary>
        AbsentFromBackend,

        /// <summary>
        /// The backend is unreachable, errored, or is printing something that cannot be
        /// positively matched. The lease MUST be retained.
        /// </summary>
        BackendIndeterminate,
    }
}

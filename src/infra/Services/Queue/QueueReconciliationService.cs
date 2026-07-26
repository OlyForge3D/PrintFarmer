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

                logger.LogWarning(
                    "[Reconciliation] Attempt {AttemptId} absent from backend → lease released, job re-queued",
                    attempt.Id);
                break;
            default:
                // Backend is unreachable — leave in current state, retry on next cycle.
                MarkRequiresReconciliation(
                    attempt,
                    "Backend unreachable during reconciliation — will retry on next cycle.");
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
                return BackendReconciliationOutcome.BackendUnreachable;
            }

            // If the backend is printing and the filename matches our job, it's active.
            if (status.State is "printing" or "starting" or "paused")
            {
                // If we have a BackendJobId, try to match it against the current filename.
                if (!string.IsNullOrWhiteSpace(attempt.BackendJobId))
                {
                    string? currentFile = status.FileName ?? status.JobName;
                    if (currentFile is not null &&
                        currentFile.Contains(attempt.BackendJobId, StringComparison.OrdinalIgnoreCase))
                    {
                        return BackendReconciliationOutcome.ActiveOnBackend;
                    }
                }

                // Backend is printing a different job — ours is absent.
                logger.LogDebug(
                    "[Reconciliation] Printer {PrinterId} is printing a different job " +
                    "(current: '{CurrentFile}', expected BackendJobId: '{BackendJobId}') — attempt {AttemptId} is absent",
                    attempt.PrinterId,
                    status.FileName ?? status.JobName ?? "(unknown)",
                    attempt.BackendJobId ?? "(none)",
                    attempt.Id);
                return BackendReconciliationOutcome.AbsentFromBackend;
            }

            // Backend is idle — our job is absent (never started or already finished).
            logger.LogDebug(
                "[Reconciliation] Printer {PrinterId} is idle (state='{State}') — attempt {AttemptId} is absent",
                attempt.PrinterId, status.State, attempt.Id);

            // If we have a BackendJobId, try to confirm via history.
            if (!string.IsNullOrWhiteSpace(attempt.BackendJobId))
            {
                try
                {
                    HistoryJob historyJob = await printersSvc.GetHistoryJobAsync(
                        attempt.PrinterId, attempt.BackendJobId, ct);

                    // Job exists in history — it ran and completed (or failed).
                    // Treat as Accepted (the print actually ran).
                    if (historyJob is not null)
                    {
                        logger.LogInformation(
                            "[Reconciliation] Attempt {AttemptId} found in backend history — treating as Accepted",
                            attempt.Id);
                        return BackendReconciliationOutcome.ActiveOnBackend;
                    }
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException)
                {
                    logger.LogDebug(
                        histEx,
                        "[Reconciliation] Could not query history for BackendJobId='{BackendJobId}' on printer {PrinterId}",
                        attempt.BackendJobId, attempt.PrinterId);

                    // Fall through to AbsentFromBackend
                }
            }

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
            return BackendReconciliationOutcome.BackendUnreachable;
        }
    }

    private enum BackendReconciliationOutcome
    {
        /// <summary>The backend is actively printing the reconciled job.</summary>
        ActiveOnBackend,

        /// <summary>The backend has no record of the job (idle, or printing another job).</summary>
        AbsentFromBackend,

        /// <summary>The backend is unreachable or returned an error.</summary>
        BackendUnreachable,
    }
}

using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
/// periodically probes for such orphaned attempts so operators can investigate
/// or the system can clear them once the printer backend confirms the outcome.
///
/// <strong>Safety:</strong> The service never blindly retries an uncertain attempt.
/// It logs and marks the attempt for operator review; human intervention or a
/// printer-backend query resolves the final state.
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

        // Find Starting jobs with attempts that are in-progress but old enough to be stale.
        List<QueueDispatchAttempt> staleAttempts = await db.QueueDispatchAttempts
            .Include(a => a.PrintJob)
            .Where(a =>
                a.Outcome == DispatchAttemptOutcome.InProgress &&
                a.ClaimedAtUtc < staleCutoff &&
                a.PrintJob != null &&
                a.PrintJob.Status == PrintJobStatus.Starting)
            .Take(20)
            .ToListAsync(ct);

        if (staleAttempts.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "[Reconciliation] Found {Count} stale InProgress dispatch attempts older than {Age} minutes",
            staleAttempts.Count,
            StaleAttemptAge.TotalMinutes);

        foreach (QueueDispatchAttempt attempt in staleAttempts)
        {
            logger.LogWarning(
                "[Reconciliation] Stale attempt {AttemptId} Job={JobId} Printer={PrinterId} StartPath={Path} ClaimedAt={ClaimedAt:u} — marking RequiresReconciliation",
                attempt.Id,
                attempt.PrintJobId,
                attempt.PrinterId,
                attempt.StartPathKind,
                attempt.ClaimedAtUtc);

            attempt.Outcome = DispatchAttemptOutcome.Unknown;
            attempt.RequiresReconciliation = true;
            attempt.IsRetryable = false;
            attempt.ErrorDetail = $"Attempt was stale (InProgress for >{StaleAttemptAge.TotalMinutes:F0} min). Manual reconciliation required.";
            attempt.UpdatedAtUtc = DateTime.UtcNow;
        }

        // Find InProgress attempts where RequiresReconciliation is already set
        // but the outcome is still Unknown — log them for operator visibility.
        List<QueueDispatchAttempt> pendingReconciliation = await db.QueueDispatchAttempts
            .Include(a => a.PrintJob)
            .Where(a =>
                a.Outcome == DispatchAttemptOutcome.Unknown &&
                a.RequiresReconciliation &&
                a.PrintJob != null &&
                a.PrintJob.Status == PrintJobStatus.Starting)
            .Take(20)
            .ToListAsync(ct);

        foreach (QueueDispatchAttempt attempt in pendingReconciliation)
        {
            logger.LogWarning(
                "[Reconciliation] Attempt {AttemptId} Job={JobId} Printer={PrinterId} is awaiting manual reconciliation (job still Starting).",
                attempt.Id,
                attempt.PrintJobId,
                attempt.PrinterId);
        }

        await db.SaveChangesAsync(ct);
    }
}

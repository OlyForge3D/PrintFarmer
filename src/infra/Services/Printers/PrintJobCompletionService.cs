using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.AutoPrint;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Service for synchronizing print job completion status when printers finish printing.
/// </summary>
public class PrintJobCompletionService : IPrintJobCompletionService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<PrinterHub> _hub;
    private readonly ILogger<PrintJobCompletionService> _logger;
    private readonly INotificationService? _notificationService;
    private readonly IPrintCostCalculator? _costCalculator;
    private readonly IAutoPrintService? _autoPrintService;

    /// <summary>
    /// Printer states that indicate a print has completed successfully.
    /// Covers Moonraker, PrusaLink, OctoPrint, and SDCP backends.
    /// </summary>
    private static readonly string[] CompletionStates =
    [

        // Moonraker
        "standby", "complete", "idle",

        // PrusaLink (case-insensitive)
        "finished", "ready",

        // OctoPrint
        "operational", "finishing"
    ];

    /// <summary>
    /// Printer states that indicate a print has failed.
    /// Covers Moonraker, PrusaLink, OctoPrint, and SDCP backends.
    /// </summary>
    private static readonly string[] FailureStates =
    [

        // Moonraker
        "error", "cancelled",

        // PrusaLink
        "stopped",

        // OctoPrint
        "error", "offline"
    ];

    /// <summary>
    /// Printer states that indicate a print is in progress.
    /// Covers all backends (case-insensitive comparison).
    /// </summary>
    private static readonly string[] PrintingStates = ["printing"];

    public PrintJobCompletionService(
        AppDbContext db,
        IHubContext<PrinterHub> hub,
        ILogger<PrintJobCompletionService> logger,
        INotificationService? notificationService = null,
        IPrintCostCalculator? costCalculator = null,
        IAutoPrintService? autoPrintService = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationService = notificationService;
        _costCalculator = costCalculator;
        _autoPrintService = autoPrintService;
    }

    /// <summary>
    /// Checks if a state represents a completed print.
    /// </summary>
    public static bool IsCompletionState(string? state) =>
        state != null && CompletionStates.Contains(state.ToLowerInvariant());

    /// <summary>
    /// Checks if a state represents a failed print.
    /// </summary>
    public static bool IsFailureState(string? state) =>
        state != null && FailureStates.Contains(state.ToLowerInvariant());

    /// <summary>
    /// Checks if a state represents an active print in progress.
    /// Supports Moonraker, PrusaLink, OctoPrint, and SDCP state names.
    /// </summary>
    public static bool IsPrintingState(string? state) =>
        state != null && PrintingStates.Contains(state.ToLowerInvariant());

    /// <inheritdoc />
    public async Task<bool> MarkCurrentJobAsCompletedAsync(Guid printerId, string completionState, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[PrintJobCompletionService] Marking current job as completed for printer {PrinterId} (state: {CompletionState})",
            printerId,
            completionState);

        // A printer can only have one real "current job", but the DB may end up with
        // multiple active rows (e.g., history seeding/import edge cases). Reconcile all.
        List<PrintJob> activeJobs = await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .Where(j =>
                j.AssignedPrinterId == printerId &&
                (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing))
            .OrderByDescending(j => j.ActualStartTime ?? j.QueuedAt)
            .ToListAsync(ct);

        if (activeJobs.Count == 0)
        {
            _logger.LogDebug(
                "[PrintJobCompletionService] No active job found for printer {PrinterId}. Nothing to complete.",
                printerId);
            return false;
        }

        DateTime completedAtUtc = DateTime.UtcNow;

        foreach (PrintJob job in activeJobs)
        {
            // Multi-copy support: increment CompletedCopies instead of immediately completing
            job.CompletedCopies++;

            if (job.CompletedCopies >= job.Copies)
            {
                // All copies done — mark job as completed
                job.Status = PrintJobStatus.Completed;
                job.ActualEndTime = completedAtUtc;

                if (job.ActualStartTime.HasValue)
                {
                    job.ActualPrintTime = job.ActualEndTime - job.ActualStartTime;
                }
            }
            else
            {
                // More copies remaining — return to queued for next copy
                job.Status = PrintJobStatus.Queued;
                job.ActualStartTime = null;
                job.UpdatedAt = completedAtUtc;
            }
        }

        PrintJob primaryJob = activeJobs[0];

        // Calculate actual cost if cost calculator is available
        if (_costCalculator != null)
        {
            try
            {
                primaryJob.ActualCost = await _costCalculator.CalculateActualCostAsync(
                    primaryJob.SpoolmanFilamentId,
                    primaryJob.ActualFilamentUsage,
                    primaryJob.EstimatedFilamentUsage,
                    ct);

                if (primaryJob.ActualCost.HasValue)
                {
                    _logger.LogInformation(
                        "[PrintJobCompletionService] Calculated actual cost for job {JobId}: {Cost:C2}",
                        primaryJob.Id, primaryJob.ActualCost.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PrintJobCompletionService] Failed to calculate actual cost for job {JobId}", primaryJob.Id);
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[PrintJobCompletionService] Job {JobId} ({JobName}) marked as completed. Duration: {Duration}",
            primaryJob.Id,
            primaryJob.Name ?? primaryJob.GcodeFile?.Name ?? "Unknown",
            primaryJob.ActualPrintTime);

        // Broadcast job queue update via SignalR
        await BroadcastJobQueueUpdateAsync(printerId, ct);

        // Send notification if configured
        if (_notificationService != null)
        {
            try
            {
                await _notificationService.SendJobCompletedAsync(
                    primaryJob.Id.ToString(),
                    primaryJob.Name ?? primaryJob.GcodeFile?.Name ?? "Print Job",
                    primaryJob.AssignedPrinter?.Name,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PrintJobCompletionService] Failed to send job completion notification");
            }
        }

        // Trigger auto-print ready-gate if enabled
        if (_autoPrintService != null)
        {
            try
            {
                await _autoPrintService.TransitionToPendingReadyAsync(printerId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PrintJobCompletionService] Failed to trigger auto-print transition for printer {PrinterId}", printerId);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkCurrentJobAsFailedAsync(Guid printerId, string failureReason, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "[PrintJobCompletionService] Marking current job as failed for printer {PrinterId} (reason: {FailureReason})",
            printerId,
            failureReason);

        // A printer can only have one real "current job", but the DB may end up with
        // multiple active rows (e.g., history seeding/import edge cases). Reconcile all.
        List<PrintJob> activeJobs = await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .Where(j =>
                j.AssignedPrinterId == printerId &&
                (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing))
            .OrderByDescending(j => j.ActualStartTime ?? j.QueuedAt)
            .ToListAsync(ct);

        if (activeJobs.Count == 0)
        {
            _logger.LogDebug(
                "[PrintJobCompletionService] No active job found for printer {PrinterId}. Nothing to mark as failed.",
                printerId);
            return false;
        }

        DateTime failedAtUtc = DateTime.UtcNow;

        foreach (PrintJob job in activeJobs)
        {
            // Update job status
            job.Status = PrintJobStatus.Failed;
            job.ActualEndTime = failedAtUtc;
            job.FailureReason = failureReason;

            // Calculate actual duration if start time is set
            if (job.ActualStartTime.HasValue)
            {
                job.ActualPrintTime = job.ActualEndTime - job.ActualStartTime;
            }
        }

        PrintJob primaryJob = activeJobs[0];

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[PrintJobCompletionService] Job {JobId} ({JobName}) marked as failed. Reason: {FailureReason}",
            primaryJob.Id,
            primaryJob.Name ?? primaryJob.GcodeFile?.Name ?? "Unknown",
            failureReason);

        // Broadcast job queue update via SignalR
        await BroadcastJobQueueUpdateAsync(printerId, ct);

        return true;
    }

    /// <summary>
    /// Broadcasts a job queue update via SignalR to notify clients of the status change.
    /// </summary>
    private async Task BroadcastJobQueueUpdateAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            // Get updated job list for this printer
            var jobs = await _db.PrintJobs
                .Include(j => j.GcodeFile)
                .Where(j => j.AssignedPrinterId == printerId)
                .OrderByDescending(j => j.QueuedAt)
                .Take(10)
                .Select(j => new
                {
                    j.Id,
                    Name = j.Name ?? j.GcodeFile!.Name,
                    j.Status,
                    j.Priority,
                    j.QueuedAt,
                    j.ActualStartTime,
                    j.ActualEndTime
                })
                .ToListAsync(ct);

            var update = new
            {
                PrinterId = printerId,
                Jobs = jobs
            };

            await _hub.Clients.All.SendAsync("jobqueueupdate", update, ct);

            _logger.LogDebug(
                "[PrintJobCompletionService] Broadcasted jobqueueupdate for printer {PrinterId} with {JobCount} jobs",
                printerId,
                jobs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PrintJobCompletionService] Failed to broadcast job queue update");
        }
    }

    /// <inheritdoc />
    public async Task<int> SyncOrphanedPrintingJobsAsync(Func<Guid, string?> printerStateLookup, CancellationToken ct = default)
    {
        _logger.LogInformation("[PrintJobCompletionService] Starting orphaned job synchronization...");

        // Find all jobs in Starting or Printing status
        List<PrintJob> orphanedJobs = await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .Where(j => j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing)
            .ToListAsync(ct);

        if (orphanedJobs.Count == 0)
        {
            _logger.LogInformation("[PrintJobCompletionService] No orphaned jobs found.");
            return 0;
        }

        _logger.LogInformation(
            "[PrintJobCompletionService] Found {Count} potentially orphaned jobs in Starting/Printing status",
            orphanedJobs.Count);

        int syncedCount = 0;
        HashSet<Guid> printersToNotify = [];

        foreach (PrintJob job in orphanedJobs)
        {
            if (!job.AssignedPrinterId.HasValue)
            {
                _logger.LogWarning(
                    "[PrintJobCompletionService] Job {JobId} has no assigned printer, skipping",
                    job.Id);
                continue;
            }

            Guid printerId = job.AssignedPrinterId.Value;
            string? currentPrinterState = printerStateLookup(printerId);

            if (currentPrinterState == null)
            {
                _logger.LogDebug(
                    "[PrintJobCompletionService] Printer {PrinterId} state unknown/offline, skipping job {JobId}",
                    printerId,
                    job.Id);
                continue;
            }

            // If printer is in a completion state but job is still "Printing", mark it as completed
            if (IsCompletionState(currentPrinterState))
            {
                _logger.LogInformation(
                    "[PrintJobCompletionService] Syncing orphaned job {JobId} ({JobName}) - printer {PrinterId} is now in state '{State}'",
                    job.Id,
                    job.Name ?? job.GcodeFile?.Name ?? "Unknown",
                    printerId,
                    currentPrinterState);

                job.Status = PrintJobStatus.Completed;
                job.ActualEndTime = DateTime.UtcNow;

                if (job.ActualStartTime.HasValue)
                {
                    job.ActualPrintTime = job.ActualEndTime - job.ActualStartTime;
                }

                syncedCount++;
                printersToNotify.Add(printerId);
            }
            else if (IsFailureState(currentPrinterState))
            {
                _logger.LogWarning(
                    "[PrintJobCompletionService] Syncing orphaned job {JobId} ({JobName}) as FAILED - printer {PrinterId} is in state '{State}'",
                    job.Id,
                    job.Name ?? job.GcodeFile?.Name ?? "Unknown",
                    printerId,
                    currentPrinterState);

                job.Status = PrintJobStatus.Failed;
                job.ActualEndTime = DateTime.UtcNow;
                job.FailureReason = $"Orphaned job synced - printer was in {currentPrinterState} state after restart";

                syncedCount++;
                printersToNotify.Add(printerId);
            }
            else if (IsPrintingState(currentPrinterState))
            {
                _logger.LogDebug(
                    "[PrintJobCompletionService] Job {JobId} still actively printing on printer {PrinterId}",
                    job.Id,
                    printerId);
            }
        }

        if (syncedCount > 0)
        {
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[PrintJobCompletionService] Synced {Count} orphaned jobs",
                syncedCount);

            // Broadcast updates for affected printers
            foreach (Guid printerId in printersToNotify)
            {
                await BroadcastJobQueueUpdateAsync(printerId, ct);
            }
        }

        return syncedCount;
    }
}

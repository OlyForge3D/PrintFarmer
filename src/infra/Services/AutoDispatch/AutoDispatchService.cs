using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Webhooks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.AutoDispatch;

/// <summary>
/// Interface for the auto-dispatch ready-gate service.
/// Manages the workflow where a printer waits for operator confirmation
/// before dispatching the next queued job.
/// </summary>
public interface IAutoDispatchService
{
    /// <summary>
    /// Transitions a printer to PendingReady state after a job completes.
    /// Called by PrintJobCompletionService when a print finishes on an auto-dispatch-enabled printer.
    /// </summary>
    Task TransitionToPendingReadyAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Marks the printer as ready. Returns the next queued job if available,
    /// along with a filament pre-flight check result.
    /// </summary>
    Task<AutoDispatchReadyResult> MarkReadyAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Skips the next queued job (cancels it) and remains in PendingReady state
    /// if more jobs are queued, or transitions to None if the queue is empty.
    /// </summary>
    Task<AutoDispatchStatusDto> SkipNextJobAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Cancels the auto-dispatch ready-gate workflow and returns the printer to None state.
    /// </summary>
    Task<AutoDispatchStatusDto> CancelAutoAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Pre-confirms that the printer bed is clear, allowing immediate job dispatch
    /// when the next job is queued without waiting for PendingReady confirmation.
    /// </summary>
    Task<AutoDispatchStatusDto> MarkPreClearAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current auto-dispatch status for a printer.
    /// </summary>
    Task<AutoDispatchStatusDto> GetStatusAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Enables or disables auto-dispatch for a printer.
    /// </summary>
    Task<AutoDispatchStatusDto> SetEnabledAsync(Guid printerId, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Gets auto-dispatch status for all printers, wrapped with global enabled state.
    /// </summary>
    Task<AutoDispatchGlobalStatusDto> GetAllStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Enables or disables auto-dispatch for all printers at once.
    /// </summary>
    Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>
/// Result of marking a printer as ready in the auto-dispatch ready-gate workflow.
/// </summary>
public class AutoDispatchReadyResult
{
    public AutoDispatchStatusDto Status { get; set; } = new();

    /// <summary>
    /// The next queued job that will be dispatched, if any.
    /// </summary>
    public NextJobDto? NextJob { get; set; }

    /// <summary>
    /// Result of the filament pre-flight check.
    /// </summary>
    public FilamentCheckResult? FilamentCheck { get; set; }
}

public class NextJobDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double? EstimatedFilamentUsageG { get; set; }

    public string? RequiredMaterialType { get; set; }

    public TimeSpan? EstimatedPrintTime { get; set; }
}

public class FilamentCheckResult
{
    public bool Sufficient { get; set; }

    public double? RemainingWeightG { get; set; }

    public double? RequiredWeightG { get; set; }

    public string? LoadedMaterial { get; set; }

    public string? RequiredMaterial { get; set; }

    public bool MaterialMismatch { get; set; }

    public string? Message { get; set; }
}

public class AutoDispatchStatusDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public bool IsReady { get; set; }

    public string? CurrentJobName { get; set; }

    public int QueueDepth { get; set; }

    public List<ReadyGateCheckDto> ReadyGateChecks { get; set; } = [];

    public string? LastActivity { get; set; }

    public string State { get; set; } = "None";

    public bool BedPreConfirmed { get; set; }

    public string? AttentionMessage { get; set; }
}

public class ReadyGateCheckDto
{
    public string Name { get; set; } = string.Empty;

    public bool Passed { get; set; }

    public string Message { get; set; } = string.Empty;

    public string CheckedAt { get; set; } = DateTime.UtcNow.ToString("o");
}

public class AutoDispatchGlobalStatusDto
{
    public bool GlobalEnabled { get; set; }

    public List<AutoDispatchStatusDto> Printers { get; set; } = [];
}

public class AutoDispatchService(
    AppDbContext db,
    IHubContext<PrinterHub> hub,
    ILogger<AutoDispatchService> logger,
    ISpoolmanService? spoolmanService = null,
    IWebhookService? webhookService = null,
    Queue.Dispatch.IAutoDispatchTrigger? dispatchTrigger = null,
    IDispatchScorer? dispatchScorer = null) : IAutoDispatchService
{
    private const string ReadyGateLogPrefix = "[AutoDispatchReadyGate]";
    private const string AutoDispatchStateChangedEventName = "autodispatchstatechanged";
    private const string AutoDispatchReadyWebhookEventName = "printer.autodispatch_ready";
    private const string AutoDispatchPendingWebhookEventName = "printer.autodispatch_pending";

    private sealed record QueuedJobSelection(PrintJob? NextJob, int QueueDepth);

    public async Task TransitionToPendingReadyAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            logger.LogWarning(ReadyGateLogPrefix + " Printer {PrinterId} not found for PendingReady transition", printerId);
            return;
        }

        if (!printer.AutoDispatchEnabled)
        {
            logger.LogDebug(ReadyGateLogPrefix + " Auto-dispatch not enabled for printer {PrinterId}, skipping", printerId);
            return;
        }

        // Guard: don't transition if the printer is actively printing
        bool hasActiveJob = await db.PrintJobs
            .AnyAsync(
                j => j.AssignedPrinterId == printerId
                     && (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing),
                ct);

        if (hasActiveJob)
        {
            logger.LogDebug(ReadyGateLogPrefix + " Printer {PrinterId} has an active job — skipping PendingReady transition", printerId);
            return;
        }

        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printerId, includeGcodeFile: false, ct);

        if (queuedJobs.QueueDepth == 0)
        {
            logger.LogDebug(ReadyGateLogPrefix + " No queued jobs for printer {PrinterId}, staying in None state", printerId);
            printer.AutoDispatchState = AutoDispatchState.None;
            printer.BedPreConfirmed = false; // Reset pre-clear flag
            await db.SaveChangesAsync(ct);
            return;
        }

        // If bed was pre-confirmed, skip PendingReady and go straight to Ready
        if (printer.BedPreConfirmed)
        {
            logger.LogInformation(
                ReadyGateLogPrefix + " Printer {PrinterId} ({Name}) bed was pre-confirmed — skipping PendingReady, going straight to Ready",
                printerId, printer.Name);
            printer.AutoDispatchState = AutoDispatchState.Ready;
            printer.BedPreConfirmed = false; // Reset the flag after using it
            await db.SaveChangesAsync(ct);

            var readyStatus = await BuildStatusDtoAsync(printer, ct);
            await hub.Clients.All.SendAsync(AutoDispatchStateChangedEventName, readyStatus, ct);

            // Trigger immediate dispatch
            dispatchTrigger?.NotifyJobQueued(printerId);

            webhookService?.Enqueue(AutoDispatchReadyWebhookEventName, new { printerId, printerName = printer.Name });
            return;
        }

        printer.AutoDispatchState = AutoDispatchState.PendingReady;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(ReadyGateLogPrefix + " Printer {PrinterId} ({Name}) transitioned to PendingReady", printerId, printer.Name);

        // Broadcast state change via SignalR
        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.All.SendAsync(AutoDispatchStateChangedEventName, status, ct);

        webhookService?.Enqueue(AutoDispatchPendingWebhookEventName, new { printerId, printerName = printer.Name });
    }

    public async Task<AutoDispatchReadyResult> MarkReadyAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        if (!printer.AutoDispatchEnabled)
        {
            throw new InvalidOperationException($"Auto-dispatch is not enabled for printer {printer.Name}");
        }

        bool hasReadyConfirmation = printer.AutoDispatchState == AutoDispatchState.PendingReady
            || printer.AutoDispatchState == AutoDispatchState.Ready
            || printer.BedPreConfirmed;

        if (!hasReadyConfirmation)
        {
            throw new InvalidOperationException($"Printer {printer.Name} is not in PendingReady state (current: {printer.AutoDispatchState})");
        }

        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printerId, includeGcodeFile: true, ct);
        PrintJob? nextJob = queuedJobs.NextJob;

        if (nextJob is null)
        {
            // No more queued jobs — return to None
            printer.AutoDispatchState = AutoDispatchState.None;
            printer.BedPreConfirmed = false;
            await db.SaveChangesAsync(ct);

            var emptyStatus = await BuildStatusDtoAsync(printer, ct);
            await hub.Clients.All.SendAsync(AutoDispatchStateChangedEventName, emptyStatus, ct);

            return new AutoDispatchReadyResult
            {
                Status = emptyStatus,
                NextJob = null,
                FilamentCheck = new FilamentCheckResult { Sufficient = true, Message = "No queued jobs remaining" },
            };
        }

        // Perform filament pre-flight check
        FilamentCheckResult filamentCheck = await CheckFilamentAsync(printer, nextJob, ct);

        // Transition to Ready state
        printer.AutoDispatchState = AutoDispatchState.Ready;
        printer.BedPreConfirmed = false;
        await db.SaveChangesAsync(ct);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.All.SendAsync(AutoDispatchStateChangedEventName, status, ct);

        logger.LogInformation(
            ReadyGateLogPrefix + " Printer {PrinterId} marked Ready. Next job: {JobName} (filament sufficient: {Sufficient})",
            printerId, nextJob.Name ?? nextJob.GcodeFile?.Name, filamentCheck.Sufficient);

        // Notify auto-dispatch that this printer is ready — triggers immediate dispatch
        dispatchTrigger?.NotifyJobQueued(printerId);

        return new AutoDispatchReadyResult
        {
            Status = status,
            NextJob = new NextJobDto
            {
                Id = nextJob.Id,
                Name = nextJob.Name ?? nextJob.GcodeFile?.Name ?? "Unknown",
                EstimatedFilamentUsageG = nextJob.EstimatedFilamentUsage,
                RequiredMaterialType = nextJob.RequiredMaterialType,
                EstimatedPrintTime = nextJob.EstimatedPrintTime,
            },
            FilamentCheck = filamentCheck,
        };
    }

    public async Task<AutoDispatchStatusDto> SkipNextJobAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        // Find and cancel the next queued job
        PrintJob? nextJob = await db.PrintJobs
            .Where(j => j.AssignedPrinterId == printerId && j.Status == PrintJobStatus.Queued)
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuePosition)
            .ThenBy(j => j.QueuedAt)
            .FirstOrDefaultAsync(ct);

        if (nextJob != null)
        {
            nextJob.Status = PrintJobStatus.Cancelled;
            nextJob.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                ReadyGateLogPrefix + " Skipped (cancelled) job {JobId} ({JobName}) for printer {PrinterId}",
                nextJob.Id, nextJob.Name, printerId);
        }

        // Check if there are more queued jobs (cancelled job already persisted above)
        bool hasMoreJobs = await db.PrintJobs
            .AnyAsync(
                j => j.AssignedPrinterId == printerId
                        && j.Status == PrintJobStatus.Queued, ct);

        printer.AutoDispatchState = hasMoreJobs ? AutoDispatchState.PendingReady : AutoDispatchState.None;
        await db.SaveChangesAsync(ct);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.All.SendAsync(AutoDispatchStateChangedEventName, status, ct);

        return status;
    }

    public async Task<AutoDispatchStatusDto> CancelAutoAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        printer.AutoDispatchState = AutoDispatchState.None;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(ReadyGateLogPrefix + " Auto-dispatch ready gate cancelled for printer {PrinterId} ({Name})", printerId, printer.Name);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.All.SendAsync(AutoDispatchStateChangedEventName, status, ct);

        return status;
    }

    public async Task<AutoDispatchStatusDto> MarkPreClearAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        if (!printer.AutoDispatchEnabled)
        {
            throw new InvalidOperationException($"Auto-dispatch is not enabled for printer {printer.Name}");
        }

        // Guard: printer must be idle (not actively printing)
        bool hasActiveJob = await db.PrintJobs
            .AnyAsync(
                j => j.AssignedPrinterId == printerId
                     && (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing),
                ct);

        if (hasActiveJob)
        {
            throw new InvalidOperationException($"Cannot pre-clear bed while printer {printer.Name} is actively printing");
        }

        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printerId, includeGcodeFile: false, ct);

        printer.BedPreConfirmed = true;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(ReadyGateLogPrefix + " Bed pre-clear confirmed for printer {PrinterId} ({Name})", printerId, printer.Name);

        if (queuedJobs.QueueDepth > 0)
        {
            // A pre-cleared idle printer just became eligible for immediate dispatch.
            dispatchTrigger?.NotifyJobQueued(printerId);
        }

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.All.SendAsync(AutoDispatchStateChangedEventName, status, ct);

        webhookService?.Enqueue("printer.bed_pre_confirmed", new { printerId, printerName = printer.Name });

        return status;
    }

    public async Task<AutoDispatchStatusDto> GetStatusAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, ct);

        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        return await BuildStatusDtoAsync(printer, ct);
    }

    public async Task<AutoDispatchStatusDto> SetEnabledAsync(Guid printerId, bool enabled, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        printer.AutoDispatchEnabled = enabled;
        if (!enabled)
        {
            printer.AutoDispatchState = AutoDispatchState.None;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            ReadyGateLogPrefix + " Auto-dispatch {Action} for printer {PrinterId} ({Name})",
            enabled ? "enabled" : "disabled", printerId, printer.Name);

        return await BuildStatusDtoAsync(printer, ct);
    }

    public async Task<AutoDispatchGlobalStatusDto> GetAllStatusAsync(CancellationToken ct = default)
    {
        List<Printer> printers = await db.Printers.ToListAsync(ct);
        Dictionary<Guid, string?> currentJobs = await GetCurrentJobNamesByPrinterAsync(printers.Select(p => p.Id), ct);

        bool globalEnabled = printers.Any(p => p.AutoDispatchEnabled);
        List<AutoDispatchStatusDto> statuses = [];
        foreach (Printer printer in printers)
        {
            QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printer.Id, includeGcodeFile: false, ct);
            statuses.Add(BuildStatusDto(
                printer,
                queuedJobs.QueueDepth,
                currentJobs.GetValueOrDefault(printer.Id)));
        }

        return new AutoDispatchGlobalStatusDto
        {
            GlobalEnabled = globalEnabled,
            Printers = statuses,
        };
    }

    public async Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        List<Printer> printers = await db.Printers.ToListAsync(ct);
        foreach (Printer printer in printers)
        {
            printer.AutoDispatchEnabled = enabled;
            if (!enabled)
            {
                printer.AutoDispatchState = AutoDispatchState.None;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            ReadyGateLogPrefix + " Auto-dispatch {Action} for ALL {Count} printers",
            enabled ? "enabled" : "disabled",
            printers.Count);

        Dictionary<Guid, string?> currentJobs = await GetCurrentJobNamesByPrinterAsync(printers.Select(p => p.Id), ct);
        List<AutoDispatchStatusDto> statuses = [];
        foreach (Printer printer in printers)
        {
            QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printer.Id, includeGcodeFile: false, ct);
            statuses.Add(BuildStatusDto(
                printer,
                queuedJobs.QueueDepth,
                currentJobs.GetValueOrDefault(printer.Id)));
        }

        return statuses;
    }

    private static AutoDispatchStatusDto BuildStatusDto(Printer printer, int queuedJobCount, string? currentJobName = null)
    {
        string now = DateTime.UtcNow.ToString("o");
        bool isReady = printer.AutoDispatchEnabled && printer.AutoDispatchState == AutoDispatchState.Ready;
        var gateChecks = BuildReadyGateChecks(printer, queuedJobCount, now);
        string? attentionMessage = BuildAttentionMessage(printer, queuedJobCount);

        return new AutoDispatchStatusDto
        {
            PrinterId = printer.Id,
            PrinterName = printer.Name,
            Enabled = printer.AutoDispatchEnabled,
            IsReady = isReady,
            CurrentJobName = currentJobName,
            QueueDepth = queuedJobCount,
            ReadyGateChecks = gateChecks,
            State = printer.AutoDispatchState.ToString(),
            BedPreConfirmed = printer.BedPreConfirmed,
            AttentionMessage = attentionMessage,
        };
    }

    private async Task<AutoDispatchStatusDto> BuildStatusDtoAsync(Printer printer, CancellationToken ct)
    {
        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printer.Id, includeGcodeFile: false, ct);

        string? currentJobName = await db.PrintJobs
            .Where(j => j.AssignedPrinterId == printer.Id
                && (j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting))
            .Select(j => j.Name ?? j.GcodeFile!.Name)
            .FirstOrDefaultAsync(ct);

        return BuildStatusDto(printer, queuedJobs.QueueDepth, currentJobName);
    }

    private static List<ReadyGateCheckDto> BuildReadyGateChecks(Printer printer, int queuedJobCount, string checkedAt)
    {
        var checks = new List<ReadyGateCheckDto>
        {
            new()
            {
                Name = "Auto-Dispatch Enabled",
                Passed = printer.AutoDispatchEnabled,
                Message = printer.AutoDispatchEnabled ? "Auto-dispatch is enabled" : "Auto-dispatch is disabled for this printer",
                CheckedAt = checkedAt,
            },
            new()
            {
                Name = "Printer Available",
                Passed = printer.IsAvailable && !printer.InMaintenance,
                Message = printer.InMaintenance
                    ? "Printer is in maintenance mode"
                    : printer.IsAvailable ? "Printer is available" : "Printer is not available",
                CheckedAt = checkedAt,
            },
            new()
            {
                Name = "Jobs in Queue",
                Passed = queuedJobCount > 0,
                Message = queuedJobCount > 0
                    ? $"{queuedJobCount} job{(queuedJobCount == 1 ? string.Empty : "s")} queued"
                    : "No jobs queued for this printer",
                CheckedAt = checkedAt,
            },
        };

        if (printer.AutoDispatchEnabled)
        {
            checks.Add(new ReadyGateCheckDto
            {
                Name = "Bed Clear Confirmed",
                Passed = printer.AutoDispatchState == AutoDispatchState.Ready || printer.BedPreConfirmed,
                Message = printer.AutoDispatchState switch
                {
                    AutoDispatchState.Ready => "Operator confirmed bed is clear",
                    _ when printer.BedPreConfirmed => "Bed pre-cleared for immediate dispatch",
                    AutoDispatchState.PendingReady => "Waiting for operator to confirm bed is clear",
                    _ => "No confirmation needed yet",
                },
                CheckedAt = checkedAt,
            });
        }

        return checks;
    }

    private static string? BuildAttentionMessage(
        Printer printer,
        int queuedJobCount)
    {
        if (!printer.AutoDispatchEnabled)
        {
            return null;
        }

        string queuedJobLabel = $"{queuedJobCount} queued job{(queuedJobCount == 1 ? string.Empty : "s")}";

        if (printer.InMaintenance)
        {
            return queuedJobCount > 0
                ? $"Printer is in maintenance mode. {queuedJobLabel} will not start until maintenance is complete and the printer is available."
                : "Printer is in maintenance mode. Complete maintenance and make the printer available before auto-dispatch can resume.";
        }

        if (!printer.IsAvailable)
        {
            return queuedJobCount > 0
                ? $"Printer is unavailable. {queuedJobLabel} will not start until the printer is available again."
                : "Printer is unavailable. Restore printer availability before auto-dispatch can resume.";
        }

        if (printer.AutoDispatchState == AutoDispatchState.PendingReady)
        {
            return queuedJobCount switch
            {
                <= 0 => "Print completed. Clear the bed and confirm ready before queued work can resume.",
                1 => "Print completed. 1 queued job is blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.",
                _ => $"Print completed. {queuedJobLabel} are blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.",
            };
        }

        if (queuedJobCount > 0 && (printer.AutoDispatchState == AutoDispatchState.Ready || printer.BedPreConfirmed))
        {
            return "Bed is clear. The next queued job will start automatically.";
        }

        return null;
    }

    private async Task<Dictionary<Guid, string?>> GetCurrentJobNamesByPrinterAsync(IEnumerable<Guid> printerIds, CancellationToken ct)
    {
        List<Guid> ids = printerIds.ToList();
        return await db.PrintJobs
            .Where(j => ids.Contains(j.AssignedPrinterId!.Value)
                && (j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting))
            .GroupBy(j => j.AssignedPrinterId!.Value)
            .ToDictionaryAsync(
                g => g.Key,
                g => g.OrderByDescending(j => j.ActualStartTime).Select(j => j.Name ?? j.GcodeFile!.Name).FirstOrDefault(),
                ct);
    }

    private async Task<QueuedJobSelection> GetQueuedJobSelectionAsync(Guid printerId, bool includeGcodeFile, CancellationToken ct)
    {
        IQueryable<PrintJob> assignedQuery = db.PrintJobs
            .Where(j => j.AssignedPrinterId == printerId && j.Status == PrintJobStatus.Queued)
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuePosition)
            .ThenBy(j => j.QueuedAt);

        IQueryable<PrintJob> unassignedQuery = db.PrintJobs
            .Where(j => j.AssignedPrinterId == null && j.Status == PrintJobStatus.Queued)
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuePosition)
            .ThenBy(j => j.QueuedAt);

        if (includeGcodeFile)
        {
            assignedQuery = assignedQuery.Include(j => j.GcodeFile);
            unassignedQuery = unassignedQuery.Include(j => j.GcodeFile);
        }

        List<PrintJob> assignedJobs = await assignedQuery.ToListAsync(ct);
        PrintJob? nextJob = assignedJobs.FirstOrDefault();
        int queueDepth = assignedJobs.Count;

        if (dispatchScorer is null)
        {
            return new QueuedJobSelection(nextJob, queueDepth);
        }

        DispatchSettings? settings = await db.DispatchSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        double minimumScoreThreshold = settings?.MinimumScoreThreshold ?? 0;

        foreach (PrintJob job in await unassignedQuery.ToListAsync(ct))
        {
            DispatchScore? printerScore = (await dispatchScorer.ScorePrintersForJobAsync(job.Id, ct))
                .FirstOrDefault(score => score.PrinterId == printerId);

            if (printerScore is null || printerScore.Eliminated || printerScore.TotalScore < minimumScoreThreshold)
            {
                continue;
            }

            queueDepth++;
            nextJob ??= job;
        }

        return new QueuedJobSelection(nextJob, queueDepth);
    }

    private async Task<FilamentCheckResult> CheckFilamentAsync(Printer printer, PrintJob nextJob, CancellationToken ct)
    {
        var result = new FilamentCheckResult
        {
            RequiredWeightG = nextJob.EstimatedFilamentUsage,
            RequiredMaterial = nextJob.RequiredMaterialType,
        };

        // If no spool is loaded or Spoolman is not configured, skip the check
        if (printer.CurrentSpoolId is null || spoolmanService is null)
        {
            result.Sufficient = true;
            result.Message = printer.CurrentSpoolId is null
                ? "No spool loaded — filament check skipped"
                : "Spoolman not configured — filament check skipped";
            return result;
        }

        try
        {
            var spool = await spoolmanService.GetSpoolByIdAsync(printer.CurrentSpoolId.Value, ct);
            if (spool is null)
            {
                result.Sufficient = true;
                result.Message = "Spool data not available — filament check skipped";
                return result;
            }

            result.RemainingWeightG = spool.RemainingWeightG;
            result.LoadedMaterial = spool.Material;

            // Check material type mismatch
            if (!string.IsNullOrEmpty(nextJob.RequiredMaterialType)
                && !string.IsNullOrEmpty(spool.Material)
                && !string.Equals(nextJob.RequiredMaterialType, spool.Material, StringComparison.OrdinalIgnoreCase))
            {
                result.MaterialMismatch = true;
                result.Sufficient = false;
                result.Message = $"Material mismatch: loaded {spool.Material}, job requires {nextJob.RequiredMaterialType}";
                return result;
            }

            // Check remaining filament weight
            if (nextJob.EstimatedFilamentUsage.HasValue && spool.RemainingWeightG.HasValue)
            {
                result.Sufficient = spool.RemainingWeightG.Value >= nextJob.EstimatedFilamentUsage.Value;
                if (!result.Sufficient)
                {
                    result.Message = $"Insufficient filament: {spool.RemainingWeightG:F1}g remaining, {nextJob.EstimatedFilamentUsage:F1}g required";
                }
                else
                {
                    result.Message = $"Filament OK: {spool.RemainingWeightG:F1}g remaining, {nextJob.EstimatedFilamentUsage:F1}g required";
                }
            }
            else
            {
                result.Sufficient = true;
                result.Message = "Filament weight data incomplete — check skipped";
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, ReadyGateLogPrefix + " Filament check failed for printer {PrinterId}", printer.Id);
            result.Sufficient = true;
            result.Message = "Filament check failed — proceeding anyway";
        }

        return result;
    }
}

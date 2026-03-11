using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Webhooks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.AutoPrint;

/// <summary>
/// Interface for the auto-print ready-gate service.
/// Manages the workflow where a printer waits for operator confirmation
/// before dispatching the next queued job.
/// </summary>
public interface IAutoPrintService
{
    /// <summary>
    /// Transitions a printer to PendingReady state after a job completes.
    /// Called by PrintJobCompletionService when a print finishes on an auto-print-enabled printer.
    /// </summary>
    Task TransitionToPendingReadyAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Marks the printer as ready. Returns the next queued job if available,
    /// along with a filament pre-flight check result.
    /// </summary>
    Task<AutoPrintReadyResult> MarkReadyAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Skips the next queued job (cancels it) and remains in PendingReady state
    /// if more jobs are queued, or transitions to None if the queue is empty.
    /// </summary>
    Task<AutoPrintStatusDto> SkipNextJobAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Cancels the auto-print workflow and returns the printer to None state.
    /// </summary>
    Task<AutoPrintStatusDto> CancelAutoAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current auto-print status for a printer.
    /// </summary>
    Task<AutoPrintStatusDto> GetStatusAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Enables or disables auto-print for a printer.
    /// </summary>
    Task<AutoPrintStatusDto> SetEnabledAsync(Guid printerId, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Gets auto-print status for all printers.
    /// </summary>
    Task<List<AutoPrintStatusDto>> GetAllStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Enables or disables auto-print for all printers at once.
    /// </summary>
    Task<List<AutoPrintStatusDto>> SetAllEnabledAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>
/// Result of marking a printer as ready in the auto-print workflow.
/// </summary>
public class AutoPrintReadyResult
{
    public AutoPrintStatusDto Status { get; set; } = new();

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

public class AutoPrintStatusDto
{
    public Guid PrinterId { get; set; }

    public bool AutoPrintEnabled { get; set; }

    public string State { get; set; } = "None";

    public int QueuedJobCount { get; set; }
}

public class AutoPrintService(
    AppDbContext db,
    IHubContext<PrinterHub> hub,
    ILogger<AutoPrintService> logger,
    ISpoolmanService? spoolmanService = null,
    IWebhookService? webhookService = null,
    Queue.Dispatch.IAutoDispatchTrigger? dispatchTrigger = null) : IAutoPrintService
{
    public async Task TransitionToPendingReadyAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            logger.LogWarning("[AutoPrint] Printer {PrinterId} not found for PendingReady transition", printerId);
            return;
        }

        if (!printer.AutoPrintEnabled)
        {
            logger.LogDebug("[AutoPrint] Auto-print not enabled for printer {PrinterId}, skipping", printerId);
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
            logger.LogDebug("[AutoPrint] Printer {PrinterId} has an active job — skipping PendingReady transition", printerId);
            return;
        }

        // Check if there are queued jobs for this printer
        bool hasQueuedJobs = await db.PrintJobs
            .AnyAsync(j => j.AssignedPrinterId == printerId && j.Status == PrintJobStatus.Queued, ct);

        if (!hasQueuedJobs)
        {
            logger.LogDebug("[AutoPrint] No queued jobs for printer {PrinterId}, staying in None state", printerId);
            printer.AutoPrintState = AutoPrintState.None;
            await db.SaveChangesAsync(ct);
            return;
        }

        printer.AutoPrintState = AutoPrintState.PendingReady;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[AutoPrint] Printer {PrinterId} ({Name}) transitioned to PendingReady", printerId, printer.Name);

        // Broadcast state change via SignalR
        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.All.SendAsync("autoprintstatechanged", status, ct);

        webhookService?.Enqueue("printer.autoprint_pending", new { printerId, printerName = printer.Name });
    }

    public async Task<AutoPrintReadyResult> MarkReadyAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        if (!printer.AutoPrintEnabled)
        {
            throw new InvalidOperationException($"Auto-print is not enabled for printer {printer.Name}");
        }

        if (printer.AutoPrintState != AutoPrintState.PendingReady)
        {
            throw new InvalidOperationException($"Printer {printer.Name} is not in PendingReady state (current: {printer.AutoPrintState})");
        }

        // Find the next queued job for this printer
        PrintJob? nextJob = await db.PrintJobs
            .Include(j => j.GcodeFile)
            .Where(j => j.AssignedPrinterId == printerId && j.Status == PrintJobStatus.Queued)
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuePosition)
            .ThenBy(j => j.QueuedAt)
            .FirstOrDefaultAsync(ct);

        if (nextJob is null)
        {
            // No more queued jobs — return to None
            printer.AutoPrintState = AutoPrintState.None;
            await db.SaveChangesAsync(ct);

            var emptyStatus = await BuildStatusDtoAsync(printer, ct);
            await hub.Clients.All.SendAsync("autoprintstatechanged", emptyStatus, ct);

            return new AutoPrintReadyResult
            {
                Status = emptyStatus,
                NextJob = null,
                FilamentCheck = new FilamentCheckResult { Sufficient = true, Message = "No queued jobs remaining" },
            };
        }

        // Perform filament pre-flight check
        FilamentCheckResult filamentCheck = await CheckFilamentAsync(printer, nextJob, ct);

        // Transition to Ready state
        printer.AutoPrintState = AutoPrintState.Ready;
        await db.SaveChangesAsync(ct);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.All.SendAsync("autoprintstatechanged", status, ct);

        logger.LogInformation(
            "[AutoPrint] Printer {PrinterId} marked Ready. Next job: {JobName} (filament sufficient: {Sufficient})",
            printerId, nextJob.Name ?? nextJob.GcodeFile?.Name, filamentCheck.Sufficient);

        // Notify auto-dispatch that this printer is ready — triggers immediate dispatch
        dispatchTrigger?.NotifyJobQueued(printerId);

        return new AutoPrintReadyResult
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

    public async Task<AutoPrintStatusDto> SkipNextJobAsync(Guid printerId, CancellationToken ct = default)
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
                "[AutoPrint] Skipped (cancelled) job {JobId} ({JobName}) for printer {PrinterId}",
                nextJob.Id, nextJob.Name, printerId);
        }

        // Check if there are more queued jobs (cancelled job already persisted above)
        bool hasMoreJobs = await db.PrintJobs
            .AnyAsync(
                j => j.AssignedPrinterId == printerId
                        && j.Status == PrintJobStatus.Queued, ct);

        printer.AutoPrintState = hasMoreJobs ? AutoPrintState.PendingReady : AutoPrintState.None;
        await db.SaveChangesAsync(ct);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.All.SendAsync("autoprintstatechanged", status, ct);

        return status;
    }

    public async Task<AutoPrintStatusDto> CancelAutoAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        printer.AutoPrintState = AutoPrintState.None;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[AutoPrint] Auto-print cancelled for printer {PrinterId} ({Name})", printerId, printer.Name);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.All.SendAsync("autoprintstatechanged", status, ct);

        return status;
    }

    public async Task<AutoPrintStatusDto> GetStatusAsync(Guid printerId, CancellationToken ct = default)
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

    public async Task<AutoPrintStatusDto> SetEnabledAsync(Guid printerId, bool enabled, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        printer.AutoPrintEnabled = enabled;
        if (!enabled)
        {
            printer.AutoPrintState = AutoPrintState.None;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[AutoPrint] Auto-print {Action} for printer {PrinterId} ({Name})",
            enabled ? "enabled" : "disabled", printerId, printer.Name);

        return await BuildStatusDtoAsync(printer, ct);
    }

    public async Task<List<AutoPrintStatusDto>> GetAllStatusAsync(CancellationToken ct = default)
    {
        List<Printer> printers = await db.Printers.ToListAsync(ct);
        Dictionary<Guid, int> queuedCounts = await GetQueuedCountsByPrinterAsync(printers.Select(p => p.Id), ct);
        return printers.Select(p => BuildStatusDto(p, queuedCounts.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<List<AutoPrintStatusDto>> SetAllEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        List<Printer> printers = await db.Printers.ToListAsync(ct);
        foreach (Printer printer in printers)
        {
            printer.AutoPrintEnabled = enabled;
            if (!enabled)
            {
                printer.AutoPrintState = AutoPrintState.None;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[AutoPrint] Auto-print {Action} for ALL {Count} printers",
            enabled ? "enabled" : "disabled",
            printers.Count);

        Dictionary<Guid, int> queuedCounts = await GetQueuedCountsByPrinterAsync(printers.Select(p => p.Id), ct);
        return printers.Select(p => BuildStatusDto(p, queuedCounts.GetValueOrDefault(p.Id))).ToList();
    }

    private async Task<Dictionary<Guid, int>> GetQueuedCountsByPrinterAsync(IEnumerable<Guid> printerIds, CancellationToken ct)
    {
        List<Guid> ids = printerIds.ToList();
        return await db.PrintJobs
            .Where(j => ids.Contains(j.AssignedPrinterId!.Value) && j.Status == PrintJobStatus.Queued)
            .GroupBy(j => j.AssignedPrinterId!.Value)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
    }

    private static AutoPrintStatusDto BuildStatusDto(Printer printer, int queuedJobCount)
    {
        return new AutoPrintStatusDto
        {
            PrinterId = printer.Id,
            AutoPrintEnabled = printer.AutoPrintEnabled,
            State = printer.AutoPrintState.ToString(),
            QueuedJobCount = queuedJobCount,
        };
    }

    private async Task<AutoPrintStatusDto> BuildStatusDtoAsync(Printer printer, CancellationToken ct)
    {
        int queuedCount = await db.PrintJobs
            .CountAsync(j => j.AssignedPrinterId == printer.Id && j.Status == PrintJobStatus.Queued, ct);

        return new AutoPrintStatusDto
        {
            PrinterId = printer.Id,
            AutoPrintEnabled = printer.AutoPrintEnabled,
            State = printer.AutoPrintState.ToString(),
            QueuedJobCount = queuedCount,
        };
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
            logger.LogWarning(ex, "[AutoPrint] Filament check failed for printer {PrinterId}", printer.Id);
            result.Sufficient = true;
            result.Message = "Filament check failed — proceeding anyway";
        }

        return result;
    }
}

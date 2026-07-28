using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
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

    Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Skips the next queued job (cancels it) and remains in PendingReady state
    /// if more jobs are queued, or transitions to None if the queue is empty.
    /// </summary>
    Task<AutoDispatchStatusDto> SkipNextJobAsync(Guid printerId, CancellationToken ct = default);

    Task<AutoDispatchStatusDto> SkipNextJobAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        byte[] expectedJobVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels the auto-dispatch ready-gate workflow and returns the printer to None state.
    /// </summary>
    Task<AutoDispatchStatusDto> CancelAutoAsync(Guid printerId, CancellationToken ct = default);

    Task<AutoDispatchStatusDto> CancelAutoAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Pre-confirms that the printer bed is clear, allowing immediate job dispatch
    /// when the next job is queued without waiting for PendingReady confirmation.
    /// </summary>
    Task<AutoDispatchStatusDto> MarkPreClearAsync(Guid printerId, CancellationToken ct = default);

    Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        string actorSubject,
        CancellationToken ct = default) =>
        MarkPreClearAsync(printerId, ct);

    Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        string actorSubject,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the current auto-dispatch status for a printer.
    /// </summary>
    Task<AutoDispatchStatusDto> GetStatusAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Enables or disables auto-dispatch for a printer.
    /// </summary>
    Task<AutoDispatchStatusDto> SetEnabledAsync(Guid printerId, bool enabled, CancellationToken ct = default);

    Task<AutoDispatchStatusDto> SetEnabledAsync(
        Guid printerId,
        bool enabled,
        byte[] expectedDispatchStateVersion,
        byte[] expectedPrinterVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Gets auto-dispatch status for all printers, wrapped with global enabled state.
    /// </summary>
    Task<AutoDispatchGlobalStatusDto> GetAllStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Enables or disables auto-dispatch for all printers at once.
    /// </summary>
    Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(bool enabled, CancellationToken ct = default);

    Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(
        bool enabled,
        IReadOnlyDictionary<Guid, AutoDispatchExpectedVersions> expectedVersions,
        CancellationToken ct = default);
}

public sealed record AutoDispatchExpectedVersions(
    byte[] DispatchStateVersion,
    byte[] PrinterVersion);

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

    public string JobKind { get; set; } =
        nameof(Farm.Infrastructure.Domain.JobKind.Standard);

    public string? JobETag { get; set; }

    public long? ExpectedPrinterConfigRevision { get; set; }
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

    public string? DispatchStateETag { get; set; }

    public string? PrinterETag { get; set; }

    public Guid? NextJobId { get; set; }

    public string? NextJobName { get; set; }

    public string? NextJobETag { get; set; }

    public string? NextJobKind { get; set; }

    public long? NextJobPrinterConfigRevision { get; set; }

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

    /// <summary>
    /// Returns the existing DispatchState or creates a new one lazily on first write.
    /// </summary>
    private static PrinterDispatchState EnsureDispatchState(Printer printer)
    {
        if (printer.DispatchState is null)
        {
            printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id };
        }

        return printer.DispatchState;
    }

    public async Task TransitionToPendingReadyAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
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
                     && (j.Status == PrintJobStatus.Starting ||
                         j.Status == PrintJobStatus.Printing ||
                         j.Status == PrintJobStatus.Paused),
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
            PrinterDispatchState ds = EnsureDispatchState(printer);
            ds.AutoDispatchState = AutoDispatchState.None;
            ds.BedPreConfirmed = false; // Reset pre-clear flag
            await db.SaveChangesAsync(ct);
            return;
        }

        // If bed was pre-confirmed, skip PendingReady and go straight to Ready
        if (printer.DispatchState?.BedPreConfirmed == true)
        {
            logger.LogInformation(
                ReadyGateLogPrefix + " Printer {PrinterId} ({Name}) bed was pre-confirmed — skipping PendingReady, going straight to Ready",
                printerId, printer.Name);
            PrinterDispatchState ds = EnsureDispatchState(printer);
            ds.AutoDispatchState = AutoDispatchState.Ready;
            ds.BedPreConfirmed = false; // Reset the flag after using it
            await db.SaveChangesAsync(ct);

            var readyStatus = await BuildStatusDtoAsync(printer, ct);
            await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, readyStatus, ct);

            // Trigger immediate dispatch
            dispatchTrigger?.NotifyJobQueued(printerId);

            webhookService?.Enqueue(AutoDispatchReadyWebhookEventName, new { printerId, printerName = printer.Name });
            return;
        }

        EnsureDispatchState(printer).AutoDispatchState = AutoDispatchState.PendingReady;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(ReadyGateLogPrefix + " Printer {PrinterId} ({Name}) transitioned to PendingReady", printerId, printer.Name);

        // Broadcast state change via SignalR
        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, status, ct);

        webhookService?.Enqueue(AutoDispatchPendingWebhookEventName, new { printerId, printerName = printer.Name });
    }

    public Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        CancellationToken ct = default) =>
        MarkReadyCoreAsync(printerId, expectedDispatchStateVersion: null, ct);

    public Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default) =>
        MarkReadyCoreAsync(printerId, expectedDispatchStateVersion, ct);

    private async Task<AutoDispatchReadyResult> MarkReadyCoreAsync(
        Guid printerId,
        byte[]? expectedDispatchStateVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);

        if (!printer.AutoDispatchEnabled)
        {
            throw new InvalidOperationException($"Auto-dispatch is not enabled for printer {printer.Name}");
        }

        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printerId, includeGcodeFile: true, ct);
        bool hasActiveJob = await db.PrintJobs
            .AnyAsync(
                j => j.AssignedPrinterId == printerId
                     && (j.Status == PrintJobStatus.Starting ||
                         j.Status == PrintJobStatus.Printing ||
                         j.Status == PrintJobStatus.Paused),
                ct);
        AutoDispatchState effectiveState = ResolveEffectiveState(printer, queuedJobs.QueueDepth, hasActiveJob);
        bool hasReadyConfirmation = effectiveState == AutoDispatchState.PendingReady
            || effectiveState == AutoDispatchState.Ready
            || (printer.DispatchState?.BedPreConfirmed ?? false);

        if (!hasReadyConfirmation)
        {
            throw new InvalidOperationException($"Printer {printer.Name} is not in PendingReady state (current: {printer.DispatchState?.AutoDispatchState ?? AutoDispatchState.None})");
        }

        PrintJob? nextJob = queuedJobs.NextJob;

        if (nextJob is null)
        {
            // No more queued jobs — return to None
            PrinterDispatchState dsEmpty = EnsureDispatchState(printer);
            dsEmpty.AutoDispatchState = AutoDispatchState.None;
            dsEmpty.BedPreConfirmed = false;
            await db.SaveChangesAsync(ct);

            var emptyStatus = await BuildStatusDtoAsync(printer, ct);
            await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, emptyStatus, ct);

            return new AutoDispatchReadyResult
            {
                Status = emptyStatus,
                NextJob = null,
                FilamentCheck = new FilamentCheckResult { Sufficient = true, Message = "No queued jobs remaining" },
            };
        }

        if (nextJob.JobKind == JobKind.FilamentCalibration)
        {
            throw new InvalidOperationException(
                "Calibration jobs require the exact-job acknowledge-bed-clear-and-start endpoint.");
        }

        // Perform filament pre-flight check
        FilamentCheckResult filamentCheck = await CheckFilamentAsync(printer, nextJob, ct);

        // Transition to Ready state
        PrinterDispatchState dsReady = EnsureDispatchState(printer);
        dsReady.AutoDispatchState = AutoDispatchState.Ready;
        dsReady.BedPreConfirmed = false;
        await db.SaveChangesAsync(ct);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, status, ct);

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
                JobKind = (
                    nextJob.JobKind ??
                    Farm.Infrastructure.Domain.JobKind.Standard).ToString(),
                JobETag = nextJob.RowVersion is { Length: > 0 }
                    ? Convert.ToBase64String(nextJob.RowVersion)
                    : null,
                ExpectedPrinterConfigRevision =
                    nextJob.PinnedPrinterConfigRevision,
            },
            FilamentCheck = filamentCheck,
        };
    }

    public Task<AutoDispatchStatusDto> SkipNextJobAsync(
        Guid printerId,
        CancellationToken ct = default) =>
        SkipNextJobCoreAsync(
            printerId,
            expectedDispatchStateVersion: null,
            expectedJobVersion: null,
            ct);

    public Task<AutoDispatchStatusDto> SkipNextJobAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        byte[] expectedJobVersion,
        CancellationToken ct = default) =>
        SkipNextJobCoreAsync(
            printerId,
            expectedDispatchStateVersion,
            expectedJobVersion,
            ct);

    private async Task<AutoDispatchStatusDto> SkipNextJobCoreAsync(
        Guid printerId,
        byte[]? expectedDispatchStateVersion,
        byte[]? expectedJobVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);

        // Find and cancel the next queued job, using the SINGLE shared ordering selector
        // (Urgent first — an ascending sort would cancel the LOWEST-priority job).
        PrintJob? nextJob = await db.PrintJobs
            .Where(j => j.AssignedPrinterId == printerId && j.Status == PrintJobStatus.Queued)
            .OrderByPriorityDescending()
            .FirstOrDefaultAsync(ct);

        if (nextJob != null)
        {
            BindJobVersion(nextJob, expectedJobVersion);
            nextJob.Status = PrintJobStatus.Cancelled;
            nextJob.UpdatedAt = DateTime.UtcNow;
            logger.LogInformation(
                ReadyGateLogPrefix + " Skipped (cancelled) job {JobId} ({JobName}) for printer {PrinterId}",
                nextJob.Id, nextJob.Name, printerId);
        }

        // Check if there are more queued jobs (cancelled job already persisted above)
        bool hasMoreJobs = await db.PrintJobs
            .AnyAsync(
                j => j.AssignedPrinterId == printerId
                        && j.Status == PrintJobStatus.Queued
                        && (nextJob == null || j.Id != nextJob.Id), ct);

        EnsureDispatchState(printer).AutoDispatchState = hasMoreJobs ? AutoDispatchState.PendingReady : AutoDispatchState.None;
        await db.SaveChangesAsync(ct);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, status, ct);

        return status;
    }

    public Task<AutoDispatchStatusDto> CancelAutoAsync(
        Guid printerId,
        CancellationToken ct = default) =>
        CancelAutoCoreAsync(printerId, expectedDispatchStateVersion: null, ct);

    public Task<AutoDispatchStatusDto> CancelAutoAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default) =>
        CancelAutoCoreAsync(printerId, expectedDispatchStateVersion, ct);

    private async Task<AutoDispatchStatusDto> CancelAutoCoreAsync(
        Guid printerId,
        byte[]? expectedDispatchStateVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);

        EnsureDispatchState(printer).AutoDispatchState = AutoDispatchState.Dismissed;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(ReadyGateLogPrefix + " Auto-dispatch ready gate cancelled for printer {PrinterId} ({Name})", printerId, printer.Name);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, status, ct);

        return status;
    }

    public async Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        CancellationToken ct = default) =>
        await MarkPreClearAsync(
            printerId,
            QueueActorIdentity.AutoDispatch,
            ct);

    public async Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        string actorSubject,
        CancellationToken ct = default) =>
        await MarkPreClearCoreAsync(
            printerId,
            actorSubject,
            expectedDispatchStateVersion: null,
            ct);

    public Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        string actorSubject,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default) =>
        MarkPreClearCoreAsync(
            printerId,
            actorSubject,
            expectedDispatchStateVersion,
            ct);

    private async Task<AutoDispatchStatusDto> MarkPreClearCoreAsync(
        Guid printerId,
        string actorSubject,
        byte[]? expectedDispatchStateVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);

        if (!printer.AutoDispatchEnabled)
        {
            throw new InvalidOperationException($"Auto-dispatch is not enabled for printer {printer.Name}");
        }

        // Guard: printer must be idle (not actively printing)
        bool hasActiveJob = await db.PrintJobs
            .AnyAsync(
                j => j.AssignedPrinterId == printerId
                     && (j.Status == PrintJobStatus.Starting ||
                         j.Status == PrintJobStatus.Printing ||
                         j.Status == PrintJobStatus.Paused),
                ct);

        if (hasActiveJob)
        {
            throw new InvalidOperationException($"Cannot pre-clear bed while printer {printer.Name} is actively printing");
        }

        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printerId, includeGcodeFile: false, ct);
        if (queuedJobs.NextJob?.JobKind == JobKind.FilamentCalibration)
        {
            throw new InvalidOperationException(
                "Calibration jobs cannot use generic bed pre-clear; acknowledge the exact job instead.");
        }

        PrinterDispatchState preClearState = EnsureDispatchState(printer);
        preClearState.BedPreConfirmed = true;

        // Pre-clearing the bed is a SAFETY OVERRIDE: it lets the next job dispatch without
        // the per-job bed-clear acknowledgement. It must be durably audited in the SAME
        // transaction as the flag it sets (issue #900, defect 13).
        _ = QueueAuditWriter.Add(
            db,
            actorSubject,
            QueueAuditOperations.SafetyOverride,
            QueueAuditOutcomes.Success,
            nameof(Printer),
            resourceId: printerId,
            printerId: printerId,
            reasonCode: "bed_pre_confirmed",
            dispatchStateRowVersion: preClearState.RowVersion,
            detail: new { queueDepth = queuedJobs.QueueDepth });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(ReadyGateLogPrefix + " Bed pre-clear confirmed for printer {PrinterId} ({Name})", printerId, printer.Name);

        if (queuedJobs.QueueDepth > 0)
        {
            // A pre-cleared idle printer just became eligible for immediate dispatch.
            dispatchTrigger?.NotifyJobQueued(printerId);
        }

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, status, ct);

        webhookService?.Enqueue("printer.bed_pre_confirmed", new { printerId, printerName = printer.Name });

        return status;
    }

    public async Task<AutoDispatchStatusDto> GetStatusAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers
            .Include(p => p.DispatchState)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, ct);

        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        return await BuildStatusDtoAsync(printer, ct);
    }

    public Task<AutoDispatchStatusDto> SetEnabledAsync(
        Guid printerId,
        bool enabled,
        CancellationToken ct = default) =>
        SetEnabledCoreAsync(
            printerId,
            enabled,
            expectedDispatchStateVersion: null,
            expectedPrinterVersion: null,
            ct);

    public Task<AutoDispatchStatusDto> SetEnabledAsync(
        Guid printerId,
        bool enabled,
        byte[] expectedDispatchStateVersion,
        byte[] expectedPrinterVersion,
        CancellationToken ct = default) =>
        SetEnabledCoreAsync(
            printerId,
            enabled,
            expectedDispatchStateVersion,
            expectedPrinterVersion,
            ct);

    private async Task<AutoDispatchStatusDto> SetEnabledCoreAsync(
        Guid printerId,
        bool enabled,
        byte[]? expectedDispatchStateVersion,
        byte[]? expectedPrinterVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);
        if (expectedPrinterVersion is not null)
        {
            db.Entry(printer).Property(candidate => candidate.RowVersion).OriginalValue =
                expectedPrinterVersion;
        }

        printer.AutoDispatchEnabled = enabled;
        if (!enabled)
        {
            EnsureDispatchState(printer).AutoDispatchState = AutoDispatchState.None;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            ReadyGateLogPrefix + " Auto-dispatch {Action} for printer {PrinterId} ({Name})",
            enabled ? "enabled" : "disabled", printerId, printer.Name);

        return await BuildStatusDtoAsync(printer, ct);
    }

    public async Task<AutoDispatchGlobalStatusDto> GetAllStatusAsync(CancellationToken ct = default)
    {
        List<Printer> printers = await db.Printers.Include(p => p.DispatchState).AsNoTracking().ToListAsync(ct);
        Dictionary<Guid, string?> currentJobs = await GetCurrentJobNamesByPrinterAsync(printers.Select(p => p.Id), ct);

        bool globalEnabled = printers.Any(p => p.AutoDispatchEnabled);
        List<AutoDispatchStatusDto> statuses = [];
        foreach (Printer printer in printers)
        {
            try
            {
                QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printer.Id, includeGcodeFile: false, ct);
                statuses.Add(BuildStatusDto(
                    printer,
                    queuedJobs.QueueDepth,
                    currentJobs.GetValueOrDefault(printer.Id),
                    queuedJobs.NextJob));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to build auto-dispatch status for printer {PrinterId} ({PrinterName})", printer.Id, printer.Name);
                statuses.Add(BuildStatusDto(printer, queuedJobCount: 0, currentJobName: null));
            }
        }

        return new AutoDispatchGlobalStatusDto
        {
            GlobalEnabled = globalEnabled,
            Printers = statuses,
        };
    }

    public async Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        return await SetAllEnabledCoreAsync(enabled, expectedVersions: null, ct);
    }

    public Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(
        bool enabled,
        IReadOnlyDictionary<Guid, AutoDispatchExpectedVersions> expectedVersions,
        CancellationToken ct = default) =>
        SetAllEnabledCoreAsync(enabled, expectedVersions, ct);

    private async Task<List<AutoDispatchStatusDto>> SetAllEnabledCoreAsync(
        bool enabled,
        IReadOnlyDictionary<Guid, AutoDispatchExpectedVersions>? expectedVersions,
        CancellationToken ct)
    {
        List<Printer> printers = await db.Printers.Include(p => p.DispatchState).ToListAsync(ct);
        foreach (Printer printer in printers)
        {
            if (expectedVersions is not null)
            {
                if (!expectedVersions.TryGetValue(printer.Id, out AutoDispatchExpectedVersions? expected))
                {
                    throw new QueuePreconditionRequiredException(
                        $"Expected versions are required for printer {printer.Id}.");
                }

                BindDispatchStateVersion(
                    printer.DispatchState,
                    expected.DispatchStateVersion);
                db.Entry(printer).Property(candidate => candidate.RowVersion).OriginalValue =
                    expected.PrinterVersion;
            }

            printer.AutoDispatchEnabled = enabled;
            if (!enabled)
            {
                EnsureDispatchState(printer).AutoDispatchState = AutoDispatchState.None;
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

    private void BindDispatchStateVersion(
        PrinterDispatchState? state,
        byte[]? expectedVersion)
    {
        if (expectedVersion is null)
        {
            return;
        }

        if (state is null)
        {
            throw new QueueRevisionConflictException(
                "The printer dispatch state no longer exists.");
        }

        db.Entry(state).Property(candidate => candidate.RowVersion).OriginalValue =
            expectedVersion;
    }

    private void BindJobVersion(PrintJob job, byte[]? expectedVersion)
    {
        if (expectedVersion is not null)
        {
            db.Entry(job).Property(candidate => candidate.RowVersion).OriginalValue =
                expectedVersion;
        }
    }

    private static AutoDispatchStatusDto BuildStatusDto(
        Printer printer,
        int queuedJobCount,
        string? currentJobName = null,
        PrintJob? nextJob = null)
    {
        string now = DateTime.UtcNow.ToString("o");
        bool hasActiveJob = !string.IsNullOrWhiteSpace(currentJobName);
        AutoDispatchState effectiveState = ResolveEffectiveState(printer, queuedJobCount, hasActiveJob);
        bool isReady = printer.AutoDispatchEnabled && effectiveState == AutoDispatchState.Ready;
        var gateChecks = BuildReadyGateChecks(printer, queuedJobCount, effectiveState, now);
        string? attentionMessage = BuildAttentionMessage(printer, queuedJobCount, effectiveState);

        return new AutoDispatchStatusDto
        {
            PrinterId = printer.Id,
            PrinterName = printer.Name,
            Enabled = printer.AutoDispatchEnabled,
            IsReady = isReady,
            CurrentJobName = currentJobName,
            QueueDepth = queuedJobCount,
            ReadyGateChecks = gateChecks,
            State = effectiveState.ToString(),
            BedPreConfirmed = printer.DispatchState?.BedPreConfirmed ?? false,
            DispatchStateETag = printer.DispatchState?.RowVersion is { Length: > 0 } rowVersion
                ? Convert.ToBase64String(rowVersion)
                : null,
            PrinterETag = printer.RowVersion is { Length: > 0 } printerRowVersion
                ? Convert.ToBase64String(printerRowVersion)
                : null,
            NextJobId = nextJob?.Id,
            NextJobName = nextJob?.Name ?? nextJob?.GcodeFile?.Name,
            NextJobETag = nextJob?.RowVersion is { Length: > 0 } jobRowVersion
                ? Convert.ToBase64String(jobRowVersion)
                : null,
            NextJobKind = nextJob is null
                ? null
                : (
                    nextJob.JobKind ??
                    Farm.Infrastructure.Domain.JobKind.Standard).ToString(),
            NextJobPrinterConfigRevision =
                nextJob?.PinnedPrinterConfigRevision,
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

        return BuildStatusDto(
            printer,
            queuedJobs.QueueDepth,
            currentJobName,
            queuedJobs.NextJob);
    }

    private static AutoDispatchState ResolveEffectiveState(Printer printer, int queuedJobCount, bool hasActiveJob)
    {
        AutoDispatchState storedState = printer.DispatchState?.AutoDispatchState ?? AutoDispatchState.None;
        bool bedPreConfirmed = printer.DispatchState?.BedPreConfirmed ?? false;

        if (storedState == AutoDispatchState.Dismissed)
        {
            return AutoDispatchState.None;
        }

        if (storedState != AutoDispatchState.None)
        {
            return storedState;
        }

        if (!printer.AutoDispatchEnabled
            || bedPreConfirmed
            || queuedJobCount <= 0
            || hasActiveJob
            || !printer.IsAvailable
            || printer.InMaintenance)
        {
            return AutoDispatchState.None;
        }

        return AutoDispatchState.PendingReady;
    }

    private static List<ReadyGateCheckDto> BuildReadyGateChecks(
        Printer printer,
        int queuedJobCount,
        AutoDispatchState effectiveState,
        string checkedAt)
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
            bool confirmationRequired = effectiveState == AutoDispatchState.PendingReady;
            checks.Add(new ReadyGateCheckDto
            {
                Name = "Bed Clear Confirmed",
                Passed = !confirmationRequired,
                Message = effectiveState switch
                {
                    AutoDispatchState.Ready => "Operator confirmed bed is clear",
                    _ when printer.DispatchState?.BedPreConfirmed is true => "Bed pre-cleared for immediate dispatch",
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
        int queuedJobCount,
        AutoDispatchState effectiveState)
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

        if (effectiveState == AutoDispatchState.PendingReady)
        {
            return queuedJobCount switch
            {
                <= 0 => "Print completed. Clear the bed and confirm ready before queued work can resume.",
                1 => "Print completed. 1 queued job is blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.",
                _ => $"Print completed. {queuedJobLabel} are blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.",
            };
        }

        if (queuedJobCount > 0 && (effectiveState == AutoDispatchState.Ready || (printer.DispatchState?.BedPreConfirmed ?? false)))
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
        // Ready-head selection MUST use the single shared ordering selector so the job the
        // operator sees at the head of the queue is exactly the job that gets dispatched.
        IQueryable<PrintJob> assignedQuery = db.PrintJobs
            .Where(j => j.AssignedPrinterId == printerId && j.Status == PrintJobStatus.Queued)
            .OrderByPriorityDescending();

        IQueryable<PrintJob> unassignedQuery = db.PrintJobs
            .Where(j => j.AssignedPrinterId == null && j.Status == PrintJobStatus.Queued)
            .OrderByPriorityDescending();

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

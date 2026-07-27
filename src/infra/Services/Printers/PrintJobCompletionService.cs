using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.AutoTagging;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Diagnostics;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IAutoDispatchService? _autoDispatchService;
    private readonly IBackendClientFactory? _backendFactory;
    private readonly ISpoolmanService? _spoolmanService;
    private readonly IAutoDispatchTrigger? _autoDispatchTrigger;
    private readonly IDiagnosticChannelService? _diagnostics;
    private readonly IJobCostCalculationService? _jobCostCalculationService;
    private readonly IAutoTagService? _autoTagService;
    private readonly ICameraSnapshotService? _cameraSnapshotService;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly IDbOutboxSequenceAllocator? _sequenceAllocator;

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
    /// Minimum time a job must be in Starting/Printing status before it can be
    /// considered orphaned. Prevents false positives when a job has just been
    /// dispatched but the printer hasn't started printing yet (file upload,
    /// heating, etc.).
    /// </summary>
    private static readonly TimeSpan OrphanedJobMinAge = TimeSpan.FromMinutes(5);

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
        IAutoDispatchService? autoDispatchService = null,
        IBackendClientFactory? backendFactory = null,
        ISpoolmanService? spoolmanService = null,
        IAutoDispatchTrigger? autoDispatchTrigger = null,
        IDiagnosticChannelService? diagnostics = null,
        IJobCostCalculationService? jobCostCalculationService = null,
        IAutoTagService? autoTagService = null,
        ICameraSnapshotService? cameraSnapshotService = null,
        IServiceScopeFactory? serviceScopeFactory = null,
        IDbOutboxSequenceAllocator? sequenceAllocator = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationService = notificationService;
        _costCalculator = costCalculator;
        _autoDispatchService = autoDispatchService;
        _backendFactory = backendFactory;
        _spoolmanService = spoolmanService;
        _autoDispatchTrigger = autoDispatchTrigger;
        _diagnostics = diagnostics;
        _jobCostCalculationService = jobCostCalculationService;
        _autoTagService = autoTagService;
        _cameraSnapshotService = cameraSnapshotService;
        _serviceScopeFactory = serviceScopeFactory;
        _sequenceAllocator = sequenceAllocator;
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
    public async Task<bool> MarkCurrentJobAsCompletedAsync(
        Guid printerId,
        string completionState,
        PrinterTerminalObservation observation,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[PrintJobCompletionService] Marking current job as completed for printer {PrinterId} (state: {CompletionState})",
            printerId,
            completionState);

        List<PrintJob> activeJobs = await LoadFencedTerminalJobsAsync(
            printerId,
            observation,
            ct);

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

        // Fetch actual filament usage from backend and record consumption in Spoolman
        await FetchAndRecordFilamentUsageAsync(primaryJob, printerId, ct);

        // Auto-tag the completed job with material, color, and nozzle info
        if (_autoTagService is not null && primaryJob.Status == PrintJobStatus.Completed)
        {
            try
            {
                await _autoTagService.GenerateTagsAsync(primaryJob, printerId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PrintJobCompletionService] Auto-tagging failed for job {JobId}", primaryJob.Id);
            }
        }

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);

        // Atomically release the matching queue lease in the same terminal transaction.
        await ReleaseLeaseForTerminalJobsAsync(printerId, activeJobs, DispatchAttemptOutcome.Accepted, ct);

        // Emit a durable lifecycle event so the outbox publisher broadcasts the completion
        // to authorized groups. Written in the SAME transaction as the status change.
        await WriteTerminalOutboxEventAsync(
            primaryJob,
            printerId,
            DispatchClaimService.EventTypeJobCompleted,
            failureCode: null,
            extraDetails: new { completionState, allCopiesDone = true },
            ct);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Calculate detailed cost breakdown after persistence without blocking the status update.
        ScheduleDetailedCostBreakdown(primaryJob.Id);

        _logger.LogInformation(
            "[PrintJobCompletionService] Job {JobId} ({JobName}) marked as completed. Duration: {Duration}",
            primaryJob.Id,
            primaryJob.Name ?? primaryJob.GcodeFile?.Name ?? "Unknown",
            primaryJob.ActualPrintTime);

        // Broadcast job queue update via SignalR
        await BroadcastJobQueueUpdateAsync(printerId, ct);

        // Capture camera snapshots (true fire-and-forget — never blocks completion)
        if (_cameraSnapshotService is not null && _serviceScopeFactory is not null)
        {
            Guid captureForPrinter = printerId;
            Guid captureForJob = primaryJob.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    using IServiceScope scope = _serviceScopeFactory.CreateScope();
                    ICameraSnapshotService svc = scope.ServiceProvider.GetRequiredService<ICameraSnapshotService>();
                    await svc.CaptureSnapshotAsync(captureForPrinter, "PrintCompleted", captureForJob, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PrintJobCompletionService] Background snapshot capture failed for printer {PrinterId}", captureForPrinter);
                }
            });
        }

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

        // Trigger the auto-dispatch ready gate if enabled
        if (_autoDispatchService != null)
        {
            try
            {
                await _autoDispatchService.TransitionToPendingReadyAsync(printerId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PrintJobCompletionService] Failed to trigger auto-dispatch transition for printer {PrinterId}", printerId);
            }
        }

        // Trigger auto-dispatch scoring for this printer
        if (_autoDispatchTrigger != null)
        {
            _autoDispatchTrigger.NotifyPrinterIdle(printerId);
            _logger.LogDebug("[PrintJobCompletionService] Auto-dispatch trigger fired for printer {PrinterId}", printerId);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkCurrentJobAsFailedAsync(
        Guid printerId,
        string failureReason,
        PrinterTerminalObservation observation,
        CancellationToken ct = default)
    {
        _logger.LogWarning(
            "[PrintJobCompletionService] Marking current job as failed for printer {PrinterId} (reason: {FailureReason})",
            printerId,
            failureReason);

        List<PrintJob> activeJobs = await LoadFencedTerminalJobsAsync(
            printerId,
            observation,
            ct);

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

        // Record partial filament consumption for failed/cancelled prints
        await FetchAndRecordFilamentUsageAsync(primaryJob, printerId, ct);

        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(_db, ct);

        // Atomically release the matching queue lease in the same terminal transaction.
        await ReleaseLeaseForTerminalJobsAsync(printerId, activeJobs, DispatchAttemptOutcome.FailedBeforeStart, ct);

        // Emit a durable lifecycle event so the outbox publisher broadcasts the failure
        // to authorized groups. Written in the SAME transaction as the status change.
        await WriteTerminalOutboxEventAsync(
            primaryJob,
            printerId,
            DispatchClaimService.EventTypeJobFailed,
            failureCode: "backend_failure",
            extraDetails: new { failureReason },
            ct);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "[PrintJobCompletionService] Job {JobId} ({JobName}) marked as failed. Reason: {FailureReason}",
            primaryJob.Id,
            primaryJob.Name ?? primaryJob.GcodeFile?.Name ?? "Unknown",
            failureReason);

        // Broadcast job queue update via SignalR
        await BroadcastJobQueueUpdateAsync(printerId, ct);

        // Capture camera snapshots on failure (true fire-and-forget)
        if (_cameraSnapshotService is not null && _serviceScopeFactory is not null)
        {
            Guid captureForPrinter = printerId;
            Guid captureForJob = primaryJob.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    using IServiceScope scope = _serviceScopeFactory.CreateScope();
                    ICameraSnapshotService svc = scope.ServiceProvider.GetRequiredService<ICameraSnapshotService>();
                    await svc.CaptureSnapshotAsync(captureForPrinter, "PrintFailed", captureForJob, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PrintJobCompletionService] Background snapshot capture failed for printer {PrinterId}", captureForPrinter);
                }
            });
        }

        return true;
    }

    /// <summary>
    /// Fetches actual filament usage from the backend and records consumption in Spoolman.
    /// Fire-and-forget — never fails the job completion if Spoolman is unavailable.
    /// </summary>
    private async Task FetchAndRecordFilamentUsageAsync(PrintJob job, Guid printerId, CancellationToken ct)
    {
        if (_backendFactory is null || _spoolmanService is null)
        {
            return;
        }

        try
        {
            Printer? printer = job.AssignedPrinter ?? await _db.Printers.FindAsync([printerId], ct);
            if (printer is null)
            {
                return;
            }

            // Try to get actual filament usage from the backend
            IBackendClient? client = _backendFactory.GetClient(printer.Backend);
            PrinterCredential? credential = !string.IsNullOrEmpty(printer.ApiKey)
                ? new PrinterCredential { ApiKey = printer.ApiKey }
                : null;

            // Try per-extruder usage first (for multi-toolhead printers)
            Dictionary<int, double>? perExtruderUsage = null;
            if (client is ISupportsPerExtruderFilamentUsage perExtruderQuery)
            {
                perExtruderUsage = await perExtruderQuery.GetLastJobFilamentUsagePerExtruderAsync(
                    printer.ServerUrl, credential, ct);
            }

            if (perExtruderUsage is { Count: > 0 })
            {
                // Multi-toolhead path
                double totalGrams = perExtruderUsage.Values.Sum();
                job.ActualFilamentUsage = totalGrams;

                // Load any existing snapshot rows created at dispatch time
                var existingUsages = await _db.Set<PrintJobToolheadUsage>()
                    .Where(u => u.PrintJobId == job.Id)
                    .ToListAsync(ct);
                var existingByIndex = existingUsages.ToDictionary(u => u.ToolheadIndex);

                // Get toolhead spool assignments for this printer (fallback only)
                var toolheads = await _db.Toolheads
                    .Where(t => t.PrinterId == printerId)
                    .OrderBy(t => t.Index)
                    .ToListAsync(ct);

                // Build consumption list for batch operation
                var consumptions = new List<(int spoolId, double grams)>();

                foreach (var (toolIndex, grams) in perExtruderUsage)
                {
                    if (existingByIndex.TryGetValue(toolIndex, out var existing))
                    {
                        // Update the dispatch snapshot row — preserve snapshotted SpoolmanSpoolId
                        existing.FilamentUsageGrams = grams;
                    }
                    else
                    {
                        // No snapshot row — create a new one using live toolhead data
                        var toolhead = toolheads.FirstOrDefault(t => t.Index == toolIndex);
                        var usage = new PrintJobToolheadUsage
                        {
                            Id = Guid.NewGuid(),
                            PrintJobId = job.Id,
                            ToolheadIndex = toolIndex,
                            SpoolmanSpoolId = toolhead?.CurrentSpoolId,
                            FilamentUsageGrams = grams,
                            FilamentName = toolhead?.CurrentMaterial,
                            FilamentColor = toolhead?.CurrentFilamentColor
                        };
                        _db.Set<PrintJobToolheadUsage>().Add(usage);
                        existing = usage;
                    }

                    // Add to batch consumption list using the record's spool (snapshotted or live)
                    if (existing.SpoolmanSpoolId.HasValue && grams > 0)
                    {
                        consumptions.Add((existing.SpoolmanSpoolId.Value, grams));
                    }
                }

                // Batch-consume all spools in a single operation
                if (consumptions.Count > 0)
                {
                    int successCount = await _spoolmanService.ConsumeMultipleFilamentsAsync(consumptions, ct);
                    _logger.LogInformation(
                        "[PrintJobCompletionService] Batch-consumed filament from {SuccessCount}/{TotalCount} spools for multi-toolhead job {JobId}",
                        successCount, consumptions.Count, job.Id);
                }
            }
            else
            {
                // Single-spool path (existing behavior)
                double? usageGrams = null;
                if (client is ISupportsFilamentUsageQuery usageQuery)
                {
                    usageGrams = await usageQuery.GetLastJobFilamentUsageGramsAsync(
                        printer.ServerUrl, credential, ct);
                }

                // Fallback to slicer estimate if no actual data
                usageGrams ??= job.EstimatedFilamentUsage;

                if (usageGrams is > 0)
                {
                    job.ActualFilamentUsage = usageGrams;

                    // Upsert single toolhead usage record (snapshot may already exist)
                    var existingSingle = await _db.Set<PrintJobToolheadUsage>()
                        .FirstOrDefaultAsync(u => u.PrintJobId == job.Id && u.ToolheadIndex == 0, ct);

                    if (existingSingle is not null)
                    {
                        // Update the dispatch snapshot row — preserve snapshotted SpoolmanSpoolId
                        existingSingle.FilamentUsageGrams = usageGrams;
                    }
                    else
                    {
                        var primaryToolhead = await _db.Toolheads
                            .FirstOrDefaultAsync(t => t.PrinterId == printerId && t.IsPrimary, ct);

                        if (primaryToolhead is not null)
                        {
                            var usage = new PrintJobToolheadUsage
                            {
                                Id = Guid.NewGuid(),
                                PrintJobId = job.Id,
                                ToolheadIndex = 0,
                                SpoolmanSpoolId = printer.CurrentSpoolId ?? primaryToolhead.CurrentSpoolId,
                                FilamentUsageGrams = usageGrams,
                                FilamentName = primaryToolhead.CurrentMaterial,
                                FilamentColor = primaryToolhead.CurrentFilamentColor
                            };
                            _db.Set<PrintJobToolheadUsage>().Add(usage);
                        }
                    }
                }

                // Record consumption in Spoolman if printer has an active spool (existing)
                if (printer.CurrentSpoolId.HasValue && usageGrams is > 0)
                {
                    bool consumed = await _spoolmanService.ConsumeFilamentAsync(
                        printer.CurrentSpoolId.Value, usageGrams.Value, ct);

                    if (consumed)
                    {
                        _logger.LogInformation(
                            "[PrintJobCompletionService] Recorded {UsedGrams:F1}g filament consumption on spool {SpoolId} for job {JobId}",
                            usageGrams.Value, printer.CurrentSpoolId.Value, job.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[PrintJobCompletionService] Failed to fetch/record filament usage for job {JobId} — continuing completion",
                job.Id);
        }
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

            await _hub.Clients.Group(AuthorizedHubGroups.Farm).SendAsync("jobqueueupdate", update, ct);

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
        _logger.LogDebug("[PrintJobCompletionService] Running orphaned job reconciliation...");

        // Generic cached-state reconciliation is safe only for jobs whose backend
        // acceptance was already proven. Starting/Paused and uncertain attempts are
        // resolved exclusively by exact backend reconciliation.
        List<PrintJob> orphanedJobs = await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .Where(j =>
                j.Status == PrintJobStatus.Printing &&
                !_db.QueueDispatchOutbox.Any(command =>
                    command.AggregateId == j.Id &&
                    command.EventType == BackendControlCommandConsumerService.EventType &&
                    (command.Status == QueueOutboxEventStatus.Processing ||
                     (command.Status == QueueOutboxEventStatus.DeadLettered &&
                      command.FailureCode == "manual_control_reconciliation_required"))) &&
                !_db.QueueDispatchAttempts.Any(attempt =>
                    attempt.PrintJobId == j.Id &&
                    (attempt.Outcome == DispatchAttemptOutcome.InProgress ||
                     attempt.Outcome == DispatchAttemptOutcome.Unknown ||
                     attempt.RequiresReconciliation)))
            .ToListAsync(ct);

        if (orphanedJobs.Count == 0)
        {
            _logger.LogDebug("[PrintJobCompletionService] No orphaned jobs found.");
            return 0;
        }

        _logger.LogInformation(
            "[PrintJobCompletionService] Found {Count} potentially orphaned jobs in Starting/Printing status",
            orphanedJobs.Count);

        int syncedCount = 0;
        HashSet<Guid> printersToNotify = [];
        List<Guid> completedJobIds = [];

        // Track which jobs went terminal on each printer so we can release leases atomically.
        // Key = printerId; Value = (list of terminal jobs, outcome to record)
        var terminalJobsByPrinter = new Dictionary<Guid, (List<PrintJob> jobs, DispatchAttemptOutcome outcome)>();

        foreach (PrintJob job in orphanedJobs)
        {
            if (!job.AssignedPrinterId.HasValue)
            {
                _logger.LogWarning(
                    "[PrintJobCompletionService] Job {JobId} has no assigned printer, skipping",
                    job.Id);
                continue;
            }

            // Skip jobs that have only recently entered Starting/Printing status.
            // The printer may still be idle because the file is uploading or the
            // print hasn't physically started yet.
            DateTime jobActiveTime = job.ActualStartTime ?? job.UpdatedAt;
            TimeSpan elapsed = DateTime.UtcNow - jobActiveTime;
            if (elapsed < OrphanedJobMinAge)
            {
                _logger.LogDebug(
                    "[PrintJobCompletionService] Job {JobId} is only {Elapsed} old (min {Min}), skipping orphan check",
                    job.Id,
                    elapsed,
                    OrphanedJobMinAge);
                continue;
            }

            bool verbose = _diagnostics?.IsEnabled(DiagnosticChannels.OrphanedJobSync) == true;

            _logger.Log(
                verbose ? LogLevel.Warning : LogLevel.Debug,
                "[OrphanedJobSync] Evaluating job {JobId} ({JobName}): Status={Status}, ActualStartTime={ActualStartTime}, UpdatedAt={UpdatedAt}, ActiveTime={ActiveTime}, Elapsed={Elapsed}",
                job.Id,
                job.Name ?? job.GcodeFile?.Name ?? "Unknown",
                job.Status,
                job.ActualStartTime?.ToString("o") ?? "(null)",
                job.UpdatedAt.ToString("o"),
                jobActiveTime.ToString("o"),
                elapsed);

            Guid printerId = job.AssignedPrinterId.Value;
            string? currentPrinterState = printerStateLookup(printerId);

            _logger.Log(
                verbose ? LogLevel.Warning : LogLevel.Debug,
                "[OrphanedJobSync] Printer {PrinterId} cached state='{CachedState}' for job {JobId} (job status={JobStatus})",
                printerId,
                currentPrinterState ?? "(null/offline)",
                job.Id,
                job.Status);

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
                completedJobIds.Add(job.Id);

                if (!terminalJobsByPrinter.TryGetValue(printerId, out var completedEntry))
                {
                    terminalJobsByPrinter[printerId] = ([job], DispatchAttemptOutcome.Accepted);
                }
                else
                {
                    completedEntry.jobs.Add(job);
                }
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

                if (!terminalJobsByPrinter.TryGetValue(printerId, out var failedEntry))
                {
                    terminalJobsByPrinter[printerId] = ([job], DispatchAttemptOutcome.FailedBeforeStart);
                }
                else
                {
                    failedEntry.jobs.Add(job);
                }
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
            await using QueueOutboxTransactionScope transaction =
                await QueueOutboxTransactionScope.BeginAsync(_db, ct);

            // Release the matching dispatch lease for each terminal printer atomically
            // in the same SaveChangesAsync call as the job status changes.
            foreach (KeyValuePair<Guid, (List<PrintJob> jobs, DispatchAttemptOutcome outcome)> kv in terminalJobsByPrinter)
            {
                await ReleaseLeaseForTerminalJobsAsync(kv.Key, kv.Value.jobs, kv.Value.outcome, ct);

                // Emit one durable lifecycle event per terminal job so the outbox publisher
                // broadcasts the orphan-sync result to authorized groups. All events are
                // committed in the SAME SaveChangesAsync call as the lease releases.
                foreach (PrintJob terminalJob in kv.Value.jobs)
                {
                    string eventType = terminalJob.Status == PrintJobStatus.Completed
                        ? DispatchClaimService.EventTypeJobCompleted
                        : DispatchClaimService.EventTypeJobOrphanSynced;

                    await WriteTerminalOutboxEventAsync(
                        terminalJob,
                        kv.Key,
                        eventType,
                        failureCode: terminalJob.Status == PrintJobStatus.Failed ? "orphan_sync_failure" : null,
                        extraDetails: new { outcome = kv.Value.outcome.ToString(), source = "orphan_sync" },
                        ct);
                }
            }

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "[PrintJobCompletionService] Synced {Count} orphaned jobs",
                syncedCount);

            foreach (Guid completedJobId in completedJobIds)
            {
                ScheduleDetailedCostBreakdown(completedJobId);
            }

            // Broadcast updates for affected printers
            foreach (Guid printerId in printersToNotify)
            {
                await BroadcastJobQueueUpdateAsync(printerId, ct);
            }
        }

        return syncedCount;
    }

    /// <summary>
    /// Adds a durable terminal lifecycle outbox event to the context in preparation for the
    /// next <see cref="AppDbContext.SaveChangesAsync"/> call. Only writes when
    /// <see cref="_sequenceAllocator"/> is available so backward-compatible callers that omit
    /// the allocator are not affected.
    /// </summary>
    private async Task WriteTerminalOutboxEventAsync(
        PrintJob job,
        Guid printerId,
        string eventType,
        string? failureCode,
        object extraDetails,
        CancellationToken ct)
    {
        if (_sequenceAllocator is null)
        {
            return;
        }

        await DispatchClaimService.AddLifecycleOutboxEventAsync(
            _db,
            _sequenceAllocator,
            eventType,
            aggregateId: job.Id,
            printerId: printerId,
            attemptId: null,
            aggregateRowVersion: job.RowVersion,
            failureCode: failureCode,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                jobId = job.Id,
                printerId,
                jobStatus = job.Status.ToString(),
                jobKind = job.JobKind?.ToString() ?? "Standard",
                failureReason = job.FailureReason,
                details = extraDetails,
            }),
            ct);
    }

    /// <summary>
    /// Schedules detailed cost breakdown using the JobCostCalculationService if available.
    /// </summary>
    private void ScheduleDetailedCostBreakdown(Guid jobId)
    {
        if (_serviceScopeFactory is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using IServiceScope scope = _serviceScopeFactory.CreateScope();
                    IJobCostCalculationService costService = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
                    await costService.CalculateAndStoreCostsAsync(jobId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PrintJobCompletionService] Background cost calculation failed for job {JobId}", jobId);
                }
            });

            return;
        }

        if (_jobCostCalculationService is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _jobCostCalculationService.CalculateAndStoreCostsAsync(jobId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PrintJobCompletionService] Background cost calculation failed for job {JobId}", jobId);
            }
        });
    }

    private async Task<List<PrintJob>> LoadFencedTerminalJobsAsync(
        Guid printerId,
        PrinterTerminalObservation observation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observation);

        PrinterDispatchState? state = await _db.PrinterDispatchStates
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.PrinterId == printerId, ct);
        if (state?.ActiveDispatchAttemptId is Guid activeAttemptId)
        {
            bool hasUnresolvedControl = await _db.QueueDispatchOutbox
                .AsNoTracking()
                .AnyAsync(
                    command =>
                        command.AggregateId == state.ActiveJobId &&
                        command.AttemptId == activeAttemptId &&
                        command.EventType == BackendControlCommandConsumerService.EventType &&
                        (command.Status == QueueOutboxEventStatus.Processing ||
                         (command.Status == QueueOutboxEventStatus.DeadLettered &&
                          command.FailureCode == "manual_control_reconciliation_required")),
                    ct);
            if (hasUnresolvedControl)
            {
                _logger.LogInformation(
                    "Deferred terminal callback for printer {PrinterId}; control command for " +
                    "attempt {AttemptId} requires exact reconciliation",
                    printerId,
                    activeAttemptId);
                return [];
            }

            if (observation.DispatchAttemptId.HasValue &&
                observation.DispatchAttemptId.Value != activeAttemptId)
            {
                return [];
            }

            QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == activeAttemptId, ct);
            if (attempt?.PrintJobId is null ||
                state.ActiveJobId != attempt.PrintJobId ||
                !MatchesAttemptIdentity(attempt, observation.BackendIdentity))
            {
                _logger.LogWarning(
                    "Ignored stale terminal callback for printer {PrinterId}; active attempt {AttemptId} " +
                    "did not match backend identity '{BackendIdentity}'",
                    printerId,
                    activeAttemptId,
                    observation.BackendIdentity ?? "(missing)");
                return [];
            }

            return await _db.PrintJobs
                .Include(job => job.GcodeFile)
                .Include(job => job.AssignedPrinter)
                .Where(job =>
                    job.Id == attempt.PrintJobId.Value &&
                    job.AssignedPrinterId == printerId &&
                    (job.Status == PrintJobStatus.Starting ||
                     job.Status == PrintJobStatus.Printing ||
                     job.Status == PrintJobStatus.Paused))
                .ToListAsync(ct);
        }

        if (observation.DispatchAttemptId.HasValue ||
            string.IsNullOrWhiteSpace(observation.BackendIdentity))
        {
            return [];
        }

        // Backward compatibility for external/pre-upgrade active rows without a lease:
        // identify one exact job by its persisted G-code/name rather than completing every
        // active row on the printer.
        List<PrintJob> legacyCandidates = await _db.PrintJobs
            .Include(job => job.GcodeFile)
            .Include(job => job.AssignedPrinter)
            .Where(job =>
                job.AssignedPrinterId == printerId &&
                (job.Status == PrintJobStatus.Starting ||
                 job.Status == PrintJobStatus.Printing ||
                 job.Status == PrintJobStatus.Paused))
            .OrderByDescending(job => job.ActualStartTime ?? job.QueuedAt)
            .ToListAsync(ct);
        PrintJob? exactLegacy = legacyCandidates.FirstOrDefault(
            job => MatchesIdentity(
                observation.BackendIdentity,
                job.GcodeFile?.Name,
                job.GcodeFile?.FileName,
                job.Name,
                job.ExternalJobId));
        return exactLegacy is null ? [] : [exactLegacy];
    }

    private static bool MatchesAttemptIdentity(
        QueueDispatchAttempt attempt,
        string? observedIdentity) =>
        MatchesIdentity(
            observedIdentity,
            attempt.BackendJobId,
            attempt.BackendCommandId,
            attempt.BackendFileName);

    private static bool MatchesIdentity(string? observedIdentity, params string?[] expected)
    {
        if (string.IsNullOrWhiteSpace(observedIdentity))
        {
            return false;
        }

        string normalizedObserved = NormalizeIdentity(observedIdentity);
        string observedFileName = Path.GetFileName(normalizedObserved);
        return expected
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeIdentity(value!))
            .Any(value =>
                string.Equals(value, normalizedObserved, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    Path.GetFileName(value),
                    observedFileName,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeIdentity(string value) =>
        value.Trim().Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Releases the dispatch lease when the active job on the printer matches one of the
    /// terminal jobs. Runs within the same <see cref="AppDbContext"/> transaction as the
    /// job-status update so the lease is never left dangling after a completion or failure.
    /// </summary>
    private async Task ReleaseLeaseForTerminalJobsAsync(
        Guid printerId,
        IEnumerable<PrintJob> terminalJobs,
        DispatchAttemptOutcome terminalOutcome,
        CancellationToken ct)
    {
        PrinterDispatchState? dispatchState = await _db.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId, ct);

        if (dispatchState is null || !dispatchState.ActiveDispatchAttemptId.HasValue)
        {
            return; // No lease to release.
        }

        // Only release when the active job is one of the jobs going terminal.
        Guid? activeJobId = dispatchState.ActiveJobId;
        bool matchesTerminal = activeJobId.HasValue &&
            terminalJobs.Any(j => j.Id == activeJobId.Value);

        if (!matchesTerminal)
        {
            return;
        }

        Guid attemptId = dispatchState.ActiveDispatchAttemptId.Value;

        // Release the exclusive printer lease.
        dispatchState.ActiveJobId = null;
        dispatchState.ActiveDispatchAttemptId = null;

        // Close the dispatch attempt (if still InProgress or Accepted — do not overwrite a
        // FailedBeforeStart that was set by ReleaseClaimOnKnownFailureAsync).
        QueueDispatchAttempt? attempt = await _db.QueueDispatchAttempts
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);

        if (attempt is not null &&
            attempt.Outcome is DispatchAttemptOutcome.InProgress or DispatchAttemptOutcome.Accepted)
        {
            attempt.Outcome = terminalOutcome;
            attempt.UpdatedAtUtc = DateTime.UtcNow;
        }

        string reasonCode = terminalOutcome == DispatchAttemptOutcome.Accepted
            ? "job_completed"
            : "job_failed";

        _ = QueueAuditWriter.Add(
            _db,
            actorSubject: attempt?.ActorSubject ?? "system",
            QueueAuditOperations.DispatchRelease,
            QueueAuditOutcomes.Success,
            nameof(PrintJob),
            resourceId: activeJobId,
            printerId: printerId,
            printJobId: activeJobId,
            dispatchAttemptId: attemptId,
            reasonCode: reasonCode,
            dispatchStateRowVersion: dispatchState.RowVersion,
            detail: new
            {
                terminalOutcome = terminalOutcome.ToString(),
                leaseReleasedAtTerminal = true,
            });

        _logger.LogInformation(
            "[PrintJobCompletionService] Released dispatch lease for printer {PrinterId} attempt {AttemptId} (outcome={Outcome})",
            printerId, attemptId, terminalOutcome);
    }

    /// <inheritdoc />
    public async Task<bool> EnsureExternalPrintJobExistsAsync(Guid printerId, string? fileName, CancellationToken ct = default)
    {
        bool hasActiveJob = await _db.PrintJobs
            .AnyAsync(
                j => j.AssignedPrinterId == printerId &&
                     (j.Status == PrintJobStatus.Starting ||
                      j.Status == PrintJobStatus.Printing ||
                      j.Status == PrintJobStatus.Paused),
                ct);

        if (hasActiveJob)
        {
            _logger.LogDebug(
                "[PrintJobCompletionService] Active job already exists for printer {PrinterId}, skipping external print creation",
                printerId);
            return false;
        }

        string displayName = !string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(fileName)
            : "External Print";

        DateTime now = DateTime.UtcNow;

        var externalJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = displayName,
            Status = PrintJobStatus.Printing,
            AssignedPrinterId = printerId,
            SourcePrinterId = printerId,
            ActualStartTime = now,
            CreatedAt = now,
            UpdatedAt = now,
            QueuedAt = now,
            IsExternalPrint = true,
            ExternalJobId = $"ext-{printerId:N}-{now:yyyyMMddHHmmss}",
        };

        _db.PrintJobs.Add(externalJob);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[PrintJobCompletionService] Created external print job {JobId} for printer {PrinterId} (file: {FileName})",
            externalJob.Id,
            printerId,
            displayName);

        await BroadcastJobQueueUpdateAsync(printerId, ct);

        return true;
    }
}

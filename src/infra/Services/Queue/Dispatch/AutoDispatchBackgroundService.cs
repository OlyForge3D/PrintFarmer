using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Background service that reacts to printer-idle events and orchestrates
/// automatic job dispatch. Event-driven via <see cref="IAutoDispatchTrigger"/>
/// in-memory events act as wake-up hints; a periodic database scan is the durable
/// source of eligible work after dropped events or process restarts. Selection and
/// in-memory job claims are serialized while independent printer dispatches run under
/// the configured async capacity limit.
/// </summary>
public sealed class AutoDispatchBackgroundService(
    AutoDispatchTrigger trigger,
    IServiceScopeFactory scopeFactory,
    DispatchConcurrencyCoordinator concurrencyCoordinator,
    IHubContext<PrinterHub> hub,
    ILogger<AutoDispatchBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _selectionLock = new(1, 1);
    private readonly object _workerSync = new();
    private readonly object _claimSync = new();
    private readonly HashSet<Task> _workers = [];
    private readonly HashSet<Guid> _claimedJobs = [];
    private static readonly TimeSpan DurableScanInterval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public override void Dispose()
    {
        _selectionLock.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[AutoDispatch] Background service started");

        try
        {
            await ReconcileStartupEligiblePrintersAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                // In-memory events are wake-up hints; a periodic database scan is the durable
                // source of eligible work after dropped events or process restarts (#900).
                DispatchTriggerEvent triggerEvent;
                using (CancellationTokenSource readCts =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    Task<DispatchTriggerEvent> readTask = trigger.ReadAsync(readCts.Token).AsTask();
                    Task scanDelay = Task.Delay(DurableScanInterval, stoppingToken);
                    Task completed = await Task.WhenAny(readTask, scanDelay);

                    if (completed == scanDelay)
                    {
                        await readCts.CancelAsync();
                        try
                        {
                            _ = await readTask;
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected: the periodic durable scan won this wait.
                        }

                        await ReconcileStartupEligiblePrintersAsync(stoppingToken);
                        continue;
                    }

                    try
                    {
                        triggerEvent = await readTask;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                // Dispatch via a tracked worker so idle printers run concurrently under the
                // configured capacity limit, with rerun coalescing and graceful drain on
                // shutdown. The cross-process database claim prevents duplicates.
                StartTrackedWorker(triggerEvent, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels the reader and every linked worker operation.
        }
        finally
        {
            trigger.StopAccepting();
            await DrainWorkersAsync();
            logger.LogInformation("[AutoDispatch] Background service stopped");
        }
    }

    private void StartTrackedWorker(
        DispatchTriggerEvent triggerEvent,
        CancellationToken stoppingToken)
    {
        Task worker = Task.Run(
            () => ProcessOwnedPrinterAsync(triggerEvent, stoppingToken),
            CancellationToken.None);
        lock (_workerSync)
        {
            _workers.Add(worker);
        }

        _ = worker.ContinueWith(
            completed =>
            {
                lock (_workerSync)
                {
                    _ = _workers.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal int TrackedWorkerCount
    {
        get
        {
            lock (_workerSync)
            {
                return _workers.Count;
            }
        }
    }

    private async Task ProcessOwnedPrinterAsync(
        DispatchTriggerEvent triggerEvent,
        CancellationToken stoppingToken)
    {
        DispatchTriggerEvent current = triggerEvent;
        bool ownsIntent = true;
        try
        {
            while (true)
            {
                await ProcessPrinterIdleAsync(
                    current.PrinterId,
                    current.SkipIdleThreshold,
                    stoppingToken);
                if (!trigger.TryCompleteProcessing(
                        current,
                        allowRerun: !stoppingToken.IsCancellationRequested,
                        out DispatchTriggerEvent rerun))
                {
                    ownsIntent = false;
                    return;
                }

                current = rerun;
            }
        }
        finally
        {
            if (ownsIntent)
            {
                _ = trigger.TryCompleteProcessing(
                    current,
                    allowRerun: false,
                    out _);
            }
        }
    }

    private async Task DrainWorkersAsync()
    {
        while (true)
        {
            Task[] activeWorkers;
            lock (_workerSync)
            {
                activeWorkers = _workers.ToArray();
            }

            if (activeWorkers.Length == 0)
            {
                return;
            }

            await Task.WhenAll(activeWorkers);
        }
    }

    internal async Task ReconcileStartupEligiblePrintersAsync(CancellationToken ct)
    {
        DateTime startupAt = DateTime.UtcNow;
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        DispatchSettings settings = await db.DispatchSettings.AsNoTracking().FirstAsync(ct);
        if (!settings.AutoDispatchEnabled || settings.AutoDispatchMode == AutoDispatchMode.Manual)
        {
            return;
        }

        List<Guid> printerIds = await db.Printers
            .AsNoTracking()
            .Include(p => p.DispatchState)
            .Where(p =>
                p.IsEnabled
                && p.IsAvailable
                && p.AutoDispatchEnabled
                && p.DispatchState != null
                && (p.DispatchState.AutoDispatchState == AutoDispatchState.Ready || p.DispatchState.BedPreConfirmed)
                && !db.PrintJobs.Any(j =>
                    j.AssignedPrinterId == p.Id
                    && PrintJobOccupancy.Statuses.Contains(j.Status))
                && db.PrintJobs.Any(j =>
                    j.Status == PrintJobStatus.Queued
                    && j.QueuedAt <= startupAt
                    && (j.AssignedPrinterId == null || j.AssignedPrinterId == p.Id)))
            .Select(p => p.Id)
            .ToListAsync(ct);

        foreach (Guid printerId in printerIds)
        {
            logger.LogInformation(
                "[AutoDispatch] Re-queueing eligible printer {PrinterId} during startup reconciliation",
                printerId);
            trigger.NotifyJobQueued(printerId);
        }
    }

    internal async Task ProcessPrinterIdleAsync(
        Guid printerId,
        bool skipIdleThreshold,
        CancellationToken serviceCt)
    {
        // Hosted-loop callers already hold trigger ownership for this printer. Keeping
        // ownership outside the dispatch body avoids one waiting task per notification;
        // direct test callers use this method only for independent printers.
        try
        {
            await ProcessPrinterIdleOwnedAsync(printerId, skipIdleThreshold, serviceCt);
        }
        catch (OperationCanceledException) when (serviceCt.IsCancellationRequested)
        {
            // Host shutdown is expected control flow.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AutoDispatch] Unhandled error for printer {PrinterId}", printerId);
        }
    }

    private async Task ProcessPrinterIdleOwnedAsync(
        Guid printerId,
        bool skipIdleThreshold,
        CancellationToken serviceCt)
    {
        PendingDispatchLease? pendingLease = skipIdleThreshold
            ? null
            : trigger.CreatePendingLease(printerId, serviceCt);
        try
        {
            DispatchSettings settings;
            await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
            {
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                settings = await db.DispatchSettings.AsNoTracking().FirstAsync(serviceCt);
            }

            if (!settings.AutoDispatchEnabled || settings.AutoDispatchMode == AutoDispatchMode.Manual)
            {
                logger.LogDebug(
                    "[AutoDispatch] Skipping printer {PrinterId}: enabled={Enabled}, mode={Mode}",
                    printerId,
                    settings.AutoDispatchEnabled,
                    settings.AutoDispatchMode);
                return;
            }

            if (pendingLease is not null)
            {
                try
                {
                    logger.LogDebug(
                        "[AutoDispatch] Printer {PrinterId} idle — waiting {Seconds}s threshold",
                        printerId,
                        settings.IdleThresholdSeconds);
                    await Task.Delay(
                        TimeSpan.FromSeconds(settings.IdleThresholdSeconds),
                        pendingLease.Token);
                }
                catch (OperationCanceledException) when (!serviceCt.IsCancellationRequested)
                {
                    logger.LogInformation(
                        "[AutoDispatch] Idle wait cancelled for printer {PrinterId}",
                        printerId);
                    return;
                }

                trigger.ClearPending(printerId, pendingLease);
                pendingLease.Dispose();
                pendingLease = null;
            }
            else
            {
                logger.LogDebug(
                    "[AutoDispatch] Printer {PrinterId} — skipping idle threshold (job-queued trigger)",
                    printerId);
            }

            if (!concurrencyCoordinator.TryClaimPrinter(printerId))
            {
                logger.LogDebug(
                    "[AutoDispatch] Printer {PrinterId} is already claimed by another dispatch",
                    printerId);
                return;
            }

            try
            {
                DispatchPlan plan;
                await _selectionLock.WaitAsync(serviceCt);
                try
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    plan = await SelectDispatchPlanAsync(
                        scope.ServiceProvider,
                        printerId,
                        settings,
                        serviceCt);
                }
                finally
                {
                    _selectionLock.Release();
                }

                try
                {
                    await ExecuteDispatchPlanAsync(plan, settings, serviceCt);
                }
                finally
                {
                    if (plan.ClaimedJobId is Guid claimedJobId)
                    {
                        ReleaseJobClaim(claimedJobId);
                    }
                }
            }
            finally
            {
                concurrencyCoordinator.ReleasePrinter(printerId);
            }
        }
        finally
        {
            if (pendingLease is not null)
            {
                trigger.ClearPending(printerId, pendingLease);
                pendingLease.Dispose();
            }
        }
    }

    private async Task<DispatchPlan> SelectDispatchPlanAsync(
        IServiceProvider sp,
        Guid printerId,
        DispatchSettings settings,
        CancellationToken ct)
    {
        AppDbContext db = sp.GetRequiredService<AppDbContext>();
        IDispatchScorer scorer = sp.GetRequiredService<IDispatchScorer>();
        Printer? printer = await db.Printers
            .Include(value => value.DispatchState)
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.Id == printerId, ct);
        if (printer is null || !printer.IsEnabled || !printer.IsAvailable)
        {
            logger.LogDebug(
                "[AutoDispatch] Printer {PrinterId} not found, disabled, or unavailable — aborting",
                printerId);
            return DispatchPlan.NoWork;
        }

        bool hasOccupyingJob = await db.PrintJobs
            .WhereOccupiesPrinter()
            .AnyAsync(job => job.AssignedPrinterId == printerId, ct);
        if (hasOccupyingJob)
        {
            logger.LogDebug(
                "[AutoDispatch] Printer {PrinterId} has an occupying job — aborting",
                printerId);
            return DispatchPlan.NoWork;
        }

        if (!printer.AutoDispatchEnabled)
        {
            logger.LogDebug(
                "[AutoDispatch] Printer {PrinterId} has per-printer auto-dispatch disabled — skipping",
                printerId);
            return DispatchPlan.NoWork;
        }

        if ((printer.DispatchState?.AutoDispatchState ?? AutoDispatchState.None) != AutoDispatchState.Ready
            && (printer.DispatchState?.BedPreConfirmed ?? false))
        {
            IAutoDispatchService readyGate = sp.GetRequiredService<IAutoDispatchService>();
            await readyGate.TransitionToPendingReadyAsync(printerId, ct);
            printer = await db.Printers
                .Include(value => value.DispatchState)
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == printerId, ct);
        }

        if (printer is null ||
            (printer.DispatchState?.AutoDispatchState ?? AutoDispatchState.None) !=
            AutoDispatchState.Ready)
        {
            logger.LogDebug(
                "[AutoDispatch] Printer {PrinterId} is not ready after ready-gate evaluation (state: {State})",
                printerId,
                printer?.DispatchState?.AutoDispatchState ?? AutoDispatchState.None);
            return DispatchPlan.NoWork;
        }

        // Find candidate jobs: unassigned queued jobs OR jobs assigned to this printer.
        // Uses the SINGLE shared ordering selector so readiness/skip and dispatch agree.
        List<PrintJob> candidateJobs = await db.PrintJobs
            .AsNoTracking()
            .Where(job => job.Status == PrintJobStatus.Queued
                && (job.AssignedPrinterId == null || job.AssignedPrinterId == printerId))
            .OrderByPriorityDescending()
            .Take(20) // reasonable batch to score
            .ToListAsync(ct);
        if (candidateJobs.Count == 0)
        {
            logger.LogDebug(
                "[AutoDispatch] No queued jobs available for printer {PrinterId}",
                printerId);
            return DispatchPlan.NoWork;
        }

        foreach (PrintJob job in candidateJobs)
        {
            if (IsJobClaimed(job.Id))
            {
                continue;
            }

            List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id, ct);
            DispatchScore? printerScore = scores.FirstOrDefault(score => score.PrinterId == printerId);
            if (printerScore is null
                || printerScore.Eliminated
                || printerScore.TotalScore < settings.MinimumScoreThreshold)
            {
                continue;
            }

            string printerName = printer.Name ?? printerId.ToString();
            string jobName = job.Name ?? "Unknown";
            if (settings.AutoDispatchMode == AutoDispatchMode.Suggest)
            {
                return new DispatchPlan(
                    DispatchPlanKind.Suggest,
                    printerId,
                    printerName,
                    job.Id,
                    jobName,
                    printerScore,
                    ClaimedJobId: null);
            }

            if (!TryClaimJob(job.Id))
            {
                continue;
            }

            return new DispatchPlan(
                DispatchPlanKind.Auto,
                printerId,
                printerName,
                job.Id,
                jobName,
                printerScore,
                ClaimedJobId: job.Id);
        }

        return new DispatchPlan(
            DispatchPlanKind.NoCompatibleJob,
            printerId,
            printer.Name ?? printerId.ToString(),
            Guid.Empty,
            string.Empty,
            Score: null,
            ClaimedJobId: null);
    }

    private async Task ExecuteDispatchPlanAsync(
        DispatchPlan plan,
        DispatchSettings settings,
        CancellationToken ct)
    {
        switch (plan.Kind)
        {
            case DispatchPlanKind.NoWork:
                return;
            case DispatchPlanKind.NoCompatibleJob:
                logger.LogInformation(
                    "[AutoDispatch] No compatible jobs above threshold ({Threshold}) for printer {PrinterId}",
                    settings.MinimumScoreThreshold,
                    plan.PrinterId);
                await hub.Clients.Group(AuthorizedHubGroups.Farm).SendAsync(
                    "dispatchfailed",
                    new DispatchFailedEvent
                    {
                        PrinterId = plan.PrinterId,
                        PrinterName = plan.PrinterName,
                        Reason = "No compatible queued jobs found above minimum score threshold",
                    },
                    ct);
                return;
            case DispatchPlanKind.Suggest:
                await ExecuteSuggestionAsync(plan, ct);
                return;
            case DispatchPlanKind.Auto:
                await ExecuteAutoDispatchAsync(
                    plan,
                    settings.MaxConcurrentDispatches,
                    ct);
                return;
            default:
                throw new InvalidOperationException($"Unknown dispatch plan kind {plan.Kind}.");
        }
    }

    private async Task ExecuteSuggestionAsync(DispatchPlan plan, CancellationToken ct)
    {
        DispatchScore score = plan.Score!;
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        logger.LogInformation(
            "[AutoDispatch] Suggesting job {JobId} for printer {PrinterName} (score: {Score:F1})",
            plan.JobId,
            plan.PrinterName,
            score.TotalScore);
        db.DispatchLogs.Add(new DispatchLog
        {
            Id = Guid.NewGuid(),
            PrintJobId = plan.JobId,
            PrinterId = plan.PrinterId,
            Action = DispatchAction.Suggested,
            Score = score.TotalScore,
            ScoreBreakdown = JsonSerializer.Serialize(score.ScoreBreakdown),
            Reason = "Auto-dispatch suggestion (Suggest mode)",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await hub.Clients.Group(AuthorizedHubGroups.Farm).SendAsync(
            "dispatchsuggestion",
            new DispatchSuggestionEvent
            {
                JobId = plan.JobId,
                JobName = plan.JobName,
                PrinterId = plan.PrinterId,
                PrinterName = plan.PrinterName,
                Score = score.TotalScore,
            },
            ct);
    }

    private async Task ExecuteAutoDispatchAsync(
        DispatchPlan plan,
        int maxConcurrentDispatches,
        CancellationToken ct)
    {
        DispatchScore score = plan.Score!;
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IServiceProvider sp = scope.ServiceProvider;
            AppDbContext db = sp.GetRequiredService<AppDbContext>();
            IJobDispatchService dispatchService = sp.GetRequiredService<IJobDispatchService>();
            logger.LogDebug(
                "[AutoDispatch] Printer {PrinterId} waiting for dispatch capacity ({MaxConcurrentDispatches})",
                plan.PrinterId,
                maxConcurrentDispatches);
            Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto dispatched;
            using (DispatchCapacityLease capacityLease =
                await concurrencyCoordinator.AcquireCapacityAsync(
                    maxConcurrentDispatches,
                    ct))
            {
                dispatched =
                await dispatchService.DispatchJobAsync(
                    plan.JobId,
                    plan.PrinterId,
                    QueueActorIdentity.AutoDispatch,
                    score,
                    ct);
            }

            if (dispatched.DispatchResult?.Outcome != DispatchAttemptOutcome.Accepted)
            {
                string outcome = dispatched.DispatchResult?.Outcome.ToString() ?? "Unavailable";
                throw new InvalidOperationException(
                    $"Auto-dispatch backend outcome was {outcome}; success was not confirmed.");
            }

            PrintJob? jobToUpdate = await db.PrintJobs.FindAsync([plan.JobId], ct);
            if (jobToUpdate is not null)
            {
                jobToUpdate.DispatchMode = (int)DispatchMode.Auto;
            }

            Printer? printerToUpdate = await db.Printers
                .Include(value => value.DispatchState)
                .FirstOrDefaultAsync(value => value.Id == plan.PrinterId, ct);
            if (printerToUpdate is not null)
            {
                PrinterDispatchState dispatchState = printerToUpdate.DispatchState
                    ?? new PrinterDispatchState { PrinterId = printerToUpdate.Id };
                dispatchState.AutoDispatchState = AutoDispatchState.None;
                dispatchState.BedPreConfirmed = false;
                if (printerToUpdate.DispatchState is null)
                {
                    printerToUpdate.DispatchState = dispatchState;
                }
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "[AutoDispatch] Dispatched job {JobId} ({JobName}) → printer {PrinterName} (score: {Score:F1})",
                plan.JobId,
                plan.JobName,
                plan.PrinterName,
                score.TotalScore);
            await hub.Clients.Group(AuthorizedHubGroups.Farm).SendAsync(
                "jobautodispatched",
                new JobAutoDispatchedEvent
                {
                    JobId = plan.JobId,
                    JobName = plan.JobName,
                    PrinterId = plan.PrinterId,
                    PrinterName = plan.PrinterName,
                    Score = score.TotalScore,
                    Mode = AutoDispatchMode.Auto,
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[AutoDispatch] Failed to dispatch job {JobId} to printer {PrinterId}",
                plan.JobId,
                plan.PrinterId);
            await RecordDispatchFailureAsync(plan, score, ex, ct);
        }
    }

    private async Task RecordDispatchFailureAsync(
        DispatchPlan plan,
        DispatchScore score,
        Exception exception,
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DispatchLogs.Add(new DispatchLog
        {
            Id = Guid.NewGuid(),
            PrintJobId = plan.JobId,
            PrinterId = plan.PrinterId,
            Action = DispatchAction.Failed,
            Score = score.TotalScore,
            Reason = $"Auto-dispatch failed: {exception.Message}",
            CreatedAtUtc = DateTime.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception saveException)
        {
            logger.LogWarning(
                saveException,
                "[AutoDispatch] Failed to save dispatch failure log");
        }

        await hub.Clients.Group(AuthorizedHubGroups.Farm).SendAsync(
            "dispatchfailed",
            new DispatchFailedEvent
            {
                JobId = plan.JobId,
                PrinterId = plan.PrinterId,
                PrinterName = plan.PrinterName,
                Reason = exception.Message,
            },
            ct);
    }

    private bool IsJobClaimed(Guid jobId)
    {
        lock (_claimSync)
        {
            return _claimedJobs.Contains(jobId);
        }
    }

    private bool TryClaimJob(Guid jobId)
    {
        lock (_claimSync)
        {
            return _claimedJobs.Add(jobId);
        }
    }

    private void ReleaseJobClaim(Guid jobId)
    {
        lock (_claimSync)
        {
            _ = _claimedJobs.Remove(jobId);
        }
    }

    private enum DispatchPlanKind
    {
        NoWork,
        NoCompatibleJob,
        Suggest,
        Auto,
    }

    private sealed record DispatchPlan(
        DispatchPlanKind Kind,
        Guid PrinterId,
        string PrinterName,
        Guid JobId,
        string JobName,
        DispatchScore? Score,
        Guid? ClaimedJobId)
    {
        public static DispatchPlan NoWork { get; } = new(
            DispatchPlanKind.NoWork,
            Guid.Empty,
            string.Empty,
            Guid.Empty,
            string.Empty,
            Score: null,
            ClaimedJobId: null);
    }
}

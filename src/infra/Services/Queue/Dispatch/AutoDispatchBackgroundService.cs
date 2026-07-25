using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
/// (no polling). Thread-safe: a SemaphoreSlim serializes dispatch decisions
/// so two printers going idle simultaneously cannot grab the same job.
/// </summary>
public sealed class AutoDispatchBackgroundService(
    AutoDispatchTrigger trigger,
    IServiceScopeFactory scopeFactory,
    IHubContext<PrinterHub> hub,
    ILogger<AutoDispatchBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);
    private int _inFlightCount;

    /// <inheritdoc />
    public override void Dispose()
    {
        _dispatchLock.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[AutoDispatch] Background service started");

        await ReconcileStartupEligiblePrintersAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            DispatchTriggerEvent triggerEvent;
            try
            {
                triggerEvent = await trigger.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Fire-and-forget the dispatch cycle for this printer. Each cycle
            // runs on its own Task so multiple idle printers are handled concurrently
            // (the dispatch lock serializes only the critical DB-read+assign window).
            _ = Task.Run(() => HandlePrinterIdleAsync(triggerEvent.PrinterId, triggerEvent.SkipIdleThreshold, stoppingToken), stoppingToken);
        }

        logger.LogInformation("[AutoDispatch] Background service stopping");
    }

    private async Task ReconcileStartupEligiblePrintersAsync(CancellationToken ct)
    {
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
                    && (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing))
                && db.PrintJobs.Any(j =>
                    j.Status == PrintJobStatus.Queued
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

    private async Task HandlePrinterIdleAsync(Guid printerId, bool skipIdleThreshold, CancellationToken serviceCt)
    {
        try
        {
            // Read settings first (scoped)
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
                    printerId, settings.AutoDispatchEnabled, settings.AutoDispatchMode);
                return;
            }

            // Skip idle threshold for job-queued triggers (upload-and-print should dispatch immediately)
            if (!skipIdleThreshold)
            {
                // Wait the idle threshold with per-printer cancellation support
                using CancellationTokenSource linkedCts = trigger.CreateLinkedCts(printerId, serviceCt);
                try
                {
                    logger.LogDebug(
                        "[AutoDispatch] Printer {PrinterId} idle — waiting {Seconds}s threshold",
                        printerId, settings.IdleThresholdSeconds);

                    await Task.Delay(TimeSpan.FromSeconds(settings.IdleThresholdSeconds), linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation(
                        "[AutoDispatch] Idle wait cancelled for printer {PrinterId} (went offline or new event)",
                        printerId);
                    return;
                }
                finally
                {
                    trigger.ClearPending(printerId);
                }
            }
            else
            {
                logger.LogDebug(
                    "[AutoDispatch] Printer {PrinterId} — skipping idle threshold (job-queued trigger)",
                    printerId);
            }

            // Enforce MaxConcurrentDispatches
            if (Interlocked.CompareExchange(ref _inFlightCount, 0, 0) >= settings.MaxConcurrentDispatches)
            {
                logger.LogWarning(
                    "[AutoDispatch] MaxConcurrentDispatches ({Max}) reached — skipping printer {PrinterId}",
                    settings.MaxConcurrentDispatches, printerId);
                return;
            }

            // Acquire dispatch lock so two printers cannot grab the same job
            await _dispatchLock.WaitAsync(serviceCt);
            try
            {
                Interlocked.Increment(ref _inFlightCount);

                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                await ExecuteDispatchCycleAsync(scope.ServiceProvider, printerId, settings, serviceCt);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightCount);
                _dispatchLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Service shutting down — expected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AutoDispatch] Unhandled error for printer {PrinterId}", printerId);
        }
    }

    private async Task ExecuteDispatchCycleAsync(
        IServiceProvider sp, Guid printerId, DispatchSettings settings, CancellationToken ct)
    {
        AppDbContext db = sp.GetRequiredService<AppDbContext>();
        IDispatchScorer scorer = sp.GetRequiredService<IDispatchScorer>();

        // Verify printer is still online and idle
        Printer? printer = await db.Printers.Include(p => p.DispatchState).AsNoTracking().FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null || !printer.IsEnabled)
        {
            logger.LogDebug("[AutoDispatch] Printer {PrinterId} not found or disabled — aborting", printerId);
            return;
        }

        // Check no job is currently active on this printer
        bool hasActiveJob = await db.PrintJobs.AnyAsync(
            j => j.AssignedPrinterId == printerId
                 && (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing),
            ct);

        if (hasActiveJob)
        {
            logger.LogDebug("[AutoDispatch] Printer {PrinterId} has an active job — aborting", printerId);
            return;
        }

        // Per-printer auto-dispatch gate: skip printers that have auto-dispatch disabled
        if (!printer.AutoDispatchEnabled)
        {
            logger.LogDebug(
                "[AutoDispatch] Printer {PrinterId} has per-printer auto-dispatch disabled — skipping",
                printerId);
            return;
        }

        // Ready-gate: only dispatch when the operator has confirmed the bed is clear (Ready state)
        // OR when they've pre-confirmed the bed is clear (BedPreConfirmed = true).
        if ((printer.DispatchState?.AutoDispatchState ?? AutoDispatchState.None) != AutoDispatchState.Ready
            && !(printer.DispatchState?.BedPreConfirmed ?? false))
        {
            logger.LogDebug(
                "[AutoDispatch] Printer {PrinterId} state is {State} and bed not pre-confirmed — waiting for operator confirmation",
                printerId, printer.DispatchState?.AutoDispatchState ?? AutoDispatchState.None);
            return;
        }

        // Find candidate jobs: unassigned queued jobs OR jobs assigned to this printer
        List<PrintJob> candidateJobs = await db.PrintJobs
            .AsNoTracking()
            .Where(j => j.Status == PrintJobStatus.Queued
                        && (j.AssignedPrinterId == null || j.AssignedPrinterId == printerId))
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuePosition)
            .ThenBy(j => j.QueuedAt)
            .Take(20) // reasonable batch to score
            .ToListAsync(ct);

        if (candidateJobs.Count == 0)
        {
            logger.LogDebug("[AutoDispatch] No queued jobs available for printer {PrinterId}", printerId);
            return;
        }

        // Score each candidate job against all printers, then check if our printer qualifies
        DispatchScore? bestMatch = null;
        PrintJob? bestJob = null;

        foreach (PrintJob job in candidateJobs)
        {
            List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id, ct);
            DispatchScore? printerScore = scores.FirstOrDefault(s => s.PrinterId == printerId);

            if (printerScore is null || printerScore.Eliminated)
            {
                continue;
            }

            if (printerScore.TotalScore < settings.MinimumScoreThreshold)
            {
                continue;
            }

            // Take the first qualifying job (they're already in priority order)
            bestMatch = printerScore;
            bestJob = job;
            break;
        }

        if (bestMatch is null || bestJob is null)
        {
            logger.LogInformation(
                "[AutoDispatch] No compatible jobs above threshold ({Threshold}) for printer {PrinterId}",
                settings.MinimumScoreThreshold, printerId);

            await hub.Clients.All.SendAsync("dispatchfailed", new DispatchFailedEvent
            {
                PrinterId = printerId,
                PrinterName = printer.Name ?? printerId.ToString(),
                Reason = "No compatible queued jobs found above minimum score threshold",
            }, ct);

            return;
        }

        // Mode: Suggest → notify but don't dispatch
        if (settings.AutoDispatchMode == AutoDispatchMode.Suggest)
        {
            logger.LogInformation(
                "[AutoDispatch] Suggesting job {JobId} for printer {PrinterName} (score: {Score:F1})",
                bestJob.Id, printer.Name, bestMatch.TotalScore);

            // Log suggestion
            db.DispatchLogs.Add(new DispatchLog
            {
                Id = Guid.NewGuid(),
                PrintJobId = bestJob.Id,
                PrinterId = printerId,
                Action = DispatchAction.Suggested,
                Score = bestMatch.TotalScore,
                ScoreBreakdown = JsonSerializer.Serialize(bestMatch.ScoreBreakdown),
                Reason = "Auto-dispatch suggestion (Suggest mode)",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);

            await hub.Clients.All.SendAsync("dispatchsuggestion", new DispatchSuggestionEvent
            {
                JobId = bestJob.Id,
                JobName = bestJob.Name ?? "Unknown",
                PrinterId = printerId,
                PrinterName = printer.Name ?? printerId.ToString(),
                Score = bestMatch.TotalScore,
            }, ct);

            return;
        }

        // Mode: Auto → dispatch the job
        try
        {
            IJobDispatchService dispatchService = sp.GetRequiredService<IJobDispatchService>();
            await dispatchService.DispatchJobAsync(bestJob.Id, printerId, "system:auto-dispatch", bestMatch, ct);

            // Batch the post-dispatch updates into a single save
            PrintJob? jobToUpdate = await db.PrintJobs.FindAsync([bestJob.Id], ct);
            if (jobToUpdate is not null)
            {
                jobToUpdate.DispatchMode = (int)DispatchMode.Auto;
            }

            if (printer.AutoDispatchEnabled)
            {
                Printer? printerToUpdate = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
                if (printerToUpdate is not null)
                {
                    PrinterDispatchState ds = printerToUpdate.DispatchState
                        ?? new PrinterDispatchState { PrinterId = printerToUpdate.Id };
                    ds.AutoDispatchState = AutoDispatchState.None;
                    ds.BedPreConfirmed = false; // Reset pre-clear flag after dispatch
                    if (printerToUpdate.DispatchState is null)
                    {
                        printerToUpdate.DispatchState = ds;
                    }
                }
            }

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "[AutoDispatch] Dispatched job {JobId} ({JobName}) → printer {PrinterName} (score: {Score:F1})",
                bestJob.Id, bestJob.Name, printer.Name, bestMatch.TotalScore);

            await hub.Clients.All.SendAsync("jobautodispatched", new JobAutoDispatchedEvent
            {
                JobId = bestJob.Id,
                JobName = bestJob.Name ?? "Unknown",
                PrinterId = printerId,
                PrinterName = printer.Name ?? printerId.ToString(),
                Score = bestMatch.TotalScore,
                Mode = AutoDispatchMode.Auto,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[AutoDispatch] Failed to dispatch job {JobId} to printer {PrinterId}",
                bestJob.Id, printerId);

            // Log failure
            db.DispatchLogs.Add(new DispatchLog
            {
                Id = Guid.NewGuid(),
                PrintJobId = bestJob.Id,
                PrinterId = printerId,
                Action = DispatchAction.Failed,
                Score = bestMatch.TotalScore,
                Reason = $"Auto-dispatch failed: {ex.Message}",
                CreatedAtUtc = DateTime.UtcNow,
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception saveEx)
            {
                logger.LogWarning(saveEx, "[AutoDispatch] Failed to save dispatch failure log");
            }

            await hub.Clients.All.SendAsync("dispatchfailed", new DispatchFailedEvent
            {
                JobId = bestJob.Id,
                PrinterId = printerId,
                PrinterName = printer.Name ?? printerId.ToString(),
                Reason = ex.Message,
            }, ct);
        }
    }
}

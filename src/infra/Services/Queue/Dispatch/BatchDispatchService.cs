using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Implements batch dispatch with configurable load-balancing strategies.
/// Thread-safe via SemaphoreSlim to prevent double-assignment during concurrent operations.
/// </summary>
public class BatchDispatchService(
    IDispatchScorer scorer,
    IJobDispatchService dispatchService,
    AppDbContext db,
    IHubContext<PrinterHub> hub,
    ILogger<BatchDispatchService> logger) : IBatchDispatchService
{
    private static readonly SemaphoreSlim BatchLock = new(1, 1);

    public async Task<BatchDispatchResult> BatchDispatchAsync(
        BatchDispatchRequest request, string userId, CancellationToken ct = default)
    {
        // Load settings for strategy and concurrency limits
        DispatchSettings settings = await db.DispatchSettings.AsNoTracking().FirstAsync(ct);
        LoadBalancingStrategy strategy = request.Strategy ?? settings.LoadBalancingStrategy;

        // Determine which jobs to dispatch
        List<PrintJob> jobs;
        if (request.DispatchAll)
        {
            jobs = await db.PrintJobs
                .Where(j => j.Status == PrintJobStatus.Queued && j.AssignedPrinterId == null)
                .OrderBy(j => j.Priority)
                .ThenBy(j => j.QueuePosition)
                .ThenBy(j => j.QueuedAt)
                .ToListAsync(ct);
        }
        else if (request.JobIds is { Count: > 0 })
        {
            jobs = await db.PrintJobs
                .Where(j => request.JobIds.Contains(j.Id)
                    && j.Status == PrintJobStatus.Queued
                    && j.AssignedPrinterId == null)
                .OrderBy(j => j.Priority)
                .ThenBy(j => j.QueuePosition)
                .ThenBy(j => j.QueuedAt)
                .ToListAsync(ct);
        }
        else
        {
            return new BatchDispatchResult();
        }

        if (jobs.Count == 0)
        {
            return new BatchDispatchResult();
        }

        Guid batchId = Guid.NewGuid();

        // Broadcast batch started
        await hub.Clients.All.SendAsync("batchdispatchstarted", new BatchDispatchStartedEvent
        {
            BatchId = batchId,
            JobCount = jobs.Count,
            Strategy = strategy,
        }, ct);

        BatchDispatchResult result = new() { TotalCount = jobs.Count };

        await BatchLock.WaitAsync(ct);
        try
        {
            result = strategy switch
            {
                LoadBalancingStrategy.RoundRobin => await DispatchRoundRobinAsync(jobs, userId, settings, ct),
                LoadBalancingStrategy.LeastBusy => await DispatchLeastBusyAsync(jobs, userId, settings, ct),
                _ => await DispatchBestFitAsync(jobs, userId, settings, ct),
            };
            result.TotalCount = jobs.Count;
        }
        finally
        {
            BatchLock.Release();
        }

        // Broadcast batch completed
        await hub.Clients.All.SendAsync("batchdispatchcompleted", new BatchDispatchCompletedEvent
        {
            BatchId = batchId,
            DispatchedCount = result.DispatchedCount,
            FailedCount = result.FailedCount,
            SkippedCount = result.SkippedCount,
        }, ct);

        logger.LogInformation(
            "[BatchDispatch] Completed: {Dispatched} dispatched, {Failed} failed, {Skipped} skipped out of {Total} jobs (strategy: {Strategy})",
            result.DispatchedCount, result.FailedCount, result.SkippedCount, result.TotalCount, strategy);

        return result;
    }

    /// <summary>
    /// BestFit: for each job, score all printers and assign to highest-scoring eligible printer.
    /// </summary>
    private async Task<BatchDispatchResult> DispatchBestFitAsync(
        List<PrintJob> jobs, string userId, DispatchSettings settings, CancellationToken ct)
    {
        BatchDispatchResult result = new();

        foreach (PrintJob job in jobs)
        {
            if (result.DispatchedCount >= settings.MaxConcurrentDispatches)
            {
                result.Results.Add(new BatchDispatchItemResult
                {
                    JobId = job.Id,
                    JobName = job.Name ?? "Unknown",
                    Status = "Skipped",
                    Reason = $"Max concurrent dispatches ({settings.MaxConcurrentDispatches}) reached",
                });
                result.SkippedCount++;
                continue;
            }

            BatchDispatchItemResult itemResult = await TryDispatchJobAsync(job, null, userId, settings, ct);
            result.Results.Add(itemResult);

            switch (itemResult.Status)
            {
                case "Dispatched":
                    result.DispatchedCount++;
                    break;
                case "Failed":
                    result.FailedCount++;
                    break;
                default:
                    result.SkippedCount++;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// RoundRobin: cycle through eligible printers, distributing one job per printer per cycle.
    /// </summary>
    private async Task<BatchDispatchResult> DispatchRoundRobinAsync(
        List<PrintJob> jobs, string userId, DispatchSettings settings, CancellationToken ct)
    {
        BatchDispatchResult result = new();

        // Build a pool of eligible printers (enabled, available, not in maintenance)
        List<Printer> allPrinters = await db.Printers
            .AsNoTracking()
            .Where(p => p.IsEnabled && p.IsAvailable && !p.InMaintenance)
            .ToListAsync(ct);

        if (allPrinters.Count == 0)
        {
            foreach (PrintJob job in jobs)
            {
                result.Results.Add(new BatchDispatchItemResult
                {
                    JobId = job.Id,
                    JobName = job.Name ?? "Unknown",
                    Status = "Skipped",
                    Reason = "No available printers",
                });
                result.SkippedCount++;
            }

            return result;
        }

        int printerIndex = 0;
        foreach (PrintJob job in jobs)
        {
            if (result.DispatchedCount >= settings.MaxConcurrentDispatches)
            {
                result.Results.Add(new BatchDispatchItemResult
                {
                    JobId = job.Id,
                    JobName = job.Name ?? "Unknown",
                    Status = "Skipped",
                    Reason = $"Max concurrent dispatches ({settings.MaxConcurrentDispatches}) reached",
                });
                result.SkippedCount++;
                continue;
            }

            // Score all printers for this job, then pick from eligible ones in round-robin order
            List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id, ct);
            List<DispatchScore> eligible = scores
                .Where(s => !s.Eliminated && s.TotalScore >= settings.MinimumScoreThreshold)
                .ToList();

            if (eligible.Count == 0)
            {
                result.Results.Add(new BatchDispatchItemResult
                {
                    JobId = job.Id,
                    JobName = job.Name ?? "Unknown",
                    Status = "Skipped",
                    Reason = "No eligible printers above minimum score threshold",
                });
                result.SkippedCount++;
                continue;
            }

            // Round-robin: pick the next eligible printer in rotation
            int idx = printerIndex % eligible.Count;
            DispatchScore chosen = eligible[idx];
            printerIndex++;

            BatchDispatchItemResult itemResult = await TryDispatchJobAsync(job, chosen.PrinterId, userId, settings, ct);
            result.Results.Add(itemResult);

            switch (itemResult.Status)
            {
                case "Dispatched":
                    result.DispatchedCount++;
                    break;
                case "Failed":
                    result.FailedCount++;
                    break;
                default:
                    result.SkippedCount++;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// LeastBusy: prefer printers with the shortest queue depth.
    /// </summary>
    private async Task<BatchDispatchResult> DispatchLeastBusyAsync(
        List<PrintJob> jobs, string userId, DispatchSettings settings, CancellationToken ct)
    {
        BatchDispatchResult result = new();

        // Track in-flight assignments during this batch to adjust queue depths
        Dictionary<Guid, int> batchAssignments = [];

        // Query queue depths once before the loop to avoid N+1 DB queries
        Dictionary<Guid, int> queueDepths = await db.PrintJobs
            .Where(j => j.AssignedPrinterId != null
                && j.Status != PrintJobStatus.Completed
                && j.Status != PrintJobStatus.Failed
                && j.Status != PrintJobStatus.Cancelled)
            .GroupBy(j => j.AssignedPrinterId!.Value)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), ct);

        foreach (PrintJob job in jobs)
        {
            if (result.DispatchedCount >= settings.MaxConcurrentDispatches)
            {
                result.Results.Add(new BatchDispatchItemResult
                {
                    JobId = job.Id,
                    JobName = job.Name ?? "Unknown",
                    Status = "Skipped",
                    Reason = $"Max concurrent dispatches ({settings.MaxConcurrentDispatches}) reached",
                });
                result.SkippedCount++;
                continue;
            }

            List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id, ct);
            List<DispatchScore> eligible = scores
                .Where(s => !s.Eliminated && s.TotalScore >= settings.MinimumScoreThreshold)
                .ToList();

            if (eligible.Count == 0)
            {
                result.Results.Add(new BatchDispatchItemResult
                {
                    JobId = job.Id,
                    JobName = job.Name ?? "Unknown",
                    Status = "Skipped",
                    Reason = "No eligible printers above minimum score threshold",
                });
                result.SkippedCount++;
                continue;
            }

            // Pick the eligible printer with lowest effective queue depth
            DispatchScore? chosen = eligible
                .OrderBy(s =>
                {
                    int dbDepth = queueDepths.GetValueOrDefault(s.PrinterId, 0);
                    int batchDepth = batchAssignments.GetValueOrDefault(s.PrinterId, 0);
                    return dbDepth + batchDepth;
                })
                .ThenByDescending(s => s.TotalScore)
                .FirstOrDefault();

            if (chosen is null)
            {
                result.Results.Add(new BatchDispatchItemResult
                {
                    JobId = job.Id,
                    JobName = job.Name ?? "Unknown",
                    Status = "Skipped",
                    Reason = "No eligible printers found",
                });
                result.SkippedCount++;
                continue;
            }

            BatchDispatchItemResult itemResult = await TryDispatchJobAsync(job, chosen.PrinterId, userId, settings, ct);
            result.Results.Add(itemResult);

            switch (itemResult.Status)
            {
                case "Dispatched":
                    result.DispatchedCount++;
                    batchAssignments[chosen.PrinterId] = batchAssignments.GetValueOrDefault(chosen.PrinterId, 0) + 1;
                    break;
                case "Failed":
                    result.FailedCount++;
                    break;
                default:
                    result.SkippedCount++;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Attempts to dispatch a single job to a specific printer (or best-fit if printerId is null).
    /// </summary>
    private async Task<BatchDispatchItemResult> TryDispatchJobAsync(
        PrintJob job, Guid? targetPrinterId, string userId, DispatchSettings settings, CancellationToken ct)
    {
        try
        {
            Guid printerId;
            double score;

            if (targetPrinterId.HasValue)
            {
                printerId = targetPrinterId.Value;
                List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id, ct);
                DispatchScore? printerScore = scores.FirstOrDefault(s => s.PrinterId == printerId);
                score = printerScore?.TotalScore ?? 0;
            }
            else
            {
                // BestFit: pick the top-scoring non-eliminated printer
                List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id, ct);
                DispatchScore? bestCandidate = scores
                    .Where(s => !s.Eliminated && s.TotalScore >= settings.MinimumScoreThreshold)
                    .OrderByDescending(s => s.TotalScore)
                    .FirstOrDefault();

                if (bestCandidate is null)
                {
                    return new BatchDispatchItemResult
                    {
                        JobId = job.Id,
                        JobName = job.Name ?? "Unknown",
                        Status = "Skipped",
                        Reason = "No eligible printers above minimum score threshold",
                    };
                }

                printerId = bestCandidate.PrinterId;
                score = bestCandidate.TotalScore;
            }

            await dispatchService.DispatchJobAsync(job.Id, printerId, userId, ct);

            Printer? printer = await db.Printers.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == printerId, ct);

            return new BatchDispatchItemResult
            {
                JobId = job.Id,
                JobName = job.Name ?? "Unknown",
                Status = "Dispatched",
                PrinterId = printerId,
                PrinterName = printer?.Name ?? printerId.ToString(),
                Score = score,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[BatchDispatch] Failed to dispatch job {JobId}", job.Id);
            return new BatchDispatchItemResult
            {
                JobId = job.Id,
                JobName = job.Name ?? "Unknown",
                Status = "Failed",
                Reason = ex.Message,
            };
        }
    }

    public async Task<DispatchQueueStatusDto> GetQueueStatusAsync(CancellationToken ct = default)
    {
        int pendingUnassigned = await db.PrintJobs
            .CountAsync(j => j.Status == PrintJobStatus.Queued && j.AssignedPrinterId == null, ct);

        int totalQueued = await db.PrintJobs
            .CountAsync(j => j.Status == PrintJobStatus.Queued, ct);

        // Printer stats
        List<Printer> printers = await db.Printers
            .AsNoTracking()
            .Where(p => p.IsEnabled)
            .ToListAsync(ct);

        // Active jobs per printer
        Dictionary<Guid, int> queueDepths = await db.PrintJobs
            .Where(j => j.AssignedPrinterId != null
                && j.Status != PrintJobStatus.Completed
                && j.Status != PrintJobStatus.Failed
                && j.Status != PrintJobStatus.Cancelled)
            .GroupBy(j => j.AssignedPrinterId!.Value)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), ct);

        // Printing printers
        HashSet<Guid> printingPrinterIds = (await db.PrintJobs
            .Where(j => j.Status == PrintJobStatus.Printing && j.AssignedPrinterId != null)
            .Select(j => j.AssignedPrinterId!.Value)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        List<PrinterQueueDepthDto> printerQueueDepthDtos = printers.Select(p => new PrinterQueueDepthDto
        {
            PrinterId = p.Id,
            PrinterName = p.Name ?? p.Id.ToString(),
            QueueDepth = queueDepths.GetValueOrDefault(p.Id, 0),
            IsPrinting = printingPrinterIds.Contains(p.Id),
            IsAvailable = p.IsAvailable && !p.InMaintenance,
        }).ToList();

        int idle = printers.Count(p => p.IsAvailable && !p.InMaintenance && !printingPrinterIds.Contains(p.Id));
        int busy = printingPrinterIds.Count;

        // Dispatch stats (last 24 hours)
        DateTime cutoff = DateTime.UtcNow.AddHours(-24);

        List<DispatchLog> recentLogs = await db.DispatchLogs
            .AsNoTracking()
            .Where(l => l.CreatedAtUtc >= cutoff)
            .ToListAsync(ct);

        List<DispatchLog> dispatched = recentLogs.Where(l => l.Action == DispatchAction.Dispatched).ToList();

        DispatchStatsDto stats = new()
        {
            DispatchesLast24Hours = dispatched.Count,
            AverageScoreLast24Hours = Math.Round(
                dispatched.Where(l => l.Score.HasValue)
                    .Select(l => l.Score!.Value)
                    .DefaultIfEmpty(0)
                    .Average(), 2),
            AutoDispatchesLast24Hours = recentLogs.Count(l =>
                l.Action == DispatchAction.Dispatched
                && l.Reason is not null
                && l.Reason.Contains("auto", StringComparison.OrdinalIgnoreCase)),
            FailedDispatchesLast24Hours = recentLogs.Count(l => l.Action == DispatchAction.Failed),
        };

        return new DispatchQueueStatusDto
        {
            PendingUnassignedJobs = pendingUnassigned,
            TotalQueuedJobs = totalQueued,
            IdlePrinters = idle,
            BusyPrinters = busy,
            PrinterQueueDepths = printerQueueDepthDtos,
            Stats = stats,
        };
    }

    public async Task<(List<DispatchHistoryDto> Items, int TotalCount)> GetDispatchHistoryAsync(
        int page, int pageSize, DateTime? dateFrom = null, DateTime? dateTo = null, CancellationToken ct = default)
    {
        int clampedPage = Math.Max(1, page);
        int clampedSize = Math.Clamp(pageSize, 1, 100);

        IQueryable<DispatchLog> baseQuery = db.DispatchLogs;

        if (dateFrom.HasValue)
        {
            baseQuery = baseQuery.Where(l => l.CreatedAtUtc >= dateFrom.Value);
        }

        if (dateTo.HasValue)
        {
            baseQuery = baseQuery.Where(l => l.CreatedAtUtc <= dateTo.Value);
        }

        int totalCount = await baseQuery.CountAsync(ct);

        List<DispatchHistoryDto> items = await baseQuery
            .AsNoTracking()
            .Include(l => l.PrintJob)
            .Include(l => l.Printer)
            .OrderByDescending(l => l.CreatedAtUtc)
            .Skip((clampedPage - 1) * clampedSize)
            .Take(clampedSize)
            .Select(l => new DispatchHistoryDto
            {
                Id = l.Id,
                PrintJobId = l.PrintJobId,
                JobName = l.PrintJob != null ? l.PrintJob.Name : null,
                PrinterId = l.PrinterId,
                PrinterName = l.Printer != null ? l.Printer.Name : null,
                Action = l.Action,
                Score = l.Score,
                Reason = l.Reason,
                CreatedAtUtc = l.CreatedAtUtc,
            })
            .ToListAsync(ct);

        return (items, totalCount);
    }
}

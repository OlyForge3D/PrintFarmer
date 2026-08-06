using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Implements batch dispatch with configurable load-balancing strategies.
/// Cross-process safety is provided by the shared database dispatch claim.
/// </summary>
public class BatchDispatchService(
    IDispatchScorer scorer,
    AppDbContext db,
    IServiceScopeFactory scopeFactory,
    DispatchConcurrencyCoordinator concurrencyCoordinator,
    IHubContext<PrinterHub> hub,
    ILogger<BatchDispatchService> logger) : IBatchDispatchService
{
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
                .OrderByPriorityDescending()
                .ToListAsync(ct);
        }
        else if (request.JobIds is { Count: > 0 })
        {
            jobs = await db.PrintJobs
                .Where(j => request.JobIds.Contains(j.Id)
                    && j.Status == PrintJobStatus.Queued
                    && j.AssignedPrinterId == null)
                .OrderByPriorityDescending()
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

        foreach (PrintJob job in jobs)
        {
            request.JobETags.TryGetValue(job.Id, out string? etag);
            QueueRevisionGuard.EnsureIfMatch(
                string.IsNullOrWhiteSpace(etag) ? string.Empty : etag,
                job.RowVersion,
                "batch dispatch");
        }

        Guid batchId = Guid.NewGuid();

        // Broadcast batch started
        await hub.Clients.Group(AuthorizedHubGroups.Administrators).SendAsync("batchdispatchstarted", new BatchDispatchStartedEvent
        {
            BatchId = batchId,
            JobCount = jobs.Count,
            Strategy = strategy,
        }, ct);

        BatchDispatchResult result = new() { TotalCount = jobs.Count };

        result = strategy switch
        {
            LoadBalancingStrategy.RoundRobin => await DispatchRoundRobinAsync(jobs, userId, settings, ct),
            LoadBalancingStrategy.LeastBusy => await DispatchLeastBusyAsync(jobs, userId, settings, ct),
            _ => await DispatchBestFitAsync(jobs, userId, settings, ct),
        };
        result.TotalCount = jobs.Count;

        // Broadcast batch completed
        await hub.Clients.Group(AuthorizedHubGroups.Administrators).SendAsync("batchdispatchcompleted", new BatchDispatchCompletedEvent
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
        return await DispatchJobsAsync(
            jobs,
            userId,
            settings,
            async (job, cancellationToken) =>
            {
                List<DispatchScore> scores =
                    await scorer.ScorePrintersForJobAsync(job.Id, cancellationToken);
                return TryClaimCandidate(scores
                    .Where(candidate =>
                        !candidate.Eliminated
                        && candidate.TotalScore >= settings.MinimumScoreThreshold)
                    .OrderByDescending(candidate => candidate.TotalScore));
            },
            ct);
    }

    /// <summary>
    /// RoundRobin: cycle through eligible printers, distributing one job per printer per cycle.
    /// </summary>
    private async Task<BatchDispatchResult> DispatchRoundRobinAsync(
        List<PrintJob> jobs, string userId, DispatchSettings settings, CancellationToken ct)
    {
        List<Printer> allPrinters = await db.Printers
            .AsNoTracking()
            .Where(p => p.IsEnabled && p.IsAvailable && !p.InMaintenance)
            .ToListAsync(ct);

        if (allPrinters.Count == 0)
        {
            return CreateSkippedResult(jobs, "No available printers");
        }

        int printerIndex = 0;
        return await DispatchJobsAsync(
            jobs,
            userId,
            settings,
            async (job, cancellationToken) =>
            {
                List<DispatchScore> scores =
                    await scorer.ScorePrintersForJobAsync(job.Id, cancellationToken);
                List<DispatchScore> eligible = scores
                    .Where(candidate =>
                        !candidate.Eliminated
                        && candidate.TotalScore >= settings.MinimumScoreThreshold)
                    .ToList();
                if (eligible.Count == 0)
                {
                    return null;
                }

                int startIndex = printerIndex++ % eligible.Count;
                IEnumerable<DispatchScore> rotated = eligible
                    .Skip(startIndex)
                    .Concat(eligible.Take(startIndex));
                return TryClaimCandidate(rotated);
            },
            ct);
    }

    /// <summary>
    /// LeastBusy: prefer printers with the shortest queue depth.
    /// </summary>
    private async Task<BatchDispatchResult> DispatchLeastBusyAsync(
        List<PrintJob> jobs, string userId, DispatchSettings settings, CancellationToken ct)
    {
        Dictionary<Guid, int> queueDepths = await db.PrintJobs
            .Where(j => j.AssignedPrinterId != null
                && j.Status != PrintJobStatus.Completed
                && j.Status != PrintJobStatus.Failed
                && j.Status != PrintJobStatus.Cancelled)
            .GroupBy(j => j.AssignedPrinterId!.Value)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), ct);

        return await DispatchJobsAsync(
            jobs,
            userId,
            settings,
            async (job, cancellationToken) =>
            {
                List<DispatchScore> scores =
                    await scorer.ScorePrintersForJobAsync(job.Id, cancellationToken);
                IEnumerable<DispatchScore> ordered = scores
                    .Where(candidate =>
                        !candidate.Eliminated
                        && candidate.TotalScore >= settings.MinimumScoreThreshold)
                    .OrderBy(candidate =>
                        queueDepths.GetValueOrDefault(candidate.PrinterId, 0))
                    .ThenByDescending(candidate => candidate.TotalScore);
                return TryClaimCandidate(ordered);
            },
            ct);
    }

    /// <summary>
    /// Selects and starts independent dispatches, preserving result order.
    /// </summary>
    private async Task<BatchDispatchResult> DispatchJobsAsync(
        List<PrintJob> jobs,
        string userId,
        DispatchSettings settings,
        Func<PrintJob, CancellationToken, Task<ClaimedCandidate?>> selectCandidate,
        CancellationToken ct)
    {
        var planned = new List<(int Index, PrintJob Job, ClaimedCandidate Candidate)>();
        var resultsByIndex = new Dictionary<int, BatchDispatchItemResult>();
        try
        {
            for (int index = 0; index < jobs.Count; index++)
            {
                PrintJob job = jobs[index];
                ClaimedCandidate? candidate = await selectCandidate(job, ct);
                if (candidate is null)
                {
                    resultsByIndex[index] = CreateSkippedItem(
                        job,
                        "No eligible unclaimed printers above minimum score threshold");
                    continue;
                }

                planned.Add((index, job, candidate));
            }
        }
        catch
        {
            foreach ((_, _, ClaimedCandidate candidate) in planned)
            {
                concurrencyCoordinator.ReleasePrinter(candidate.Score.PrinterId);
            }

            throw;
        }

        List<Task<BatchDispatchItemResult>> pendingTasks = planned
            .Select(item => DispatchClaimedJobAsync(
                item.Job,
                item.Candidate,
                userId,
                settings,
                ct))
            .ToList();
        BatchDispatchItemResult[] completed =
            await Task.WhenAll(pendingTasks);
        for (int completedIndex = 0; completedIndex < completed.Length; completedIndex++)
        {
            resultsByIndex[planned[completedIndex].Index] = completed[completedIndex];
        }

        BatchDispatchResult result = new();
        foreach (BatchDispatchItemResult itemResult in resultsByIndex
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value))
        {
            AddResult(result, itemResult);
        }

        return result;
    }

    /// <summary>
    /// Runs upload/start work under shared global capacity and a held printer claim.
    /// </summary>
    private async Task<BatchDispatchItemResult> DispatchClaimedJobAsync(
        PrintJob job,
        ClaimedCandidate candidate,
        string userId,
        DispatchSettings settings,
        CancellationToken ct)
    {
        try
        {
            using DispatchCapacityLease capacityLease =
                await concurrencyCoordinator.AcquireCapacityAsync(
                    settings.MaxConcurrentDispatches,
                    ct);
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IJobDispatchService dispatchService =
                scope.ServiceProvider.GetRequiredService<IJobDispatchService>();
            AppDbContext dispatchDb =
                scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto dispatched =
                await dispatchService.DispatchJobAsync(
                    job.Id,
                    candidate.Score.PrinterId,
                    userId,
                    candidate.Score,
                    ct);

            DispatchAttemptOutcome? outcome = dispatched.DispatchResult?.Outcome;
            if (outcome != DispatchAttemptOutcome.Accepted)
            {
                return new BatchDispatchItemResult
                {
                    JobId = job.Id,
                    JobName = job.Name ?? "Unknown",
                    PrinterId = candidate.Score.PrinterId,
                    Score = candidate.Score.TotalScore,
                    Status = outcome == DispatchAttemptOutcome.Unknown ? "Unknown" : "Failed",
                    Reason = dispatched.DispatchResult?.ErrorCode ?? "Dispatch was not accepted by the backend.",
                };
            }

            Printer? printer = await dispatchDb.Printers.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == candidate.Score.PrinterId, ct);

            return new BatchDispatchItemResult
            {
                JobId = job.Id,
                JobName = job.Name ?? "Unknown",
                Status = "Dispatched",
                PrinterId = candidate.Score.PrinterId,
                PrinterName = printer?.Name ?? candidate.Score.PrinterId.ToString(),
                Score = candidate.Score.TotalScore,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        finally
        {
            concurrencyCoordinator.ReleasePrinter(candidate.Score.PrinterId);
        }
    }

    private ClaimedCandidate? TryClaimCandidate(
        IEnumerable<DispatchScore> candidates)
    {
        foreach (DispatchScore candidate in candidates)
        {
            if (concurrencyCoordinator.TryClaimPrinter(candidate.PrinterId))
            {
                return new ClaimedCandidate(candidate);
            }
        }

        return null;
    }

    private static BatchDispatchResult CreateSkippedResult(
        IEnumerable<PrintJob> jobs,
        string reason)
    {
        BatchDispatchResult result = new();
        foreach (PrintJob job in jobs)
        {
            AddResult(result, CreateSkippedItem(job, reason));
        }

        return result;
    }

    private static BatchDispatchItemResult CreateSkippedItem(
        PrintJob job,
        string reason) =>
        new()
        {
            JobId = job.Id,
            JobName = job.Name ?? "Unknown",
            Status = "Skipped",
            Reason = reason,
        };

    private static void AddResult(
        BatchDispatchResult result,
        BatchDispatchItemResult itemResult)
    {
        result.Results.Add(itemResult);
        switch (itemResult.Status)
        {
            case "Dispatched":
                result.DispatchedCount++;
                break;
            case "Failed":
            case "Unknown":
                result.FailedCount++;
                break;
            default:
                result.SkippedCount++;
                break;
        }
    }

    private sealed record ClaimedCandidate(DispatchScore Score);

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

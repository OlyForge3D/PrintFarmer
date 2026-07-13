using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.ShiftPlan;

/// <summary>
/// Default <see cref="IIdleWindowService"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// An idle window is a projected gap on a printer's assigned queue timeline.
/// Windows are computed as <c>[now, ETA of first assigned job)</c>, using
/// existing print statistics for run-time estimates. Printers currently in
/// an active print (Assigned/Starting/Printing/Paused) have no immediate
/// idle window until that job finishes.
/// </para>
/// <para>
/// Dispatch alignment: for each candidate window, we also check whether the
/// printer is a viable target for any unassigned queued job at or above the
/// dispatcher's configured minimum score threshold. When it is, the window
/// still reports (so callers can reason about it) but with
/// <see cref="IdleWindow.IsDispatchEligibleNow"/> set — the shift-plan
/// compiler MUST NOT recommend maintenance in that window, because the
/// dispatcher would have started a job instead.
/// </para>
/// </remarks>
public sealed class IdleWindowService : IIdleWindowService
{
    private readonly IQueueDataService _queue;
    private readonly IDispatchScorer _dispatchScorer;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<IdleWindowService> _logger;

    public IdleWindowService(
        IQueueDataService queue,
        IDispatchScorer dispatchScorer,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<IdleWindowService> logger)
    {
        _queue = queue;
        _dispatchScorer = dispatchScorer;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IdleWindow>> GetIdleWindowsAsync(TimeSpan minWindow, CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;

        List<Printer> printers = await _queue.GetAvailablePrintersAsync(ct);
        if (printers.Count == 0)
        {
            return Array.Empty<IdleWindow>();
        }

        // Resolve dispatch threshold + list of eligible-for-dispatch queued jobs once.
        double minScore;
        List<PrintJob> dispatchCandidates;
        await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(ct))
        {
            DispatchSettings dispatchSettings = await db.DispatchSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
                ?? new DispatchSettings();
            minScore = dispatchSettings.MinimumScoreThreshold;

            // Unassigned jobs in a state the dispatcher could pick up. Reuse job.Status
            // Queued semantics — assignment yields Status=Assigned.
            dispatchCandidates = await db.PrintJobs
                .AsNoTracking()
                .Where(j => j.AssignedPrinterId == null && j.Status == PrintJobStatus.Queued)
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.QueuePosition)
                .Take(20)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        List<IdleWindow> results = new(printers.Count);

        foreach (Printer printer in printers)
        {
            ct.ThrowIfCancellationRequested();

            List<PrintJob> assigned = await _queue.GetPrintJobsForPrinterAsync(printer.Id, ct).ConfigureAwait(false);

            DateTime? busyUntil = ProjectBusyUntil(assigned, now);
            DateTime windowStart = busyUntil ?? now;

            // We only surface windows starting at "now" — future windows come from
            // subsequent compiler passes as the queue drains. Skip a printer that
            // is actively working on something (busyUntil > now).
            if (windowStart > now)
            {
                continue;
            }

            // Bound the window at the ETA of the next assigned queued job on this
            // printer that has not yet started (Queued/Assigned). MaxValue = open-ended.
            DateTime windowEnd = ProjectNextBoundary(assigned, now) ?? DateTime.MaxValue;
            if (windowEnd - windowStart < minWindow)
            {
                continue;
            }

            bool dispatchEligibleNow = await IsDispatchEligibleAsync(printer.Id, dispatchCandidates, minScore, ct)
                .ConfigureAwait(false);

            results.Add(new IdleWindow(printer.Id, printer.Name, windowStart, windowEnd, dispatchEligibleNow));
        }

        return results;
    }

    private static DateTime? ProjectBusyUntil(IEnumerable<PrintJob> assigned, DateTime now)
    {
        // If any active job is running, we're busy until its ETA.
        PrintJob? active = assigned.FirstOrDefault(j =>
            j.Status == PrintJobStatus.Starting
            || j.Status == PrintJobStatus.Printing
            || j.Status == PrintJobStatus.Paused);
        if (active is null)
        {
            return null;
        }

        DateTime start = active.ActualStartTime ?? now;
        TimeSpan estimate = active.EstimatedPrintTime ?? TimeSpan.Zero;
        return start + estimate;
    }

    private static DateTime? ProjectNextBoundary(IEnumerable<PrintJob> assigned, DateTime now)
    {
        // First queued/assigned-but-not-started job on this printer sets the window end.
        PrintJob? next = assigned
            .Where(j => j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned)
            .OrderBy(j => j.QueuePosition)
            .FirstOrDefault();
        if (next is null)
        {
            return null;
        }

        // Best-effort ETA: use estimate if available, else no bound.
        // We conservatively return "now" so the window is 0-length only if the job
        // is imminent. Callers filter by minWindow.
        return next.EstimatedPrintTime is null ? (DateTime?)null : now;
    }

    private async Task<bool> IsDispatchEligibleAsync(
        Guid printerId,
        List<PrintJob> candidateJobs,
        double minScore,
        CancellationToken ct)
    {
        // If there are no unassigned queued jobs, the printer is truly idle.
        if (candidateJobs.Count == 0)
        {
            return false;
        }

        // Score at most a bounded number of jobs to avoid unbounded scoring work
        // when queues are deep. This mirrors AutoDispatchBackgroundService's
        // "take first qualifying" strategy.
        foreach (PrintJob job in candidateJobs)
        {
            ct.ThrowIfCancellationRequested();

            List<DispatchScore> scores;
            try
            {
                scores = await _dispatchScorer.ScorePrintersForJobAsync(job.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Idle window: dispatch scoring failed for job {JobId}", job.Id);
                continue;
            }

            DispatchScore? printerScore = scores.FirstOrDefault(s => s.PrinterId == printerId);
            if (printerScore is null || printerScore.Eliminated)
            {
                continue;
            }

            if (printerScore.TotalScore >= minScore)
            {
                return true;
            }
        }

        return false;
    }
}

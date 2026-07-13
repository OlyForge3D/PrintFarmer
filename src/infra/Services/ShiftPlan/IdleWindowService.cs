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
/// A printer is only considered idle (and therefore eligible for a window) when
/// it has NO active job (Starting, Printing, or Paused). Overdue or zero-ETA
/// active jobs do NOT produce a window — the printer is busy until the active
/// job resolves.
/// </para>
/// <para>
/// Window end-time: the start of the next queued/assigned-but-not-yet-started
/// job on the printer. If that job has no ETA the window ends immediately
/// (returns <c>now</c>) so callers never see an artificially wide window while
/// a queued job is waiting.
/// </para>
/// <para>
/// Dispatch alignment: for each candidate window we check whether the printer
/// is a viable target for any unassigned queued job, using the same gates as
/// <c>AutoDispatchBackgroundService</c>:
/// <list type="bullet">
///   <item>Global auto-dispatch enabled and mode ≠ Manual.</item>
///   <item>Per-printer auto-dispatch enabled.</item>
///   <item>Printer in Ready state or BedPreConfirmed.</item>
///   <item>No active job on the printer.</item>
///   <item>Candidate job selection and ordering identical to the dispatcher.</item>
/// </list>
/// When eligible the window is still reported (so callers can reason about it)
/// but with <see cref="IdleWindow.IsDispatchEligibleNow"/> set — the shift-plan
/// compiler MUST NOT schedule maintenance in that window.
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

        // Load dispatch settings, global candidates, and per-printer dispatch states
        // in a single DB round-trip.
        bool globalDispatchEnabled;
        double minScore;
        List<PrintJob> globalCandidates;
        Dictionary<Guid, PrinterDispatchState?> dispatchStates;

        await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(ct))
        {
            DispatchSettings dispatchSettings = await db.DispatchSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
                ?? new DispatchSettings();

            globalDispatchEnabled = dispatchSettings.AutoDispatchEnabled
                && dispatchSettings.AutoDispatchMode != AutoDispatchMode.Manual;
            minScore = dispatchSettings.MinimumScoreThreshold;

            // Candidate query mirrors AutoDispatchBackgroundService.ExecuteDispatchCycleAsync exactly:
            // unassigned queued jobs, same ordering (Priority asc, QueuePosition asc, QueuedAt asc).
            globalCandidates = await db.PrintJobs
                .AsNoTracking()
                .Where(j => j.AssignedPrinterId == null && j.Status == PrintJobStatus.Queued)
                .OrderBy(j => j.Priority)
                .ThenBy(j => j.QueuePosition)
                .ThenBy(j => j.QueuedAt)
                .Take(20)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // Load per-printer dispatch states for ready-gate checks.
            HashSet<Guid> printerIdSet = [.. printers.Select(p => p.Id)];
            dispatchStates = await db.Printers
                .AsNoTracking()
                .Include(p => p.DispatchState)
                .Where(p => printerIdSet.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.DispatchState, ct)
                .ConfigureAwait(false);
        }

        List<IdleWindow> results = new(printers.Count);

        foreach (Printer printer in printers)
        {
            ct.ThrowIfCancellationRequested();

            List<PrintJob> assigned = await _queue.GetPrintJobsForPrinterAsync(printer.Id, ct).ConfigureAwait(false);

            // Fix 1: If the printer has ANY active job (Starting/Printing/Paused),
            // it is not idle right now — emit no window.
            bool hasActiveJob = assigned.Any(j =>
                j.Status == PrintJobStatus.Starting
                || j.Status == PrintJobStatus.Printing
                || j.Status == PrintJobStatus.Paused);

            if (hasActiveJob)
            {
                continue;
            }

            // Window starts at now (printer is idle) and ends at the next queued job.
            DateTime windowStart = now;
            DateTime windowEnd = ProjectNextBoundary(assigned, now) ?? DateTime.MaxValue;
            if (windowEnd - windowStart < minWindow)
            {
                continue;
            }

            dispatchStates.TryGetValue(printer.Id, out PrinterDispatchState? printerDispatchState);

            bool dispatchEligibleNow = await IsDispatchEligibleAsync(
                    printer, printerDispatchState, globalDispatchEnabled,
                    globalCandidates, assigned, minScore, ct)
                .ConfigureAwait(false);

            results.Add(new IdleWindow(printer.Id, printer.Name, windowStart, windowEnd, dispatchEligibleNow));
        }

        return results;
    }

    private static DateTime? ProjectNextBoundary(IEnumerable<PrintJob> assigned, DateTime now)
    {
        // Fix 1: if any queued/assigned job exists on this printer, the window ends
        // immediately (now) regardless of whether that job has an ETA. A queued job
        // waiting to start means the printer's idle window is 0-length or less.
        PrintJob? next = assigned
            .Where(j => j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned)
            .OrderBy(j => j.QueuePosition)
            .FirstOrDefault();

        if (next is null)
        {
            return null; // open-ended idle window
        }

        // Return now so the window length is 0 for MinWindow filtering.
        return now;
    }

    private async Task<bool> IsDispatchEligibleAsync(
        Printer printer,
        PrinterDispatchState? dispatchState,
        bool globalDispatchEnabled,
        List<PrintJob> globalCandidates,
        List<PrintJob> assignedJobs,
        double minScore,
        CancellationToken ct)
    {
        // Fix 2: mirror all dispatcher gates exactly.

        // Gate 1: global auto-dispatch enabled + mode != Manual
        if (!globalDispatchEnabled)
        {
            return false;
        }

        // Gate 2: per-printer auto-dispatch flag
        if (!printer.AutoDispatchEnabled)
        {
            return false;
        }

        // Gate 3: ready-gate (operator confirmed bed is clear or pre-confirmed)
        bool isReady = (dispatchState?.AutoDispatchState ?? AutoDispatchState.None) == AutoDispatchState.Ready
            || (dispatchState?.BedPreConfirmed ?? false);
        if (!isReady)
        {
            return false;
        }

        // Gate 4: no active job (already guaranteed by the caller — if hasActiveJob we
        // returned early, so this is always true here, but state explicitly for clarity).

        // Candidates: global unassigned + jobs already assigned to this printer that
        // are still queued — mirrors the dispatcher's candidate selection exactly.
        List<PrintJob> candidates = globalCandidates
            .Concat(assignedJobs.Where(j => j.Status == PrintJobStatus.Queued))
            .DistinctBy(j => j.Id)
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuePosition)
            .ThenBy(j => j.QueuedAt)
            .Take(20)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        foreach (PrintJob job in candidates)
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

            DispatchScore? printerScore = scores.FirstOrDefault(s => s.PrinterId == printer.Id);
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

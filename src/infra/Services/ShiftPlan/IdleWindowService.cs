using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Mutations;
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
    private readonly IMutationWatermarkReader? _watermarkReader;

    public IdleWindowService(
        IQueueDataService queue,
        IDispatchScorer dispatchScorer,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<IdleWindowService> logger,
        IMutationWatermarkReader? watermarkReader = null)
    {
        _queue = queue;
        _dispatchScorer = dispatchScorer;
        _dbFactory = dbFactory;
        _logger = logger;
        _watermarkReader = watermarkReader;
    }

    public async Task<IReadOnlyList<IdleWindow>> GetIdleWindowsAsync(TimeSpan minWindow, CancellationToken ct = default)
    {
        IdleWindowResult result = await GetIdleWindowsWithIndeterminateAsync(minWindow, ct).ConfigureAwait(false);
        return result.Windows;
    }

    public async Task<IdleWindowResult> GetIdleWindowsWithIndeterminateAsync(TimeSpan minWindow, CancellationToken ct = default)
    {
        long? rootOrigin = await OriginWatermark
            .CaptureAsync(_watermarkReader, _logger, "idle-window inputs", ct)
            .ConfigureAwait(false);
        List<long?> requiredOrigins = [rootOrigin];
        DateTime now = DateTime.UtcNow;

        List<Printer> printers = await _queue.GetAvailablePrintersAsync(ct);
        if (printers.Count == 0)
        {
            return new IdleWindowResult(Array.Empty<IdleWindow>(), new HashSet<Guid>(), rootOrigin);
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
        Dictionary<Guid, IReadOnlyDictionary<Guid, DispatchScore>?> scorerCache = new();

        // Fix R4-1: printers whose dispatch eligibility could not be determined this
        // pass (every evaluated candidate's scoring threw). They are excluded from
        // 'results' just like before, but we now surface them so a fail-closed caller
        // (the maintenance source) can tell an outage apart from a genuinely absent
        // window instead of silently seeing the printer drop out of the set.
        HashSet<Guid> indeterminate = new();

        foreach (Printer printer in printers)
        {
            ct.ThrowIfCancellationRequested();

            List<PrintJob> assigned = await _queue.GetPrintJobsForPrinterAsync(printer.Id, ct).ConfigureAwait(false);

            // Fix 1: If the printer has ANY active job (Starting/Printing/Paused),
            // it is not idle right now — emit no window.
            bool hasActiveJob = assigned.Any(j => j.Status.OccupiesPrinter());

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

            // Fix R3-1: a null result means scoring failed for every candidate we
            // evaluated, so dispatch eligibility is genuinely unknown for this printer
            // this pass — exclude it from the idle-window set entirely rather than
            // default to "not eligible" (which would let a maintenance source schedule
            // work into a window that may in fact be dispatch-eligible).
            bool? dispatchEligibleNow = await IsDispatchEligibleAsync(
                    printer, printerDispatchState, globalDispatchEnabled,
                    globalCandidates, assigned, minScore, scorerCache, requiredOrigins, ct)
                .ConfigureAwait(false);

            if (dispatchEligibleNow is null)
            {
                _logger.LogWarning(
                    "Idle window: dispatch eligibility unknown for printer {PrinterId} ({PrinterName}) — scoring failed for every evaluated candidate; excluding from idle-window set this pass",
                    printer.Id, printer.Name);
                _ = indeterminate.Add(printer.Id);
                continue;
            }

            results.Add(new IdleWindow(printer.Id, printer.Name, windowStart, windowEnd, dispatchEligibleNow.Value));
        }

        return new IdleWindowResult(
            results,
            indeterminate,
            OriginWatermark.Combine([.. requiredOrigins]));
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

    /// <returns>
    /// <c>true</c>/<c>false</c> when eligibility could be conclusively determined;
    /// <c>null</c> when scoring failed for every candidate evaluated, meaning dispatch
    /// state is unknown (Fix R3-1). Callers must treat <c>null</c> as "exclude this
    /// printer from the idle-window set", never as "not eligible" — a scorer outage
    /// must not fail open into scheduling maintenance during a window that may in
    /// fact be dispatch-eligible.
    /// </returns>
    private async Task<bool?> IsDispatchEligibleAsync(
        Printer printer,
        PrinterDispatchState? dispatchState,
        bool globalDispatchEnabled,
        List<PrintJob> globalCandidates,
        List<PrintJob> assignedJobs,
        double minScore,
        Dictionary<Guid, IReadOnlyDictionary<Guid, DispatchScore>?> scorerCache,
        List<long?> requiredOrigins,
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

        // Fix R3-1: a scorer exception used to be swallowed unconditionally, so if
        // every candidate threw, the loop fell through to "return false" — reporting
        // conclusive non-eligibility when in truth nothing was ever successfully
        // scored. Track whether any candidate failed so we can report "unknown"
        // instead of a false negative.
        bool anyScorerFailed = false;

        foreach (PrintJob job in candidates)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyDictionary<Guid, DispatchScore>? scoresByPrinter =
                await GetScoresByPrinterAsync(job.Id, scorerCache, requiredOrigins, ct).ConfigureAwait(false);
            if (scoresByPrinter is null)
            {
                anyScorerFailed = true;
                continue;
            }

            if (!scoresByPrinter.TryGetValue(printer.Id, out DispatchScore? printerScore) || printerScore.Eliminated)
            {
                continue;
            }

            if (printerScore.TotalScore >= minScore)
            {
                // A conclusive positive match short-circuits regardless of any
                // earlier scorer failure — we do not need certainty about every
                // remaining candidate once one has confirmed eligibility.
                return true;
            }
        }

        return anyScorerFailed ? null : false;
    }

    private async Task<IReadOnlyDictionary<Guid, DispatchScore>?> GetScoresByPrinterAsync(
        Guid jobId,
        Dictionary<Guid, IReadOnlyDictionary<Guid, DispatchScore>?> scorerCache,
        List<long?> requiredOrigins,
        CancellationToken ct)
    {
        if (scorerCache.TryGetValue(jobId, out IReadOnlyDictionary<Guid, DispatchScore>? cached))
        {
            return cached;
        }

        try
        {
            DispatchScoreResult result;
            if (_dispatchScorer is IDispatchScorerWithOrigin scorerWithOrigin)
            {
                result = await scorerWithOrigin
                    .ScorePrintersForJobWithOriginAsync(jobId, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                List<DispatchScore> scores = await _dispatchScorer
                    .ScorePrintersForJobAsync(jobId, ct)
                    .ConfigureAwait(false);
                result = new DispatchScoreResult(scores, OriginWatermark: null);
            }

            requiredOrigins.Add(result.OriginWatermark);
            IReadOnlyDictionary<Guid, DispatchScore> byPrinter = result.Scores
                .GroupBy(s => s.PrinterId)
                .ToDictionary(g => g.Key, g => g.First());
            scorerCache[jobId] = byPrinter;
            return byPrinter;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Idle window: dispatch scoring failed for job {JobId}", jobId);
            scorerCache[jobId] = null;
            return null;
        }
    }
}

namespace Farm.Moonraker.Emulator.Domain;

/// <summary>One completed (or errored/cancelled) print job entry in Moonraker's job history.</summary>
public sealed class HistoryJobEntry
{
    public required string JobId { get; init; }

    public required string Filename { get; init; }

    public double StartTime { get; init; }

    public double? EndTime { get; set; }

    public double PrintDuration { get; set; }

    public double TotalDuration { get; set; }

    public double FilamentUsed { get; set; }

    /// <summary>completed | cancelled | error | in_progress | klippy_shutdown.</summary>
    public string Status { get; set; } = "completed";

    public bool Exists { get; init; } = true;

    public string? ThumbnailUrl { get; set; }
}

/// <summary>
/// Per-printer in-memory job history store backing <c>server/history/*</c>, including
/// aggregate totals independent of the per-job list (Moonraker tracks totals across all
/// jobs ever recorded, even ones since pruned from the list).
/// </summary>
public sealed class HistoryStore
{
    private readonly List<HistoryJobEntry> _jobs = [];
    private readonly object _gate = new();

    /// <summary>
    /// Backing counter for <see cref="NextJobId"/>. Monotonic and process-lifetime-scoped
    /// (never reset by a scenario/printer reset — resetting it while prior entries remain
    /// in <see cref="_jobs"/> would let a new job collide with an old one's id). Starts at
    /// 1 so the first newly recorded job is deterministically "job-0001", the second
    /// "job-0002", and so on — reproducible for a fixed sequence of operations, unlike the
    /// random GUID fragment this replaced.
    /// </summary>
    private int _nextJobSequence;

    public double TotalJobs { get; private set; }

    public double TotalTime { get; private set; }

    public double TotalPrintTime { get; private set; }

    public double TotalFilamentUsed { get; private set; }

    public double LongestJob { get; private set; }

    public double LongestPrint { get; private set; }

    /// <summary>Allocates the next deterministic, monotonically increasing job id (e.g. "job-0001", "job-0002", ...).</summary>
    public string NextJobId() => $"job-{Interlocked.Increment(ref _nextJobSequence):D4}";

    public HistoryJobEntry Add(HistoryJobEntry entry)
    {
        lock (_gate)
        {
            _jobs.Add(entry);
            TotalJobs++;
            TotalTime += entry.TotalDuration;
            TotalPrintTime += entry.PrintDuration;
            TotalFilamentUsed += entry.FilamentUsed;
            LongestJob = Math.Max(LongestJob, entry.TotalDuration);
            LongestPrint = Math.Max(LongestPrint, entry.PrintDuration);
            return entry;
        }
    }

    public IReadOnlyList<HistoryJobEntry> Snapshot()
    {
        lock (_gate)
        {
            return _jobs.OrderBy(j => j.StartTime).ToList();
        }
    }

    public HistoryJobEntry? Find(string jobId)
    {
        lock (_gate)
        {
            return _jobs.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
        }
    }

    public bool Remove(string jobId)
    {
        lock (_gate)
        {
            return _jobs.RemoveAll(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal)) > 0;
        }
    }

    public void ResetTotals()
    {
        lock (_gate)
        {
            TotalJobs = 0;
            TotalTime = 0;
            TotalPrintTime = 0;
            TotalFilamentUsed = 0;
            LongestJob = 0;
            LongestPrint = 0;
        }
    }
}

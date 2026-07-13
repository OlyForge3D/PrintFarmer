using System.Collections.Concurrent;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IToolheadActivityAccumulator"/> (issue #711,
/// round-14). Keeps a small rolling window of per-tool active seconds per printer, populated from a
/// backend's status-poll cadence and drained once per statistics sync cycle.
/// </summary>
public sealed class ToolheadActivityAccumulator : IToolheadActivityAccumulator
{
    /// <summary>
    /// Segments longer than this are treated as a telemetry gap (the WebSocket dropped, the printer
    /// paused off-camera, etc.) and credited to no tool, so stale telemetry never fabricates wear.
    /// Defaults to the status freshness window; during a real print Moonraker samples far more often.
    /// </summary>
    private readonly TimeSpan _maxSegment;

    /// <summary>
    /// Absolute ceiling on the seconds retained for a single tool between drains. Drains normally
    /// happen every sync interval (minutes), so this only guards a pathological case where draining
    /// never occurs (e.g. the sync service is disabled) during an extremely long print.
    /// </summary>
    private const double AbsoluteMaxSecondsPerTool = 48 * 60 * 60;

    private readonly ConcurrentDictionary<Guid, PrinterActivity> _activity = new();

    /// <summary>Creates an accumulator using the default freshness-window segment cap (2 minutes).</summary>
    public ToolheadActivityAccumulator()
        : this(PrinterStatusFreshness.MaximumAge)
    {
    }

    /// <summary>Creates an accumulator with an explicit maximum credited segment (used by tests).</summary>
    public ToolheadActivityAccumulator(TimeSpan maxSegment)
    {
        _maxSegment = maxSegment > TimeSpan.Zero ? maxSegment : PrinterStatusFreshness.MaximumAge;
    }

    /// <inheritdoc />
    public void Sample(Guid printerId, int? activeToolIndex, bool isPrinting, DateTime timestampUtc)
    {
        PrinterActivity activity = _activity.GetOrAdd(printerId, static _ => new PrinterActivity());
        lock (activity.Gate)
        {
            // Ignore stale/out-of-order samples so a late arrival cannot rewind the window.
            if (activity.LastSampleUtc is DateTime last)
            {
                if (timestampUtc < last)
                {
                    return;
                }

                TimeSpan elapsed = timestampUtc - last;
                if (activity.LastPrinting
                    && activity.LastToolIndex is int tool
                    && tool >= 0
                    && elapsed > TimeSpan.Zero
                    && elapsed <= _maxSegment)
                {
                    double prior = activity.ActiveSeconds.TryGetValue(tool, out double seconds) ? seconds : 0d;
                    activity.ActiveSeconds[tool] = Math.Min(prior + elapsed.TotalSeconds, AbsoluteMaxSecondsPerTool);
                }
            }

            activity.LastSampleUtc = timestampUtc;
            activity.LastToolIndex = activeToolIndex;
            activity.LastPrinting = isPrinting;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<int, double> DrainActiveSeconds(Guid printerId)
    {
        if (!_activity.TryGetValue(printerId, out PrinterActivity? activity))
        {
            return EmptyResult;
        }

        lock (activity.Gate)
        {
            if (activity.ActiveSeconds.Count == 0)
            {
                return EmptyResult;
            }

            // Snapshot and clear the buckets, but keep Last* fields so the segment spanning this
            // drain is credited into the next interval rather than discarded.
            Dictionary<int, double> drained = new(activity.ActiveSeconds);
            activity.ActiveSeconds.Clear();
            return drained;
        }
    }

    /// <inheritdoc />
    public void Reset(Guid printerId) => _activity.TryRemove(printerId, out _);

    private static readonly IReadOnlyDictionary<int, double> EmptyResult =
        new Dictionary<int, double>();

    private sealed class PrinterActivity
    {
        public object Gate { get; } = new();

        public Dictionary<int, double> ActiveSeconds { get; } = new();

        public DateTime? LastSampleUtc { get; set; }

        public int? LastToolIndex { get; set; }

        public bool LastPrinting { get; set; }
    }
}

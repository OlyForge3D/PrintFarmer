using System.Collections.Concurrent;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IToolheadActivityAccumulator"/> (issue #711,
/// round-14). Retains baseline-scoped per-tool active seconds until a statistics sync both observes
/// an external baseline advance and commits it successfully.
/// </summary>
public sealed class ToolheadActivityAccumulator : IToolheadActivityAccumulator
{
    private const int MaxTrackedToolIndexExclusive = 32;

    /// <summary>
    /// Segments longer than this are treated as a telemetry gap (the WebSocket dropped, the printer
    /// paused off-camera, etc.) and credited to no tool, so stale telemetry never fabricates wear.
    /// Defaults to the status freshness window; during a real print Moonraker samples far more often.
    /// </summary>
    private readonly TimeSpan _maxSegment;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<Guid, PrinterActivity> _activity = new();

    /// <summary>Creates an accumulator using the default freshness-window segment cap (2 minutes).</summary>
    public ToolheadActivityAccumulator()
        : this(PrinterStatusFreshness.MaximumAge, TimeProvider.System)
    {
    }

    /// <summary>Creates an accumulator with an explicit maximum credited segment (used by tests).</summary>
    public ToolheadActivityAccumulator(TimeSpan maxSegment)
        : this(maxSegment, TimeProvider.System)
    {
    }

    /// <summary>
    /// Creates an accumulator with explicit segment freshness and monotonic time providers.
    /// </summary>
    public ToolheadActivityAccumulator(TimeSpan maxSegment, TimeProvider timeProvider)
    {
        _maxSegment = maxSegment > TimeSpan.Zero ? maxSegment : PrinterStatusFreshness.MaximumAge;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public void Sample(Guid printerId, int? activeToolIndex, bool isPrinting)
    {
        PrinterActivity activity = _activity.GetOrAdd(printerId, static _ => new PrinterActivity());
        long timestamp = _timeProvider.GetTimestamp();
        int? trackedToolIndex = NormalizeToolIndex(activeToolIndex);
        lock (activity.Gate)
        {
            if (activity.LastTimestamp is long last)
            {
                if (timestamp < last)
                {
                    return;
                }

                TimeSpan elapsed = _timeProvider.GetElapsedTime(last, timestamp);
                if (elapsed > TimeSpan.Zero)
                {
                    activity.CumulativeWindowSeconds += elapsed.TotalSeconds;
                }

                if (activity.LastPrinting
                    && activity.LastToolIndex is int tool
                    && tool >= 0
                    && tool < MaxTrackedToolIndexExclusive
                    && elapsed > TimeSpan.Zero
                    && elapsed <= _maxSegment)
                {
                    double prior = activity.CumulativeActiveSeconds.TryGetValue(tool, out double seconds)
                        ? seconds
                        : 0d;
                    activity.CumulativeActiveSeconds[tool] = prior + elapsed.TotalSeconds;
                }
            }

            activity.LastTimestamp = timestamp;
            activity.LastToolIndex = trackedToolIndex;
            activity.LastPrinting = isPrinting;
            activity.Sequence++;
        }
    }

    /// <inheritdoc />
    public ToolheadActivitySnapshot PeekActiveSeconds(Guid printerId)
    {
        if (!_activity.TryGetValue(printerId, out PrinterActivity? activity))
        {
            return ToolheadActivitySnapshot.Empty(printerId);
        }

        lock (activity.Gate)
        {
            Dictionary<int, double> pending = [];
            Dictionary<int, double> cumulativeSnapshot = [];
            foreach ((int toolIndex, double cumulativeSeconds) in activity.CumulativeActiveSeconds)
            {
                if (toolIndex is < 0 or >= MaxTrackedToolIndexExclusive)
                {
                    continue;
                }

                cumulativeSnapshot[toolIndex] = cumulativeSeconds;
                double acknowledged = activity.AcknowledgedActiveSeconds.TryGetValue(
                    toolIndex,
                    out double acknowledgedSeconds)
                    ? acknowledgedSeconds
                    : 0d;
                double seconds = Math.Max(0, cumulativeSeconds - acknowledged);
                if (seconds > 0)
                {
                    pending[toolIndex] = seconds;
                }
            }

            double windowSeconds = Math.Max(
                0,
                activity.CumulativeWindowSeconds - activity.AcknowledgedWindowSeconds);
            return new ToolheadActivitySnapshot(
                printerId,
                activity.Generation,
                activity.Sequence,
                pending,
                cumulativeSnapshot,
                pending.Values.Sum(),
                windowSeconds,
                activity.CumulativeWindowSeconds);
        }
    }

    /// <inheritdoc />
    public void AckActiveSecondsThrough(ToolheadActivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_activity.TryGetValue(snapshot.PrinterId, out PrinterActivity? activity))
        {
            return;
        }

        lock (activity.Gate)
        {
            if (activity.Generation != snapshot.Generation
                || snapshot.ThroughSequence <= activity.AcknowledgedThroughSequence)
            {
                return;
            }

            foreach ((int toolIndex, double cumulativeSeconds) in snapshot.CumulativeActiveSeconds)
            {
                double acknowledged = activity.AcknowledgedActiveSeconds.TryGetValue(
                    toolIndex,
                    out double acknowledgedSeconds)
                    ? acknowledgedSeconds
                    : 0d;
                activity.AcknowledgedActiveSeconds[toolIndex] = Math.Max(acknowledged, cumulativeSeconds);
            }

            activity.AcknowledgedWindowSeconds = Math.Max(
                activity.AcknowledgedWindowSeconds,
                snapshot.CumulativeWindowSeconds);
            activity.AcknowledgedThroughSequence = snapshot.ThroughSequence;
            CompactAcknowledged(activity);
        }
    }

    /// <inheritdoc />
    public void Reset(Guid printerId) => _activity.TryRemove(printerId, out _);

    private static int? NormalizeToolIndex(int? activeToolIndex) =>
        activeToolIndex is int toolIndex && toolIndex is >= 0 and < MaxTrackedToolIndexExclusive
            ? toolIndex
            : null;

    private static void CompactAcknowledged(PrinterActivity activity)
    {
        foreach (int toolIndex in activity.AcknowledgedActiveSeconds.Keys.ToArray())
        {
            if (!activity.CumulativeActiveSeconds.TryGetValue(toolIndex, out double cumulativeSeconds)
                || activity.AcknowledgedActiveSeconds[toolIndex] >= cumulativeSeconds)
            {
                activity.AcknowledgedActiveSeconds.Remove(toolIndex);
                activity.CumulativeActiveSeconds.Remove(toolIndex);
            }
        }

        if (activity.AcknowledgedWindowSeconds >= activity.CumulativeWindowSeconds)
        {
            activity.AcknowledgedWindowSeconds = 0;
            activity.CumulativeWindowSeconds = 0;
        }
    }

    private sealed class PrinterActivity
    {
        public object Gate { get; } = new();

        public Guid Generation { get; } = Guid.NewGuid();

        public Dictionary<int, double> CumulativeActiveSeconds { get; } = new();

        public Dictionary<int, double> AcknowledgedActiveSeconds { get; } = new();

        public long? LastTimestamp { get; set; }

        public int? LastToolIndex { get; set; }

        public bool LastPrinting { get; set; }

        public long Sequence { get; set; }

        public long AcknowledgedThroughSequence { get; set; } = -1;

        public double CumulativeWindowSeconds { get; set; }

        public double AcknowledgedWindowSeconds { get; set; }
    }
}

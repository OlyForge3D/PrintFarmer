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
    public void Sample(Guid printerId, int? activeToolIndex, bool isPrinting) =>
        RecordSegment(
            printerId,
            NormalizeToolIndex(activeToolIndex),
            isPrinting ? SegmentState.Printing : SegmentState.Unknown);

    /// <inheritdoc />
    public void SampleKnownIdle(Guid printerId) =>
        RecordSegment(printerId, trackedToolIndex: null, SegmentState.KnownIdle);

    private void RecordSegment(Guid printerId, int? trackedToolIndex, SegmentState state)
    {
        PrinterActivity activity = _activity.GetOrAdd(printerId, static _ => new PrinterActivity());
        long timestamp = _timeProvider.GetTimestamp();
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
                    // The complete monotonic window always advances (issue #711, round-19
                    // V19-1/H19-1): known-idle seconds are tracked IN PARALLEL, not instead of, so
                    // callers can compute an effective coverage denominator as
                    // (windowSeconds - knownIdleSeconds) without losing the raw total.
                    activity.CumulativeWindowSeconds += elapsed.TotalSeconds;

                    if (activity.LastState == SegmentState.KnownIdle
                        && elapsed <= _maxSegment)
                    {
                        activity.CumulativeKnownIdleSeconds += elapsed.TotalSeconds;
                    }
                }

                if (activity.LastState == SegmentState.Printing
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
            activity.LastState = state;
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
            double knownIdleSeconds = Math.Max(
                0,
                activity.CumulativeKnownIdleSeconds - activity.AcknowledgedKnownIdleSeconds);
            return new ToolheadActivitySnapshot(
                printerId,
                activity.Generation,
                activity.Sequence,
                pending,
                cumulativeSnapshot,
                pending.Values.Sum(),
                windowSeconds,
                activity.CumulativeWindowSeconds,
                knownIdleSeconds,
                activity.CumulativeKnownIdleSeconds);
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
            activity.AcknowledgedKnownIdleSeconds = Math.Max(
                activity.AcknowledgedKnownIdleSeconds,
                snapshot.CumulativeKnownIdleSeconds);
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
        foreach (int toolIndex in activity.AcknowledgedActiveSeconds.Keys
                     .Where(toolIndex =>
                         !activity.CumulativeActiveSeconds.TryGetValue(toolIndex, out double cumulativeSeconds)
                         || activity.AcknowledgedActiveSeconds[toolIndex] >= cumulativeSeconds)
                     .ToArray())
        {
            activity.AcknowledgedActiveSeconds.Remove(toolIndex);
            activity.CumulativeActiveSeconds.Remove(toolIndex);
        }

        if (activity.AcknowledgedWindowSeconds >= activity.CumulativeWindowSeconds)
        {
            activity.AcknowledgedWindowSeconds = 0;
            activity.CumulativeWindowSeconds = 0;
        }

        if (activity.AcknowledgedKnownIdleSeconds >= activity.CumulativeKnownIdleSeconds)
        {
            activity.AcknowledgedKnownIdleSeconds = 0;
            activity.CumulativeKnownIdleSeconds = 0;
        }
    }

    /// <summary>
    /// The observation recorded by the most recent <see cref="Sample"/>/<see cref="SampleKnownIdle"/>
    /// call, used to attribute the NEXT elapsed segment (issue #711, round-19 V19-1/H19-1).
    /// </summary>
    private enum SegmentState
    {
        /// <summary>
        /// Not printing, but not confirmed idle either (stale/disconnected telemetry, a restart-gap
        /// survivor, or printing with an unrecognized/unmapped tool index). Contributes to the window
        /// denominator only — this is where a legitimate coverage clamp reduces attribution.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Actively printing with a recognized physical tool. Contributes to both the active-seconds
        /// numerator and the window denominator.
        /// </summary>
        Printing = 1,

        /// <summary>
        /// Confirmed not printing based on fresh telemetry. Excluded from both the active-seconds
        /// numerator and the effective coverage denominator.
        /// </summary>
        KnownIdle = 2,
    }

    private sealed class PrinterActivity
    {
        public object Gate { get; } = new();

        public Guid Generation { get; } = Guid.NewGuid();

        public Dictionary<int, double> CumulativeActiveSeconds { get; } = new();

        public Dictionary<int, double> AcknowledgedActiveSeconds { get; } = new();

        public long? LastTimestamp { get; set; }

        public int? LastToolIndex { get; set; }

        public SegmentState LastState { get; set; }

        public long Sequence { get; set; }

        public long AcknowledgedThroughSequence { get; set; } = -1;

        public double CumulativeWindowSeconds { get; set; }

        public double AcknowledgedWindowSeconds { get; set; }

        public double CumulativeKnownIdleSeconds { get; set; }

        public double AcknowledgedKnownIdleSeconds { get; set; }
    }
}

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Accumulates interval-aware per-tool "active time" telemetry so that an external-history
/// print-hour delta can be attributed to the physical toolheads that actually did the work over a
/// sync interval, rather than to a single latest-known active tool (issue #711, round-14).
///
/// <para>
/// A backend plugin that surfaces the currently active physical tool (for example the Moonraker
/// plugin, which tracks the Snapmaker U1 active lane, a Happy Hare active tool, or the native
/// Klipper active extruder) calls <see cref="Sample"/> on every status poll. The accumulator adds
/// the elapsed wall-clock time between consecutive samples to the tool that was active during that
/// segment, but only while the printer was actually printing. On each statistics sync cycle
/// <c>PrintStatsSyncHostedService</c> calls <see cref="DrainActiveSeconds"/> to read and
/// clear the per-tool seconds, normalizes them into attribution weights, and distributes the
/// external-history delta accordingly.
/// </para>
///
/// <para>
/// The "no fabrication" guarantee (issue #711, round-10 Finding 1) is preserved: only real,
/// telemetry-backed active-tool seconds are ever accumulated. Segments longer than the freshness
/// window are treated as telemetry gaps and dropped (credited to no tool) rather than assumed to be
/// continuous printing; when a cycle drains nothing the caller leaves the delta unattributed instead
/// of equal-splitting it across idle heads.
/// </para>
///
/// <para>
/// Storage is an in-memory rolling window (see <c>ToolheadActivityAccumulator</c>). This is
/// intentional for the first backend: a process restart drops in-flight (un-drained) attribution for
/// the current interval, but every subsequent cycle is correct because backend print-hours are
/// authoritative and only the intra-interval distribution is approximated.
/// </para>
/// </summary>
public interface IToolheadActivityAccumulator
{
    /// <summary>
    /// Records a point-in-time observation of which physical tool is active on a printer. The time
    /// between this sample and the previous one is credited to the previously-active tool when the
    /// printer was printing during that segment.
    /// </summary>
    /// <param name="printerId">The printer being sampled.</param>
    /// <param name="activeToolIndex">
    /// The backend/G-code index of the physically active toolhead, or <c>null</c> when the active
    /// tool is unknown. A <c>null</c> or negative index credits no tool for the following segment.
    /// </param>
    /// <param name="isPrinting">
    /// <c>true</c> when the printer is actively printing; only printing segments accrue wear.
    /// </param>
    /// <param name="timestampUtc">The observation time (UTC), expected to be monotonic per printer.</param>
    void Sample(Guid printerId, int? activeToolIndex, bool isPrinting, DateTime timestampUtc);

    /// <summary>
    /// Atomically reads and clears the accumulated per-tool active seconds for a printer, returning a
    /// map of backend/G-code tool index → seconds observed as active-and-printing since the previous
    /// drain. The last-observed sample (tool, printing flag, timestamp) is preserved so a segment that
    /// straddles the drain boundary is carried into the next interval without losing time. Returns an
    /// empty map when nothing accrued.
    /// </summary>
    IReadOnlyDictionary<int, double> DrainActiveSeconds(Guid printerId);

    /// <summary>
    /// Discards all accumulated state for a printer (used when a printer is deleted so its buckets do
    /// not linger).
    /// </summary>
    void Reset(Guid printerId);
}

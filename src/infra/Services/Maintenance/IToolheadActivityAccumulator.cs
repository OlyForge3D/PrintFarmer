namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Accumulates interval-aware per-tool "active time" telemetry so that an external-history
/// print-hour delta can be attributed to the physical toolheads that actually did the work over a
/// external-baseline interval, rather than to a single latest-known active tool (issue #711).
///
/// <para>
/// A backend plugin that surfaces the currently active physical tool (for example the Moonraker
/// plugin, which tracks the Snapmaker U1 active lane, a Happy Hare active tool, or the native
/// Klipper active extruder) calls <see cref="Sample"/> on every status poll. The accumulator adds
/// the elapsed wall-clock time between consecutive samples to the tool that was active during that
/// segment, but only while the printer was actually printing. Statistics synchronization peeks at
/// the retained telemetry only when the external baseline advances, then acknowledges exactly that
/// snapshot after the database commit succeeds.
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
/// Storage is an in-memory rolling window (see <c>ToolheadActivityAccumulator</c>). The statistics
/// sync persists the external-baseline wall-clock boundary separately, so a process restart turns
/// the missing in-memory interval into unknown coverage instead of extrapolating the surviving
/// suffix across the full external-hours delta.
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
    void Sample(Guid printerId, int? activeToolIndex, bool isPrinting);

    /// <summary>
    /// Atomically captures pending telemetry without removing it. The snapshot includes known
    /// per-tool seconds and the complete monotonic elapsed window since the previous acknowledgment.
    /// </summary>
    ToolheadActivitySnapshot PeekActiveSeconds(Guid printerId);

    /// <summary>
    /// Acknowledges only the telemetry represented by <paramref name="snapshot"/>. Samples recorded
    /// after the peek remain pending, and stale snapshots from a reset generation are ignored.
    /// </summary>
    /// <param name="snapshot">The successfully persisted snapshot.</param>
    void AckActiveSecondsThrough(ToolheadActivitySnapshot snapshot);

    /// <summary>
    /// Discards all accumulated state for a printer (used when a printer is deleted so its buckets do
    /// not linger).
    /// </summary>
    void Reset(Guid printerId);
}

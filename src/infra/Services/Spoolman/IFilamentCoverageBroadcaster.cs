using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Broadcasts filament coverage change notifications over the shared SignalR
/// printer hub (issue #709). The wire event name is the lowercase string
/// <c>filamentcoveragechanged</c> to stay consistent with existing PrintFarmer
/// SignalR conventions (see <c>printerupdated</c>, <c>jobqueueupdate</c>).
///
/// <para>
/// The broadcaster deliberately does NOT push the full coverage payload —
/// callers should re-fetch <c>GET /api/printers/{id}/filament-coverage</c> (or
/// the fleet variant) so authentication, permissions, and eventual filter
/// changes remain owned by the controller pipeline.
/// </para>
///
/// <para>
/// Emission is best-effort and may be debounced or batched by callers, but per
/// Dallas's acceptance addendum on #709 the visible coverage state must update
/// within 5 seconds of the source change.
/// </para>
/// </summary>
public interface IFilamentCoverageBroadcaster
{
    /// <summary>
    /// Notifies subscribers that a single printer's coverage may have changed
    /// (e.g. spool binding change, active job progress tick, queue mutation).
    /// </summary>
    /// <param name="printerId">The printer whose coverage may have changed.</param>
    /// <param name="reason">Machine-readable reason from <see cref="FilamentCoverageChangeReasons"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task BroadcastPrinterChangedAsync(Guid printerId, string reason, CancellationToken ct);

    /// <summary>
    /// Notifies subscribers that the fleet aggregate coverage may have changed
    /// (e.g. bulk queue mutation, settings update, mass spool import).
    /// </summary>
    /// <param name="reason">Machine-readable reason from <see cref="FilamentCoverageChangeReasons"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task BroadcastFleetChangedAsync(string reason, CancellationToken ct);
}

/// <summary>
/// Canonical machine-readable reason codes emitted by
/// <see cref="IFilamentCoverageBroadcaster"/>. These string values are the
/// wire contract with the React and iOS clients — do not change without
/// coordinating a client update. Pinned by Dallas's F4 addendum on #709.
/// </summary>
public static class FilamentCoverageChangeReasons
{
    /// <summary>Active job progress ticked far enough to shift the projection.</summary>
    public const string JobProgress = "jobProgress";

    /// <summary>A job was assigned or unassigned to/from a printer.</summary>
    public const string JobAssignment = "jobAssignment";

    /// <summary>Assigned-queue composition changed (reordering, add, cancel).</summary>
    public const string QueueChanged = "queueChanged";

    /// <summary>A toolhead's bound spool changed (mount/unmount/swap).</summary>
    public const string SpoolBinding = "spoolBinding";

    /// <summary>A spool's Spoolman remaining weight was mutated (usage recon or manual edit).</summary>
    public const string SpoolWeight = "spoolWeight";

    /// <summary>Coverage thresholds (lead time, reserve, queued-shortage toggle) changed.</summary>
    public const string ThresholdChanged = "thresholdChanged";
}

/// <summary>
/// Lightweight payload shipped with the <c>filamentcoveragechanged</c> SignalR
/// event. Kept minimal so it can be safely broadcast to all subscribers.
/// Property names on the wire are camelCase per the API contract.
/// </summary>
/// <param name="PrinterId">Printer whose coverage changed, or null for fleet-wide invalidation.</param>
/// <param name="Reason">Machine-readable reason; see <see cref="FilamentCoverageChangeReasons"/>.</param>
/// <param name="OccurredAt">UTC timestamp at which the invalidation was emitted.</param>
public record FilamentCoverageChangedEvent(Guid? PrinterId, string Reason, DateTime OccurredAt);

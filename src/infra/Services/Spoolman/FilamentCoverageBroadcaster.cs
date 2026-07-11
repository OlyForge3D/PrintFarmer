using System.Collections.Concurrent;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Default <see cref="IFilamentCoverageBroadcaster"/> implementation that
/// emits <c>filamentcoveragechanged</c> events on the shared
/// <see cref="PrinterHub"/> — same pattern used for <c>printerupdated</c>,
/// <c>jobqueueupdate</c>, and other cross-cutting invalidation signals.
///
/// <para>
/// Coalesces bursts on the same (printerId, reason) key inside a short
/// window (<see cref="CoalesceWindow"/>) so high-frequency mutation sources
/// like progress ticks cannot trigger broadcast storms (#709 convergence
/// item 5). The first event in each window is emitted immediately; further
/// events with the same key within the window are dropped.
/// </para>
/// </summary>
public class FilamentCoverageBroadcaster(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<FilamentCoverageBroadcaster> logger)
    : IFilamentCoverageBroadcaster
{
    private const string EventName = "filamentcoveragechanged";

    /// <summary>
    /// Minimum interval between two emissions for the same (printerId, reason)
    /// key. Chosen to be tight enough that operators still see live updates
    /// but wide enough to swallow tight update bursts (e.g. multi-toolhead
    /// spool binding sweeps or per-tick progress signals).
    /// </summary>
    internal static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(250);

    private readonly IHubContext<PrinterHub> _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ILogger<FilamentCoverageBroadcaster> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Last emit time per (printerId, reason) key. Uses Guid.Empty for fleet.
    private readonly ConcurrentDictionary<(Guid Scope, string Reason), DateTime> _lastEmit = new();

    public async Task BroadcastPrinterChangedAsync(Guid printerId, string reason, CancellationToken ct)
    {
        if (!IsEnabled())
        {
            return;
        }

        string safeReason = NormalizeReason(reason);
        if (!ShouldEmit(printerId, safeReason))
        {
            return;
        }

        try
        {
            FilamentCoverageChangedEvent payload = new(printerId, safeReason, DateTime.UtcNow);
            await _hub.Clients.All.SendAsync(EventName, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[FilamentCoverage] Failed to broadcast {Event} for printer {PrinterId} reason={Reason}",
                EventName,
                printerId,
                safeReason);
        }
    }

    public async Task BroadcastFleetChangedAsync(string reason, CancellationToken ct)
    {
        if (!IsEnabled())
        {
            return;
        }

        string safeReason = NormalizeReason(reason);
        if (!ShouldEmit(Guid.Empty, safeReason))
        {
            return;
        }

        try
        {
            FilamentCoverageChangedEvent payload = new(null, safeReason, DateTime.UtcNow);
            await _hub.Clients.All.SendAsync(EventName, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[FilamentCoverage] Failed to broadcast fleet {Event} reason={Reason}",
                EventName,
                safeReason);
        }
    }

    // Returns true if the caller should emit the event now, false if the
    // (scope, reason) pair was emitted too recently.
    private bool ShouldEmit(Guid scope, string reason)
    {
        DateTime now = DateTime.UtcNow;
        (Guid Scope, string Reason) key = (scope, reason);
        while (true)
        {
            if (!_lastEmit.TryGetValue(key, out DateTime last))
            {
                if (_lastEmit.TryAdd(key, now))
                {
                    return true;
                }

                continue;
            }

            if (now - last < CoalesceWindow)
            {
                return false;
            }

            if (_lastEmit.TryUpdate(key, now, last))
            {
                return true;
            }
        }
    }

    private bool IsEnabled()
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IOperatorFeatureGate gate = scope.ServiceProvider.GetRequiredService<IOperatorFeatureGate>();
            return gate.IsEnabled(OperatorFeature.FilamentCoverage);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FilamentCoverage] Feature gate unavailable; suppressing broadcast");
            return false;
        }
    }

    // Defensive: never send an empty reason on the wire. Unknown reasons fall
    // back to "queueChanged" (the most conservative refetch trigger) rather
    // than a bespoke string a client won't recognize.
    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? FilamentCoverageChangeReasons.QueueChanged : reason;
}

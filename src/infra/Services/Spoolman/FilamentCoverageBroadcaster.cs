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
/// like progress ticks cannot trigger broadcast storms. The first event is
/// emitted immediately; when later events are suppressed, exactly one trailing
/// event carries the latest occurrence after the burst settles.
/// </para>
/// </summary>
public class FilamentCoverageBroadcaster(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<FilamentCoverageBroadcaster> logger,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
    Func<DateTime>? utcNow = null)
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
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync =
        delayAsync ?? ((delay, token) => Task.Delay(delay, token));

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    // Uses Guid.Empty for fleet scope. Each state owns one cancellable debounce
    // window and is removed when the window settles.
    private readonly ConcurrentDictionary<(Guid Scope, string Reason), CoalescingState> _states = new();

    public async Task BroadcastPrinterChangedAsync(Guid printerId, string reason, CancellationToken ct)
    {
        if (!IsEnabled())
        {
            return;
        }

        string safeReason = NormalizeReason(reason);
        FilamentCoverageChangedEvent payload = new(printerId, safeReason, _utcNow());
        await QueueAsync(payload, ct).ConfigureAwait(false);
    }

    public async Task BroadcastFleetChangedAsync(string reason, CancellationToken ct)
    {
        if (!IsEnabled())
        {
            return;
        }

        string safeReason = NormalizeReason(reason);
        FilamentCoverageChangedEvent payload = new(null, safeReason, _utcNow());
        await QueueAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task QueueAsync(FilamentCoverageChangedEvent payload, CancellationToken ct)
    {
        (Guid Scope, string Reason) key = (payload.PrinterId ?? Guid.Empty, payload.Reason);
        while (true)
        {
            if (!_states.TryGetValue(key, out CoalescingState? state))
            {
                CoalescingState created = new(payload);
                if (_states.TryAdd(key, created))
                {
                    ScheduleWindow(key, created, created.Version, created.DelayCancellation.Token);
                    await SendPayloadAsync(payload, ct).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            CancellationTokenSource? previousDelay = null;
            long version = 0;
            CancellationToken token = default;
            lock (state.Sync)
            {
                if (!_states.TryGetValue(key, out CoalescingState? current)
                    || !ReferenceEquals(current, state))
                {
                    continue;
                }

                state.LatestPayload = payload;
                state.HasSuppressedEvent = true;
                state.Version++;
                version = state.Version;
                previousDelay = state.DelayCancellation;
                state.DelayCancellation = new CancellationTokenSource();
                token = state.DelayCancellation.Token;
            }

            await previousDelay.CancelAsync().ConfigureAwait(false);
            previousDelay.Dispose();
            ScheduleWindow(key, state, version, token);
            return;
        }
    }

    private void ScheduleWindow(
        (Guid Scope, string Reason) key,
        CoalescingState state,
        long version,
        CancellationToken token)
    {
        _ = CompleteWindowAsync(key, state, version, token);
    }

    private async Task CompleteWindowAsync(
        (Guid Scope, string Reason) key,
        CoalescingState state,
        long version,
        CancellationToken token)
    {
        try
        {
            await _delayAsync(CoalesceWindow, token).ConfigureAwait(false);

            FilamentCoverageChangedEvent? trailingPayload = null;
            lock (state.Sync)
            {
                if (state.Version != version
                    || !_states.TryGetValue(key, out CoalescingState? current)
                    || !ReferenceEquals(current, state))
                {
                    return;
                }

                _ = _states.TryRemove(key, out _);
                if (state.HasSuppressedEvent)
                {
                    trailingPayload = state.LatestPayload;
                }

                state.DelayCancellation.Dispose();
            }

            if (trailingPayload is not null && IsEnabled())
            {
                await SendPayloadAsync(trailingPayload, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer event replaced this debounce window.
        }
        catch (Exception ex)
        {
            RemoveStateIfCurrent(key, state, version);
            _logger.LogDebug(
                ex,
                "[FilamentCoverage] Failed to complete trailing broadcast for scope {Scope} reason={Reason}",
                key.Scope,
                key.Reason);
        }
    }

    private void RemoveStateIfCurrent(
        (Guid Scope, string Reason) key,
        CoalescingState state,
        long version)
    {
        lock (state.Sync)
        {
            if (state.Version != version
                || !_states.TryGetValue(key, out CoalescingState? current)
                || !ReferenceEquals(current, state))
            {
                return;
            }

            _ = _states.TryRemove(key, out _);
            state.DelayCancellation.Dispose();
        }
    }

    private async Task SendPayloadAsync(FilamentCoverageChangedEvent payload, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(EventName, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[FilamentCoverage] Failed to broadcast {Event} for scope {Scope} reason={Reason}",
                EventName,
                payload.PrinterId,
                payload.Reason);
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

    private sealed class CoalescingState(FilamentCoverageChangedEvent initialPayload)
    {
        public object Sync { get; } = new();

        public FilamentCoverageChangedEvent LatestPayload { get; set; } = initialPayload;

        public bool HasSuppressedEvent { get; set; }

        public long Version { get; set; } = 1;

        public CancellationTokenSource DelayCancellation { get; set; } = new();
    }
}

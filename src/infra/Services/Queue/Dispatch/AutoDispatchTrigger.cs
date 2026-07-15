using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>Payload carried through the dispatch trigger channel.</summary>
public readonly record struct DispatchTriggerEvent(Guid PrinterId, bool SkipIdleThreshold)
{
    internal long OwnershipGeneration { get; init; }
}

/// <summary>
/// Owns one generation of a pending per-printer idle wait.
/// </summary>
public sealed class PendingDispatchLease : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly object _sync = new();
    private bool _disposed;

    internal PendingDispatchLease(long generation, CancellationToken parent)
    {
        Generation = generation;
        _source = CancellationTokenSource.CreateLinkedTokenSource(parent);
    }

    /// <summary>Gets the monotonically increasing ownership generation.</summary>
    public long Generation { get; }

    /// <summary>Gets the cancellation token owned by this lease.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Gets whether this lease has been cancelled.</summary>
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    internal void Cancel()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _source.Cancel();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _source.Dispose();
        }
    }
}

/// <summary>
/// Singleton trigger for notifying the auto-dispatch background service
/// that a printer has become idle or a new job has been queued.
/// </summary>
public interface IAutoDispatchTrigger
{
    /// <summary>Signals that a printer finished a job and may be ready for the next one.</summary>
    void NotifyPrinterIdle(Guid printerId);

    /// <summary>Signals that a queued job may be dispatched immediately.</summary>
    void NotifyJobQueued(Guid printerId);

    /// <summary>Cancels the currently owned idle wait for a printer.</summary>
    void CancelPendingDispatch(Guid printerId);
}

/// <summary>Readable side of the trigger, consumed by the background service.</summary>
public interface IAutoDispatchTriggerReader
{
    /// <summary>Reads the next coalesced dispatch intent.</summary>
    ValueTask<DispatchTriggerEvent> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Keyed coalescing trigger. Ownership remains attached to a printer from channel dequeue
/// through worker completion. Notifications received while that owner is active merge into
/// one rerun intent rather than creating waiting worker tasks.
/// </summary>
public sealed class AutoDispatchTrigger : IAutoDispatchTrigger, IAutoDispatchTriggerReader
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    private readonly ConcurrentDictionary<Guid, DispatchIntentState> _intentStates = new();
    private readonly ConcurrentCancellationMap _pendingCancellations = new();
    private long _nextOwnershipGeneration;
    private int _acceptingNotifications = 1;

    /// <inheritdoc />
    public void NotifyPrinterIdle(Guid printerId)
    {
        Enqueue(printerId, skipIdleThreshold: false);
    }

    /// <inheritdoc />
    public void NotifyJobQueued(Guid printerId)
    {
        Enqueue(printerId, skipIdleThreshold: true);
    }

    /// <inheritdoc />
    public void CancelPendingDispatch(Guid printerId)
    {
        _pendingCancellations.Cancel(printerId);
    }

    /// <inheritdoc />
    public async ValueTask<DispatchTriggerEvent> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            Guid printerId = await _channel.Reader.ReadAsync(ct);
            if (!_intentStates.TryGetValue(printerId, out DispatchIntentState? state))
            {
                continue;
            }

            lock (state)
            {
                if (state.IsRetired || !state.IsQueued || !state.HasPendingIntent)
                {
                    continue;
                }

                state.IsQueued = false;
                state.IsInFlight = true;
                state.OwnershipGeneration = Interlocked.Increment(ref _nextOwnershipGeneration);
                return state.TakePendingIntent(printerId) with
                {
                    OwnershipGeneration = state.OwnershipGeneration,
                };
            }
        }
    }

    /// <summary>
    /// Completes one owned evaluation. If a newer notification arrived, ownership stays with
    /// the same worker and exactly one merged rerun is returned. Otherwise the idle state is
    /// retired and removed with value identity so a racing enqueue can safely create a new one.
    /// </summary>
    internal bool TryCompleteProcessing(
        DispatchTriggerEvent completed,
        bool allowRerun,
        out DispatchTriggerEvent rerun)
    {
        rerun = default;
        if (!_intentStates.TryGetValue(completed.PrinterId, out DispatchIntentState? state))
        {
            return false;
        }

        lock (state)
        {
            if (state.IsRetired
                || !state.IsInFlight
                || state.OwnershipGeneration != completed.OwnershipGeneration)
            {
                return false;
            }

            if (allowRerun && state.HasPendingIntent)
            {
                rerun = state.TakePendingIntent(completed.PrinterId) with
                {
                    OwnershipGeneration = state.OwnershipGeneration,
                };
                return true;
            }

            state.HasPendingIntent = false;
            state.SkipIdleThreshold = false;
            state.IsInFlight = false;
            state.IsRetired = true;
            _ = ((ICollection<KeyValuePair<Guid, DispatchIntentState>>)_intentStates)
                .Remove(new KeyValuePair<Guid, DispatchIntentState>(completed.PrinterId, state));
            return false;
        }
    }

    /// <summary>
    /// Stops accepting host-lifetime notifications and retires every state not owned by a
    /// draining worker. In-flight owners remove their own state after cancellation.
    /// </summary>
    internal void StopAccepting()
    {
        if (Interlocked.Exchange(ref _acceptingNotifications, 0) == 0)
        {
            return;
        }

        foreach (KeyValuePair<Guid, DispatchIntentState> entry in _intentStates)
        {
            DispatchIntentState state = entry.Value;
            lock (state)
            {
                state.HasPendingIntent = false;
                state.SkipIdleThreshold = false;
                if (state.IsInFlight)
                {
                    continue;
                }

                state.IsQueued = false;
                state.IsRetired = true;
                _ = ((ICollection<KeyValuePair<Guid, DispatchIntentState>>)_intentStates)
                    .Remove(entry);
            }
        }
    }

    /// <summary>Creates and registers a generation-owned idle-wait lease.</summary>
    public PendingDispatchLease CreatePendingLease(Guid printerId, CancellationToken serviceCt)
    {
        return _pendingCancellations.Create(printerId, serviceCt);
    }

    /// <summary>Clears a pending lease only when the caller still owns that generation.</summary>
    public void ClearPending(Guid printerId, PendingDispatchLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _pendingCancellations.Remove(printerId, lease);
    }

    /// <summary>Returns whether an idle-wait lease is currently registered.</summary>
    public bool HasPendingDispatch(Guid printerId)
    {
        return _pendingCancellations.IsRegistered(printerId);
    }

    internal int IntentStateCount => _intentStates.Count;

    internal int PendingRerunCount
    {
        get
        {
            int count = 0;
            foreach (DispatchIntentState state in _intentStates.Values)
            {
                lock (state)
                {
                    if (!state.IsRetired && state.IsInFlight && state.HasPendingIntent)
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }

    private void Enqueue(Guid printerId, bool skipIdleThreshold)
    {
        // A fresh event supersedes an active idle timer. Its replacement intent is merged
        // under the same state lock used by worker completion, closing the handoff race.
        _pendingCancellations.Cancel(printerId);
        if (Volatile.Read(ref _acceptingNotifications) == 0)
        {
            return;
        }

        while (true)
        {
            DispatchIntentState state = _intentStates.GetOrAdd(
                printerId,
                static _ => new DispatchIntentState());
            bool queueState = false;
            lock (state)
            {
                if (Volatile.Read(ref _acceptingNotifications) == 0)
                {
                    state.IsRetired = true;
                    _ = ((ICollection<KeyValuePair<Guid, DispatchIntentState>>)_intentStates)
                        .Remove(new KeyValuePair<Guid, DispatchIntentState>(printerId, state));
                    return;
                }

                if (state.IsRetired)
                {
                    continue;
                }

                state.Merge(skipIdleThreshold);
                if (!state.IsQueued && !state.IsInFlight)
                {
                    state.IsQueued = true;
                    queueState = true;
                }
            }

            if (!queueState)
            {
                return;
            }

            if (_channel.Writer.TryWrite(printerId))
            {
                return;
            }

            // The channel is intentionally unbounded and never completed during normal host
            // lifetime. Keep failure handling identity-safe if that policy changes later.
            lock (state)
            {
                state.IsQueued = false;
                state.HasPendingIntent = false;
                state.SkipIdleThreshold = false;
                state.IsRetired = true;
                _ = ((ICollection<KeyValuePair<Guid, DispatchIntentState>>)_intentStates)
                    .Remove(new KeyValuePair<Guid, DispatchIntentState>(printerId, state));
            }

            throw new InvalidOperationException("Auto-dispatch trigger channel rejected an intent.");
        }
    }

    private sealed class DispatchIntentState
    {
        public bool IsQueued { get; set; }

        public bool IsInFlight { get; set; }

        public bool IsRetired { get; set; }

        public bool HasPendingIntent { get; set; }

        public bool SkipIdleThreshold { get; set; }

        public long OwnershipGeneration { get; set; }

        public void Merge(bool skipIdleThreshold)
        {
            HasPendingIntent = true;
            SkipIdleThreshold |= skipIdleThreshold;
        }

        public DispatchTriggerEvent TakePendingIntent(Guid printerId)
        {
            var result = new DispatchTriggerEvent(printerId, SkipIdleThreshold);
            HasPendingIntent = false;
            SkipIdleThreshold = false;
            return result;
        }
    }

    private sealed class ConcurrentCancellationMap
    {
        private readonly ConcurrentDictionary<Guid, PendingDispatchLease> _map = new();
        private long _nextGeneration;

        public PendingDispatchLease Create(Guid key, CancellationToken parent)
        {
            var lease = new PendingDispatchLease(
                Interlocked.Increment(ref _nextGeneration),
                parent);
            while (true)
            {
                if (_map.TryGetValue(key, out PendingDispatchLease? previous))
                {
                    if (_map.TryUpdate(key, lease, previous))
                    {
                        previous.Cancel();
                        return lease;
                    }

                    continue;
                }

                if (_map.TryAdd(key, lease))
                {
                    return lease;
                }
            }
        }

        public void Cancel(Guid key)
        {
            if (_map.TryRemove(key, out PendingDispatchLease? lease))
            {
                lease.Cancel();
            }
        }

        public void Remove(Guid key, PendingDispatchLease lease)
        {
            _ = ((ICollection<KeyValuePair<Guid, PendingDispatchLease>>)_map)
                .Remove(new KeyValuePair<Guid, PendingDispatchLease>(key, lease));
        }

        public bool IsRegistered(Guid key)
        {
            return _map.ContainsKey(key);
        }
    }
}

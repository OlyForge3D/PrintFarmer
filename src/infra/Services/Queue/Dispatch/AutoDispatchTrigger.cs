using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>Payload carried through the dispatch trigger channel.</summary>
public readonly record struct DispatchTriggerEvent(Guid PrinterId, bool SkipIdleThreshold);

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
/// Keyed coalescing trigger. At most one unread channel entry exists per printer,
/// so duplicate notifications consume bounded memory without dropping distinct printers.
/// </summary>
public sealed class AutoDispatchTrigger : IAutoDispatchTrigger, IAutoDispatchTriggerReader
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    private readonly ConcurrentDictionary<Guid, DispatchTriggerEvent> _pendingIntents = new();
    private readonly ConcurrentCancellationMap _pendingCancellations = new();

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
            if (_pendingIntents.TryRemove(printerId, out DispatchTriggerEvent triggerEvent))
            {
                return triggerEvent;
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

    private void Enqueue(Guid printerId, bool skipIdleThreshold)
    {
        // A fresh event supersedes an active idle timer. The coalesced replacement
        // below is then processed in per-printer order by the background service.
        _pendingCancellations.Cancel(printerId);
        var incoming = new DispatchTriggerEvent(printerId, skipIdleThreshold);
        while (true)
        {
            if (_pendingIntents.TryGetValue(printerId, out DispatchTriggerEvent current))
            {
                var merged = new DispatchTriggerEvent(
                    printerId,
                    current.SkipIdleThreshold || skipIdleThreshold);
                if (_pendingIntents.TryUpdate(printerId, merged, current))
                {
                    return;
                }

                continue;
            }

            if (_pendingIntents.TryAdd(printerId, incoming))
            {
                if (!_channel.Writer.TryWrite(printerId))
                {
                    _ = ((ICollection<KeyValuePair<Guid, DispatchTriggerEvent>>)_pendingIntents)
                        .Remove(new KeyValuePair<Guid, DispatchTriggerEvent>(printerId, incoming));
                    throw new InvalidOperationException("Auto-dispatch trigger channel rejected an intent.");
                }

                return;
            }
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

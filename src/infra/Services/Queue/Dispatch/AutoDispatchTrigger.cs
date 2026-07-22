using System.Threading.Channels;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Payload carried through the dispatch trigger channel.
/// </summary>
public readonly record struct DispatchTriggerEvent(Guid PrinterId, bool SkipIdleThreshold);

/// <summary>
/// Singleton trigger for notifying the auto-dispatch background service
/// that a printer has become idle or a new job has been queued.
/// Fire-and-forget from scoped services.
/// </summary>
public interface IAutoDispatchTrigger
{
    /// <summary>
    /// Signals that a printer finished a job and may be ready for the next one.
    /// The idle threshold delay will apply before dispatch.
    /// </summary>
    void NotifyPrinterIdle(Guid printerId);

    /// <summary>
    /// Signals that a new job was queued for a specific printer.
    /// Skips the idle threshold delay for immediate dispatch.
    /// </summary>
    void NotifyJobQueued(Guid printerId);

    /// <summary>
    /// Cancels any pending idle-wait for the specified printer
    /// (e.g., when the printer goes offline before the threshold elapses).
    /// </summary>
    void CancelPendingDispatch(Guid printerId);
}

/// <summary>
/// Readable side of the trigger — consumed exclusively by the background service.
/// </summary>
public interface IAutoDispatchTriggerReader
{
    /// <summary>
    /// Reads dispatch trigger events. Blocks until one is available or cancelled.
    /// </summary>
    ValueTask<DispatchTriggerEvent> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Channel-backed implementation of the auto-dispatch trigger.
/// </summary>
public sealed class AutoDispatchTrigger : IAutoDispatchTrigger, IAutoDispatchTriggerReader
{
    private readonly Channel<DispatchTriggerEvent> _channel = Channel.CreateBounded<DispatchTriggerEvent>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly ConcurrentCancellationMap _pendingCancellations = new();

    public void NotifyPrinterIdle(Guid printerId)
    {
        _channel.Writer.TryWrite(new DispatchTriggerEvent(printerId, SkipIdleThreshold: false));
    }

    public void NotifyJobQueued(Guid printerId)
    {
        _channel.Writer.TryWrite(new DispatchTriggerEvent(printerId, SkipIdleThreshold: true));
    }

    public void CancelPendingDispatch(Guid printerId)
    {
        _pendingCancellations.Cancel(printerId);
    }

    public ValueTask<DispatchTriggerEvent> ReadAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAsync(ct);
    }

    /// <summary>
    /// Creates a linked CancellationTokenSource that will be cancelled
    /// if <see cref="CancelPendingDispatch"/> is called for this printer.
    /// </summary>
    public CancellationTokenSource CreateLinkedCts(Guid printerId, CancellationToken serviceCt)
    {
        return _pendingCancellations.CreateLinked(printerId, serviceCt);
    }

    /// <summary>
    /// Removes the per-printer CTS after the dispatch cycle completes.
    /// </summary>
    public void ClearPending(Guid printerId)
    {
        _pendingCancellations.Remove(printerId);
    }

    /// <summary>
    /// Returns whether a pending idle-wait CTS is currently registered for the printer.
    /// Intended for tests to deterministically await registration before issuing a cancel,
    /// avoiding a race against the (DB-bound) registration step under load.
    /// </summary>
    public bool HasPendingDispatch(Guid printerId)
    {
        return _pendingCancellations.IsRegistered(printerId);
    }

    /// <summary>
    /// Thread-safe map of per-printer CancellationTokenSources for pending idle waits.
    /// </summary>
    private sealed class ConcurrentCancellationMap
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _map = new();

        public void Cancel(Guid key)
        {
            if (_map.TryRemove(key, out CancellationTokenSource? cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        public CancellationTokenSource CreateLinked(Guid key, CancellationToken parent)
        {
            Cancel(key); // cancel any previous pending wait
            var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _map[key] = cts;
            return cts;
        }

        public void Remove(Guid key)
        {
            if (_map.TryRemove(key, out CancellationTokenSource? cts))
            {
                cts.Dispose();
            }
        }

        public bool IsRegistered(Guid key)
        {
            return _map.ContainsKey(key);
        }
    }
}

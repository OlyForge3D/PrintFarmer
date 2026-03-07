using System.Threading.Channels;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Singleton trigger for notifying the auto-dispatch background service
/// that a printer has become idle. Fire-and-forget from scoped services.
/// </summary>
public interface IAutoDispatchTrigger
{
    /// <summary>
    /// Signals that a printer finished a job and may be ready for the next one.
    /// </summary>
    void NotifyPrinterIdle(Guid printerId);

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
    /// Reads idle-printer notifications. Blocks until one is available or cancelled.
    /// </summary>
    ValueTask<Guid> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Channel-backed implementation of the auto-dispatch trigger.
/// </summary>
public sealed class AutoDispatchTrigger : IAutoDispatchTrigger, IAutoDispatchTriggerReader
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly ConcurrentCancellationMap _pendingCancellations = new();

    public void NotifyPrinterIdle(Guid printerId)
    {
        _channel.Writer.TryWrite(printerId);
    }

    public void CancelPendingDispatch(Guid printerId)
    {
        _pendingCancellations.Cancel(printerId);
    }

    public ValueTask<Guid> ReadAsync(CancellationToken ct)
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
    }
}

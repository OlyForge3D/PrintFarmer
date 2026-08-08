using System.Collections.Concurrent;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Coordinates in-process dispatch concurrency across every dispatch entry point.
/// Printer claims protect selection while capacity leases bound only upload/start work.
/// </summary>
public sealed class DispatchConcurrencyCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<Guid, byte> _claimedPrinters = [];
    private readonly SemaphoreSlim _capacity = new(0, int.MaxValue);
    private readonly object _capacitySync = new();
    private int _configuredLimit;
    private int _withheldReturns;
    private int _inFlightCount;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Gets the number of uploads or starts currently in flight.</summary>
    public int InFlightCount => Volatile.Read(ref _inFlightCount);

    /// <summary>
    /// Attempts to claim a printer for selection and dispatch without waiting.
    /// </summary>
    public bool TryClaimPrinter(Guid printerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _claimedPrinters.TryAdd(printerId, 0);
    }

    /// <summary>Releases a previously acquired in-process printer claim.</summary>
    public void ReleasePrinter(Guid printerId)
    {
        _ = _claimedPrinters.TryRemove(printerId, out _);
    }

    /// <summary>
    /// Waits for global dispatch capacity and starts the real in-flight window.
    /// </summary>
    public async Task<DispatchCapacityLease> AcquireCapacityAsync(
        int requestedLimit,
        CancellationToken cancellationToken)
    {
        ConfigureCapacity(requestedLimit);
        await _capacity.WaitAsync(cancellationToken);
        _ = Interlocked.Increment(ref _inFlightCount);
        return new DispatchCapacityLease(ReleaseCapacity);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_capacitySync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _capacity.Dispose();
        }
    }

    private void ConfigureCapacity(int requestedLimit)
    {
        lock (_capacitySync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int newLimit = Math.Max(1, requestedLimit);
            if (!_initialized)
            {
                _initialized = true;
                _configuredLimit = newLimit;
                _capacity.Release(newLimit);
                return;
            }

            if (newLimit > _configuredLimit)
            {
                int increase = newLimit - _configuredLimit;
                int restoredReturns = Math.Min(increase, _withheldReturns);
                _withheldReturns -= restoredReturns;
                increase -= restoredReturns;
                if (increase > 0)
                {
                    _capacity.Release(increase);
                }
            }
            else if (newLimit < _configuredLimit)
            {
                int reduction = _configuredLimit - newLimit;
                while (reduction > 0 && _capacity.Wait(0))
                {
                    reduction--;
                }

                _withheldReturns += reduction;
            }

            _configuredLimit = newLimit;
        }
    }

    private void ReleaseCapacity()
    {
        _ = Interlocked.Decrement(ref _inFlightCount);
        lock (_capacitySync)
        {
            if (_disposed)
            {
                return;
            }

            if (_withheldReturns > 0)
            {
                _withheldReturns--;
            }
            else
            {
                _capacity.Release();
            }
        }
    }
}

/// <summary>Owns one global in-flight dispatch slot.</summary>
public sealed class DispatchCapacityLease(Action release) : IDisposable
{
    private Action? _release = release;

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

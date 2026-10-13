using System.Collections.Concurrent;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Serializes camera synchronization per printer so overlapping syncs (an explicit discover racing the
/// background timer or a connect event) cannot both create the same discovered camera. Register as a
/// singleton; syncs for different printers still run concurrently.
/// </summary>
public sealed class PrinterCameraSyncGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Process-wide gate used when none is supplied through dependency injection.</summary>
    public static PrinterCameraSyncGate Shared { get; } = new();

    /// <summary>Waits for exclusive sync access to the printer; dispose the result to release it.</summary>
    /// <param name="printerId">Printer being synchronized.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IDisposable> AcquireAsync(Guid printerId, CancellationToken ct)
    {
        SemaphoreSlim semaphore = _locks.GetOrAdd(printerId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}

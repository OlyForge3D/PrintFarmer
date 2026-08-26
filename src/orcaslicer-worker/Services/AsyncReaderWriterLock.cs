using System.Diagnostics.CodeAnalysis;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Provides writer-preferring asynchronous reader/writer coordination.
/// </summary>
internal sealed class AsyncReaderWriterLock : IDisposable
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly SemaphoreSlim _roomEmpty = new(1, 1);
    private readonly SemaphoreSlim _readerMutex = new(1, 1);
    private int _readerCount;
    private bool _disposed;

    public async ValueTask<Releaser> AcquireReadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _turnstile.WaitAsync(cancellationToken);
        _turnstile.Release();

        await _readerMutex.WaitAsync(cancellationToken);
        bool roomAcquired = false;
        try
        {
            if (_readerCount == 0)
            {
                await _roomEmpty.WaitAsync(cancellationToken);
                roomAcquired = true;
            }

            _readerCount++;
            return new Releaser(this, isWriter: false);
        }
        catch
        {
            if (roomAcquired)
            {
                _roomEmpty.Release();
            }

            throw;
        }
        finally
        {
            _readerMutex.Release();
        }
    }

    public async ValueTask<Releaser> AcquireWriteAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _turnstile.WaitAsync(cancellationToken);
        try
        {
            await _roomEmpty.WaitAsync(cancellationToken);
            return new Releaser(this, isWriter: true);
        }
        catch
        {
            _turnstile.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readerMutex.Dispose();
        _roomEmpty.Dispose();
        _turnstile.Dispose();
    }

    private async ValueTask ReleaseReaderAsync()
    {
        await _readerMutex.WaitAsync();
        try
        {
            _readerCount--;
            if (_readerCount == 0)
            {
                _roomEmpty.Release();
            }
        }
        finally
        {
            _readerMutex.Release();
        }
    }

    private void ReleaseWriter()
    {
        _roomEmpty.Release();
        _turnstile.Release();
    }

    internal sealed class Releaser(
        AsyncReaderWriterLock owner,
        bool isWriter) : IAsyncDisposable
    {
        [SuppressMessage(
            "Usage",
            "CA2213:Disposable fields should be disposed",
            Justification = "This lease does not own the shared lock and releases only its acquired access.")]
        private AsyncReaderWriterLock? _owner = owner;

        public async ValueTask DisposeAsync()
        {
            AsyncReaderWriterLock? currentOwner =
                Interlocked.Exchange(ref _owner, null);
            if (currentOwner is null)
            {
                return;
            }

            if (isWriter)
            {
                currentOwner.ReleaseWriter();
            }
            else
            {
                await currentOwner.ReleaseReaderAsync();
            }
        }
    }
}

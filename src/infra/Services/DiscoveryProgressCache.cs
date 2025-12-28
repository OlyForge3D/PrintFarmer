using System.Collections.Concurrent;

namespace Farm.Infrastructure.Services;

public interface IDiscoveryProgressCache
{
    void Set(string sessionId, DiscoveryProgressDto progress);
    bool TryGet(string sessionId, out DiscoveryProgressDto? progress);
    void Remove(string sessionId);

    /// <summary>
    /// Stores a CancellationTokenSource for a discovery session, allowing clients to request cancellation.
    /// </summary>
    void SetCancellationSource(string sessionId, CancellationTokenSource cts);

    /// <summary>
    /// Attempts to cancel a discovery session by its sessionId.
    /// </summary>
    bool TryCancel(string sessionId);
}

public class DiscoveryProgressCache : IDiscoveryProgressCache
{
    private readonly ConcurrentDictionary<string, DiscoveryProgressDto> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string sessionId, DiscoveryProgressDto progress)
    {
        _cache[sessionId] = progress;
    }

    public bool TryGet(string sessionId, out DiscoveryProgressDto? progress)
    {
        bool found = _cache.TryGetValue(sessionId, out DiscoveryProgressDto? value);
        progress = value;
        return found;
    }

    public void Remove(string sessionId)
    {
        _ = _cache.TryRemove(sessionId, out _);
        // Also remove and dispose the cancellation source if it exists
        if (_cancellationSources.TryRemove(sessionId, out CancellationTokenSource? cts))
        {
            cts.Dispose();
        }
    }

    public void SetCancellationSource(string sessionId, CancellationTokenSource cts)
    {
        _cancellationSources[sessionId] = cts;
    }

    public bool TryCancel(string sessionId)
    {
        if (_cancellationSources.TryGetValue(sessionId, out CancellationTokenSource? cts) && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            return true;
        }
        return false;
    }
}

using System.Collections.Concurrent;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services;

public interface IDiscoveryProgressCache
{
    void Set(string sessionId, DiscoveryProgressDto progress);
    bool TryGet(string sessionId, out DiscoveryProgressDto? progress);
}

public class DiscoveryProgressCache : IDiscoveryProgressCache
{
    private readonly ConcurrentDictionary<string, DiscoveryProgressDto> _cache = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string sessionId, DiscoveryProgressDto progress)
    {
        _cache[sessionId] = progress;
    }

    public bool TryGet(string sessionId, out DiscoveryProgressDto? progress)
    {
        var found = _cache.TryGetValue(sessionId, out var value);
        progress = value;
        return found;
    }
}

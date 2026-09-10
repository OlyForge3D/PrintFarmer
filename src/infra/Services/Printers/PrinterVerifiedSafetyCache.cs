using Microsoft.Extensions.Caching.Memory;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Application-wide cache for per-printer verified safety discovery.</summary>
public interface IPrinterVerifiedSafetyCache
{
    /// <summary>Gets a cached discovery result for one printer identity.</summary>
    bool TryGet(
        Guid printerId,
        object identityKey,
        out PrinterVerifiedSafetyDto? safety);

    /// <summary>Gets the current invalidation generation for a printer.</summary>
    long GetGeneration(Guid printerId);

    /// <summary>Caches a discovery result and associates it with the printer.</summary>
    bool Set(
        Guid printerId,
        object identityKey,
        PrinterVerifiedSafetyDto safety,
        TimeSpan duration,
        long expectedGeneration);

    /// <summary>Evicts every cached identity for a printer across request scopes.</summary>
    void Invalidate(Guid printerId);
}

/// <inheritdoc />
public sealed class PrinterVerifiedSafetyCache(
    IMemoryCache memoryCache) : IPrinterVerifiedSafetyCache
{
    private readonly IMemoryCache _memoryCache =
        memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));

    private readonly object _sync = new();
    private readonly Dictionary<Guid, HashSet<object>> _keysByPrinter = [];
    private readonly Dictionary<Guid, long> _generations = [];

    /// <inheritdoc />
    public bool TryGet(
        Guid printerId,
        object identityKey,
        out PrinterVerifiedSafetyDto? safety)
    {
        lock (_sync)
        {
            if (_memoryCache.TryGetValue(identityKey, out safety) &&
                safety is not null)
            {
                return true;
            }

            if (_keysByPrinter.TryGetValue(
                    printerId,
                    out HashSet<object>? keys))
            {
                _ = keys.Remove(identityKey);
            }

            safety = null;
            return false;
        }
    }

    /// <inheritdoc />
    public long GetGeneration(Guid printerId)
    {
        lock (_sync)
        {
            return _generations.GetValueOrDefault(printerId);
        }
    }

    /// <inheritdoc />
    public bool Set(
        Guid printerId,
        object identityKey,
        PrinterVerifiedSafetyDto safety,
        TimeSpan duration,
        long expectedGeneration)
    {
        lock (_sync)
        {
            if (_generations.GetValueOrDefault(printerId) !=
                expectedGeneration)
            {
                return false;
            }

            if (!_keysByPrinter.TryGetValue(
                    printerId,
                    out HashSet<object>? keys))
            {
                keys = [];
                _keysByPrinter.Add(printerId, keys);
            }

            _ = keys.Add(identityKey);
            _memoryCache.Set(identityKey, safety, duration);
            return true;
        }
    }

    /// <inheritdoc />
    public void Invalidate(Guid printerId)
    {
        lock (_sync)
        {
            _generations[printerId] =
                _generations.GetValueOrDefault(printerId) + 1;
            if (!_keysByPrinter.Remove(
                    printerId,
                    out HashSet<object>? keys))
            {
                return;
            }

            foreach (object key in keys)
            {
                _memoryCache.Remove(key);
            }
        }
    }
}

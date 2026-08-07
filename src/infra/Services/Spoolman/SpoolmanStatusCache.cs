using System.Collections.Concurrent;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Provides a shared, short-lived cache for spool data displayed in printer status updates.
/// </summary>
public interface ISpoolmanStatusCache
{
    /// <summary>
    /// Gets a spool for the poll/status path, coalescing concurrent cache misses by spool ID.
    /// </summary>
    Task<SpoolmanSpoolDto?> GetSpoolAsync(
        int spoolId,
        Func<int, CancellationToken, Task<SpoolmanSpoolDto?>> valueFactory,
        CancellationToken ct);
}

/// <summary>
/// Caches printer-status spool lookups without affecting fresh reads used by mutation paths.
/// </summary>
public sealed class SpoolmanStatusCache(IMemoryCache cache, TimeProvider timeProvider) : ISpoolmanStatusCache
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache = cache;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ConcurrentDictionary<int, Lazy<Task<StatusCacheEntry>>> _inflight = new();

    /// <inheritdoc/>
    public async Task<SpoolmanSpoolDto?> GetSpoolAsync(
        int spoolId,
        Func<int, CancellationToken, Task<SpoolmanSpoolDto?>> valueFactory,
        CancellationToken ct)
    {
        string cacheKey = GetCacheKey(spoolId);
        if (_cache.TryGetValue(cacheKey, out StatusCacheEntry? cached) && cached is not null)
        {
            if (cached.ExpiresAtUtc > _timeProvider.GetUtcNow())
            {
                return cached.Spool;
            }

            _cache.Remove(cacheKey);
        }

        var candidate = new Lazy<Task<StatusCacheEntry>>(
            () => FetchAndCacheAsync(cacheKey, spoolId, valueFactory, ct),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Task<StatusCacheEntry>> request = _inflight.GetOrAdd(spoolId, candidate);

        try
        {
            StatusCacheEntry entry = await request.Value.ConfigureAwait(false);
            return entry.Spool;
        }
        finally
        {
            _ = _inflight.TryRemove(spoolId, out _);
        }
    }

    private async Task<StatusCacheEntry> FetchAndCacheAsync(
        string cacheKey,
        int spoolId,
        Func<int, CancellationToken, Task<SpoolmanSpoolDto?>> valueFactory,
        CancellationToken ct)
    {
        SpoolmanSpoolDto? spool = await valueFactory(spoolId, ct).ConfigureAwait(false);
        var entry = new StatusCacheEntry(spool, _timeProvider.GetUtcNow().Add(CacheTtl));
        _ = _cache.Set(cacheKey, entry, CacheTtl);
        return entry;
    }

    private static string GetCacheKey(int spoolId) => $"spoolman:printer-status:{spoolId}";

    private sealed record StatusCacheEntry(SpoolmanSpoolDto? Spool, DateTimeOffset ExpiresAtUtc);
}

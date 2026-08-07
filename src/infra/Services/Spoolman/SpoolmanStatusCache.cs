using System.Collections.Concurrent;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Provides a shared, short-lived cache for spool data displayed in printer status updates.
/// </summary>
public interface ISpoolmanStatusCache
{
    /// <summary>
    /// Gets a spool for the poll/status path, coalescing concurrent cache misses by spool ID.
    /// </summary>
    Task<SpoolmanSpoolDto?> GetSpoolAsync(int spoolId, CancellationToken ct);
}

/// <summary>
/// Caches printer-status spool lookups without affecting fresh reads used by mutation paths.
/// </summary>
public sealed class SpoolmanStatusCache(
    IMemoryCache cache,
    TimeProvider timeProvider,
    IServiceScopeFactory scopeFactory) : ISpoolmanStatusCache
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan UpstreamFetchTimeout = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache = cache;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ConcurrentDictionary<int, Lazy<Task<SpoolmanSpoolDto?>>> _inflight = new();

    /// <inheritdoc/>
    public async Task<SpoolmanSpoolDto?> GetSpoolAsync(int spoolId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (TryGetCachedSpool(spoolId, out SpoolmanSpoolDto? cached))
        {
            return cached;
        }

        Lazy<Task<SpoolmanSpoolDto?>>? candidate = null;
        candidate = new Lazy<Task<SpoolmanSpoolDto?>>(
            () => FetchAndCacheAsync(spoolId, candidate!),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Task<SpoolmanSpoolDto?>> request = _inflight.GetOrAdd(spoolId, candidate);
        return await request.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<SpoolmanSpoolDto?> FetchAndCacheAsync(
        int spoolId,
        Lazy<Task<SpoolmanSpoolDto?>> owner)
    {
        try
        {
            if (TryGetCachedSpool(spoolId, out SpoolmanSpoolDto? cached))
            {
                return cached;
            }

            using IServiceScope scope = _scopeFactory.CreateScope();
            ISpoolmanService spoolmanService = scope.ServiceProvider.GetRequiredService<ISpoolmanService>();
            using var timeout = new CancellationTokenSource(UpstreamFetchTimeout);
            SpoolmanSpoolDto? spool = await spoolmanService
                .GetSpoolByIdAsync(spoolId, timeout.Token)
                .ConfigureAwait(false);
            if (spool is not null)
            {
                var entry = new StatusCacheEntry(spool, _timeProvider.GetUtcNow().Add(CacheTtl));
                _ = _cache.Set(GetCacheKey(spoolId), entry, CacheTtl);
            }

            return spool;
        }
        finally
        {
            RemoveInflightRequest(spoolId, owner);
        }
    }

    private bool TryGetCachedSpool(int spoolId, out SpoolmanSpoolDto? spool)
    {
        string cacheKey = GetCacheKey(spoolId);
        if (_cache.TryGetValue(cacheKey, out StatusCacheEntry? cached) && cached is not null)
        {
            if (cached.ExpiresAtUtc > _timeProvider.GetUtcNow())
            {
                spool = cached.Spool;
                return true;
            }

            _cache.Remove(cacheKey);
        }

        spool = null;
        return false;
    }

    private void RemoveInflightRequest(int spoolId, Lazy<Task<SpoolmanSpoolDto?>> owner)
    {
        if (_inflight.TryGetValue(spoolId, out Lazy<Task<SpoolmanSpoolDto?>>? current)
            && ReferenceEquals(current, owner))
        {
            _ = _inflight.TryRemove(spoolId, out _);
        }
    }

    private static string GetCacheKey(int spoolId) => $"spoolman:printer-status:{spoolId}";

    private sealed record StatusCacheEntry(SpoolmanSpoolDto Spool, DateTimeOffset ExpiresAtUtc);
}

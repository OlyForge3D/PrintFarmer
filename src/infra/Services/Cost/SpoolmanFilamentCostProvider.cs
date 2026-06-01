using Farm.Infrastructure.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cost;

/// <summary>
/// Spoolman-backed implementation of <see cref="IFilamentCostProvider"/>.
/// Fetches spool and filament pricing from the configured Spoolman server and caches
/// results per ID for <see cref="CacheTtl"/> to avoid per-job round-trips.
/// Returns <c>null</c> gracefully when Spoolman is not configured or unreachable.
/// </summary>
public sealed class SpoolmanFilamentCostProvider : IFilamentCostProvider
{
    // 5-minute TTL per the issue acceptance criteria.
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private const string SpoolCacheKeyPrefix = "spoolman_cpg_spool_";
    private const string FilamentCacheKeyPrefix = "spoolman_cpg_filament_";

    private readonly ISpoolmanService _spoolman;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SpoolmanFilamentCostProvider> _logger;

    public SpoolmanFilamentCostProvider(
        ISpoolmanService spoolman,
        IMemoryCache cache,
        ILogger<SpoolmanFilamentCostProvider> logger)
    {
        _spoolman = spoolman ?? throw new ArgumentNullException(nameof(spoolman));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<decimal?> GetSpoolCostPerGramAsync(int spoolId, CancellationToken ct = default)
    {
        string key = SpoolCacheKeyPrefix + spoolId;

        // Null sentinel (bool false) distinguishes "cached as unavailable" from "not cached".
        if (_cache.TryGetValue(key, out decimal? cached))
        {
            return cached;
        }

        decimal? result = await FetchSpoolCostPerGramAsync(spoolId, ct);
        _cache.Set(key, result, CacheTtl);
        return result;
    }

    /// <inheritdoc/>
    public async Task<decimal?> GetFilamentCostPerGramAsync(int filamentId, CancellationToken ct = default)
    {
        string key = FilamentCacheKeyPrefix + filamentId;

        if (_cache.TryGetValue(key, out decimal? cached))
        {
            return cached;
        }

        decimal? result = await FetchFilamentCostPerGramAsync(filamentId, ct);
        _cache.Set(key, result, CacheTtl);
        return result;
    }

    /// <summary>
    /// Fetches spool price data from Spoolman. Price cascade:
    /// spool.Price / spool.InitialWeightG → filament.Price / filament.Weight.
    /// Returns <c>null</c> if data is unavailable.
    /// </summary>
    private async Task<decimal?> FetchSpoolCostPerGramAsync(int spoolId, CancellationToken ct)
    {
        try
        {
            SpoolmanSpoolDto? spool = await _spoolman.GetSpoolByIdAsync(spoolId, ct);
            if (spool is null)
            {
                return null;
            }

            // Level 1: spool-specific price override.
            if (spool.Price is > 0 && spool.InitialWeightG is > 0)
            {
                return ToDecimalCostPerGram(spool.Price.Value, spool.InitialWeightG.Value);
            }

            // Level 2: fall back to filament product price / weight.
            if (spool.FilamentId.HasValue)
            {
                return await FetchFilamentCostPerGramAsync(spool.FilamentId.Value, ct);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch cost per gram for spool {SpoolId} from Spoolman. Returning null.", spoolId);
            return null;
        }
    }

    /// <summary>
    /// Fetches filament product price data from Spoolman.
    /// Returns <c>null</c> if data is unavailable.
    /// </summary>
    private async Task<decimal?> FetchFilamentCostPerGramAsync(int filamentId, CancellationToken ct)
    {
        try
        {
            SpoolmanFilamentDto? filament = await _spoolman.GetFilamentByIdAsync(filamentId, ct);
            if (filament is null)
            {
                return null;
            }

            if (filament.Price is > 0 && filament.Weight is > 0)
            {
                return ToDecimalCostPerGram(filament.Price.Value, filament.Weight.Value);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch cost per gram for filament {FilamentId} from Spoolman. Returning null.", filamentId);
            return null;
        }
    }

    /// <summary>
    /// Converts price-per-spool and weight-in-grams to cost per gram using decimal precision.
    /// </summary>
    private static decimal? ToDecimalCostPerGram(double price, double weightGrams)
    {
        if (weightGrams <= 0)
        {
            return null;
        }

        return (decimal)price / (decimal)weightGrams;
    }
}

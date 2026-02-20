using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Service for fetching filament and material data from the SpoolmanDB community database.
/// Fetches filaments from GitHub Pages (has temp ranges) with Spoolman external fallback.
/// Fetches materials from Spoolman's external endpoint.
/// </summary>
public interface ISpoolmanDbService
{
    /// <summary>Fetches all filaments from SpoolmanDB (cached). Primary: GitHub Pages, fallback: Spoolman external.</summary>
    Task<IReadOnlyList<SpoolmanDbFilamentEntry>> GetFilamentsAsync(CancellationToken ct);

    /// <summary>Fetches all materials from SpoolmanDB via Spoolman's external endpoint (cached).</summary>
    Task<IReadOnlyList<SpoolmanDbMaterialEntry>> GetMaterialsAsync(CancellationToken ct);
}

/// <summary>
/// Fetches SpoolmanDB filament data from GitHub Pages (canonical source with temp ranges),
/// falling back to Spoolman's /api/v1/external/filament when GitHub is unreachable.
/// Materials are fetched from Spoolman's /api/v1/external/material endpoint.
/// Results are cached in-memory for 1 hour.
/// </summary>
public class SpoolmanDbService : ISpoolmanDbService
{
    private const string FilamentsCacheKey = "spoolmandb_filaments";
    private const string MaterialsCacheKey = "spoolmandb_materials";

    /// <summary>
    /// Canonical SpoolmanDB source with complete data including extruder_temp_range and bed_temp_range.
    /// Spoolman's /api/v1/external/filament strips these fields, so we prefer GitHub Pages.
    /// </summary>
    private const string GitHubPagesFilamentsUrl = "https://donkie.github.io/SpoolmanDB/filaments.json";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions SpoolmanDbJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ISpoolmanService _spoolmanService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SpoolmanDbService> _logger;

    public SpoolmanDbService(HttpClient httpClient, ISpoolmanService spoolmanService, IMemoryCache cache, ILogger<SpoolmanDbService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanDbFilamentEntry>> GetFilamentsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(FilamentsCacheKey, out IReadOnlyList<SpoolmanDbFilamentEntry>? cached) && cached != null)
        {
            return cached;
        }

        IReadOnlyList<SpoolmanDbFilamentEntry> result = await FetchFilamentsWithFallbackAsync(ct);
        _cache.Set(FilamentsCacheKey, result, CacheDuration);
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanDbMaterialEntry>> GetMaterialsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(MaterialsCacheKey, out IReadOnlyList<SpoolmanDbMaterialEntry>? cached) && cached != null)
        {
            return cached;
        }

        IReadOnlyList<SpoolmanDbMaterialEntry> result = await _spoolmanService.GetExternalMaterialsAsync(ct);
        _cache.Set(MaterialsCacheKey, result, CacheDuration);
        return result;
    }

    /// <summary>
    /// Fetches filaments from GitHub Pages (includes extruder_temp_range and bed_temp_range).
    /// Falls back to Spoolman's external endpoint if GitHub Pages is unreachable.
    /// </summary>
    private async Task<IReadOnlyList<SpoolmanDbFilamentEntry>> FetchFilamentsWithFallbackAsync(CancellationToken ct)
    {
        // Primary: GitHub Pages (has temp ranges that Spoolman's external endpoint strips)
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            List<SpoolmanDbFilamentEntry>? filaments = await _httpClient.GetFromJsonAsync<List<SpoolmanDbFilamentEntry>>(
                GitHubPagesFilamentsUrl, SpoolmanDbJsonOptions, cts.Token);

            if (filaments is { Count: > 0 })
            {
                _logger.LogDebug("Retrieved {Count} filaments from SpoolmanDB (GitHub Pages)", filaments.Count);
                return filaments.AsReadOnly();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch from GitHub Pages, falling back to Spoolman external endpoint");
        }

        // Fallback: Spoolman's external endpoint (no temp ranges, but at least available)
        try
        {
            IReadOnlyList<SpoolmanDbFilamentEntry> fallback = await _spoolmanService.GetExternalFilamentsAsync(ct);
            _logger.LogDebug("Retrieved {Count} filaments from Spoolman external endpoint (fallback)", fallback.Count);
            return fallback;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Both GitHub Pages and Spoolman external endpoint failed for filament data");
            return [];
        }
    }
}

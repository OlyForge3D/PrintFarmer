using System.Net.Http.Json;
using System.Text.Json;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Host.Services;

/// <summary>
/// HTTP-based catalog adapter that calls the main API to resolve printer models
/// and manufacturer information. Results are cached in-memory to reduce round trips.
/// Failures degrade gracefully by returning empty collections or <c>null</c>.
/// </summary>
public sealed class HttpCatalogServiceAdapter : ICatalogServiceAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HttpCatalogServiceAdapter> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private const string ManufacturersCacheKey = "catalog:manufacturers";
    private const string ManufacturerMapCacheKey = "catalog:manufacturer-map";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Initialises a new instance of <see cref="HttpCatalogServiceAdapter"/>.
    /// </summary>
    /// <param name="httpClientFactory">Factory providing the named <c>MainApi</c> client.</param>
    /// <param name="cache">In-memory cache for catalog lookups.</param>
    /// <param name="logger">Logger instance.</param>
    public HttpCatalogServiceAdapter(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<HttpCatalogServiceAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetManufacturerNamesAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(ManufacturersCacheKey, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using HttpClient http = _httpClientFactory.CreateClient("MainApi");
            List<ManufacturerApiResponse>? manufacturers =
                await http.GetFromJsonAsync<List<ManufacturerApiResponse>>("api/catalog/manufacturers", JsonOptions, ct);

            manufacturers ??= [];

            IReadOnlyList<string> names = manufacturers
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            // Also cache the Id→Name map for use by GetModelByIdAsync
            Dictionary<Guid, string> map = manufacturers
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .ToDictionary(m => m.Id, m => m.Name);
            _cache.Set(ManufacturerMapCacheKey, map, CacheDuration);

            _cache.Set(ManufacturersCacheKey, names, CacheDuration);
            return names;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to fetch manufacturer names from main API");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetPrinterModelNameAsync(Guid printerModelId, CancellationToken ct = default)
    {
        CatalogModelInfo? info = await GetModelByIdAsync(printerModelId, ct);
        return info?.Name;
    }

    /// <inheritdoc />
    public async Task<CatalogModelInfo?> GetModelByIdAsync(Guid modelId, CancellationToken ct = default)
    {
        string cacheKey = $"catalog:model:{modelId}";

        if (_cache.TryGetValue(cacheKey, out CatalogModelInfo? cached))
        {
            return cached;
        }

        try
        {
            using HttpClient http = _httpClientFactory.CreateClient("MainApi");
            using HttpResponseMessage response = await http.GetAsync($"api/catalog/printer-models/{modelId}", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Catalog model lookup for {ModelId} returned {Status}", modelId, response.StatusCode);
                return null;
            }

            PrinterModelApiResponse? dto =
                await response.Content.ReadFromJsonAsync<PrinterModelApiResponse>(JsonOptions, ct);

            if (dto is null)
            {
                return null;
            }

            // Resolve manufacturer name from cached map (populated by GetManufacturerNamesAsync)
            string? manufacturerName = await ResolveManufacturerNameAsync(dto.ManufacturerId, ct);
            var info = new CatalogModelInfo(dto.Id, dto.Name, manufacturerName);
            _cache.Set(cacheKey, info, CacheDuration);
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to resolve catalog model {ModelId} from main API", modelId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlicerModelAliasDto>> GetModelAliasesAsync(Guid modelId, CancellationToken ct = default)
    {
        string cacheKey = $"catalog:aliases:{modelId}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<SlicerModelAliasDto>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using HttpClient http = _httpClientFactory.CreateClient("MainApi");
            List<SlicerModelAliasDto>? aliases =
                await http.GetFromJsonAsync<List<SlicerModelAliasDto>>(
                    $"api/catalog/printer-models/{modelId}/aliases", JsonOptions, ct);

            IReadOnlyList<SlicerModelAliasDto> result = aliases ?? (IReadOnlyList<SlicerModelAliasDto>)[];
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to fetch model aliases for {ModelId} from main API", modelId);
            return [];
        }
    }

    /// <summary>
    /// Minimal projection of the main API's manufacturer response.
    /// </summary>
    private sealed record ManufacturerApiResponse(Guid Id, string Name);

    /// <summary>
    /// Minimal projection of the main API's printer model response.
    /// Maps the <c>PrinterModelDto</c> fields needed for catalog lookups.
    /// The model endpoint returns <c>ManufacturerId</c>, not a name.
    /// </summary>
    private sealed record PrinterModelApiResponse(Guid Id, string Name, Guid ManufacturerId);

    /// <summary>
    /// Resolves a manufacturer name from the cached Id→Name map.
    /// Populates the map by calling <see cref="GetManufacturerNamesAsync"/> if not cached.
    /// </summary>
    private async Task<string?> ResolveManufacturerNameAsync(Guid manufacturerId, CancellationToken ct)
    {
        if (!_cache.TryGetValue(ManufacturerMapCacheKey, out Dictionary<Guid, string>? map) || map is null)
        {
            // Trigger manufacturer fetch to populate the map
            _ = await GetManufacturerNamesAsync(ct);
            _cache.TryGetValue(ManufacturerMapCacheKey, out map);
        }

        return map is not null && map.TryGetValue(manufacturerId, out string? name) ? name : null;
    }
}

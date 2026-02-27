using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.OpenFilamentDb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.OpenFilamentDb;

/// <summary>
/// Fetches and caches filament data from the Open Filament Database static JSON API
/// hosted on GitHub Pages.
/// </summary>
public class OpenFilamentDbService : IOpenFilamentDbService
{
    private const string BaseUrl = "https://openfilamentcollective.github.io/open-filament-database/api/v1/";
    private const string BrandsCacheKey = "ofd_brands";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OpenFilamentDbService> _logger;

    public OpenFilamentDbService(HttpClient httpClient, IMemoryCache cache, ILogger<OpenFilamentDbService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<OfdBrand>> GetBrandsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(BrandsCacheKey, out IReadOnlyList<OfdBrand>? cached) && cached is not null)
        {
            return cached;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        OfdBrandsResponse? response = await _httpClient.GetFromJsonAsync<OfdBrandsResponse>(
            $"{BaseUrl}brands/index.json", JsonOptions, cts.Token);

        ReadOnlyCollection<OfdBrand> brands = response?.Brands?.AsReadOnly() ?? new ReadOnlyCollection<OfdBrand>([]);
        _logger.LogDebug("Retrieved {Count} brands from Open Filament Database", brands.Count);
        _cache.Set(BrandsCacheKey, brands, CacheDuration);
        return brands;
    }

    public async Task<OfdBrandDetailResponse> GetBrandDetailAsync(string brandSlug, CancellationToken ct)
    {
        string cacheKey = $"ofd_brand_{brandSlug}";
        if (_cache.TryGetValue(cacheKey, out OfdBrandDetailResponse? cached) && cached is not null)
        {
            return cached;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        OfdBrandDetailResponse? response = await _httpClient.GetFromJsonAsync<OfdBrandDetailResponse>(
            $"{BaseUrl}brands/{brandSlug}/index.json", JsonOptions, cts.Token);

        OfdBrandDetailResponse result = response ?? new OfdBrandDetailResponse();
        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<IReadOnlyList<OfdFlattenedEntry>> GetFlattenedEntriesAsync(
        string brandSlug, string brandName,
        string materialSlug, string materialName,
        CancellationToken ct)
    {
        string cacheKey = $"ofd_entries_{brandSlug}_{materialSlug}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<OfdFlattenedEntry>? cached) && cached is not null)
        {
            return cached;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        // Fetch material detail to get filament list
        string materialUrl = $"{BaseUrl}brands/{brandSlug}/materials/{materialSlug}/index.json";
        OfdMaterialDetailResponse? materialDetail = await _httpClient.GetFromJsonAsync<OfdMaterialDetailResponse>(
            materialUrl, JsonOptions, cts.Token);

        if (materialDetail?.Filaments is null or { Count: 0 })
        {
            return [];
        }

        List<OfdFlattenedEntry> entries = [];

        foreach (OfdFilamentSummary filSummary in materialDetail.Filaments)
        {
            // Fetch filament detail (has variants and temp ranges)
            string filamentUrl = $"{BaseUrl}brands/{brandSlug}/materials/{materialSlug}/filaments/{filSummary.Slug}/index.json";
            OfdFilamentDetailResponse? filDetail;
            try
            {
                filDetail = await _httpClient.GetFromJsonAsync<OfdFilamentDetailResponse>(
                    filamentUrl, JsonOptions, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch filament {Slug} from OFD", filSummary.Slug);
                continue;
            }

            if (filDetail is null || filDetail.Discontinued)
            {
                continue;
            }

            foreach (OfdVariantSummary varSummary in filDetail.Variants)
            {
                // Fetch full variant with sizes
                string variantUrl = $"{BaseUrl}brands/{brandSlug}/materials/{materialSlug}/filaments/{filSummary.Slug}/variants/{varSummary.Slug}.json";
                OfdVariantDetailResponse? varDetail;
                try
                {
                    varDetail = await _httpClient.GetFromJsonAsync<OfdVariantDetailResponse>(
                        variantUrl, JsonOptions, cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch variant {Slug} from OFD", varSummary.Slug);
                    continue;
                }

                if (varDetail is null || varDetail.Discontinued)
                {
                    continue;
                }

                foreach (OfdSize size in varDetail.Sizes)
                {
                    if (size.Discontinued)
                    {
                        continue;
                    }

                    entries.Add(new OfdFlattenedEntry
                    {
                        EntryId = $"{varDetail.Id}:{size.Id}",
                        BrandName = brandName,
                        FilamentName = filDetail.Name,
                        Material = materialName,
                        ColorName = FormatColorName(varDetail.ColorName),
                        ColorHex = varDetail.ColorHex,
                        Density = filDetail.Density,
                        Diameter = size.Diameter,
                        Weight = size.FilamentWeight,
                        MinPrintTemp = filDetail.MinPrintTemperature,
                        MaxPrintTemp = filDetail.MaxPrintTemperature,
                        MinBedTemp = filDetail.MinBedTemperature,
                        MaxBedTemp = filDetail.MaxBedTemperature,
                        Translucent = varDetail.Traits?.Translucent ?? false,
                        Glow = varDetail.Traits?.Glow ?? false,
                        Matte = varDetail.Traits?.Matte ?? false,
                    });
                }
            }
        }

        ReadOnlyCollection<OfdFlattenedEntry> result = entries.AsReadOnly();
        _cache.Set(cacheKey, result, CacheDuration);
        _logger.LogDebug("Flattened {Count} entries for {Brand}/{Material} from OFD", result.Count, brandSlug, materialSlug);
        return result;
    }

    /// <summary>Converts snake_case color names to Title Case (e.g., "azure_blue" → "Azure Blue").</summary>
    private static string FormatColorName(string name) =>
        string.Join(' ', name.Split('_').Select(w =>
            w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));
}

using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Microsoft.Extensions.Caching.Memory;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for fetching filament and material data from the SpoolmanDB community database.
/// Data is cached to reduce external requests.
/// </summary>
public interface ISpoolmanDbService
{
    /// <summary>Fetches all filaments from SpoolmanDB (cached).</summary>
    Task<IReadOnlyList<SpoolmanDbFilamentEntry>> GetFilamentsAsync(CancellationToken ct);

    /// <summary>Fetches all materials from SpoolmanDB (cached).</summary>
    Task<IReadOnlyList<SpoolmanDbMaterialEntry>> GetMaterialsAsync(CancellationToken ct);
}

/// <summary>
/// Implementation of SpoolmanDB service with HTTP fetch and in-memory caching.
/// </summary>
public class SpoolmanDbService : ISpoolmanDbService
{
    private const string FilamentsUrl = "https://donkie.github.io/SpoolmanDB/filaments.json";
    private const string MaterialsUrl = "https://donkie.github.io/SpoolmanDB/materials.json";
    private const string FilamentsCacheKey = "spoolmandb_filaments";
    private const string MaterialsCacheKey = "spoolmandb_materials";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;

    public SpoolmanDbService(HttpClient http, IMemoryCache cache)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanDbFilamentEntry>> GetFilamentsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(FilamentsCacheKey, out IReadOnlyList<SpoolmanDbFilamentEntry>? cached) && cached != null)
        {
            return cached;
        }

        HttpResponseMessage response = await _http.GetAsync(FilamentsUrl, ct);
        response.EnsureSuccessStatusCode();

        List<SpoolmanDbFilamentEntry>? filaments = await response.Content.ReadFromJsonAsync<List<SpoolmanDbFilamentEntry>>(JsonOptions, ct);
        IReadOnlyList<SpoolmanDbFilamentEntry> result = filaments?.AsReadOnly() ?? (IReadOnlyList<SpoolmanDbFilamentEntry>)Array.Empty<SpoolmanDbFilamentEntry>();

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

        HttpResponseMessage response = await _http.GetAsync(MaterialsUrl, ct);
        response.EnsureSuccessStatusCode();

        List<SpoolmanDbMaterialEntry>? materials = await response.Content.ReadFromJsonAsync<List<SpoolmanDbMaterialEntry>>(JsonOptions, ct);
        IReadOnlyList<SpoolmanDbMaterialEntry> result = materials?.AsReadOnly() ?? (IReadOnlyList<SpoolmanDbMaterialEntry>)Array.Empty<SpoolmanDbMaterialEntry>();

        _cache.Set(MaterialsCacheKey, result, CacheDuration);
        return result;
    }
}

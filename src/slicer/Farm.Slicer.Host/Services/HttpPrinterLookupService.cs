using System.Net.Http.Json;
using System.Text.Json;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Host.Services;

/// <summary>
/// HTTP-based printer lookup that calls the main API to resolve printer details.
/// Results are cached in-memory to reduce round trips. Failures degrade gracefully
/// by returning <c>null</c> / <c>"Unknown"</c> instead of throwing.
/// </summary>
public sealed class HttpPrinterLookupService : IPrinterLookupService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HttpPrinterLookupService> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Initialises a new instance of <see cref="HttpPrinterLookupService"/>.
    /// </summary>
    /// <param name="httpClientFactory">Factory providing the named <c>MainApi</c> client.</param>
    /// <param name="cache">In-memory cache for printer lookups.</param>
    /// <param name="logger">Logger instance.</param>
    public HttpPrinterLookupService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<HttpPrinterLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PrinterInfo?> GetPrinterByIdAsync(Guid printerId, CancellationToken ct = default)
    {
        string cacheKey = $"printer:{printerId}";

        if (_cache.TryGetValue(cacheKey, out PrinterInfo? cached))
        {
            return cached;
        }

        try
        {
            using HttpClient http = _httpClientFactory.CreateClient("MainApi");
            using HttpResponseMessage response = await http.GetAsync($"api/printers/{printerId}", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Printer lookup for {PrinterId} returned {Status}", printerId, response.StatusCode);
                return null;
            }

            PrinterApiResponse? dto = await response.Content.ReadFromJsonAsync<PrinterApiResponse>(JsonOptions, ct);

            if (dto is null)
            {
                return null;
            }

            // Basic PrinterDto does not include ModelId; use null.
            // ModelName is resolved by the main API and included in the response.
            var info = new PrinterInfo(dto.Id, dto.Name, ModelId: null, dto.ModelName);
            _cache.Set(cacheKey, info, CacheDuration);
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to resolve printer {PrinterId} from main API", printerId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string> GetPrinterNameAsync(Guid printerId, CancellationToken ct = default)
    {
        PrinterInfo? info = await GetPrinterByIdAsync(printerId, ct);
        return info?.Name ?? "Unknown";
    }

    /// <summary>
    /// Minimal projection of the main API's <c>PrinterDto</c> response.
    /// Only the fields needed for cross-domain resolution are included.
    /// The basic GET /api/printers/{id} endpoint returns Name and ModelName
    /// but not ModelId. Use the /details endpoint if ModelId is needed.
    /// </summary>
    private sealed record PrinterApiResponse(
        Guid Id,
        string Name,
        string? ModelName);
}

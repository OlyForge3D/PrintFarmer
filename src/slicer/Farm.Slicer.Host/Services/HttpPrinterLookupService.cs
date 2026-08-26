using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Host.Services;

/// <summary>
/// HTTP-based printer lookup that calls the main API to resolve printer details.
/// Results are cached in-memory to reduce round trips. Transport failures degrade gracefully
/// by returning <c>null</c> / <c>"Unknown"</c>; service authentication failures remain explicit.
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
            using HttpResponseMessage response =
                await http.GetAsync(SlicerHostLookupContract.PrinterPath(printerId), ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Printer lookup for {PrinterId} returned {Status}", printerId, response.StatusCode);
                return null;
            }

            SlicerHostPrinterLookupDto? dto =
                await response.Content.ReadFromJsonAsync<SlicerHostPrinterLookupDto>(
                    JsonOptions,
                    ct);

            if (dto is null)
            {
                return null;
            }

            var info = new PrinterInfo(dto.Id, dto.Name, dto.ModelId, dto.ModelName);
            _cache.Set(cacheKey, info, CacheDuration);
            return info;
        }
        catch (Exception ex) when (IsRecoverableFailure(ex))
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

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is TaskCanceledException or JsonException
        || (exception is HttpRequestException httpRequestException
            && !MainApiResponseGuard.IsAuthenticationFailure(httpRequestException));
}

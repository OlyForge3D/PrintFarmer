using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Parsing;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Spoolman;

public class SpoolmanService(HttpClient http, ISettingsService settingsService, ILogger<SpoolmanService> logger) : ISpoolmanService
{
    /// <summary>
    /// Density applied when a caller creates a filament without one. Spoolman requires
    /// <c>density</c> (it has no default) and rejects the payload with HTTP 422 otherwise.
    /// Matches the fallback already used by the SpoolmanDB import in FilamentTypeService.
    /// </summary>
    internal const double DefaultFilamentDensity = 1.24d;

    /// <summary>
    /// Diameter applied when a caller creates a filament without one. Spoolman requires
    /// <c>diameter</c> (it has no default) and rejects the payload with HTTP 422 otherwise.
    /// </summary>
    internal const double DefaultFilamentDiameter = 1.75d;

    private readonly HttpClient http = http;
    private readonly ISettingsService settingsService = settingsService;
    private readonly ILogger<SpoolmanService> logger = logger;

    public SpoolmanConfigDto? GetConfig()
    {
        SpoolmanSettings? settings = settingsService.Get<SpoolmanSettings>();
        return settings is null || string.IsNullOrWhiteSpace(settings.BaseUrl) ? null : new SpoolmanConfigDto(settings.BaseUrl);
    }

    public async Task<SpoolmanProbeResult> ProbeAsync(string candidateBaseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidateBaseUrl))
        {
            return new SpoolmanProbeResult(false, Message: "BaseUrl is required");
        }

        string raw = candidateBaseUrl.Trim();
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? baseUri))
        {
            return new SpoolmanProbeResult(false, Message: "Invalid URL");
        }

        // Use provided URL (normalization is lightweight here)
        string normalized = UrlNormalizer.NormalizeBaseUrl(raw);
        string[] probePaths = new[] { "/api/v1/health", "/api/v1/info" };

        foreach (string path in probePaths)
        {
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Get, normalized + path);
                using HttpResponseMessage resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    string? version = null;
                    try
                    {
                        using Stream stream = await resp.Content.ReadAsStreamAsync(ct);
                        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                        JsonElement root = doc.RootElement;
                        if (root.TryGetProperty("version", out JsonElement vProp) && vProp.ValueKind == JsonValueKind.String)
                        {
                            version = vProp.GetString();
                        }
                        else if (root.TryGetProperty("spoolman_version", out JsonElement svProp) && svProp.ValueKind == JsonValueKind.String)
                        {
                            version = svProp.GetString();
                        }
                    }
                    catch
                    {
                    }

                    return new SpoolmanProbeResult(true, NormalizedUrl: normalized, EndpointTried: path, StatusCode: (int)resp.StatusCode, Version: version);
                }
            }
            catch (Exception ex)
            {
                if (path == probePaths[^1])
                {
                    (string? Category, string? Message) = CategorizeException(ex);
                    logger.LogError(ex, "Probe failed for {Url}", LogSanitizer.Sanitize(candidateBaseUrl));
                    return new SpoolmanProbeResult(false, NormalizedUrl: normalized, EndpointTried: path, StatusCode: null, Version: null, Message: Message, ErrorCategory: Category);
                }
            }
        }

        return new SpoolmanProbeResult(false, NormalizedUrl: normalized, Message: "Probe endpoints failed");
    }

    public async Task<SpoolmanProbeResult> HealthProbeAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return new SpoolmanProbeResult(false, Message: "Spoolman not configured");
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');
        string[] probePaths = new[] { "/api/v1/health", "/api/v1/info" };
        foreach (string p in probePaths)
        {
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Get, baseUrl + p);
                using HttpResponseMessage resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    return new SpoolmanProbeResult(true, NormalizedUrl: baseUrl, EndpointTried: p, StatusCode: (int)resp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                if (p == probePaths[^1])
                {
                    logger.LogError(ex, "Health probe failed for configured Spoolman");
                    return new SpoolmanProbeResult(false, NormalizedUrl: baseUrl, EndpointTried: p, StatusCode: null, Version: null, Message: ex.Message);
                }
            }
        }

        return new SpoolmanProbeResult(false, NormalizedUrl: baseUrl, Message: "Probe endpoints failed");
    }

    private static (string? Category, string? Message) CategorizeException(Exception ex)
    {
        if (ex is TaskCanceledException or OperationCanceledException)
        {
            return ("timeout", "Connection timed out");
        }

        if (ex is HttpRequestException hre)
        {
            if (hre.InnerException is System.Net.Sockets.SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound => ("dns_failure", "Host could not be resolved"),
                    System.Net.Sockets.SocketError.ConnectionRefused => ("connection_refused", "Connection refused"),
                    System.Net.Sockets.SocketError.TimedOut => ("timeout", "Connection timed out"),
                    _ => ("network_error", hre.Message)
                };
            }

            return ("http_error", hre.Message);
        }

        if (ex is System.Security.Authentication.AuthenticationException)
        {
            return ("tls_error", "TLS/SSL negotiation failed");
        }

        return ("unknown", ex.Message);
    }

    public void SetConfig(SpoolmanConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string? baseUrl = UrlNormalizer.NormalizeBaseUrlNullable(config.BaseUrl);

        SpoolmanSettings settings = settingsService.Get<SpoolmanSettings>() ?? new SpoolmanSettings();
        settings.BaseUrl = baseUrl ?? string.Empty;
        settingsService.Save(settings);
    }

    public void ClearConfig()
    {
        SpoolmanSettings settings = settingsService.Get<SpoolmanSettings>() ?? new SpoolmanSettings();
        settings.BaseUrl = string.Empty;
        settingsService.Save(settings);
    }

    public async Task<SpoolmanPagedResult<SpoolmanSpoolDto>> ListSpoolsAsync(SpoolmanSpoolQueryParams queryParams, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queryParams);

        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug("Spoolman not configured – returning empty spool list");
            return new SpoolmanPagedResult<SpoolmanSpoolDto>([], 0);
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');
        string url = BuildSpoolQueryUrl(baseUrl, queryParams);

        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Spoolman spool listing returned {StatusCode}", resp.StatusCode);
                return new SpoolmanPagedResult<SpoolmanSpoolDto>([], 0);
            }

            // Read total count from Spoolman's X-Total-Count header
            int totalCount = 0;
            if (resp.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? values))
            {
                string? headerValue = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(headerValue))
                {
                    _ = int.TryParse(headerValue, CultureInfo.InvariantCulture, out totalCount);
                }
            }

            using JsonDocument? doc = await TryParseJsonAsync(resp.Content, ct);
            if (doc is null)
            {
                logger.LogWarning("Spoolman spool listing returned invalid JSON");
                return new SpoolmanPagedResult<SpoolmanSpoolDto>([], 0);
            }

            List<SpoolmanSpoolDto> items = new();
            foreach (JsonElement item in SpoolmanJsonParser.EnumerateItems(doc.RootElement))
            {
                ct.ThrowIfCancellationRequested();
                items.Add(SpoolmanJsonParser.ParseSpool(item));
            }

            // If Spoolman didn't return an X-Total-Count header, use offset + item count as a
            // lower-bound so callers know there are at least that many items total, preventing
            // premature pagination cutoff on non-zero-offset pages.
            if (totalCount == 0 && items.Count > 0)
            {
                totalCount = Math.Max(totalCount, (queryParams.Offset ?? 0) + items.Count);
            }

            logger.LogDebug("Retrieved {Count} spools (total {TotalCount})", items.Count, totalCount);
            return new SpoolmanPagedResult<SpoolmanSpoolDto>(items, totalCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch spools from Spoolman");
            return new SpoolmanPagedResult<SpoolmanSpoolDto>([], 0);
        }
    }

    /// <summary>
    /// Builds the Spoolman /api/v1/spool URL with query parameters for server-side pagination, filtering, and sorting.
    /// </summary>
    internal static string BuildSpoolQueryUrl(string baseUrl, SpoolmanSpoolQueryParams queryParams)
    {
        List<string> parts = new();

        if (queryParams.Limit.HasValue)
        {
            parts.Add($"limit={queryParams.Limit.Value}");
        }

        if (queryParams.Offset.HasValue && queryParams.Offset.Value > 0)
        {
            parts.Add($"offset={queryParams.Offset.Value}");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Sort))
        {
            parts.Add($"sort={Uri.EscapeDataString(queryParams.Sort)}");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Search))
        {
            parts.Add($"filament.name={Uri.EscapeDataString(queryParams.Search)}");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Material))
        {
            parts.Add($"filament.material={Uri.EscapeDataString(queryParams.Material)}");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Vendor))
        {
            parts.Add($"filament.vendor.name={Uri.EscapeDataString(queryParams.Vendor)}");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Location))
        {
            parts.Add($"location={Uri.EscapeDataString(queryParams.Location)}");
        }

        if (queryParams.AllowArchived == true)
        {
            parts.Add("allow_archived=true");
        }

        string url = $"{baseUrl}/api/v1/spool";
        return parts.Count > 0 ? $"{url}?{string.Join('&', parts)}" : url;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanFilamentDto>> ListFilamentsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug($"Spoolman not configured – returning empty filament list");
            return [];
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/api/v1/filament";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            HttpResponseMessage response = await http.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Spoolman filament list returned status {StatusCode}", (int)response.StatusCode);
                return [];
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning($"Spoolman filament endpoint did not return an array");
                return [];
            }

            var filaments = new List<SpoolmanFilamentDto>();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                filaments.Add(SpoolmanJsonParser.ParseFilament(el));
            }

            logger.LogDebug("Retrieved {FilamentsCount} filament types", filaments.Count);
            return filaments;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to fetch filaments from Spoolman");
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<SpoolmanPagedResult<SpoolmanFilamentDto>> ListFilamentsPagedAsync(SpoolmanFilamentQueryParams queryParams, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queryParams);

        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug("Spoolman not configured – returning empty filament list");
            return new SpoolmanPagedResult<SpoolmanFilamentDto>([], 0);
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');
        string url = BuildFilamentQueryUrl(baseUrl, queryParams);

        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Spoolman filament listing returned {StatusCode}", resp.StatusCode);
                return new SpoolmanPagedResult<SpoolmanFilamentDto>([], 0);
            }

            // Read total count from Spoolman's X-Total-Count header
            int totalCount = 0;
            if (resp.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? values))
            {
                string? headerValue = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(headerValue))
                {
                    _ = int.TryParse(headerValue, CultureInfo.InvariantCulture, out totalCount);
                }
            }

            using JsonDocument? doc = await TryParseJsonAsync(resp.Content, ct);
            if (doc is null)
            {
                logger.LogWarning("Spoolman filament listing returned invalid JSON");
                return new SpoolmanPagedResult<SpoolmanFilamentDto>([], 0);
            }

            List<SpoolmanFilamentDto> items = new();
            foreach (JsonElement item in SpoolmanJsonParser.EnumerateItems(doc.RootElement))
            {
                ct.ThrowIfCancellationRequested();
                items.Add(SpoolmanJsonParser.ParseFilament(item));
            }

            // If Spoolman didn't return an X-Total-Count header, use offset + item count as a
            // lower-bound so callers know there are at least that many items total, preventing
            // premature pagination cutoff on non-zero-offset pages.
            if (totalCount == 0 && items.Count > 0)
            {
                totalCount = Math.Max(totalCount, (queryParams.Offset ?? 0) + items.Count);
            }

            logger.LogDebug("Retrieved {Count} filaments (total {TotalCount})", items.Count, totalCount);
            return new SpoolmanPagedResult<SpoolmanFilamentDto>(items, totalCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch filaments from Spoolman");
            return new SpoolmanPagedResult<SpoolmanFilamentDto>([], 0);
        }
    }

    /// <inheritdoc/>
    public async Task<SpoolmanFilamentDto?> GetFilamentByBarcodeAsync(string barcode, CancellationToken ct)
    {
        string trimmedBarcode = barcode.Trim();
        if (string.IsNullOrWhiteSpace(trimmedBarcode))
        {
            return null;
        }

        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return null;
        }

        const int pageSize = 500;
        string baseUrl = cfg.BaseUrl.TrimEnd('/');

        // Resolution is by `gtin` only. `article_number` holds vendor SKUs, so matching a
        // scanned barcode against it would be a category error (and would reintroduce the
        // raw exact-match semantics that GTIN-14 normalization exists to eliminate).
        string? normalizedGtin = GtinNormalizer.Normalize(trimmedBarcode);
        if (normalizedGtin is null)
        {
            return null;
        }

        List<SpoolmanFilamentDto> matches = await CollectBarcodeMatchesAsync(
            baseUrl,
            pageSize,
            gtinFilter: normalizedGtin,
            isMatch: filament => string.Equals(GtinNormalizer.Normalize(filament.Gtin), normalizedGtin, StringComparison.Ordinal),
            debugFilterName: "gtin",
            ct);

        if (matches.Count == 0)
        {
            // The server-side `gtin=` filter is an exact string match, so it only finds
            // records whose stored value is already normalized to 14 digits. A filament
            // whose `gtin` was populated outside PrintFarmer's own write path (e.g. directly
            // in Spoolman, or via a future import) may still store the equivalent UPC-12 or
            // EAN-13 form. Spoolman answering with zero rows for that filter is NOT the same
            // as the filter request failing, so the earlier "page is null" fallback never
            // triggers for this case -- fall back to an unfiltered full scan comparing
            // normalized GTIN values so the UPC-12 <-> EAN-13 equivalence acceptance
            // criterion holds regardless of how the stored value was formatted.
            matches = await CollectBarcodeMatchesAsync(
                baseUrl,
                pageSize,
                gtinFilter: null,
                isMatch: filament => string.Equals(GtinNormalizer.Normalize(filament.Gtin), normalizedGtin, StringComparison.Ordinal),
                debugFilterName: "gtin (full scan)",
                ct);
        }

        if (matches.Count == 0)
        {
            return null;
        }

        SpoolmanFilamentDto first = matches.OrderBy(f => f.Id).First();
        if (matches.Count > 1)
        {
            logger.LogWarning(
                "Barcode {Barcode} matched {Count} Spoolman filaments; returning filament {FilamentId}.",
                LogSanitizer.Sanitize(trimmedBarcode),
                matches.Count,
                first.Id);
        }

        return first;
    }

    /// <summary>
    /// Pages through Spoolman filaments, applying a server-side filter when possible (falling
    /// back to a full scan if the server rejects the filter query param), and collects the
    /// filaments satisfying <paramref name="isMatch"/>.
    /// </summary>
    private async Task<List<SpoolmanFilamentDto>> CollectBarcodeMatchesAsync(
        string baseUrl,
        int pageSize,
        string? gtinFilter,
        Func<SpoolmanFilamentDto, bool> isMatch,
        string debugFilterName,
        CancellationToken ct)
    {
        var matches = new List<SpoolmanFilamentDto>();

        // Only meaningful when a filter is actually applied: the retry below exists to recover
        // from a server that rejects the filter query param. With no filter there is nothing to
        // drop, so retrying would reissue an identical request against an already-failing page.
        bool useFilter = gtinFilter is not null;
        int offset = 0;

        while (true)
        {
            SpoolmanPagedResult<SpoolmanFilamentDto>? page = await FetchBarcodeFilamentPageAsync(
                baseUrl,
                useFilter ? gtinFilter : null,
                pageSize,
                offset,
                ct);

            if (page is null)
            {
                if (useFilter)
                {
                    logger.LogDebug("Spoolman {Filter} filament filter failed; falling back to full filament scan.", debugFilterName);
                    useFilter = false;
                    offset = 0;
                    matches.Clear();
                    continue;
                }

                return matches;
            }

            foreach (SpoolmanFilamentDto filament in page.Items.Where(isMatch))
            {
                matches.Add(filament);
            }

            if (page.Items.Count == 0 || page.Items.Count < pageSize)
            {
                break;
            }

            if (page.TotalCount > 0 && offset + page.Items.Count >= page.TotalCount)
            {
                break;
            }

            offset += pageSize;
        }

        return matches;
    }

    /// <summary>
    /// Builds the Spoolman /api/v1/filament URL with query parameters for server-side pagination, filtering, and sorting.
    /// </summary>
    internal static string BuildFilamentQueryUrl(string baseUrl, SpoolmanFilamentQueryParams queryParams)
    {
        List<string> parts = new();

        if (queryParams.Limit.HasValue)
        {
            parts.Add($"limit={queryParams.Limit.Value}");
        }

        if (queryParams.Offset.HasValue && queryParams.Offset.Value > 0)
        {
            parts.Add($"offset={queryParams.Offset.Value}");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Sort))
        {
            parts.Add($"sort={Uri.EscapeDataString(queryParams.Sort)}");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Search))
        {
            parts.Add($"name={Uri.EscapeDataString(queryParams.Search)}");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Material))
        {
            parts.Add($"material={Uri.EscapeDataString(queryParams.Material)}");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Vendor))
        {
            parts.Add($"vendor.name={Uri.EscapeDataString(queryParams.Vendor)}");
        }

        string url = $"{baseUrl}/api/v1/filament";
        return parts.Count > 0 ? $"{url}?{string.Join('&', parts)}" : url;
    }

    private async Task<SpoolmanPagedResult<SpoolmanFilamentDto>?> FetchBarcodeFilamentPageAsync(
        string baseUrl,
        string? gtin,
        int limit,
        int offset,
        CancellationToken ct)
    {
        List<string> parts =
        [
            $"limit={limit}",
            $"offset={offset}",
        ];

        if (!string.IsNullOrWhiteSpace(gtin))
        {
            parts.Add($"gtin={Uri.EscapeDataString(gtin)}");
        }

        string url = $"{baseUrl}/api/v1/filament?{string.Join('&', parts)}";

        using HttpRequestMessage req = new(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage resp = await http.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Spoolman barcode filament lookup returned {StatusCode}", resp.StatusCode);
            return null;
        }

        int totalCount = 0;
        if (resp.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? values))
        {
            string? headerValue = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(headerValue))
            {
                _ = int.TryParse(headerValue, CultureInfo.InvariantCulture, out totalCount);
            }
        }

        using JsonDocument? doc = await TryParseJsonAsync(resp.Content, ct);
        if (doc is null)
        {
            logger.LogWarning("Spoolman barcode filament lookup returned invalid JSON");
            return null;
        }

        List<SpoolmanFilamentDto> items = new();
        foreach (JsonElement item in SpoolmanJsonParser.EnumerateItems(doc.RootElement))
        {
            ct.ThrowIfCancellationRequested();
            items.Add(SpoolmanJsonParser.ParseFilament(item));
        }

        return new SpoolmanPagedResult<SpoolmanFilamentDto>(items, totalCount);
    }

    /// <inheritdoc/>
    public async Task<SpoolmanFilamentDto?> GetFilamentByIdAsync(int filamentId, CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return null;
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}/api/v1/filament/{filamentId}";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            HttpResponseMessage response = await http.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Spoolman filament {FilamentId} returned status {StatusCode}", filamentId, (int)response.StatusCode);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token);
            using JsonDocument doc = JsonDocument.Parse(json);
            return SpoolmanJsonParser.ParseFilament(doc.RootElement);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception fetching Spoolman filament {FilamentId}", filamentId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanVendorDto>> ListVendorsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return [];
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}/api/v1/vendor";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            HttpResponseMessage response = await http.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Spoolman vendor list returned status {StatusCode}", (int)response.StatusCode);
                return [];
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token);
            using JsonDocument doc = JsonDocument.Parse(json);
            var vendors = new List<SpoolmanVendorDto>();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                int id = SpoolmanJsonParser.TryGetInt(el, "id");
                string name = SpoolmanJsonParser.TryGetString(el, "name") ?? string.Empty;
                string? externalId = SpoolmanJsonParser.TryGetString(el, "external_id");
                vendors.Add(new SpoolmanVendorDto(id, name, externalId));
            }

            return vendors;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception fetching Spoolman vendors");
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<SpoolmanVendorDto> CreateVendorAsync(string name, string? externalId, CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            throw new InvalidOperationException("Spoolman is not configured.");
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}/api/v1/vendor";
        var body = new Dictionary<string, object?> { ["name"] = name };
        if (!string.IsNullOrWhiteSpace(externalId))
        {
            body["external_id"] = externalId;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        string jsonBody = JsonSerializer.Serialize(body);
        using var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, content, cts.Token);
        await EnsureSpoolmanSuccessAsync(response, "create a vendor", cts.Token);

        string json = await response.Content.ReadAsStringAsync(cts.Token);
        using JsonDocument doc = JsonDocument.Parse(json);
        int id = SpoolmanJsonParser.TryGetInt(doc.RootElement, "id");
        string vendorName = SpoolmanJsonParser.TryGetString(doc.RootElement, "name") ?? name;
        string? extId = SpoolmanJsonParser.TryGetString(doc.RootElement, "external_id");
        return new SpoolmanVendorDto(id, vendorName, extId);
    }

    /// <inheritdoc/>
    public async Task<SpoolmanFilamentDto> CreateFilamentInSpoolmanAsync(SpoolmanCreateFilamentRequest request, CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            throw new InvalidOperationException("Spoolman is not configured.");
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}/api/v1/filament";

        // Spoolman requires density and diameter on create; both are omitted from the JSON
        // when null, which makes Spoolman reject the whole payload with HTTP 422. Backfill
        // sane defaults so clients that don't collect these fields still succeed.
        SpoolmanCreateFilamentRequest normalized = NormalizeGtin(request with
        {
            Density = request.Density is > 0 ? request.Density : DefaultFilamentDensity,
            Diameter = request.Diameter is > 0 ? request.Diameter : DefaultFilamentDiameter,
        });
        string jsonBody = BuildFilamentJson(normalized);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, content, cts.Token);
        await EnsureSpoolmanSuccessAsync(response, "create a filament", cts.Token);

        string json = await response.Content.ReadAsStringAsync(cts.Token);
        using JsonDocument doc = JsonDocument.Parse(json);
        return SpoolmanJsonParser.ParseFilament(doc.RootElement);
    }

    /// <inheritdoc/>
    public async Task<SpoolmanFilamentDto> UpdateFilamentInSpoolmanAsync(int filamentId, SpoolmanCreateFilamentRequest request, CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            throw new InvalidOperationException("Spoolman is not configured.");
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}/api/v1/filament/{filamentId}";
        string jsonBody = BuildFilamentJson(NormalizeGtin(request));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };
        HttpResponseMessage response = await http.SendAsync(httpRequest, cts.Token);
        await EnsureSpoolmanSuccessAsync(response, $"update filament {filamentId}", cts.Token);

        string json = await response.Content.ReadAsStringAsync(cts.Token);
        using JsonDocument doc = JsonDocument.Parse(json);
        return SpoolmanJsonParser.ParseFilament(doc.RootElement);
    }

    /// <inheritdoc/>
    public async Task<SpoolmanFilamentDto?> SaveBarcodeMappingAsync(int filamentId, string barcode, CancellationToken ct)
    {
        string trimmedBarcode = barcode.Trim();
        if (filamentId <= 0 || string.IsNullOrWhiteSpace(trimmedBarcode))
        {
            return null;
        }

        string? normalizedGtin = GtinNormalizer.Normalize(trimmedBarcode);
        if (normalizedGtin is null)
        {
            logger.LogWarning(
                "Rejected barcode {Barcode} for filament {FilamentId}: not a valid GTIN-8/12/13/14 (bad length or check digit).",
                LogSanitizer.Sanitize(trimmedBarcode),
                filamentId);
            return null;
        }

        SpoolmanFilamentDto? target = await GetFilamentByIdAsync(filamentId, ct);
        if (target is null)
        {
            return null;
        }

        SpoolmanFilamentDto? existing = await GetFilamentByBarcodeAsync(trimmedBarcode, ct);
        if (existing is not null && existing.Id != filamentId)
        {
            logger.LogWarning(
                "Barcode {Barcode} is already assigned to Spoolman filament {ExistingFilamentId}; also assigning it to filament {FilamentId}.",
                LogSanitizer.Sanitize(trimmedBarcode),
                existing.Id,
                filamentId);
        }

        return await UpdateFilamentInSpoolmanAsync(
            filamentId,
            new SpoolmanCreateFilamentRequest { Gtin = normalizedGtin },
            ct);
    }

    /// <inheritdoc/>
    public async Task<SpoolmanSpoolDto?> CreateSpoolByBarcodeAsync(SpoolmanImportSpoolByBarcodeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Barcode))
        {
            return null;
        }

        SpoolmanFilamentDto? filament = await GetFilamentByBarcodeAsync(request.Barcode.Trim(), ct);
        if (filament is null)
        {
            return null;
        }

        SpoolmanSpoolRequest spoolRequest = new()
        {
            FilamentId = filament.Id,
            RemainingWeight = request.RemainingWeight,
            InitialWeight = request.InitialWeight,
            SpoolWeight = request.SpoolWeight,
            Location = request.Location,
            LotNumber = request.LotNumber,
            Price = request.Price,
            Comment = request.Comment,
        };

        return await CreateSpoolInSpoolmanAsync(spoolRequest, ct);
    }

    /// <inheritdoc/>
    public async Task<SpoolmanBulkUpdateResult> BulkUpdateFilamentsAsync(SpoolmanBulkUpdateFilamentsRequest request, CancellationToken ct)
    {
        if (request.FilamentIds is not { Length: > 0 })
        {
            return new SpoolmanBulkUpdateResult(0, 0, []);
        }

        // Build a partial update request with only the fields the caller wants to change
        var patch = new SpoolmanCreateFilamentRequest
        {
            VendorId = request.VendorId,
            Material = request.Material,
            Price = request.Price,
            SettingsExtruderTemp = request.SettingsExtruderTemp,
            SettingsBedTemp = request.SettingsBedTemp,
            Comment = request.Comment,
        };

        int updated = 0;
        int errorCount = 0;
        List<string> errors = [];

        foreach (int id in request.FilamentIds)
        {
            try
            {
                await UpdateFilamentInSpoolmanAsync(id, patch, ct);
                updated++;
            }
            catch (Exception ex)
            {
                errors.Add($"Filament {id}: {ex.Message}");
                errorCount++;
            }
        }

        return new SpoolmanBulkUpdateResult(updated, errorCount, [.. errors]);
    }

    /// <inheritdoc/>
    public async Task DeleteFilamentFromSpoolmanAsync(int filamentId, CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            throw new InvalidOperationException("Spoolman is not configured.");
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}/api/v1/filament/{filamentId}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, url);
        HttpResponseMessage response = await http.SendAsync(httpRequest, cts.Token);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public async Task<SpoolmanBulkUpdateResult> BulkDeleteFilamentsAsync(int[] filamentIds, CancellationToken ct)
    {
        if (filamentIds is not { Length: > 0 })
        {
            return new SpoolmanBulkUpdateResult(0, 0, []);
        }

        int deleted = 0;
        int errorCount = 0;
        List<string> errors = [];

        foreach (int id in filamentIds)
        {
            try
            {
                await DeleteFilamentFromSpoolmanAsync(id, ct);
                deleted++;
            }
            catch (Exception ex)
            {
                errors.Add($"Filament {id}: {ex.Message}");
                errorCount++;
            }
        }

        return new SpoolmanBulkUpdateResult(deleted, errorCount, [.. errors]);
    }

    /// <summary>
    /// Throws a <see cref="SpoolmanApiException"/> carrying Spoolman's own error detail when the
    /// response is not successful. Preferred over <c>EnsureSuccessStatusCode()</c>, whose message
    /// ("Response status code does not indicate success: 422 (Unprocessable Entity).") tells the
    /// user nothing about which field Spoolman rejected.
    /// </summary>
    private static async Task EnsureSpoolmanSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            body = string.Empty;
        }

        throw new SpoolmanApiException(response.StatusCode, operation, ParseSpoolmanErrorDetail(body));
    }

    /// <summary>
    /// Extracts a readable message from a Spoolman error body. FastAPI validation failures use
    /// <c>{"detail":[{"loc":["body","density"],"msg":"Field required"}]}</c>; Spoolman's own
    /// handlers use <c>{"detail":"..."}</c> or <c>{"message":"..."}</c>.
    /// </summary>
    private static string? ParseSpoolmanErrorDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Truncate(body);
            }

            if (root.TryGetProperty("detail", out JsonElement detail))
            {
                switch (detail.ValueKind)
                {
                    case JsonValueKind.String:
                        return Truncate(detail.GetString());
                    case JsonValueKind.Array:
                        List<string> parts = [];
                        foreach (JsonElement item in detail.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            string? msg = item.TryGetProperty("msg", out JsonElement msgElement) ? msgElement.GetString() : null;
                            string? field = null;
                            if (item.TryGetProperty("loc", out JsonElement loc) && loc.ValueKind == JsonValueKind.Array)
                            {
                                field = string.Join(
                                    '.',
                                    loc.EnumerateArray()
                                        .Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : l.ToString())
                                        .Where(l => !string.IsNullOrEmpty(l)));
                            }

                            if (!string.IsNullOrWhiteSpace(msg))
                            {
                                parts.Add(string.IsNullOrWhiteSpace(field) ? msg : $"{field}: {msg}");
                            }
                        }

                        return parts.Count > 0 ? Truncate(string.Join("; ", parts)) : Truncate(body);
                    default:
                        break;
                }
            }

            return root.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String
                ? Truncate(message.GetString())
                : Truncate(body);
        }
        catch (JsonException)
        {
            return Truncate(body);
        }
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static SpoolmanCreateFilamentRequest NormalizeGtin(SpoolmanCreateFilamentRequest request)
    {
        if (request.Gtin is null)
        {
            return request;
        }

        string normalizedGtin = GtinNormalizer.Normalize(request.Gtin)
            ?? throw new ArgumentException(
                "GTIN is not a valid GTIN-8/12/13/14 (bad length or check digit).",
                nameof(request));

        return request with { Gtin = normalizedGtin };
    }

    private static string BuildFilamentJson(SpoolmanCreateFilamentRequest request)
    {
        var body = new Dictionary<string, object?>();

        if (request.Density.HasValue)
        {
            body["density"] = request.Density.Value;
        }

        if (request.Diameter.HasValue)
        {
            body["diameter"] = request.Diameter.Value;
        }

        if (request.Name != null)
        {
            body["name"] = request.Name;
        }

        if (request.VendorId.HasValue)
        {
            body["vendor_id"] = request.VendorId.Value;
        }

        if (request.Material != null)
        {
            body["material"] = request.Material;
        }

        if (request.Weight.HasValue)
        {
            body["weight"] = request.Weight.Value;
        }

        if (request.SpoolWeight.HasValue)
        {
            body["spool_weight"] = request.SpoolWeight.Value;
        }

        if (request.SettingsExtruderTemp.HasValue)
        {
            body["settings_extruder_temp"] = request.SettingsExtruderTemp.Value;
        }

        if (request.SettingsBedTemp.HasValue)
        {
            body["settings_bed_temp"] = request.SettingsBedTemp.Value;
        }

        if (request.ColorHex != null)
        {
            body["color_hex"] = request.ColorHex;
        }

        if (request.ExternalId != null)
        {
            body["external_id"] = request.ExternalId;
        }

        if (request.Comment != null)
        {
            body["comment"] = request.Comment;
        }

        if (request.Price.HasValue)
        {
            body["price"] = request.Price.Value;
        }

        if (request.ArticleNumber != null)
        {
            body["article_number"] = request.ArticleNumber;
        }

        if (request.Gtin != null)
        {
            body["gtin"] = request.Gtin;
        }

        if (request.MultiColorHexes != null)
        {
            body["multi_color_hexes"] = request.MultiColorHexes;
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// Gets all material types directly from Spoolman's /api/v1/material endpoint.
    /// </summary>
    public async Task<IReadOnlyList<SpoolmanMaterialDto>> ListMaterialsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug($"Spoolman not configured – returning empty material list");
            return [];
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/api/v1/material";

        try
        {
            MaterialPageFetchResult result = await FetchAllMaterialPagesAsync(url, ct);
            if (result.AttemptedPages > 1)
            {
                logger.LogInformation("Retrieved {Count} materials across {AttemptedPages} pages", result.Items.Count, result.AttemptedPages);
            }
            else
            {
                logger.LogDebug("Retrieved {Count} materials", result.Items.Count);
            }

            return result.Items;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to fetch materials from Spoolman");
            return [];
        }
    }

    private sealed record MaterialPageFetchResult(List<SpoolmanMaterialDto> Items, bool Success, int AttemptedPages, HttpStatusCode? LastStatusCode);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAvailableMaterialsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug("Spoolman not configured – returning empty available materials list");
            return [];
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');

        // Try the native endpoint first (OlyForge3D/Spoolman fork feature)
        try
        {
            string nativeUrl = $"{baseUrl}/api/v1/spool/materials/available";
            using HttpResponseMessage response = await http.GetAsync(nativeUrl, ct);

            if (response.IsSuccessStatusCode)
            {
                List<string>? materials = await response.Content.ReadFromJsonAsync<List<string>>(ct);
                if (materials is not null)
                {
                    logger.LogDebug("Retrieved {Count} available materials from native Spoolman endpoint", materials.Count);
                    return materials.AsReadOnly();
                }
            }

            if (response.StatusCode is not HttpStatusCode.NotFound)
            {
                logger.LogWarning("Spoolman native materials/available returned {StatusCode}, falling back to aggregation", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Native Spoolman materials/available endpoint not available, falling back to aggregation");
        }

        // Fallback: fetch all filament definitions and aggregate distinct materials.
        // Filaments are the source of truth for material types — querying spools
        // was limited (500 cap) and missed materials with no active spools.
        IReadOnlyList<SpoolmanFilamentDto> filaments = await ListFilamentsAsync(ct);

        return filaments
            .Where(f => !string.IsNullOrWhiteSpace(f.Material))
            .Select(f => f.Material!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<SpoolFilterOptionsDto> GetFilterOptionsAsync(CancellationToken ct)
    {
        // Fetch all spools (up to 10 000) to extract distinct filter values
        const int pageSize = 500;
        List<SpoolmanSpoolDto> allSpools = new();
        int offset = 0;
        int totalCount;

        do
        {
            SpoolmanPagedResult<SpoolmanSpoolDto> page = await ListSpoolsAsync(
                new SpoolmanSpoolQueryParams { Limit = pageSize, Offset = offset, AllowArchived = true }, ct);

            allSpools.AddRange(page.Items);
            totalCount = page.TotalCount;
            offset += pageSize;
        }
        while (offset < totalCount && allSpools.Count < totalCount);

        var materials = allSpools
            .Where(s => !string.IsNullOrWhiteSpace(s.Material))
            .Select(s => s.Material)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        var vendors = allSpools
            .Where(s => !string.IsNullOrWhiteSpace(s.Vendor))
            .Select(s => s.Vendor!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        var locations = allSpools
            .Where(s => !string.IsNullOrWhiteSpace(s.Location))
            .Select(s => s.Location!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        logger.LogDebug(
            "Computed filter options from {Count} spools: {Materials} materials, {Vendors} vendors, {Locations} locations",
            allSpools.Count, materials.Count, vendors.Count, locations.Count);

        return new SpoolFilterOptionsDto(materials, vendors, locations);
    }

    /// <inheritdoc />
    public async Task<FilamentFilterOptionsDto> GetFilamentFilterOptionsAsync(CancellationToken ct)
    {
        IReadOnlyList<SpoolmanFilamentDto> filaments = await ListFilamentsAsync(ct);

        var materials = filaments
            .Where(f => !string.IsNullOrWhiteSpace(f.Material))
            .Select(f => f.Material!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        var vendors = filaments
            .Where(f => !string.IsNullOrWhiteSpace(f.Vendor))
            .Select(f => f.Vendor!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        logger.LogDebug(
            "Computed filament filter options from {Count} filaments: {Materials} materials, {Vendors} vendors",
            filaments.Count, materials.Count, vendors.Count);

        return new FilamentFilterOptionsDto(materials, vendors);
    }

    private async Task<MaterialPageFetchResult> FetchAllMaterialPagesAsync(string initialUrl, CancellationToken ct)
    {
        List<SpoolmanMaterialDto> collected = new();
        string? nextUrl = initialUrl;
        int page = 0;
        HttpStatusCode? lastStatus = null;
        bool anySuccess = false;
        const int MAX_PAGES = 20; // safety cap

        while (!string.IsNullOrWhiteSpace(nextUrl) && page < MAX_PAGES)
        {
            page++;
            using HttpRequestMessage req = new(HttpMethod.Get, nextUrl);
            req.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage resp = await http.SendAsync(req, ct);
            lastStatus = resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                // Stop paging on first failure after at least one success; otherwise treat as total failure
                break;
            }

            anySuccess = true;

            string? mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(mediaType) && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Spoolman material page {Page} content-type {MediaType} not JSON; aborting", page, LogSanitizer.Sanitize(mediaType));
                break;
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                logger.LogDebug("Spoolman material page {Page} returned empty response; aborting", page);
                break;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Handle both array formats:
            // 1. Simple string array: ["PLA", "ABS", "PETG"]
            // 2. Object array: [{"id": 1, "name": "PLA"}, ...]
            if (root.ValueKind == JsonValueKind.Array)
            {
                JsonElement[] currentBatch = root.EnumerateArray().ToArray();
                if (currentBatch.Length == 0)
                {
                    // Empty batch is normal end-of-pagination
                    break;
                }

                foreach (JsonElement el in currentBatch)
                {
                    SpoolmanMaterialDto? parsedMaterial = null;

                    if (el.ValueKind == JsonValueKind.String)
                    {
                        // Simple string format: "PLA"
                        string materialName = el.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(materialName))
                        {
                            parsedMaterial = new SpoolmanMaterialDto(
                                Id: 0, // No ID available in string format
                                Name: materialName,
                                Density: null,
                                ColorHex: null);
                        }
                    }
                    else if (el.ValueKind == JsonValueKind.Object && SpoolmanJsonParser.TryParseMaterial(el, out SpoolmanMaterialDto objectMaterial))
                    {
                        // Object format: {"id": 1, "name": "PLA", ...}
                        parsedMaterial = objectMaterial;
                    }

                    if (parsedMaterial != null)
                    {
                        collected.Add(parsedMaterial);
                    }
                }

                // Check for pagination - Spoolman uses standard HTTP header-based pagination
                // For materials, we expect a single page usually, but handle pagination if present
                nextUrl = null;

                // Typical page size - if less than full page, probably last page
                if (currentBatch.Length < 100)
                {
                    break;
                }
            }
        }

        return new MaterialPageFetchResult(collected, anySuccess, page, lastStatus);
    }

    public async Task<SpoolmanSpoolDto?> GetSpoolByIdAsync(int spoolId, CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return null;
        }

        // Official Spoolman endpoint for getting a specific spool
        string baseUrl = cfg.BaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/api/v1/spool/{spoolId}";
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            // Skip clearly-non-JSON payloads
            string? mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(mediaType) && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using JsonDocument? doc = await TryParseJsonAsync(resp.Content, ct);
            return doc is null ? null : SpoolmanJsonParser.ParseSpool(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<SpoolmanSpoolDto> CreateSpoolInSpoolmanAsync(SpoolmanSpoolRequest request, CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            throw new InvalidOperationException("Spoolman is not configured.");
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}{SpoolApiPath}";
        string jsonBody = BuildSpoolJson(request);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, content, cts.Token);
        await EnsureSpoolmanSuccessAsync(response, "create a spool", cts.Token);

        string json = await response.Content.ReadAsStringAsync(cts.Token);
        using JsonDocument doc = JsonDocument.Parse(json);
        return SpoolmanJsonParser.ParseSpool(doc.RootElement);
    }

    /// <inheritdoc/>
    public async Task<SpoolmanSpoolDto> UpdateSpoolInSpoolmanAsync(int spoolId, SpoolmanSpoolRequest request, CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            throw new InvalidOperationException("Spoolman is not configured.");
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}{SpoolApiPath}/{spoolId}";
        string jsonBody = BuildSpoolJson(request);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };
        HttpResponseMessage response = await http.SendAsync(httpRequest, cts.Token);
        await EnsureSpoolmanSuccessAsync(response, $"update spool {spoolId}", cts.Token);

        string json = await response.Content.ReadAsStringAsync(cts.Token);
        using JsonDocument doc = JsonDocument.Parse(json);
        return SpoolmanJsonParser.ParseSpool(doc.RootElement);
    }

    /// <inheritdoc/>
    public async Task DeleteSpoolFromSpoolmanAsync(int spoolId, CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            throw new InvalidOperationException("Spoolman is not configured.");
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}{SpoolApiPath}/{spoolId}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, url);
        HttpResponseMessage response = await http.SendAsync(httpRequest, cts.Token);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public async Task<SpoolmanBulkUpdateResult> BulkUpdateSpoolsAsync(SpoolmanBulkUpdateSpoolsRequest request, CancellationToken ct)
    {
        if (request.SpoolIds is not { Length: > 0 })
        {
            return new SpoolmanBulkUpdateResult(0, 0, []);
        }

        var patch = new SpoolmanSpoolRequest
        {
            Location = request.Location,
            LotNumber = request.LotNumber,
            Price = request.Price,
            Comment = request.Comment,
            Archived = request.Archived,
        };

        int updated = 0;
        int errorCount = 0;
        List<string> errors = [];

        using var semaphore = new SemaphoreSlim(5);
        var tasks = request.SpoolIds.Select(async id =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await UpdateSpoolInSpoolmanAsync(id, patch, ct);
                Interlocked.Increment(ref updated);
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add($"Spool {id}: {ex.Message}");
                }

                Interlocked.Increment(ref errorCount);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        return new SpoolmanBulkUpdateResult(updated, errorCount, [.. errors]);
    }

    /// <inheritdoc/>
    public async Task<SpoolmanBulkUpdateResult> BulkDeleteSpoolsAsync(int[] spoolIds, CancellationToken ct)
    {
        if (spoolIds is not { Length: > 0 })
        {
            return new SpoolmanBulkUpdateResult(0, 0, []);
        }

        int deleted = 0;
        int errorCount = 0;
        List<string> errors = [];

        using var semaphore = new SemaphoreSlim(5);
        var tasks = spoolIds.Select(async id =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await DeleteSpoolFromSpoolmanAsync(id, ct);
                Interlocked.Increment(ref deleted);
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add($"Spool {id}: {ex.Message}");
                }

                Interlocked.Increment(ref errorCount);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        return new SpoolmanBulkUpdateResult(deleted, errorCount, [.. errors]);
    }

    // Manual JSON construction required: this DTO deserializes from camelCase (frontend)
    // but must serialize to snake_case with non-standard key "lot_nr" for Spoolman API.
    private static string BuildSpoolJson(SpoolmanSpoolRequest request)
    {
        var body = new Dictionary<string, object?>();

        if (request.FilamentId.HasValue)
        {
            body["filament_id"] = request.FilamentId.Value;
        }

        if (request.RemainingWeight.HasValue)
        {
            body["remaining_weight"] = request.RemainingWeight.Value;
        }

        if (request.InitialWeight.HasValue)
        {
            body["initial_weight"] = request.InitialWeight.Value;
        }

        if (request.SpoolWeight.HasValue)
        {
            body["spool_weight"] = request.SpoolWeight.Value;
        }

        if (request.Location != null)
        {
            body["location"] = request.Location;
        }

        if (request.LotNumber != null)
        {
            body["lot_nr"] = request.LotNumber;
        }

        if (request.Price.HasValue)
        {
            body["price"] = request.Price.Value;
        }

        if (request.Comment != null)
        {
            body["comment"] = request.Comment;
        }

        if (request.Archived.HasValue)
        {
            body["archived"] = request.Archived.Value;
        }

        return System.Text.Json.JsonSerializer.Serialize(body);
    }

    private static async Task<JsonDocument?> TryParseJsonAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            await using Stream s = await content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct);
        }
        catch
        {
            // fall back to string sniffing
            try
            {
                string text = await content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('<'))
                {
                    return null; // HTML, not JSON
                }
            }
            catch
            {
            }

            return null;
        }
    }

    // no extra constructors

    /// <summary>
    /// Scans the configured network ranges for available Spoolman instances.
    /// Uses the discovery settings to determine which network ranges to scan.
    /// </summary>
    /// <param name="networkRanges">The network ranges to scan (CIDR notation or IP ranges).</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<IEnumerable<SpoolmanDiscoveryResult>> ScanNetworkForSpoolmanAsync(IEnumerable<string> networkRanges, CancellationToken ct = default)
    {
        List<SpoolmanDiscoveryResult> results = new();
        if (networkRanges == null)
        {
            results.Add(new SpoolmanDiscoveryResult(string.Empty, false, "No network subnets configured in discovery settings"));
            return results;
        }

        List<string> ips = networkRanges
            .SelectMany(r => NetworkRangeHelper.ExpandNetworkRange(r, msg => logger.LogWarning("{Message}", LogSanitizer.Sanitize(msg))))
            .Distinct()
            .ToList();
        Task<SpoolmanDiscoveryResult>[] tasks = ips.Select(ip => ScanIpForSpoolmanAsync(ip, ct)).ToArray();
        SpoolmanDiscoveryResult[] scanResults = await Task.WhenAll(tasks);
        results.AddRange(scanResults.Where(r => r.IsAvailable || !string.IsNullOrEmpty(r.Error)));
        return results;
    }

    private async Task<SpoolmanDiscoveryResult> ScanIpForSpoolmanAsync(string ip, CancellationToken ct)
    {
        string url = $"http://{ip}:7912";
        System.Diagnostics.Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            using CancellationTokenSource combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            // Try to get the Spoolman info endpoint
            HttpResponseMessage response = await http.GetAsync($"{url}/api/v1/info", combined.Token);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    string content = await response.Content.ReadAsStringAsync(combined.Token);
                    JsonDocument json = JsonDocument.Parse(content);
                    string? version = json.RootElement.TryGetProperty("version", out JsonElement versionProp)
                        ? versionProp.GetString()
                        : null;

                    return new SpoolmanDiscoveryResult(url, true, null, version, stopwatch.Elapsed);
                }
                catch
                {
                    return new SpoolmanDiscoveryResult(url, true, null, null, stopwatch.Elapsed);
                }
            }

            return new SpoolmanDiscoveryResult(url, false, $"HTTP {response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SpoolmanDiscoveryResult(url, false, "Scan cancelled");
        }
        catch (OperationCanceledException)
        {
            return new SpoolmanDiscoveryResult(url, false, "Timeout");
        }
        catch (Exception ex)
        {
            return new SpoolmanDiscoveryResult(url, false, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanDbFilamentEntry>> GetExternalFilamentsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug("Spoolman not configured – cannot fetch external filaments");
            return [];
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}/api/v1/external/filament";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            HttpResponseMessage response = await http.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();

            List<SpoolmanDbFilamentEntry>? filaments = await response.Content.ReadFromJsonAsync<List<SpoolmanDbFilamentEntry>>(ExternalJsonOptions, cts.Token);
            IReadOnlyList<SpoolmanDbFilamentEntry> result = filaments?.AsReadOnly() ?? (IReadOnlyList<SpoolmanDbFilamentEntry>)Array.Empty<SpoolmanDbFilamentEntry>();

            logger.LogDebug("Retrieved {Count} external filaments from Spoolman", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch external filaments from Spoolman at {Url}", LogSanitizer.Sanitize(url));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanDbMaterialEntry>> GetExternalMaterialsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug("Spoolman not configured – cannot fetch external materials");
            return [];
        }

        string url = $"{cfg.BaseUrl.TrimEnd('/')}/api/v1/external/material";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            HttpResponseMessage response = await http.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();

            List<SpoolmanDbMaterialEntry>? materials = await response.Content.ReadFromJsonAsync<List<SpoolmanDbMaterialEntry>>(ExternalJsonOptions, cts.Token);
            IReadOnlyList<SpoolmanDbMaterialEntry> result = materials?.AsReadOnly() ?? (IReadOnlyList<SpoolmanDbMaterialEntry>)Array.Empty<SpoolmanDbMaterialEntry>();

            logger.LogDebug("Retrieved {Count} external materials from Spoolman", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch external materials from Spoolman at {Url}", LogSanitizer.Sanitize(url));
            throw;
        }
    }

    private const string SpoolApiPath = "/api/v1/spool";

    /// <inheritdoc/>
    public async Task<bool> ConsumeFilamentAsync(int spoolId, double usedWeightGrams, CancellationToken ct)
    {
        if (usedWeightGrams <= 0)
        {
            return false;
        }

        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogWarning("ConsumeFilamentAsync: Spoolman is not configured, skipping consumption");
            return false;
        }

        try
        {
            // Get current spool to read existing used_weight
            SpoolmanSpoolDto? spool = await GetSpoolByIdAsync(spoolId, ct).ConfigureAwait(false);
            if (spool is null)
            {
                logger.LogWarning("ConsumeFilamentAsync: Spool {SpoolId} not found in Spoolman", spoolId);
                return false;
            }

            double currentUsed = spool.UsedWeightG ?? 0;
            double newUsedWeight = currentUsed + usedWeightGrams;

            string url = $"{cfg.BaseUrl.TrimEnd('/')}{SpoolApiPath}/{spoolId}";
            string jsonBody = System.Text.Json.JsonSerializer.Serialize(new { used_weight = newUsedWeight });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var httpRequest = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
            };
            HttpResponseMessage response = await http.SendAsync(httpRequest, cts.Token);
            response.EnsureSuccessStatusCode();

            logger.LogInformation(
                "ConsumeFilamentAsync: Recorded {UsedGrams:F1}g consumption on spool {SpoolId} (total: {TotalUsed:F1}g)",
                usedWeightGrams, spoolId, newUsedWeight);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ConsumeFilamentAsync: Failed to record consumption on spool {SpoolId}", spoolId);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<int> ConsumeMultipleFilamentsAsync(IEnumerable<(int spoolId, double grams)> consumptions, CancellationToken ct)
    {
        int successCount = 0;
        foreach ((int spoolId, double grams) in consumptions)
        {
            if (grams <= 0)
            {
                continue;
            }

            bool ok = await ConsumeFilamentAsync(spoolId, grams, ct);
            if (ok)
            {
                successCount++;
            }
        }

        return successCount;
    }

    private static readonly JsonSerializerOptions ExternalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

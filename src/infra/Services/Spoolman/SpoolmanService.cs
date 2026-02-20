using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Parsing;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Spoolman;

public class SpoolmanService(HttpClient http, ISettingsService settingsService, IUnifiedLoggingService logger) : ISpoolmanService
{
    private readonly HttpClient http = http;
    private readonly ISettingsService settingsService = settingsService;
    private readonly IUnifiedLoggingService logger = logger;

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
                    logger.LogError(ex, "Probe failed for {Url}", candidateBaseUrl);
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

    public async Task<IReadOnlyList<SpoolmanSpoolDto>> ListSpoolsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug($"Spoolman not configured – returning empty spool list", null, null);
            return [];
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');

        // Candidate endpoints (some deployments may expose plural or require trailing slash)
        string[] candidates =
        [
            "/api/v1/spool",
            "/api/v1/spool/",
            "/api/v1/spools",   // fallback (in case of alternative routing)
            "/api/v1/spools/"
        ];

        foreach (string ep in candidates)
        {
            string full = baseUrl + ep;
            try
            {
                PageFetchResult result = await FetchAllPagesAsync(full, ct);
                if (result.Items.Count > 0)
                {
                    if (result.AttemptedPages > 1)
                    {
                        logger.LogInformation($"Retrieved {result.Items.Count} spools across {result.AttemptedPages} pages via endpoint {ep}", null, null);
                    }
                    else
                    {
                        logger.LogDebug($"Retrieved {result.Items.Count} spools via endpoint {ep}", null, null);
                    }

                    return result.Items;
                }

                // If zero AND status success we still try next candidate, but log once
                if (result.Success && result.Items.Count == 0)
                {
                    logger.LogWarning($"Spoolman endpoint {ep} returned 0 spools (status {result.LastStatusCode}). Trying next candidate…", null, null);
                }
                else if (!result.Success)
                {
                    logger.LogDebug($"Spoolman endpoint {ep} non-success status {result.LastStatusCode}; trying next candidate", null, null);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, $"Exception when querying Spoolman endpoint {ep}; trying next candidate", null, null);
            }
        }

        logger.LogWarning($"All candidate Spoolman endpoints returned 0 spools or failed – returning empty list", null, null);
        return [];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanFilamentDto>> ListFilamentsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug($"Spoolman not configured – returning empty filament list", null, null);
            return [];
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');

        string[] candidates =
        [
            "/api/v1/filament",
            "/api/v1/filament/",
            "/api/v1/filaments",
            "/api/v1/filaments/"
        ];

        foreach (string ep in candidates)
        {
            string full = baseUrl + ep;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));

                HttpResponseMessage response = await http.GetAsync(full, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogDebug($"Spoolman filament endpoint {ep} returned status {(int)response.StatusCode}; trying next candidate", null, null);
                    continue;
                }

                string json = await response.Content.ReadAsStringAsync(cts.Token);
                using JsonDocument doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    logger.LogDebug($"Spoolman filament endpoint {ep} did not return an array; trying next candidate", null, null);
                    continue;
                }

                var filaments = new List<SpoolmanFilamentDto>();
                foreach (JsonElement el in doc.RootElement.EnumerateArray())
                {
                    filaments.Add(SpoolmanJsonParser.ParseFilament(el));
                }

                logger.LogDebug($"Retrieved {filaments.Count} filament types via endpoint {ep}", null, null);
                return filaments;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, $"Exception when querying Spoolman filament endpoint {ep}; trying next candidate", null, null);
            }
        }

        logger.LogWarning($"All candidate Spoolman filament endpoints failed – returning empty list", null, null);
        return [];
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
                logger.LogWarning($"Spoolman filament {filamentId} returned status {(int)response.StatusCode}", null, null);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token);
            using JsonDocument doc = JsonDocument.Parse(json);
            return SpoolmanJsonParser.ParseFilament(doc.RootElement);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Exception fetching Spoolman filament {filamentId}", null, null);
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
                logger.LogWarning($"Spoolman vendor list returned status {(int)response.StatusCode}", null, null);
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
            logger.LogWarning(ex, "Exception fetching Spoolman vendors", null, null);
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
        response.EnsureSuccessStatusCode();

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
        string jsonBody = BuildFilamentJson(request);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, content, cts.Token);
        response.EnsureSuccessStatusCode();

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
        string jsonBody = BuildFilamentJson(request);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };
        HttpResponseMessage response = await http.SendAsync(httpRequest, cts.Token);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cts.Token);
        using JsonDocument doc = JsonDocument.Parse(json);
        return SpoolmanJsonParser.ParseFilament(doc.RootElement);
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

        if (request.MultiColorHexes != null)
        {
            body["multi_color_hexes"] = request.MultiColorHexes;
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// Gets all material types directly from Spoolman's /api/v1/material endpoint.
    /// This is the correct endpoint for getting material definitions like PLA, ABS, PETG, etc.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public async Task<IReadOnlyList<SpoolmanMaterialDto>> ListMaterialsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug($"Spoolman not configured – returning empty material list", null, null);
            return [];
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');

        // Candidate endpoints for materials
        string[] candidates =
        {
            "/api/v1/material",
            "/api/v1/material/",
            "/api/v1/materials",   // fallback (in case of alternative routing)
            "/api/v1/materials/"
        };

        foreach (string ep in candidates)
        {
            string full = baseUrl + ep;
            try
            {
                MaterialPageFetchResult result = await FetchAllMaterialPagesAsync(full, ct);
                if (result.Items.Count > 0)
                {
                    if (result.AttemptedPages > 1)
                    {
                        logger.LogInformation($"Retrieved {result.Items.Count} materials across {result.AttemptedPages} pages via endpoint {ep}", null, null);
                    }
                    else
                    {
                        logger.LogDebug($"Retrieved {result.Items.Count} materials via endpoint {ep}", null, null);
                    }

                    return result.Items;
                }
                else if (result.Success)
                {
                    logger.LogInformation($"Successfully queried Spoolman material endpoint {ep} but got 0 results", null, null);
                    return [];
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, $"Exception when querying Spoolman material endpoint {ep}; trying next candidate", null, null);
            }
        }

        logger.LogWarning($"All candidate Spoolman material endpoints returned 0 materials or failed – returning empty list", null, null);
        return [];
    }

    private sealed record PageFetchResult(List<SpoolmanSpoolDto> Items, bool Success, int AttemptedPages, HttpStatusCode? LastStatusCode);

    private sealed record MaterialPageFetchResult(List<SpoolmanMaterialDto> Items, bool Success, int AttemptedPages, HttpStatusCode? LastStatusCode);

    private async Task<PageFetchResult> FetchAllPagesAsync(string initialUrl, CancellationToken ct)
    {
        List<SpoolmanSpoolDto> collected = new();
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
                logger.LogDebug($"Spoolman page {page} content-type {mediaType} not JSON; aborting");
                break;
            }

            using JsonDocument? doc = await TryParseJsonAsync(resp.Content, ct);
            if (doc is null)
            {
                logger.LogDebug($"Spoolman page {page} invalid JSON; aborting");
                break;
            }

            JsonElement root = doc.RootElement;
            int before = collected.Count;
            foreach (JsonElement item in SpoolmanJsonParser.EnumerateItems(root))
            {
                ct.ThrowIfCancellationRequested();
                collected.Add(SpoolmanJsonParser.ParseSpool(item));
            }

            int added = collected.Count - before;
            logger.LogDebug($"Spoolman page {page} added {added} spools (total {collected.Count})");

            // Pagination detection: common DRF style { "next": "url or null" }
            string? next = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("next", out JsonElement nextProp) && nextProp.ValueKind == JsonValueKind.String)
            {
                string? n = nextProp.GetString();
                if (!string.IsNullOrWhiteSpace(n))
                {
                    next = n;
                }
            }

            if (string.IsNullOrWhiteSpace(next))
            {
                break; // no further pages
            }

            // If relative, build absolute
            if (!Uri.TryCreate(next, UriKind.Absolute, out Uri? nextAbs) && Uri.TryCreate(initialUrl, UriKind.Absolute, out Uri? baseAbs))
            {
                try
                {
                    string baseRoot = $"{baseAbs.Scheme}://{baseAbs.Authority}";
                    nextUrl = baseRoot.TrimEnd('/') + (next.StartsWith('/') ? next : "/" + next);
                }
                catch
                {
                    nextUrl = next; // fallback raw
                }
            }
            else
            {
                nextUrl = next;
            }
        }

        return new PageFetchResult(collected, anySuccess, page, lastStatus);
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
                logger.LogDebug($"Spoolman material page {page} content-type {mediaType} not JSON; aborting");
                break;
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                logger.LogDebug($"Spoolman material page {page} returned empty response; aborting");
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
            .SelectMany(r => NetworkRangeHelper.ExpandNetworkRange(r, msg => logger.LogWarning(msg)))
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
            logger.LogDebug("Spoolman not configured – cannot fetch external filaments", null, null);
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

            logger.LogDebug($"Retrieved {result.Count} external filaments from Spoolman", null, null);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch external filaments from Spoolman at {Url}", url);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanDbMaterialEntry>> GetExternalMaterialsAsync(CancellationToken ct)
    {
        SpoolmanConfigDto? cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug("Spoolman not configured – cannot fetch external materials", null, null);
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

            logger.LogDebug($"Retrieved {result.Count} external materials from Spoolman", null, null);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch external materials from Spoolman at {Url}", url);
            throw;
        }
    }

    private static readonly JsonSerializerOptions ExternalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

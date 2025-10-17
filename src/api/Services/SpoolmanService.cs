using System.Globalization;
using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services;

public class SpoolmanService(HttpClient http, ISettingsService settingsService, IUnifiedLoggingService logger) : ISpoolmanService
{
    private readonly HttpClient http = http;
    private readonly ISettingsService settingsService = settingsService;
    private readonly IUnifiedLoggingService logger = logger;

    public SpoolmanConfigDto? GetConfig()
    {
        SpoolmanSettings? settings = settingsService.Get<SpoolmanSettings>();
        if (settings is null || string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return null;
        }
        return new SpoolmanConfigDto(settings.BaseUrl);
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
        string normalized = raw.TrimEnd('/');
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
                    catch { }

                    return new SpoolmanProbeResult(true, NormalizedUrl: normalized, EndpointTried: path, StatusCode: (int)resp.StatusCode, Version: version);
                }
            }
            catch (Exception ex)
            {
                if (path == probePaths[^1])
                {
                    (string? category, string? message) = CategorizeException(ex);
                    logger.LogError(ex, "Probe failed for {Url}", candidateBaseUrl);
                    return new SpoolmanProbeResult(false, NormalizedUrl: normalized, EndpointTried: path, StatusCode: null, Version: null, Message: message, ErrorCategory: category);
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

    private static (string? category, string? message) CategorizeException(Exception ex)
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
        string? baseUrl = NormalizeBaseUrl(config.BaseUrl);

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

    private static string? NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url; // allow null/empty to propagate (controller returns 200 with success=false)
        }

        string t = url.Trim();
        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            t = "http://" + t;
        }
        return t.TrimEnd('/');
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

    /// <summary>
    /// Gets all material types directly from Spoolman's /api/v1/material endpoint.
    /// This is the correct endpoint for getting material definitions like PLA, ABS, PETG, etc.
    /// </summary>
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
            foreach (JsonElement item in EnumerateItems(root, ct))
            {
                collected.Add(ParseSpool(item));
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
                                ColorHex: null
                            );
                        }
                    }
                    else if (el.ValueKind == JsonValueKind.Object && TryParseMaterialFromJson(el, out SpoolmanMaterialDto objectMaterial))
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
                if (currentBatch.Length < 100) // Typical page size - if less than full page, probably last page
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
            if (doc is null)
            {
                return null;
            }

            return ParseSpool(doc.RootElement);
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
            catch { }
            return null;
        }
    }

    private static IEnumerable<JsonElement> EnumerateItems(JsonElement root, CancellationToken ct)
    {
        // If the root is an array, return items directly
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in root.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                yield return el;
            }
        }

        // If it's an object, try common list containers
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetArray(root, out JsonElement arr, "results", "spools", "items", "data"))
        {
            foreach (JsonElement el in arr.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                yield return el;
            }
        }
    }

    private static bool TryGetArray(JsonElement obj, out JsonElement arrayEl, params string[] names)
    {
        arrayEl = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (string name in names)
        {
            if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Array)
            {
                arrayEl = el;
                return true;
            }
        }
        // case-insensitive scan
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            foreach (string name in names)
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    arrayEl = prop.Value;
                    return true;
                }
            }
        }
        return false;
    }

    private static SpoolmanSpoolDto ParseSpool(JsonElement el)
    {
        int id = TryGetInt(el, "id", "spool_id");
        string name = TryGetString(el, "name", "display_name") ?? (id != 0 ? $"Spool {id}" : "Spool");

        // Material can be a string or nested inside an object
        string material = TryGetString(el, "material")
                      ?? TryGetStringFromObject(el, ["material"], ["name", "material", "material_name", "material__name"])
                      ?? TryGetStringFromObject(el, ["filament", "profile"], ["material", "material_name", "material__name"])
                      ?? string.Empty;

        // Remaining weight could be in grams or another field name
        double? remaining = TryGetDoubleNullable(el, "remaining_weight_g", "remaining_weight", "remaining_weight_grams", "mass_remaining_g", "weight_remaining_g");

        // Color may be direct or nested under filament/profile
        string? color = TryGetString(el, "color_hex", "color")
            ?? TryGetStringFromObject(el, ["filament", "profile"], ["color_hex", "hex_color", "color"]);
        color = NormalizeHexColor(color);

        // Filament name and vendor/manufacturer
        string? filamentName = TryGetString(el, "filament_name")
                   ?? TryGetStringFromObject(el, ["filament", "profile"], ["name", "filament_name", "display_name"]);
        string? vendor =
            // Preferred path per Spoolman: filament.vendor.name
            TryGetStringAtPath(el, "filament", "vendor", "name")
            // Common alternative: profile.vendor.name
            ?? TryGetStringAtPath(el, "profile", "vendor", "name")
            // Fallbacks
            ?? TryGetStringFromObject(el, ["filament", "profile"], ["vendor", "manufacturer", "brand", "name"])
            ?? TryGetString(el, "vendor", "manufacturer");

        // In-use/active detection
        bool? inUse = TryGetBool(el, "in_use", "is_active", "active", "selected");
        if (!inUse.HasValue)
        {
            // Some schemas use archived=false to indicate active
            bool? archived = TryGetBool(el, "archived");
            if (archived.HasValue)
            {
                inUse = !archived.Value;
            }
        }

        // Extended numeric fields (weight/length)
        double? initialWeight = TryGetDoubleNullable(el, "initial_weight", "initial_weight_g", "initial_weight_grams");
        double? usedWeight = TryGetDoubleNullable(el, "used_weight", "used_weight_g", "used_weight_grams");
        double? spoolWeight = TryGetDoubleNullable(el, "spool_weight", "empty_spool_weight");
        double? remainingLength = TryGetDoubleNullable(el, "remaining_length", "remaining_length_mm");
        double? usedLength = TryGetDoubleNullable(el, "used_length", "used_length_mm");

        // Location, lot/batch and archived
        string? location = TryGetString(el, "location", "storage_location");
        string? lotNumber = TryGetString(el, "lot_nr", "lot", "batch", "batch_nr");
        bool? archivedFlag = TryGetBool(el, "archived");

        // Dates: registered, first used, last used (tolerant to various names and formats)
        DateTime? registeredAt = TryGetDateTime(el, "registered");
        DateTime? firstUsedAt = TryGetDateTime(el, "first_used");
        DateTime? lastUsedAt = TryGetDateTime(el, "last_used");

        return new SpoolmanSpoolDto(
            Id: id,
            Name: name,
            Material: material,
            RemainingWeightG: remaining,
            ColorHex: color,
            InUse: inUse ?? false,
            FilamentName: filamentName,
            Vendor: vendor,
            RegisteredAt: registeredAt,
            FirstUsedAt: firstUsedAt,
            LastUsedAt: lastUsedAt,
            InitialWeightG: initialWeight,
            UsedWeightG: usedWeight,
            SpoolWeightG: spoolWeight,
            RemainingLengthMm: remainingLength,
            UsedLengthMm: usedLength,
            Location: location,
            LotNumber: lotNumber,
            Archived: archivedFlag);
    }

    private static int TryGetInt(JsonElement el, params string[] names)
    {
        foreach (string n in names)
        {
            if (el.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i))
            {
                return i;
            }
        }
        // case-insensitive
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in el.EnumerateObject())
            {
                foreach (string n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int i))
                    {
                        return i;
                    }
                }
            }
        }
        return 0;
    }

    private static double? TryGetDoubleNullable(JsonElement el, params string[] names)
    {
        foreach (string n in names)
        {
            if (el.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d))
            {
                return d;
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in el.EnumerateObject())
            {
                foreach (string n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out double d))
                    {
                        return d;
                    }
                }
            }
        }
        return null;
    }

    private static string? NormalizeHexColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        s = s.ToUpperInvariant();
        // Expand shorthand RGB like F0A -> FF00AA
        if (s.Length == 3 && s.All(IsHex))
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }
        // Drop alpha if present (ARGB/RGBA -> take first 6 of last 8)
        if (s.Length == 8 && s.All(IsHex))
        {
            // Heuristic: keep first 6 (assume RRGGBB and ignore alpha at the end)
            s = s[..6];
        }
        if (s.Length == 6 && s.All(IsHex))
        {
            return "#" + s;
        }

        return null;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    private static string? TryGetString(JsonElement el, params string[] names)
    {
        foreach (string n in names)
        {
            if (el.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in el.EnumerateObject())
            {
                foreach (string n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    {
                        return p.Value.GetString();
                    }
                }
            }
        }
        return null;
    }

    private static string? TryGetStringFromObject(JsonElement el, string[] objPathCandidates, string[] fieldCandidates)
    {
        // Look for nested objects using any of the candidate names, then extract a string from candidate fields
        foreach (string objName in objPathCandidates)
        {
            if (TryGetObject(el, out JsonElement nested, objName))
            {
                string? s = TryGetString(nested, fieldCandidates);
                if (!string.IsNullOrEmpty(s))
                {
                    return s;
                }
            }
        }
        return null;
    }

    private static string? TryGetStringAtPath(JsonElement el, params string[] path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        JsonElement current = el;
        for (int i = 0; i < path.Length; i++)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryGetPropertyCaseInsensitive(current, path[i], out JsonElement next))
            {
                return null;
            }

            if (i == path.Length - 1)
            {
                return next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            }
            current = next;
        }
        return null;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        return false;
    }

    private static bool TryGetObject(JsonElement el, out JsonElement found, params string[] names)
    {
        found = default;
        foreach (string n in names)
        {
            if (el.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Object)
            {
                found = v;
                return true;
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in el.EnumerateObject())
            {
                foreach (string n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                    {
                        found = p.Value;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static bool? TryGetBool(JsonElement el, params string[] names)
    {
        foreach (string n in names)
        {
            if (el.TryGetProperty(n, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            {
                return v.GetBoolean();
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in el.EnumerateObject())
            {
                foreach (string n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && (p.Value.ValueKind == JsonValueKind.True || p.Value.ValueKind == JsonValueKind.False))
                    {
                        return p.Value.GetBoolean();
                    }
                }
            }
        }
        return null;
    }

    private static DateTime? TryGetDateTime(JsonElement el, params string[] names)
    {
        // Try string ISO formats
        foreach (string n in names)
        {
            if (el.TryGetProperty(n, out JsonElement v))
            {
                DateTime? dt = FromJsonElementToDateTime(v);
                if (dt.HasValue)
                {
                    return dt;
                }
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in el.EnumerateObject())
            {
                foreach (string n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                    {
                        DateTime? dt = FromJsonElementToDateTime(p.Value);
                        if (dt.HasValue)
                        {
                            return dt;
                        }
                    }
                }
            }
        }
        return null;
    }

    private static DateTime? FromJsonElementToDateTime(JsonElement v)
    {
        try
        {
            if (v.ValueKind == JsonValueKind.String)
            {
                string? s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                {
                    return DateTime.SpecifyKind(parsed, parsed.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : parsed.Kind);
                }
            }
            else if (v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetInt64(out long num))
                {
                    // Heuristic: epoch seconds vs milliseconds
                    // >= 1e12 -> milliseconds, >= 1e9 -> seconds
                    if (num >= 1_000_000_000_000)
                    {
                        return DateTimeOffset.FromUnixTimeMilliseconds(num).UtcDateTime;
                    }

                    if (num >= 1_000_000_000)
                    {
                        return DateTimeOffset.FromUnixTimeSeconds(num).UtcDateTime;
                    }
                }
                else if (v.TryGetDouble(out double dnum))
                {
                    long ln = (long)dnum;
                    if (ln >= 1_000_000_000_000)
                    {
                        return DateTimeOffset.FromUnixTimeMilliseconds(ln).UtcDateTime;
                    }

                    if (ln >= 1_000_000_000)
                    {
                        return DateTimeOffset.FromUnixTimeSeconds(ln).UtcDateTime;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Tries to parse a Spoolman material from JSON element
    /// </summary>
    private static bool TryParseMaterialFromJson(JsonElement el, out SpoolmanMaterialDto material)
    {
        try
        {
            int id = TryGetInt(el, "id");
            string name = TryGetString(el, "name") ?? string.Empty;

            // Skip materials without required fields
            if (string.IsNullOrWhiteSpace(name))
            {
                material = default!;
                return false;
            }

            double? density = TryGetDoubleNullable(el, "density");
            string? colorHex = TryGetString(el, "color_hex");
            colorHex = NormalizeHexColor(colorHex);

            material = new SpoolmanMaterialDto(
                Id: id,
                Name: name,
                Density: density,
                ColorHex: colorHex
            );
            return true;
        }
        catch
        {
            material = default!;
            return false;
        }
    }

    // no extra constructors

    /// <summary>
    /// Scans the configured network ranges for available Spoolman instances.
    /// Uses the discovery settings to determine which network ranges to scan.
    /// </summary>
    public async Task<IEnumerable<SpoolmanDiscoveryResult>> ScanNetworkForSpoolmanAsync(IEnumerable<string> networkRanges, CancellationToken ct = default)
    {
        List<SpoolmanDiscoveryResult> results = new();
        if (networkRanges == null)
        {
            results.Add(new SpoolmanDiscoveryResult("", false, "No network subnets configured in discovery settings"));
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
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

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

}

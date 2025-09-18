using System.Globalization;
using System.Net;
using System.Text.Json;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services;

public class SpoolmanService : ISpoolmanService
{
    private readonly HttpClient http;
    private readonly AppDbContext db;
    private readonly ILogger<SpoolmanService> logger;
    private readonly INetworkDiscoverySettingsService networkSettings;

    public SpoolmanService(HttpClient http, AppDbContext db, ILogger<SpoolmanService> logger, INetworkDiscoverySettingsService networkSettings)
    {
        this.http = http;
        this.db = db;
        this.logger = logger;
        this.networkSettings = networkSettings;
    }

    public SpoolmanConfigDto? GetConfig()
    {
        SpoolmanConfig? row = db.SpoolmanConfigs.FirstOrDefault(c => c.Id == 1);
        if (row is null)
        {
            // One-time migration from legacy JSON file if present
            try
            {
                string legacyPath = Path.Combine(AppContext.BaseDirectory, "spoolman.config.json");
                if (File.Exists(legacyPath))
                {
                    string text = File.ReadAllText(legacyPath);
                    SpoolmanConfigDto? cfg = JsonSerializer.Deserialize<SpoolmanConfigDto>(text);
                    if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.BaseUrl))
                    {
                        SetConfig(cfg);
                        row = db.SpoolmanConfigs.FirstOrDefault(c => c.Id == 1);
                    }
                }
            }
            catch { }
        }
        return row is null ? null : new SpoolmanConfigDto(row.BaseUrl);
    }

    public void SetConfig(SpoolmanConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string? baseUrl = NormalizeBaseUrl(config.BaseUrl);
        SpoolmanConfig? row = db.SpoolmanConfigs.FirstOrDefault(c => c.Id == 1);
        if (row is null)
        {
            row = new SpoolmanConfig { Id = 1, BaseUrl = baseUrl ?? string.Empty };
            db.SpoolmanConfigs.Add(row);
        }
        else
        {
            row.BaseUrl = baseUrl ?? string.Empty;
            db.SpoolmanConfigs.Update(row);
        }
        db.SaveChanges();
    }

    public void ClearConfig()
    {
        SpoolmanConfig? row = db.SpoolmanConfigs.FirstOrDefault(c => c.Id == 1);
        if (row is not null)
        {
            db.SpoolmanConfigs.Remove(row);
            db.SaveChanges();
        }
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
            logger.LogDebug("Spoolman not configured – returning empty spool list");
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
                        logger.LogInformation("Retrieved {Count} spools across {Pages} pages via endpoint {Endpoint}", result.Items.Count, result.AttemptedPages, ep);
                    }
                    else
                    {
                        logger.LogDebug("Retrieved {Count} spools via endpoint {Endpoint}", result.Items.Count, ep);
                    }
                    return result.Items;
                }

                // If zero AND status success we still try next candidate, but log once
                if (result.Success && result.Items.Count == 0)
                {
                    logger.LogWarning("Spoolman endpoint {Endpoint} returned 0 spools (status {Status}). Trying next candidate…", ep, result.LastStatusCode);
                }
                else if (!result.Success)
                {
                    logger.LogDebug("Spoolman endpoint {Endpoint} non-success status {Status}; trying next candidate", ep, result.LastStatusCode);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Exception when querying Spoolman endpoint {Endpoint}; trying next candidate", ep);
            }
        }

        logger.LogWarning("All candidate Spoolman endpoints returned 0 spools or failed – returning empty list");
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
            logger.LogDebug("Spoolman not configured – returning empty material list");
            return [];
        }

        string baseUrl = cfg.BaseUrl.TrimEnd('/');

        // Candidate endpoints for materials
        string[] candidates =
        [
            "/api/v1/material",
            "/api/v1/material/",
            "/api/v1/materials",   // fallback (in case of alternative routing)
            "/api/v1/materials/"
        ];

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
                        logger.LogInformation("Retrieved {Count} materials across {Pages} pages via endpoint {Endpoint}", result.Items.Count, result.AttemptedPages, ep);
                    }
                    else
                    {
                        logger.LogDebug("Retrieved {Count} materials via endpoint {Endpoint}", result.Items.Count, ep);
                    }
                    return result.Items;
                }
                else if (result.Success)
                {
                    logger.LogInformation("Successfully queried Spoolman material endpoint {Endpoint} but got 0 results", ep);
                    return [];
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Exception when querying Spoolman material endpoint {Endpoint}; trying next candidate", ep);
            }
        }

        logger.LogWarning("All candidate Spoolman material endpoints returned 0 materials or failed – returning empty list");
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
                logger.LogDebug("Spoolman page {Page} content-type {MediaType} not JSON; aborting", page, mediaType);
                break;
            }

            using JsonDocument? doc = await TryParseJsonAsync(resp.Content, ct);
            if (doc is null)
            {
                logger.LogDebug("Spoolman page {Page} invalid JSON; aborting", page);
                break;
            }

            JsonElement root = doc.RootElement;
            int before = collected.Count;
            foreach (JsonElement item in EnumerateItems(root, ct))
            {
                collected.Add(ParseSpool(item));
            }

            int added = collected.Count - before;
            logger.LogDebug("Spoolman page {Page} added {Added} spools (total {Total})", page, added, collected.Count);

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
                logger.LogDebug("Spoolman material page {Page} content-type {MediaType} not JSON; aborting", page, mediaType);
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
    public async Task<IEnumerable<SpoolmanDiscoveryResult>> ScanNetworkForSpoolmanAsync(CancellationToken ct = default)
    {
        // Get network ranges from discovery settings
        var discoverySettings = networkSettings.GetSettings();
        if (discoverySettings.NetworkRanges.Count == 0)
        {
            return new[] { new SpoolmanDiscoveryResult("", false, "No network ranges configured in discovery settings") };
        }

        // Scan each network range for Spoolman instances
        var tasks = discoverySettings.NetworkRanges
            .SelectMany(ExpandNetworkRange)
            .Select(ip => ScanIpForSpoolmanAsync(ip, ct))
            .ToArray();

        var scanResults = await Task.WhenAll(tasks);
        return scanResults.Where(r => r.IsAvailable || !string.IsNullOrEmpty(r.Error));
    }

    /// <summary>
    /// Expands a network range specification into individual IP addresses.
    /// Supports formats like "192.168.1.1-192.168.1.254" and "192.168.1.0/24"
    /// </summary>
    private IEnumerable<string> ExpandNetworkRange(string range)
    {
        try
        {
            // Handle CIDR notation (e.g., "192.168.1.0/24")
            if (range.Contains('/'))
            {
                var parts = range.Split('/');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var network) && int.TryParse(parts[1], out var prefixLength))
                {
                    return ExpandCidrRange(network, prefixLength);
                }
            }
            
            // Handle range notation (e.g., "192.168.1.1-192.168.1.254")
            if (range.Contains('-'))
            {
                var parts = range.Split('-');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0].Trim(), out var startIp) && IPAddress.TryParse(parts[1].Trim(), out var endIp))
                {
                    return ExpandIpRange(startIp, endIp);
                }
            }

            // Single IP address
            if (IPAddress.TryParse(range, out _))
            {
                return new[] { range };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to expand network range '{Range}': {Error}", range, ex.Message);
        }

        return Enumerable.Empty<string>();
    }

    /// <summary>
    /// Expands a CIDR range into individual IP addresses (limited to reasonable subnet sizes)
    /// </summary>
    private IEnumerable<string> ExpandCidrRange(IPAddress network, int prefixLength)
    {
        // Limit to /16 or smaller subnets to avoid excessive scanning
        if (prefixLength < 16)
        {
            logger.LogWarning("CIDR range too large (/{PrefixLength}), limiting to /16", prefixLength);
            prefixLength = 16;
        }

        var networkBytes = network.GetAddressBytes();
        var hostBits = 32 - prefixLength;
        var maxHosts = Math.Min(1 << hostBits, 1024); // Limit to 1024 IPs max

        for (int i = 1; i < maxHosts - 1; i++) // Skip network and broadcast
        {
            var hostBytes = BitConverter.GetBytes(i);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(hostBytes);
            }

            var ipBytes = new byte[4];
            for (int j = 0; j < 4; j++)
            {
                ipBytes[j] = (byte)(networkBytes[j] | hostBytes[j]);
            }

            yield return new IPAddress(ipBytes).ToString();
        }
    }

    /// <summary>
    /// Expands an IP range into individual addresses
    /// </summary>
    private IEnumerable<string> ExpandIpRange(IPAddress startIp, IPAddress endIp)
    {
        var start = BitConverter.ToUInt32(startIp.GetAddressBytes().Reverse().ToArray(), 0);
        var end = BitConverter.ToUInt32(endIp.GetAddressBytes().Reverse().ToArray(), 0);
        
        // Limit range size to prevent excessive scanning
        if (end - start > 1024)
        {
            logger.LogWarning("IP range too large ({Start}-{End}), limiting to 1024 addresses", startIp, endIp);
            end = start + 1024;
        }

        for (uint ip = start; ip <= end; ip++)
        {
            var bytes = BitConverter.GetBytes(ip).Reverse().ToArray();
            yield return new IPAddress(bytes).ToString();
        }
    }

    /// <summary>
    /// Scans a single IP address for a Spoolman instance on port 7912
    /// </summary>
    private async Task<SpoolmanDiscoveryResult> ScanIpForSpoolmanAsync(string ip, CancellationToken ct)
    {
        var url = $"http://{ip}:7912";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            
            // Try to get the Spoolman info endpoint
            var response = await http.GetAsync($"{url}/api/v1/info", combined.Token);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var content = await response.Content.ReadAsStringAsync(combined.Token);
                    var json = JsonDocument.Parse(content);
                    var version = json.RootElement.TryGetProperty("version", out var versionProp)
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

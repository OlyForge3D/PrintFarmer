using System.Globalization;
using System.Net;
using System.Text.Json;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services;

public class SpoolmanService : ISpoolmanService
{
    private readonly HttpClient http;
    private readonly AppDbContext db;
    private readonly ILogger<SpoolmanService> logger;

    public SpoolmanService(HttpClient http, AppDbContext db, ILogger<SpoolmanService> logger)
    {
        this.http = http;
        this.db = db;
        this.logger = logger;
    }

    public SpoolmanConfigDto? GetConfig()
    {
        var row = db.SpoolmanConfigs.FirstOrDefault(c => c.Id == 1);
        if (row is null)
        {
            // One-time migration from legacy JSON file if present
            try
            {
                var legacyPath = Path.Combine(AppContext.BaseDirectory, "spoolman.config.json");
                if (File.Exists(legacyPath))
                {
                    var text = File.ReadAllText(legacyPath);
                    var cfg = JsonSerializer.Deserialize<SpoolmanConfigDto>(text);
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
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var row = db.SpoolmanConfigs.FirstOrDefault(c => c.Id == 1);
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
        var row = db.SpoolmanConfigs.FirstOrDefault(c => c.Id == 1);
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

        var t = url.Trim();
        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            t = "http://" + t;
        }
        return t.TrimEnd('/');
    }

    public async Task<IReadOnlyList<SpoolmanSpoolDto>> ListSpoolsAsync(CancellationToken ct)
    {
        var cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            logger.LogDebug("Spoolman not configured – returning empty spool list");
            return [];
        }

        var baseUrl = cfg.BaseUrl.TrimEnd('/');

        // Candidate endpoints (some deployments may expose plural or require trailing slash)
        string[] candidates =
        [
            "/api/v1/spool",
            "/api/v1/spool/",
            "/api/v1/spools",   // fallback (in case of alternative routing)
            "/api/v1/spools/"
        ];

        foreach (var ep in candidates)
        {
            var full = baseUrl + ep;
            try
            {
                var result = await FetchAllPagesAsync(full, ct);
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

    private sealed record PageFetchResult(List<SpoolmanSpoolDto> Items, bool Success, int AttemptedPages, HttpStatusCode? LastStatusCode);

    private async Task<PageFetchResult> FetchAllPagesAsync(string initialUrl, CancellationToken ct)
    {
        var collected = new List<SpoolmanSpoolDto>();
        string? nextUrl = initialUrl;
        int page = 0;
        HttpStatusCode? lastStatus = null;
        bool anySuccess = false;
        const int MAX_PAGES = 20; // safety cap

        while (!string.IsNullOrWhiteSpace(nextUrl) && page < MAX_PAGES)
        {
            page++;
            using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await http.SendAsync(req, ct);
            lastStatus = resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                // Stop paging on first failure after at least one success; otherwise treat as total failure
                break;
            }
            anySuccess = true;

            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(mediaType) && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Spoolman page {Page} content-type {MediaType} not JSON; aborting", page, mediaType);
                break;
            }

            using var doc = await TryParseJsonAsync(resp.Content, ct);
            if (doc is null)
            {
                logger.LogDebug("Spoolman page {Page} invalid JSON; aborting", page);
                break;
            }

            var root = doc.RootElement;
            int before = collected.Count;
            foreach (var item in EnumerateItems(root, ct))
            {
                collected.Add(ParseSpool(item));
            }

            int added = collected.Count - before;
            logger.LogDebug("Spoolman page {Page} added {Added} spools (total {Total})", page, added, collected.Count);

            // Pagination detection: common DRF style { "next": "url or null" }
            string? next = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("next", out var nextProp) && nextProp.ValueKind == JsonValueKind.String)
            {
                var n = nextProp.GetString();
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
            if (!Uri.TryCreate(next, UriKind.Absolute, out var nextAbs) && Uri.TryCreate(initialUrl, UriKind.Absolute, out var baseAbs))
            {
                try
                {
                    var baseRoot = $"{baseAbs.Scheme}://{baseAbs.Authority}";
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

    public async Task<SpoolmanSpoolDto?> GetSpoolByIdAsync(int spoolId, CancellationToken ct)
    {
        var cfg = GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return null;
        }

        // Official Spoolman endpoint for getting a specific spool
        var baseUrl = cfg.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/api/v1/spool/{spoolId}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            // Skip clearly-non-JSON payloads
            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(mediaType) && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var doc = await TryParseJsonAsync(resp.Content, ct);
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
            await using var s = await content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct);
        }
        catch
        {
            // fall back to string sniffing
            try
            {
                var text = await content.ReadAsStringAsync(ct);
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
            foreach (var el in root.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                yield return el;
            }
        }

        // If it's an object, try common list containers
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetArray(root, out var arr, "results", "spools", "items", "data"))
        {
            foreach (var el in arr.EnumerateArray())
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

        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array)
            {
                arrayEl = el;
                return true;
            }
        }
        // case-insensitive scan
        foreach (var prop in obj.EnumerateObject())
        {
            foreach (var name in names)
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
        var id = TryGetInt(el, "id", "spool_id");
        var name = TryGetString(el, "name", "display_name") ?? (id != 0 ? $"Spool {id}" : "Spool");

        // Material can be a string or nested inside an object
        var material = TryGetString(el, "material")
                      ?? TryGetStringFromObject(el, ["material"], ["name", "material", "material_name", "material__name"])
                      ?? TryGetStringFromObject(el, ["filament", "profile"], ["material", "material_name", "material__name"])
                      ?? string.Empty;

        // Remaining weight could be in grams or another field name
        double? remaining = TryGetDoubleNullable(el, "remaining_weight_g", "remaining_weight", "remaining_weight_grams", "mass_remaining_g", "weight_remaining_g");

        // Color may be direct or nested under filament/profile
        var color = TryGetString(el, "color_hex", "color")
            ?? TryGetStringFromObject(el, ["filament", "profile"], ["color_hex", "hex_color", "color"]);
        color = NormalizeHexColor(color);

        // Filament name and vendor/manufacturer
        var filamentName = TryGetString(el, "filament_name")
                   ?? TryGetStringFromObject(el, ["filament", "profile"], ["name", "filament_name", "display_name"]);
        var vendor =
            // Preferred path per Spoolman: filament.vendor.name
            TryGetStringAtPath(el, "filament", "vendor", "name")
            // Common alternative: profile.vendor.name
            ?? TryGetStringAtPath(el, "profile", "vendor", "name")
            // Fallbacks
            ?? TryGetStringFromObject(el, ["filament", "profile"], ["vendor", "manufacturer", "brand", "name"])
            ?? TryGetString(el, "vendor", "manufacturer");

        // In-use/active detection
        var inUse = TryGetBool(el, "in_use", "is_active", "active", "selected");
        if (!inUse.HasValue)
        {
            // Some schemas use archived=false to indicate active
            var archived = TryGetBool(el, "archived");
            if (archived.HasValue)
            {
                inUse = !archived.Value;
            }
        }

        // Extended numeric fields (weight/length)
        var initialWeight = TryGetDoubleNullable(el, "initial_weight", "initial_weight_g", "initial_weight_grams");
        var usedWeight = TryGetDoubleNullable(el, "used_weight", "used_weight_g", "used_weight_grams");
        var spoolWeight = TryGetDoubleNullable(el, "spool_weight", "empty_spool_weight");
        var remainingLength = TryGetDoubleNullable(el, "remaining_length", "remaining_length_mm");
        var usedLength = TryGetDoubleNullable(el, "used_length", "used_length_mm");

        // Location, lot/batch and archived
        var location = TryGetString(el, "location", "storage_location");
        var lotNumber = TryGetString(el, "lot_nr", "lot", "batch", "batch_nr");
        var archivedFlag = TryGetBool(el, "archived");

        // Dates: registered, first used, last used (tolerant to various names and formats)
        var registeredAt = TryGetDateTime(el, "registered");
        var firstUsedAt = TryGetDateTime(el, "first_used");
        var lastUsedAt = TryGetDateTime(el, "last_used");

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
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            {
                return i;
            }
        }
        // case-insensitive
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                foreach (var n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var i))
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
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            {
                return d;
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                foreach (var n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d))
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

        var s = raw.Trim();
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
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                foreach (var n in names)
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
        foreach (var objName in objPathCandidates)
        {
            if (TryGetObject(el, out var nested, objName))
            {
                var s = TryGetString(nested, fieldCandidates);
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

            if (!TryGetPropertyCaseInsensitive(current, path[i], out var next))
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

        foreach (var p in obj.EnumerateObject())
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
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Object)
            {
                found = v;
                return true;
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                foreach (var n in names)
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
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            {
                return v.GetBoolean();
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                foreach (var n in names)
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
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v))
            {
                var dt = FromJsonElementToDateTime(v);
                if (dt.HasValue)
                {
                    return dt;
                }
            }
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                foreach (var n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                    {
                        var dt = FromJsonElementToDateTime(p.Value);
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
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    return DateTime.SpecifyKind(parsed, parsed.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : parsed.Kind);
                }
            }
            else if (v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetInt64(out var num))
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
                else if (v.TryGetDouble(out var dnum))
                {
                    var ln = (long)dnum;
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

    // no extra constructors
}

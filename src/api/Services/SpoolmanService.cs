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

    public SpoolmanService(HttpClient http, AppDbContext db)
    {
        this.http = http;
        this.db = db;
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
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var row = db.SpoolmanConfigs.FirstOrDefault(c => c.Id == 1);
        if (row is null)
        {
            row = new SpoolmanConfig { Id = 1, BaseUrl = baseUrl };
            db.SpoolmanConfigs.Add(row);
        }
        else
        {
            row.BaseUrl = baseUrl;
            db.SpoolmanConfigs.Update(row);
        }
        db.SaveChanges();
    }

    private static string NormalizeBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
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
            return [];
        }

        // Official Spoolman endpoint for listing spools
        var baseUrl = cfg.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/api/v1/spool";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }
            // Skip clearly-non-JSON payloads
            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(mediaType) && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var all = new List<SpoolmanSpoolDto>();
            using var doc = await TryParseJsonAsync(resp.Content, ct);
            if (doc is null)
            {
                return [];
            }

            foreach (var item in EnumerateItems(doc.RootElement, ct))
            {
                all.Add(ParseSpool(item));
            }
            return all;
        }
        catch
        {
            return [];
        }
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
            yield break;
        }

        // If it's an object, try common list containers
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (TryGetArray(root, out var arr, "results", "spools", "items", "data"))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return el;
                }
                yield break;
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

        // Dates: registered, first used, last used (tolerant to various names and formats)
        var registeredAt = TryGetDateTime(el, "registered");
        var firstUsedAt = TryGetDateTime(el, "first_used");
        var lastUsedAt = TryGetDateTime(el, "last_used");

        return new SpoolmanSpoolDto(id, name ?? "Spool", material, remaining, color, inUse ?? false, filamentName, vendor,
            registeredAt, firstUsedAt, lastUsedAt);
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
                if (!string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, out var parsed))
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

using System.Globalization;
using System.Text.Json;

namespace Farm.Infrastructure.Parsing;

/// <summary>
/// Shared Spoolman JSON parser used by both the direct Spoolman integration
/// (via SpoolmanService HTTP) and the per-printer Moonraker proxy path.
/// Provides tolerant parsing that handles various Spoolman API response formats.
/// </summary>
public static class SpoolmanJsonParser
{
    /// <summary>
    /// Parses a JSON string containing an array of Spoolman spool objects into DTOs.
    /// Handles both flat arrays and paginated response wrappers.
    /// </summary>
    /// <param name="json">Raw JSON string (expected to be an array of spool objects or a paginated wrapper)</param>
    /// <returns>List of parsed spool DTOs, or empty list if parsing fails</returns>
    public static IReadOnlyList<SpoolmanSpoolDto> ParseSpools(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            List<SpoolmanSpoolDto> result = new();

            foreach (JsonElement item in EnumerateItems(doc.RootElement))
            {
                result.Add(ParseSpool(item));
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Parses a JSON string containing an array of Spoolman filament objects into DTOs.
    /// </summary>
    /// <param name="json">Raw JSON string (expected to be an array of filament objects)</param>
    /// <returns>List of parsed filament DTOs, or empty list if parsing fails</returns>
    public static IReadOnlyList<SpoolmanFilamentDto> ParseFilaments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            List<SpoolmanFilamentDto> result = new();

            foreach (JsonElement item in EnumerateItems(doc.RootElement))
            {
                result.Add(ParseFilament(item));
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Parses a single Spoolman spool JSON element into a DTO.
    /// </summary>
    public static SpoolmanSpoolDto ParseSpool(JsonElement el)
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

        // Price: may be a direct field or nested under filament
        double? price = TryGetDoubleNullable(el, "price")
            ?? TryGetDoubleNullableFromObject(el, ["filament", "profile"], ["price", "cost", "spool_price"]);

        // Comment
        string? comment = TryGetString(el, "comment");

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
            Archived: archivedFlag,
            Price: price,
            Comment: comment);
    }

    /// <summary>
    /// Parses a single Spoolman filament JSON element into a DTO.
    /// </summary>
    public static SpoolmanFilamentDto ParseFilament(JsonElement el)
    {
        int id = TryGetInt(el, "id", "filament_id");
        string? name = TryGetString(el, "name", "display_name");
        string? material = TryGetString(el, "material");

        string? colorHex = TryGetString(el, "color_hex", "hex_color", "color");
        colorHex = NormalizeHexColor(colorHex);

        // Vendor can be a nested object or a direct string
        string? vendor = TryGetStringAtPath(el, "vendor", "name")
            ?? TryGetString(el, "vendor", "manufacturer");

        double? density = TryGetDoubleNullable(el, "density");
        double? diameter = TryGetDoubleNullable(el, "diameter");
        double? weight = TryGetDoubleNullable(el, "weight");
        double? spoolWeight = TryGetDoubleNullable(el, "spool_weight");
        double? price = TryGetDoubleNullable(el, "price");
        int? extruderTemp = TryGetIntNullable(el, "settings_extruder_temp");
        int? bedTemp = TryGetIntNullable(el, "settings_bed_temp");
        string? articleNumber = TryGetString(el, "article_number");
        string? comment = TryGetString(el, "comment");
        string? multiColorHexes = TryGetString(el, "multi_color_hexes");
        string? externalId = TryGetString(el, "external_id");

        return new SpoolmanFilamentDto(
            Id: id,
            Name: name,
            Material: material,
            ColorHex: colorHex,
            Vendor: vendor,
            Density: density,
            Diameter: diameter,
            Weight: weight,
            SpoolWeight: spoolWeight,
            Price: price,
            SettingsExtruderTemp: extruderTemp,
            SettingsBedTemp: bedTemp,
            ArticleNumber: articleNumber,
            Comment: comment,
            MultiColorHexes: multiColorHexes,
            ExternalId: externalId);
    }

    /// <summary>
    /// Tries to parse a Spoolman material from a JSON element.
    /// </summary>
    public static bool TryParseMaterial(JsonElement el, out SpoolmanMaterialDto material)
    {
        try
        {
            int id = TryGetInt(el, "id");
            string name = TryGetString(el, "name") ?? string.Empty;

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
                ColorHex: colorHex);
            return true;
        }
        catch
        {
            material = default!;
            return false;
        }
    }

    // ===== Enumeration helpers =====

    /// <summary>
    /// Enumerates items from a JSON root that may be a flat array or a paginated wrapper object.
    /// Handles common patterns: plain array, or object with "results", "spools", "items", or "data" array.
    /// </summary>
    public static IEnumerable<JsonElement> EnumerateItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in root.EnumerateArray())
            {
                yield return el;
            }

            yield break;
        }

        // Single object
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetArray(root, out JsonElement arr, "results", "spools", "items", "data"))
        {
            foreach (JsonElement el in arr.EnumerateArray())
            {
                yield return el;
            }

            yield break;
        }

        // Single object without a list wrapper — yield the object itself
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
        }
    }

    // ===== Low-level JSON helpers (tolerant, case-insensitive) =====
    public static int TryGetInt(JsonElement el, params string[] names)
    {
        foreach (string n in names)
        {
            if (el.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i))
            {
                return i;
            }
        }

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

    internal static int? TryGetIntNullable(JsonElement el, params string[] names)
    {
        foreach (string n in names)
        {
            if (el.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i))
            {
                return i;
            }
        }

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

        return null;
    }

    internal static double? TryGetDoubleNullable(JsonElement el, params string[] names)
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

    internal static string? NormalizeHexColor(string? raw)
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
            s = s[..6];
        }

        return s.Length == 6 && s.All(IsHex) ? "#" + s : null;
    }

    internal static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    public static string? TryGetString(JsonElement el, params string[] names)
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

    internal static string? TryGetStringFromObject(JsonElement el, string[] objPathCandidates, string[] fieldCandidates)
    {
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

    internal static double? TryGetDoubleNullableFromObject(JsonElement el, string[] objPathCandidates, string[] fieldCandidates)
    {
        foreach (string objName in objPathCandidates)
        {
            if (TryGetObject(el, out JsonElement nested, objName))
            {
                double? d = TryGetDoubleNullable(nested, fieldCandidates);
                if (d.HasValue)
                {
                    return d;
                }
            }
        }

        return null;
    }

    internal static string? TryGetStringAtPath(JsonElement el, params string[] path)
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

    internal static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
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

    internal static bool TryGetObject(JsonElement el, out JsonElement found, params string[] names)
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

    internal static bool TryGetArray(JsonElement obj, out JsonElement arrayEl, params string[] names)
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

    internal static bool? TryGetBool(JsonElement el, params string[] names)
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

    internal static DateTime? TryGetDateTime(JsonElement el, params string[] names)
    {
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

    internal static DateTime? FromJsonElementToDateTime(JsonElement v)
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
        catch
        {
        }

        return null;
    }
}

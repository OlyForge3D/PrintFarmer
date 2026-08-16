using System.Globalization;
using System.Text.Json;

namespace Farm.Slicer.ProfileParsing;

/// <summary>
/// Generic, tolerant value getters for OrcaSlicer profile JSON. OrcaSlicer stores many scalar
/// settings as single-element arrays (e.g. <c>["0.4"]</c>) or as strings even when the value is
/// numeric/boolean; these helpers normalize that without imposing any particular derived-field
/// policy. Lifted verbatim from <c>orcaslicer-worker</c>'s <c>OrcaProfilesService</c> (#1615) so
/// both the worker and the calibration profile resolver parse the same way instead of maintaining
/// two independent copies that can silently drift.
/// </summary>
public static class OrcaRawValueParser
{
    public static int? ParseIntValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number)
        {
            return elem.TryGetInt32(out int val) ? val : null;
        }
        else if (elem.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(elem.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) ? val : null;
        }
        else if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() > 0)
        {
            // OrcaSlicer stores many values as single-element arrays like ["260"]
            JsonElement firstElem = elem[0];
            return ParseIntValue(firstElem);
        }

        return null;
    }

    public static double? ParseDoubleValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number)
        {
            return elem.TryGetDouble(out double val) ? val : null;
        }
        else if (elem.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(elem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double val) ? val : null;
        }
        else if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() > 0)
        {
            // OrcaSlicer stores many values as single-element arrays like ["0.2"]
            JsonElement firstElem = elem[0];
            return ParseDoubleValue(firstElem);
        }

        return null;
    }

    /// <summary>
    /// Safely parse a string value from a JsonElement that could be a string or array.
    /// OrcaSlicer stores many values as single-element arrays like ["PLA"].
    /// </summary>
    public static string? ParseStringValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString();
        }
        else if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() > 0)
        {
            JsonElement firstElem = elem[0];
            return ParseStringValue(firstElem);
        }

        return null;
    }

    public static bool ParseBoolValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.True)
        {
            return true;
        }
        else if (elem.ValueKind == JsonValueKind.String)
        {
            string? val = elem.GetString();
            return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
        }

        return false;
    }

    public static int? ParseOptionalInt(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out JsonElement elem))
        {
            return ParseIntValue(elem);
        }

        return null;
    }

    public static double? ParseOptionalDouble(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out JsonElement elem))
        {
            return ParseDoubleValue(elem);
        }

        return null;
    }

    public static bool? ParseOptionalBool(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out JsonElement elem))
        {
            return ParseBoolValue(elem);
        }

        return null;
    }

    public static string? ParseOptionalString(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out JsonElement elem))
        {
            return ParseStringValue(elem);
        }

        return null;
    }
}

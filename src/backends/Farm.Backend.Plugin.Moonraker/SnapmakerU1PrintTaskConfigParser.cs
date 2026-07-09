using System.Text.Json;

namespace Farm.Backend.Plugin.Moonraker;

internal sealed record SnapmakerU1PrintTaskConfigStatus(
    SnapmakerU1LaneStatus[] Lanes,
    int ActiveTool);

internal sealed record SnapmakerU1LaneStatus(
    int Index,
    bool Loaded,
    string? Material,
    string? SubType,
    string? Color,
    bool Official,
    bool IsActive)
{
    public string? FilamentName => string.IsNullOrWhiteSpace(SubType)
        ? Material
        : string.IsNullOrWhiteSpace(Material)
            ? SubType
            : $"{Material} {SubType}";
}

internal static class SnapmakerU1PrintTaskConfigParser
{
    internal const int LaneCount = 4;

    public static bool TryParse(JsonElement statusObj, out SnapmakerU1PrintTaskConfigStatus status)
    {
        status = new SnapmakerU1PrintTaskConfigStatus([], -2);

        if (!statusObj.TryGetProperty("print_task_config", out JsonElement config) ||
            config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty("filament_exist", out JsonElement filamentExist) ||
            filamentExist.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        SnapmakerU1LaneStatus[] lanes = new SnapmakerU1LaneStatus[LaneCount];
        int activeTool = TryReadActiveTool(statusObj) ?? -2;

        for (int i = 0; i < lanes.Length; i++)
        {
            bool loaded = ReadBool(filamentExist, i);
            string? material = loaded ? ReadStringArrayProperty(config, "filament_type", i) : null;
            string? subType = loaded ? NormalizeSubType(ReadStringArrayProperty(config, "filament_sub_type", i)) : null;
            string? color = loaded ? ReadHexColor(ReadStringArrayProperty(config, "filament_color_rgba", i)) : null;
            bool official = loaded && ReadBoolArrayProperty(config, "filament_official", i);

            lanes[i] = new SnapmakerU1LaneStatus(
                i,
                loaded,
                NormalizeString(material),
                subType,
                color,
                official,
                i == activeTool);
        }

        status = new SnapmakerU1PrintTaskConfigStatus(lanes, activeTool);
        return true;
    }

    private static int? TryReadActiveTool(JsonElement statusObj)
    {
        if (statusObj.TryGetProperty("toolhead", out JsonElement toolhead) &&
            toolhead.ValueKind == JsonValueKind.Object &&
            toolhead.TryGetProperty("extruder", out JsonElement extruder))
        {
            return ReadExtruderIndex(extruder);
        }

        if (statusObj.TryGetProperty("print_task_config", out JsonElement config) &&
            config.ValueKind == JsonValueKind.Object)
        {
            foreach (string propertyName in new[] { "active_extruder", "active_extruder_index", "active_tool", "current_extruder" })
            {
                if (config.TryGetProperty(propertyName, out JsonElement activeTool))
                {
                    int? index = ReadExtruderIndex(activeTool);
                    if (index.HasValue)
                    {
                        return index;
                    }
                }
            }
        }

        return null;
    }

    private static int? ReadExtruderIndex(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numericIndex))
        {
            return numericIndex is >= 0 and < LaneCount ? numericIndex : null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string normalized = raw.Trim();
        if (int.TryParse(normalized, out int parsedIndex))
        {
            return parsedIndex is >= 0 and < LaneCount ? parsedIndex : null;
        }

        if (!normalized.StartsWith("extruder", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string suffix = normalized["extruder".Length..];
        if (suffix.Length == 0)
        {
            return 0;
        }

        return int.TryParse(suffix, out int extruderIndex) && extruderIndex is >= 0 and < LaneCount
            ? extruderIndex
            : null;
    }

    private static bool ReadBoolArrayProperty(JsonElement obj, string propertyName, int index)
    {
        return obj.TryGetProperty(propertyName, out JsonElement arr) &&
               arr.ValueKind == JsonValueKind.Array &&
               ReadBool(arr, index);
    }

    private static bool ReadBool(JsonElement arr, int index)
    {
        if (index < 0 || index >= arr.GetArrayLength())
        {
            return false;
        }

        JsonElement value = arr[index];
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Number => value.TryGetInt32(out int number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed)
                ? parsed
                : string.Equals(value.GetString(), "1", StringComparison.Ordinal),
            _ => false
        };
    }

    private static string? ReadStringArrayProperty(JsonElement obj, string propertyName, int index)
    {
        if (!obj.TryGetProperty(propertyName, out JsonElement arr) ||
            arr.ValueKind != JsonValueKind.Array ||
            index < 0 ||
            index >= arr.GetArrayLength())
        {
            return null;
        }

        JsonElement value = arr[index];
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string? NormalizeString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeSubType(string? value)
    {
        string? normalized = NormalizeString(value);
        return normalized is null || string.Equals(normalized, "NONE", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static string? ReadHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim().TrimStart('#');
        if (trimmed.Length < 6)
        {
            return null;
        }

        string rgb = trimmed[..6];
        return rgb.All(Uri.IsHexDigit) ? $"#{rgb.ToUpperInvariant()}" : null;
    }
}

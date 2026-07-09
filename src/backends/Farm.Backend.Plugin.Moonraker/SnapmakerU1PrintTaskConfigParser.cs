using System.Text.Json;

namespace Farm.Backend.Plugin.Moonraker;

internal sealed record SnapmakerU1PrintTaskConfigDelta(
    SnapmakerU1LaneDelta[] Lanes,
    int? ActiveTool,
    bool HasLaneFields);

internal sealed record SnapmakerU1LaneDelta(
    int Index,
    bool? Loaded = null,
    bool HasMaterial = false,
    string? Material = null,
    bool HasSubType = false,
    string? SubType = null,
    bool HasColor = false,
    string? Color = null,
    bool? Official = null);

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

    /// <summary>
    /// Parses a Moonraker delta for Snapmaker U1 lane fields without assuming every companion array is present.
    /// </summary>
    public static bool TryParseDelta(
        JsonElement statusObj,
        bool allowToolheadOnly,
        out SnapmakerU1PrintTaskConfigDelta delta)
    {
        int? activeTool = TryReadActiveTool(statusObj);

        if (!statusObj.TryGetProperty("print_task_config", out JsonElement config) ||
            config.ValueKind != JsonValueKind.Object)
        {
            delta = new SnapmakerU1PrintTaskConfigDelta([], activeTool, HasLaneFields: false);
            return allowToolheadOnly && activeTool.HasValue;
        }

        SnapmakerU1LaneDelta[] lanes = ParseLaneDeltas(config);
        bool hasLaneFields = lanes.Length > 0;
        delta = new SnapmakerU1PrintTaskConfigDelta(lanes, activeTool, hasLaneFields);
        return hasLaneFields || activeTool.HasValue;
    }

    public static SnapmakerU1LaneStatus[] CreateEmptyLanes()
    {
        SnapmakerU1LaneStatus[] lanes = new SnapmakerU1LaneStatus[LaneCount];
        for (int i = 0; i < lanes.Length; i++)
        {
            lanes[i] = new SnapmakerU1LaneStatus(i, Loaded: false, Material: null, SubType: null, Color: null, Official: false, IsActive: false);
        }

        return lanes;
    }

    private static SnapmakerU1LaneDelta[] ParseLaneDeltas(JsonElement config)
    {
        // Hardware-inferred from U1Hub/SnapCon and not yet confirmed on a real U1 (#685):
        // Snapmaker's Moonraker object appears to use filament_exist, filament_color_rgba,
        // filament_type, filament_sub_type, and filament_official. These differ from the
        // color_rgba/type/sub_type names quoted in #693, so parsing stays defensive.
        JsonElement filamentExist = GetArray(config, "filament_exist");
        JsonElement colorRgba = GetArray(config, "filament_color_rgba");
        JsonElement filamentType = GetArray(config, "filament_type");
        JsonElement filamentSubType = GetArray(config, "filament_sub_type");
        JsonElement filamentOfficial = GetArray(config, "filament_official");

        bool hasFilamentExist = filamentExist.ValueKind == JsonValueKind.Array;
        bool hasColor = colorRgba.ValueKind == JsonValueKind.Array;
        bool hasMaterial = filamentType.ValueKind == JsonValueKind.Array;
        bool hasSubType = filamentSubType.ValueKind == JsonValueKind.Array;
        bool hasOfficial = filamentOfficial.ValueKind == JsonValueKind.Array;

        if (!hasFilamentExist && !hasColor && !hasMaterial && !hasSubType && !hasOfficial)
        {
            return [];
        }

        List<SnapmakerU1LaneDelta> lanes = [];
        for (int i = 0; i < LaneCount; i++)
        {
            bool loaded = false;
            string? material = null;
            string? subType = null;
            string? color = null;
            bool official = false;

            bool laneHasLoaded = hasFilamentExist && TryReadBool(filamentExist, i, out loaded);
            bool laneHasMaterial = hasMaterial && TryReadString(filamentType, i, out material);
            bool laneHasSubType = hasSubType && TryReadString(filamentSubType, i, out subType);
            bool laneHasColor = hasColor && TryReadColor(colorRgba, i, out color);
            bool laneHasOfficial = hasOfficial && TryReadBool(filamentOfficial, i, out official);

            if (!laneHasLoaded && !laneHasMaterial && !laneHasSubType && !laneHasColor && !laneHasOfficial)
            {
                continue;
            }

            lanes.Add(new SnapmakerU1LaneDelta(
                i,
                Loaded: laneHasLoaded ? loaded : null,
                HasMaterial: laneHasMaterial,
                Material: laneHasMaterial ? NormalizeString(material) : null,
                HasSubType: laneHasSubType,
                SubType: laneHasSubType ? NormalizeSubType(subType) : null,
                HasColor: laneHasColor,
                Color: laneHasColor ? color : null,
                Official: laneHasOfficial ? official : null));
        }

        return lanes.ToArray();
    }

    private static JsonElement GetArray(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array
            ? arr
            : default;
    }

    private static int? TryReadActiveTool(JsonElement statusObj)
    {
        if (statusObj.TryGetProperty("toolhead", out JsonElement toolhead) &&
            toolhead.ValueKind == JsonValueKind.Object &&
            toolhead.TryGetProperty("extruder", out JsonElement extruder))
        {
            // Hardware-inferred from U1Hub/SnapCon and not yet confirmed on a real U1 (#685):
            // Snapmaker U1 active physical head appears in Klipper's zero-based toolhead.extruder value.
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

    private static bool TryReadBool(JsonElement arr, int index, out bool result)
    {
        result = false;
        if (index < 0 || index >= arr.GetArrayLength())
        {
            return false;
        }

        JsonElement value = arr[index];
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                result = false;
                return true;
            case JsonValueKind.Number:
                if (value.TryGetInt32(out int number))
                {
                    result = number != 0;
                    return true;
                }

                return false;
            case JsonValueKind.String:
                string? raw = value.GetString();
                if (bool.TryParse(raw, out bool parsed))
                {
                    result = parsed;
                    return true;
                }

                if (raw is "0" or "1")
                {
                    result = raw == "1";
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static bool TryReadString(JsonElement arr, int index, out string? result)
    {
        result = null;
        if (index < 0 || index >= arr.GetArrayLength())
        {
            return false;
        }

        JsonElement value = arr[index];
        if (value.ValueKind == JsonValueKind.String)
        {
            result = value.GetString();
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            result = value.GetRawText();
            return true;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return false;
    }

    private static bool TryReadColor(JsonElement arr, int index, out string? color)
    {
        color = null;
        if (!TryReadString(arr, index, out string? value))
        {
            return false;
        }

        color = ReadHexColor(value);
        return true;
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

        // Hardware-inferred from U1Hub/SnapCon and not yet confirmed on a real U1 (#685):
        // filament_color_rgba appears to carry RGBA hex; PrintFarmer stores RGB, so drop alpha.
        string rgb = trimmed[..6];
        return rgb.All(Uri.IsHexDigit) ? $"#{rgb.ToUpperInvariant()}" : null;
    }
}

using System.Globalization;
using System.Text.Json;

namespace Farm.Slicer.ProfileParsing;

/// <summary>
/// Field-specific extraction for the OrcaSlicer machine-profile facts needed by calibration
/// eligibility (#1613 §4.3): printable area/bed geometry, build volume, motion type, motion
/// limits, heated-bed/chamber flags, and active-nozzle facts. Property lookup is
/// case-insensitive and numeric/boolean getters are null-safe (absent or malformed input yields
/// <see langword="null"/> rather than throwing or defaulting), matching PR-1's
/// <c>CalibrationMachineProfileDeriver</c> semantics exactly — this class is that logic, lifted
/// so both <c>orcaslicer-worker</c> and the producer-side <c>CalibrationProfileResolver</c> can
/// share the same parsing instead of drifting (#1615).
/// </summary>
public static class OrcaMachineProfileFields
{
    /// <summary>
    /// Parses <c>printable_area</c> into its raw point list. OrcaSlicer represents this as either
    /// an array of point strings (e.g. <c>["0x0","250x0","250x250","0x250"]</c>) or a single
    /// comma-joined string (e.g. <c>"0x0,220x0,220x220,0x220"</c>); both forms are supported.
    /// Callers decide their own derived value from the point list (e.g. the worker takes a fixed
    /// index for build volume, while calibration derives bed origin/build volume from the
    /// bounding box) so this function stays a pure, policy-free parser.
    /// </summary>
    public static List<(double X, double Y)>? ParsePrintableAreaPoints(JsonElement root)
    {
        if (!TryGetProperty(root, "printable_area", out JsonElement value))
        {
            return null;
        }

        List<string> pointStrings = [];
        if (value.ValueKind == JsonValueKind.Array)
        {
            pointStrings.AddRange(value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!));
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            pointStrings.AddRange((value.GetString() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        List<(double X, double Y)> points = pointStrings
            .Select(TryParsePoint)
            .Where(point => point.HasValue)
            .Select(point => point!.Value)
            .ToList();

        return points.Count > 0 ? points : null;
    }

    public static double? ParsePrintableHeight(JsonElement root) =>
        GetFirstNumber(root, "printable_height") ?? GetFirstNumber(root, "max_print_height");

    public static double? ParseNozzleDiameter(JsonElement root) =>
        GetFirstNumber(root, "nozzle_diameter");

    /// <summary>Raw <c>nozzle_type</c> string. Mapping to the <c>NozzleType</c> enum is left to
    /// callers that can reference <c>Farm.Infrastructure</c> — this library stays enum-free.</summary>
    public static string? ParseNozzleTypeRaw(JsonElement root) =>
        GetFirstString(root, "nozzle_type");

    public static int? ParseMaxAccelerationX(JsonElement root) =>
        ToInt(GetFirstNumber(root, "machine_max_acceleration_x"));

    public static int? ParseMaxFeedrateX(JsonElement root) =>
        ToInt(GetFirstNumber(root, "machine_max_speed_x"));

    public static bool? ParseHasHeatedBed(JsonElement root) =>
        GetFirstBool(root, "has_heated_bed");

    public static bool? ParseHasHeatedChamber(JsonElement root) =>
        GetFirstBool(root, "has_heated_chamber");

    public static int? ParseMaxHotendTemperature(JsonElement root) =>
        ToInt(GetFirstNumber(root, "max_hotend_temp")) ?? ToInt(GetFirstNumber(root, "nozzle_temperature_range_high"));

    /// <summary>Raw motion-type string (<c>printer_type</c> falling back to <c>machine_type</c>).
    /// Mapping to the <c>CalibrationMotionType</c> enum is left to callers.</summary>
    public static string? ParseMotionTypeRaw(JsonElement root) =>
        GetFirstString(root, "printer_type") ?? GetFirstString(root, "machine_type");

    public static int? ToInt(double? value) =>
        value.HasValue ? (int)Math.Round(value.Value, MidpointRounding.AwayFromZero) : null;

    public static double? GetFirstNumber(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value))
        {
            return null;
        }

        JsonElement candidate = value.ValueKind == JsonValueKind.Array
            ? (value.GetArrayLength() > 0 ? value[0] : default)
            : value;
        return TryReadNumber(candidate, out double number) ? number : null;
    }

    public static string? GetFirstString(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value))
        {
            return null;
        }

        JsonElement candidate = value.ValueKind == JsonValueKind.Array
            ? (value.GetArrayLength() > 0 ? value[0] : default)
            : value;
        return candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : null;
    }

    public static bool? GetFirstBool(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value))
        {
            return null;
        }

        JsonElement candidate = value.ValueKind == JsonValueKind.Array
            ? (value.GetArrayLength() > 0 ? value[0] : default)
            : value;
        return candidate.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(candidate.GetString(), "true", StringComparison.OrdinalIgnoreCase)
                ? true
                : string.Equals(candidate.GetString(), "false", StringComparison.OrdinalIgnoreCase)
                    ? false
                    : null,
            _ => null,
        };
    }

    /// <summary>Case-insensitive property lookup, since OrcaSlicer JSON key casing is not
    /// guaranteed to be consistent across profile sources.</summary>
    public static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            JsonElement? match = root.EnumerateObject()
                .Where(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                .Select(p => (JsonElement?)p.Value)
                .FirstOrDefault();
            if (match.HasValue)
            {
                value = match.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static (double X, double Y)? TryParsePoint(string pointString)
    {
        string[] parts = pointString.Split(['x', 'X']);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
        {
            return (x, y);
        }

        return null;
    }

    private static bool TryReadNumber(JsonElement item, out double number)
    {
        if (item.ValueKind == JsonValueKind.Number)
        {
            return item.TryGetDouble(out number);
        }

        if (item.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(
                item.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number);
        }

        number = 0;
        return false;
    }
}

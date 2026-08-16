using System.Globalization;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>
/// Derives calibration-eligibility facts from the raw JSON of a resolved OrcaSlicer machine
/// profile (see #1613 §4.3/§4.4). This is the only place in <c>src/api</c> that understands the
/// OrcaSlicer profile-JSON shape; every other consumer works with the typed
/// <see cref="DerivedMachineFacts"/> result. Parsing is deliberately fail-safe: malformed or
/// absent input yields absent (<see langword="null"/>) facts rather than throwing, so a bad
/// profile degrades to "still missing" (an existing, well-understood rejection path) instead of
/// crashing eligibility evaluation.
/// </summary>
internal static class CalibrationMachineProfileDeriver
{
    public static DerivedMachineFacts Derive(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return DerivedMachineFacts.Empty;
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return DerivedMachineFacts.Empty;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return DerivedMachineFacts.Empty;
        }

        List<(double X, double Y)>? polygonPoints = ParsePrintableArea(root);
        IReadOnlyList<CalibrationPointDto>? printablePolygon = polygonPoints is { Count: > 0 }
            ? polygonPoints.Select(point => new CalibrationPointDto(point.X, point.Y)).ToArray()
            : null;

        double? bedOriginX = null;
        double? bedOriginY = null;
        double? buildVolumeX = null;
        double? buildVolumeY = null;
        if (polygonPoints is { Count: > 0 })
        {
            double minX = polygonPoints.Min(point => point.X);
            double minY = polygonPoints.Min(point => point.Y);
            double maxX = polygonPoints.Max(point => point.X);
            double maxY = polygonPoints.Max(point => point.Y);
            bedOriginX = minX;
            bedOriginY = minY;
            buildVolumeX = maxX - minX;
            buildVolumeY = maxY - minY;
        }

        double? buildVolumeZ = GetFirstNumber(root, "printable_height")
            ?? GetFirstNumber(root, "max_print_height");

        int? maxAcceleration = ToInt(GetFirstNumber(root, "machine_max_acceleration_x"));
        int? maxTravelSpeed = ToInt(GetFirstNumber(root, "machine_max_speed_x"));

        bool? hasHeatedBed = GetFirstBool(root, "has_heated_bed");
        bool? hasHeatedChamber = GetFirstBool(root, "has_heated_chamber");

        double? nozzleDiameter = GetFirstNumber(root, "nozzle_diameter");
        NozzleType? nozzleType = ParseNozzleType(GetFirstString(root, "nozzle_type"));

        int? maxHotendTemperature = ToInt(GetFirstNumber(root, "max_hotend_temp"))
            ?? ToInt(GetFirstNumber(root, "nozzle_temperature_range_high"));

        CalibrationMotionType? motionType =
            ParseMotionType(GetFirstString(root, "printer_type")) ??
            ParseMotionType(GetFirstString(root, "machine_type"));

        return new DerivedMachineFacts(
            printablePolygon,
            bedOriginX,
            bedOriginY,
            buildVolumeX,
            buildVolumeY,
            buildVolumeZ,
            motionType,
            maxAcceleration,
            maxTravelSpeed,
            hasHeatedBed,
            hasHeatedChamber,
            nozzleDiameter,
            nozzleType,
            maxHotendTemperature,
            maxHotendTemperature);
    }

    private static List<(double X, double Y)>? ParsePrintableArea(JsonElement root)
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

    private static (double X, double Y)? TryParsePoint(string pointString)
    {
        string[] parts = pointString.Split('x', 'X');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
        {
            return (x, y);
        }

        return null;
    }

    private static NozzleType? ParseNozzleType(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        string normalized = rawValue
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.ToLowerInvariant() switch
        {
            "brass" => NozzleType.Brass,
            "hardenedsteel" => NozzleType.HardenedSteel,
            "stainlesssteel" => NozzleType.StainlessSteel,
            "tungstencarbide" => NozzleType.TungstenCarbide,
            "abrasive" => NozzleType.Abrasive,
            _ => null,
        };
    }

    private static CalibrationMotionType? ParseMotionType(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        string normalized = rawValue
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.ToLowerInvariant() switch
        {
            "cartesian" => CalibrationMotionType.Cartesian,
            "corexy" or "corexy2" or "corexz" => CalibrationMotionType.CoreXY,
            "delta" or "kossel" => CalibrationMotionType.Delta,
            _ => null,
        };
    }

    private static int? ToInt(double? value) =>
        value.HasValue ? (int)Math.Round(value.Value, MidpointRounding.AwayFromZero) : null;

    private static double? GetFirstNumber(JsonElement root, string propertyName)
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

    private static string? GetFirstString(JsonElement root, string propertyName)
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

    private static bool? GetFirstBool(JsonElement root, string propertyName)
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

    private static bool TryGetProperty(
        JsonElement root,
        string propertyName,
        out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

/// <summary>
/// Facts derivable from a resolved OrcaSlicer machine profile's raw JSON, used as the
/// profile-derived fallback source in <c>coalesce(explicit override, profile-derived value)</c>
/// per #1613 §4.2. A <see langword="null"/> field means the profile did not assert that fact.
/// </summary>
internal readonly record struct DerivedMachineFacts(
    IReadOnlyList<CalibrationPointDto>? PrintablePolygon,
    double? BedOriginX,
    double? BedOriginY,
    double? BuildVolumeX,
    double? BuildVolumeY,
    double? BuildVolumeZ,
    CalibrationMotionType? MotionType,
    int? MaxAcceleration,
    int? MaxTravelSpeed,
    bool? HasHeatedBed,
    bool? HasHeatedChamber,
    double? NozzleDiameter,
    NozzleType? NozzleType,
    int? NozzleMaxTemperature,
    int? HotendMaxTemperature)
{
    public static DerivedMachineFacts Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
}

using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.ProfileParsing;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Facts derivable from a resolved OrcaSlicer machine profile's raw JSON (#1613 §4.3), used to
/// populate the typed derived fields on <see cref="ResolvedCalibrationProfile"/>. Extraction is
/// deliberately fail-safe: malformed or absent input yields absent (<see langword="null"/>)
/// facts rather than throwing, matching PR-1's (#1614) original <c>CalibrationMachineProfileDeriver</c>
/// semantics exactly &#8212; this is that logic, now producer-side so both the monolith and split
/// deployments populate it once via <see cref="OrcaMachineProfileFields"/> instead of leaving
/// <c>src/api</c> to re-parse the raw JSON itself (#1615 PR-2).
/// </summary>
public static class MachineProfileDerivedFieldsExtractor
{
    public static MachineProfileDerivedFields Extract(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return MachineProfileDerivedFields.Empty;
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return MachineProfileDerivedFields.Empty;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return MachineProfileDerivedFields.Empty;
        }

        List<(double X, double Y)>? polygonPoints = OrcaMachineProfileFields.ParsePrintableAreaPoints(root);
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

        double? buildVolumeZ = OrcaMachineProfileFields.ParsePrintableHeight(root);
        int? maxAcceleration = OrcaMachineProfileFields.ParseMaxAccelerationX(root);
        int? maxTravelSpeed = OrcaMachineProfileFields.ParseMaxFeedrateX(root);
        bool? hasHeatedBed = OrcaMachineProfileFields.ParseHasHeatedBed(root);
        bool? hasHeatedChamber = OrcaMachineProfileFields.ParseHasHeatedChamber(root);
        double? nozzleDiameter = OrcaMachineProfileFields.ParseNozzleDiameter(root);
        NozzleType? nozzleType = ParseNozzleType(OrcaMachineProfileFields.ParseNozzleTypeRaw(root));
        int? maxHotendTemperature = OrcaMachineProfileFields.ParseMaxHotendTemperature(root);
        CalibrationMotionType? motionType = ParseMotionType(OrcaMachineProfileFields.ParseMotionTypeRaw(root));

        return new MachineProfileDerivedFields(
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
            "diamond" => NozzleType.Diamond,
            "ruby" => NozzleType.Ruby,
            "platedcopper" => NozzleType.PlatedCopper,
            "toolsteel" => NozzleType.ToolSteel,
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
}

/// <summary>
/// Typed machine-profile facts derived from raw OrcaSlicer profile JSON (#1613 §4.3). A
/// <see langword="null"/> field means the profile did not assert that fact. Mirrors the shape of
/// PR-1's (#1614) internal <c>DerivedMachineFacts</c>, which <c>CalibrationMachineProfileDeriver</c>
/// now simply reads off <see cref="ResolvedCalibrationProfile"/> instead of re-deriving.
/// </summary>
public readonly record struct MachineProfileDerivedFields(
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
    public static MachineProfileDerivedFields Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
}

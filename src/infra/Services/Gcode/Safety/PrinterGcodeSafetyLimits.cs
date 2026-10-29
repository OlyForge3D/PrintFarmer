using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Gcode.Safety;

/// <summary>Builds the shared authoritative, non-calibration machine safety envelope.</summary>
public static class PrinterGcodeSafetyLimits
{
    public static GcodeSafetyLimits Build(Printer printer)
    {
        Toolhead? toolhead = printer.Toolheads?.FirstOrDefault(t => t.IsPrimary)
            ?? printer.Toolheads?.FirstOrDefault();
        return new(
            new(
                toolhead?.NozzleMaxTemperature,
                toolhead?.HotendMaxTemperature ?? toolhead?.HotendModel?.MaxTemp, toolhead?.IsDirectDrive),
            new(
                ToDecimal(printer.MaxBuildVolumeX), ToDecimal(printer.MaxBuildVolumeY),
                ToDecimal(printer.MaxBuildVolumeZ), ToDecimal(printer.BedOriginX), ToDecimal(printer.BedOriginY),
                ParsePolygon(printer.PrintablePolygonJson), ParseRegions(printer.ExcludedRegionsJson)),
            new(
                printer.MaxBedTemp, printer.HasHeatedChamber, printer.MaxChamberTemp,
                printer.MaxPrintSpeed, printer.MaxTravelSpeed, printer.MaxAcceleration),

            // Flow needs the loaded filament diameter, not an assumed machine default.
            GcodeSafetyPrintLimits.Empty);
    }

    private static decimal? ToDecimal(double? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!double.IsFinite(value.Value) || value > (double)decimal.MaxValue || value < (double)decimal.MinValue)
        {
            throw new GcodeSafetyGeometryException("A configured safety coordinate is not finite or representable.");
        }

        return (decimal)value.Value;
    }

    private static List<GcodeSafetyPoint> ParsePolygon(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : Points(Parse<List<Point?>>(json));

    private static List<GcodeSafetyExcludedRegion> ParseRegions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<Region?>? regions = Parse<List<Region?>>(json);
        List<GcodeSafetyExcludedRegion> result = [];
        foreach (Region? region in regions ?? [])
        {
            if (region is null)
            {
                throw new GcodeSafetyGeometryException("A configured excluded region is null.");
            }

            result.Add(new(region.Name ?? string.Empty, Points(region.Polygon)));
        }

        return result;
    }

    private static List<GcodeSafetyPoint> Points(IReadOnlyList<Point?>? points)
    {
        if (points is null || points.Count < 3)
        {
            throw new GcodeSafetyGeometryException("A configured safety polygon requires at least three points.");
        }

        return points.Select(point => point is null
            ? throw new GcodeSafetyGeometryException("A configured safety polygon contains a null point.")
            : new GcodeSafetyPoint(ToDecimal(point.X)!.Value, ToDecimal(point.Y)!.Value)).ToList();
    }

    private static T? Parse<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            throw new GcodeSafetyGeometryException("Configured safety geometry is malformed.");
        }
    }

    private sealed record Point(
        [property: JsonPropertyName("x"), JsonRequired] double X,
        [property: JsonPropertyName("y"), JsonRequired] double Y);

    private sealed record Region(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("polygon"), JsonRequired] IReadOnlyList<Point?>? Polygon);
}

/// <summary>Configured geometry cannot be silently treated as an unguarded machine.</summary>
public sealed class GcodeSafetyGeometryException : InvalidOperationException
{
    public GcodeSafetyGeometryException()
    {
    }

    public GcodeSafetyGeometryException(string message) : base(message)
    {
    }

    public GcodeSafetyGeometryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

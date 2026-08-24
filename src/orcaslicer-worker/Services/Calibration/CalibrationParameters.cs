using System.Text.Json;
using Farm.Slicer.Module.Models;

namespace Farm.OrcaSlicer.Worker.Services.Calibration;

/// <summary>
/// Resolved, method-specific calibration parameters, filled in from the client-supplied
/// <c>calibration.params</c> map (issue #1938) with sensible defaults for any key the client
/// omits.
/// </summary>
public sealed record CalibrationParameters
{
    /// <summary>Temperature of the bottom-most temperature tower band, in Celsius.</summary>
    public double StartTemperatureC { get; init; } = 230;

    /// <summary>Temperature decrease applied per band, in Celsius. The issue specifies 5C.</summary>
    public double TemperatureStepC { get; init; } = 5;

    /// <summary>Print height of each temperature tower band, in millimetres. The issue specifies 10mm.</summary>
    public double BandHeightMm { get; init; } = 10;

    /// <summary>Total number of temperature tower bands.</summary>
    public int BandCount { get; init; } = 9;

    /// <summary>
    /// Parses a job's <c>CalibrationParamsJson</c> (a flat <c>string, double</c> JSON object) into
    /// strongly typed parameters for <paramref name="method"/>, applying that method's defaults for
    /// any key that is absent or the JSON itself is null/blank.
    /// </summary>
    public static CalibrationParameters Parse(string? calibrationParamsJson, CalibrationMethod method)
    {
        var defaults = new CalibrationParameters();
        if (string.IsNullOrWhiteSpace(calibrationParamsJson))
        {
            return defaults;
        }

        Dictionary<string, double>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, double>>(calibrationParamsJson);
        }
        catch (JsonException)
        {
            // Malformed params never crash the slice; fall back to the method's defaults.
            return defaults;
        }

        if (values is null)
        {
            return defaults;
        }

        return method switch
        {
            CalibrationMethod.TemperatureTower => defaults with
            {
                StartTemperatureC = ReadOrDefault(values, "start_temperature", defaults.StartTemperatureC),
                TemperatureStepC = ReadOrDefault(values, "temperature_step", defaults.TemperatureStepC),
                BandHeightMm = ReadOrDefault(values, "band_height_mm", defaults.BandHeightMm),
                BandCount = (int)ReadOrDefault(values, "band_count", defaults.BandCount),
            },
            _ => defaults,
        };
    }

    private static double ReadOrDefault(Dictionary<string, double> values, string key, double fallback) =>
        values.TryGetValue(key, out double value) ? value : fallback;
}

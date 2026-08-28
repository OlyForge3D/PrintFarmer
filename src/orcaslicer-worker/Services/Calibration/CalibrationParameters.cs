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
    /// Permissive <c>filament_max_volumetric_speed</c> ceiling applied before slicing a max
    /// volumetric speed calibration (issue #2135), in mm³/s. Upstream's
    /// <c>CalibUtils::calib_max_vol_speed</c> hard-codes this to 50 so the slicer's own
    /// auto speed-limiting never clamps the print below the range the calibration tower's
    /// built-in, width-increasing geometry is designed to sweep through.
    /// </summary>
    public double MaxVolumetricSpeedCeilingMm3s { get; init; } = 50;

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
                StartTemperatureC = ReadOrDefault(values, "start_temperature", defaults.StartTemperatureC, MinTemperatureC, MaxTemperatureC),
                TemperatureStepC = ReadOrDefault(values, "temperature_step", defaults.TemperatureStepC, MinTemperatureStepC, MaxTemperatureStepC),
                BandHeightMm = ReadOrDefault(values, "band_height_mm", defaults.BandHeightMm, MinBandHeightMm, MaxBandHeightMm),
                BandCount = (int)ReadOrDefault(values, "band_count", defaults.BandCount, MinBandCount, MaxBandCount),
            },
            CalibrationMethod.MaximumVolumetricSpeed => defaults with
            {
                MaxVolumetricSpeedCeilingMm3s = ReadOrDefault(
                    values,
                    "max_volumetric_speed_ceiling_mm3s",
                    defaults.MaxVolumetricSpeedCeilingMm3s,
                    MinVolumetricSpeedCeilingBoundMm3s,
                    MaxVolumetricSpeedCeilingBoundMm3s),
            },
            _ => defaults,
        };
    }

    // Bounds below are deliberately generous but finite: they exist only to reject adversarial or
    // malformed client input (NaN/Infinity, absurd band counts that would blow up gcode-template
    // generation and worker memory/CPU), not to second-guess a legitimate calibration profile.
    private const double MinTemperatureC = 100;
    private const double MaxTemperatureC = 350;
    private const double MinTemperatureStepC = 0.1;
    private const double MaxTemperatureStepC = 50;
    private const double MinBandHeightMm = 0.1;
    private const double MaxBandHeightMm = 200;
    private const double MinBandCount = 1;
    private const double MaxBandCount = 50;

    // Upstream's constant ceiling is 50mm³/s; the bounds below give a little headroom for
    // unusual filaments/nozzles while still rejecting adversarial or malformed input.
    private const double MinVolumetricSpeedCeilingBoundMm3s = 1;
    private const double MaxVolumetricSpeedCeilingBoundMm3s = 100;

    /// <summary>
    /// Reads <paramref name="key"/> from <paramref name="values"/>, falling back to
    /// <paramref name="fallback"/> when the key is absent, non-finite (NaN/Infinity — never
    /// producible by valid client JSON but defensively rejected anyway), or outside
    /// [<paramref name="min"/>, <paramref name="max"/>]. This keeps a single adversarial or
    /// malformed value from forcing unbounded loop counts or nonsensical gcode temperatures.
    /// </summary>
    private static double ReadOrDefault(Dictionary<string, double> values, string key, double fallback, double min, double max)
    {
        if (!values.TryGetValue(key, out double value))
        {
            return fallback;
        }

        if (!double.IsFinite(value) || value < min || value > max)
        {
            return fallback;
        }

        return value;
    }
}

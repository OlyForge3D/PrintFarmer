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
    /// Pressure advance (Klipper) / linear advance (Marlin) value of the bottom-most pressure
    /// advance tower band. See <see cref="Farm.OrcaSlicer.Worker.Services.Calibration.PressureAdvanceTowerGcodeBuilder"/>.
    /// </summary>
    public double StartAdvance { get; init; } = 0.0;

    /// <summary>Pressure/linear advance increase applied per band.</summary>
    public double AdvanceStep { get; init; } = 0.002;

    /// <summary>
    /// Parses a job's <c>CalibrationParamsJson</c> (a flat <c>string, double</c> JSON object) into
    /// strongly typed parameters for <paramref name="method"/>, applying that method's defaults for
    /// any key that is absent or the JSON itself is null/blank.
    /// </summary>
    public static CalibrationParameters Parse(string? calibrationParamsJson, CalibrationMethod method)
    {
        var defaults = new CalibrationParameters();
        Dictionary<string, double> values = [];
        if (!string.IsNullOrWhiteSpace(calibrationParamsJson))
        {
            try
            {
                // An empty map falls through to the method's own defaults below, same as absent
                // JSON: every method-specific default (e.g. the pressure advance tower's
                // 5mm/20-band shape, vs. the temperature tower's 10mm/9-band shape) must be
                // applied via the switch below, not by returning the record's own field defaults
                // directly — those are shaped for the temperature tower only.
                values = JsonSerializer.Deserialize<Dictionary<string, double>>(calibrationParamsJson) ?? [];
            }
            catch (JsonException)
            {
                // Malformed params never crash the slice; fall back to the method's defaults.
            }
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
            CalibrationMethod.PressureAdvanceTower => BuildPressureAdvanceTowerParameters(defaults, values),
            _ => defaults,
        };
    }

    // Resolves the pressure advance tower's parameters, then clamps AdvanceStep so the tallest
    // band's compounded advance value (StartAdvance + (BandCount - 1) * AdvanceStep) never exceeds
    // MaxAdvance. Each individual field is already bounds-checked in isolation by ReadOrDefault,
    // but that alone does not stop e.g. StartAdvance=2.0, AdvanceStep=0.5, BandCount=50 from
    // compounding to an out-of-range 26.5 on the topmost band -- a real hardware-safety gap, since
    // this value is embedded directly into a SET_PRESSURE_ADVANCE/M900 K gcode command sent to the
    // printer.
    private static CalibrationParameters BuildPressureAdvanceTowerParameters(CalibrationParameters defaults, Dictionary<string, double> values)
    {
        double startAdvance = ReadOrDefault(values, "start_advance", PressureAdvanceTowerDefaults.StartAdvance, MinAdvance, MaxAdvance);
        double advanceStep = ReadOrDefault(values, "advance_step", PressureAdvanceTowerDefaults.AdvanceStep, MinAdvanceStep, MaxAdvanceStep);
        double bandHeightMm = ReadOrDefault(values, "band_height_mm", PressureAdvanceTowerDefaults.BandHeightMm, MinBandHeightMm, MaxBandHeightMm);
        int bandCount = (int)ReadOrDefault(values, "band_count", PressureAdvanceTowerDefaults.BandCount, MinBandCount, MaxBandCount);

        if (bandCount > 1)
        {
            // Only ever shrink the step, never grow it back up to MinAdvanceStep: when
            // startAdvance already leaves little or no headroom below MaxAdvance (e.g.
            // startAdvance == MaxAdvance), the only safe step is at or near zero, so the
            // safety cap must be allowed to go below MinAdvanceStep -- the invariant that the
            // topmost band never exceeds MaxAdvance takes priority over the "steps are at
            // least MinAdvanceStep" convenience floor.
            double maxStepForBounds = Math.Max(0.0, (MaxAdvance - startAdvance) / (bandCount - 1));
            advanceStep = Math.Min(advanceStep, maxStepForBounds);
        }

        return defaults with
        {
            StartAdvance = startAdvance,
            AdvanceStep = advanceStep,
            BandHeightMm = bandHeightMm,
            BandCount = bandCount,
        };
    }

    // The pressure advance tower's own defaults for the shared BandHeightMm/BandCount properties
    // (5mm/20 bands, i.e. a 100mm tower) differ from the temperature tower's (10mm/9 bands), so
    // they are applied explicitly above rather than inherited from `defaults`.
    private static class PressureAdvanceTowerDefaults
    {
        public const double StartAdvance = 0.0;
        public const double AdvanceStep = 0.002;
        public const double BandHeightMm = 5;
        public const double BandCount = 20;
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

    // Matches Farm.Modules.Calibration.Services.Calibration.CalibrationMeasurementRanges.PressureAdvance
    // (0.0-2.0) so the worker's own bounds-checking never diverges from the saga layer's.
    private const double MinAdvance = 0.0;
    private const double MaxAdvance = 2.0;
    private const double MinAdvanceStep = 0.0001;
    private const double MaxAdvanceStep = 0.5;

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

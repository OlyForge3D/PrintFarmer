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

    /// <summary>Retraction length of the bottom-most retraction tower band, in millimetres.</summary>
    public double StartRetractionMm { get; init; } = 0.2;

    /// <summary>Retraction length increase applied per band above band 0, in millimetres.</summary>
    public double RetractionStepMm { get; init; } = 0.2;

    /// <summary>Print height of each retraction tower band, in millimetres.</summary>
    public double RetractionBandHeightMm { get; init; } = 5;

    /// <summary>Total number of retraction tower bands.</summary>
    public int RetractionBandCount { get; init; } = 8;

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

        switch (method)
        {
            case CalibrationMethod.TemperatureTower:
                return defaults with
                {
                    StartTemperatureC = ReadOrDefault(values, "start_temperature", defaults.StartTemperatureC, MinTemperatureC, MaxTemperatureC),
                    TemperatureStepC = ReadOrDefault(values, "temperature_step", defaults.TemperatureStepC, MinTemperatureStepC, MaxTemperatureStepC),
                    BandHeightMm = ReadOrDefault(values, "band_height_mm", defaults.BandHeightMm, MinBandHeightMm, MaxBandHeightMm),
                    BandCount = (int)ReadOrDefault(values, "band_count", defaults.BandCount, MinBandCount, MaxBandCount),
                };
            case CalibrationMethod.Retraction:
                CalibrationParameters parsed = defaults with
                {
                    StartRetractionMm = ReadOrDefault(values, "start_retraction_mm", defaults.StartRetractionMm, MinRetractionMm, MaxRetractionMm),
                    RetractionStepMm = ReadOrDefault(values, "retraction_step_mm", defaults.RetractionStepMm, MinRetractionStepMm, MaxRetractionStepMm),
                    RetractionBandHeightMm = ReadOrDefault(values, "retraction_band_height_mm", defaults.RetractionBandHeightMm, MinRetractionBandHeightMm, MaxRetractionBandHeightMm),
                    RetractionBandCount = (int)ReadOrDefault(values, "retraction_band_count", defaults.RetractionBandCount, MinRetractionBandCount, MaxRetractionBandCount),
                };
                return ClampRetractionTopBand(parsed, defaults);
            default:
                return defaults;
        }
    }

    /// <summary>
    /// Per-field bounds checking in <see cref="ReadOrDefault"/> only constrains
    /// <c>StartRetractionMm</c>, not the retraction length the top band reaches
    /// (<c>StartRetractionMm + (RetractionBandCount - 1) * RetractionStepMm</c>). A client could
    /// otherwise combine in-range individual values (e.g. a high start, a large step, and many
    /// bands) into a top-band retraction far beyond <see cref="MaxRetractionMm"/> — well outside
    /// any printer's real firmware-retraction range. If the computed top band exceeds that bound,
    /// discard the whole parameter set and fall back to <paramref name="defaults"/> rather than
    /// silently clamping one field and leaving the others client-controlled.
    /// </summary>
    private static CalibrationParameters ClampRetractionTopBand(CalibrationParameters parameters, CalibrationParameters defaults)
    {
        double topBandRetractionMm = parameters.StartRetractionMm + ((parameters.RetractionBandCount - 1) * parameters.RetractionStepMm);
        return topBandRetractionMm > MaxRetractionMm ? defaults : parameters;
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

    // Same rationale as the temperature-tower bounds above, sized for a retraction-length sweep
    // (millimetres, not Celsius) rather than temperature.
    private const double MinRetractionMm = 0;
    private const double MaxRetractionMm = 10;
    private const double MinRetractionStepMm = 0.01;
    private const double MaxRetractionStepMm = 5;
    private const double MinRetractionBandHeightMm = 0.1;
    private const double MaxRetractionBandHeightMm = 200;
    private const double MinRetractionBandCount = 1;
    private const double MaxRetractionBandCount = 50;

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

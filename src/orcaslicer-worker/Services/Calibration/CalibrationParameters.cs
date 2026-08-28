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
    /// volumetric speed calibration (issue #2135), in mm³/s. Verified against upstream source
    /// (<c>CalibUtils::calib_max_vol_speed</c>, <c>src/slicer/Utils/CalibUtils.cpp</c> in
    /// OrcaSlicer/OrcaSlicer): <c>filament_config.set_key_value("filament_max_volumetric_speed",
    /// new ConfigOptionFloats{50})</c> — the constant really is 50, not a larger value.
    /// <para>
    /// <strong>This ceiling is not upstream's sweep mechanism</strong> (an earlier revision of
    /// this comment claimed it was; that was checked against upstream source during a later
    /// review round and found wrong). Upstream's actual per-layer speed variation comes from a
    /// live, in-process <c>Print::set_calib_params</c>/<c>calib_mode()</c> override consumed by a
    /// <c>GCode.cpp</c> switch (<c>case CalibMode::Calib_Vol_speed_Tower: ... start + print_z *
    /// step ...</c>) — set only by the GUI wizard immediately before slicing in the same process,
    /// never persisted to the 3MF project file, and with no CLI flag. It is therefore not
    /// reachable from this worker's separate-process, CLI-driven pipeline at all (see the fuller
    /// explanation and citations on <see cref="Farm.Slicer.Module.Models.CalibrationMethod"/>).
    /// This ceiling remains a safety margin only, exactly as it is upstream: it keeps OrcaSlicer's
    /// ordinary flow-based auto speed-limiting from clamping the client-selected process profile's
    /// print speed below what the bundled tower geometry needs while slicing proceeds at that
    /// profile's own constant speed.
    /// </para>
    /// </summary>
    public double MaxVolumetricSpeedCeilingMm3s { get; init; } = 50;

    /// <summary>
    /// Target firmware flavor for an input shaping / resonance-compensation calibration (issue
    /// #2139), from <c>calibration.firmwareFlavor</c>. Report-only: the worker never writes
    /// firmware configuration itself, but it must know which firmware the operator targets so it
    /// can refuse an unsupported or missing flavor explicitly (see
    /// <see cref="InputShapingFirmwareFlavors.IsSupported"/>) rather than silently slicing a
    /// ringing tower the operator has no firmware-specific guidance to act on.
    /// <see langword="null"/> means the client did not supply one at all.
    /// </summary>
    public string? FirmwareFlavor { get; init; }

    /// <summary>
    /// Parses a job's <c>CalibrationParamsJson</c> into strongly typed parameters for
    /// <paramref name="method"/>, applying that method's defaults for any key that is absent,
    /// the wrong JSON kind, or the JSON itself is null/blank/malformed.
    /// </summary>
    /// <remarks>
    /// Parses per-key via <see cref="JsonDocument"/> rather than deserializing the whole payload
    /// into a single <c>Dictionary&lt;string, double&gt;</c> (as earlier revisions did), because
    /// <see cref="CalibrationMethod.InputShaping"/> (issue #2139) needs a string <see cref="FirmwareFlavor"/>
    /// alongside every other method's numeric-only params in the same JSON object; a
    /// whole-payload <c>Dictionary&lt;string, double&gt;</c> deserialization throws and falls back
    /// to <em>all</em> defaults the instant any key holds a string, which would make a firmware
    /// flavor unparseable. Per-key extraction keeps every existing numeric method's behavior
    /// unchanged (a non-numeric value at a numeric key still falls back to that key's own
    /// default, exactly as the old whole-payload catch-all did) while adding string support only
    /// where a method actually asks for it.
    /// </remarks>
    public static CalibrationParameters Parse(string? calibrationParamsJson, CalibrationMethod method)
    {
        var defaults = new CalibrationParameters();
        if (string.IsNullOrWhiteSpace(calibrationParamsJson))
        {
            return defaults;
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(calibrationParamsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Malformed params never crash the slice; fall back to the method's defaults.
            return defaults;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return defaults;
        }

        return method switch
        {
            CalibrationMethod.TemperatureTower => defaults with
            {
                StartTemperatureC = ReadOrDefault(root, "start_temperature", defaults.StartTemperatureC, MinTemperatureC, MaxTemperatureC),
                TemperatureStepC = ReadOrDefault(root, "temperature_step", defaults.TemperatureStepC, MinTemperatureStepC, MaxTemperatureStepC),
                BandHeightMm = ReadOrDefault(root, "band_height_mm", defaults.BandHeightMm, MinBandHeightMm, MaxBandHeightMm),
                BandCount = (int)ReadOrDefault(root, "band_count", defaults.BandCount, MinBandCount, MaxBandCount),
            },
            CalibrationMethod.MaximumVolumetricSpeed => defaults with
            {
                MaxVolumetricSpeedCeilingMm3s = ReadOrDefault(
                    root,
                    "max_volumetric_speed_ceiling_mm3s",
                    defaults.MaxVolumetricSpeedCeilingMm3s,
                    MinVolumetricSpeedCeilingBoundMm3s,
                    MaxVolumetricSpeedCeilingBoundMm3s),
            },
            CalibrationMethod.InputShaping => defaults with
            {
                FirmwareFlavor = ReadStringOrDefault(root, "firmware_flavor", defaults.FirmwareFlavor),
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
    /// Reads <paramref name="key"/> from <paramref name="root"/>, falling back to
    /// <paramref name="fallback"/> when the key is absent, not a JSON number, non-finite
    /// (NaN/Infinity — never producible by valid client JSON but defensively rejected anyway), or
    /// outside [<paramref name="min"/>, <paramref name="max"/>]. This keeps a single adversarial
    /// or malformed value from forcing unbounded loop counts or nonsensical gcode temperatures.
    /// </summary>
    private static double ReadOrDefault(JsonElement root, string key, double fallback, double min, double max)
    {
        if (!root.TryGetProperty(key, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out double value))
        {
            return fallback;
        }

        if (!double.IsFinite(value) || value < min || value > max)
        {
            return fallback;
        }

        return value;
    }

    /// <summary>
    /// Reads <paramref name="key"/> from <paramref name="root"/> as a string, falling back to
    /// <paramref name="fallback"/> when the key is absent, blank, or not a JSON string.
    /// </summary>
    private static string? ReadStringOrDefault(JsonElement root, string key, string? fallback)
    {
        if (!root.TryGetProperty(key, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        string? value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}

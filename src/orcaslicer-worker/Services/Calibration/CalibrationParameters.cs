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
    /// Permissive <c>filament_max_volumetric_speed</c> ceiling applied before slicing a VFA
    /// (resonance speed) calibration (issue #2140), in mm³/s. Verified against upstream source
    /// (<c>CalibUtils::calib_VFA</c>, <c>src/slic3r/Utils/CalibUtils.cpp</c> in
    /// OrcaSlicer/OrcaSlicer): <c>filament_config.set_key_value("filament_max_volumetric_speed",
    /// new ConfigOptionFloats{200})</c> — VFA's own outer-wall speed sweep runs faster than
    /// <see cref="MaxVolumetricSpeedCeilingMm3s"/>'s 50 would allow, so upstream raises this
    /// method's ceiling separately rather than reusing the max-volumetric-speed method's value.
    /// <para>
    /// Exactly as documented for <see cref="MaxVolumetricSpeedCeilingMm3s"/>, this ceiling is not
    /// upstream's sweep mechanism: VFA's actual per-layer outer wall speed ramp comes from the
    /// same live, in-process <c>Print::set_calib_params</c>/<c>calib_mode()</c> override
    /// (<c>GCode.cpp</c>'s <c>case CalibMode::Calib_VFA_Tower</c>), set only by the GUI wizard
    /// immediately before slicing, never persisted to the 3MF project file, and with no CLI flag
    /// — so it is not reachable from this worker's separate-process, CLI-driven pipeline (see the
    /// fuller explanation on <see cref="Farm.Slicer.Module.Models.CalibrationMethod"/>). This
    /// ceiling remains a safety margin only: it keeps OrcaSlicer's ordinary flow-based auto
    /// speed-limiting from clamping the client-selected process profile's print speed below what
    /// the bundled tower geometry needs while slicing proceeds at that profile's own constant
    /// speed.
    /// </para>
    /// </summary>
    public double VfaMaxVolumetricSpeedCeilingMm3s { get; init; } = 200;

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
            CalibrationMethod.Retraction => ClampRetractionTopBand(
                defaults with
                {
                    StartRetractionMm = ReadOrDefault(values, "start_retraction_mm", defaults.StartRetractionMm, MinRetractionMm, MaxRetractionMm),
                    RetractionStepMm = ReadOrDefault(values, "retraction_step_mm", defaults.RetractionStepMm, MinRetractionStepMm, MaxRetractionStepMm),
                    RetractionBandHeightMm = ReadOrDefault(values, "retraction_band_height_mm", defaults.RetractionBandHeightMm, MinRetractionBandHeightMm, MaxRetractionBandHeightMm),
                    RetractionBandCount = (int)ReadOrDefault(values, "retraction_band_count", defaults.RetractionBandCount, MinRetractionBandCount, MaxRetractionBandCount),
                },
                defaults),
            CalibrationMethod.MaximumVolumetricSpeed => defaults with
            {
                MaxVolumetricSpeedCeilingMm3s = ReadOrDefault(
                    values,
                    "max_volumetric_speed_ceiling_mm3s",
                    defaults.MaxVolumetricSpeedCeilingMm3s,
                    MinVolumetricSpeedCeilingBoundMm3s,
                    MaxVolumetricSpeedCeilingBoundMm3s),
            },
            CalibrationMethod.PressureAdvanceTower => BuildPressureAdvanceTowerParameters(defaults, values),
            CalibrationMethod.Vfa => defaults with
            {
                VfaMaxVolumetricSpeedCeilingMm3s = ReadOrDefault(
                    values,
                    "vfa_max_volumetric_speed_ceiling_mm3s",
                    defaults.VfaMaxVolumetricSpeedCeilingMm3s,
                    MinVfaVolumetricSpeedCeilingBoundMm3s,
                    MaxVfaVolumetricSpeedCeilingBoundMm3s),
            },
            _ => defaults,
        };
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

    // Resolves the pressure advance tower's parameters, then either clamps AdvanceStep so the
    // tallest band's compounded advance value (StartAdvance + (BandCount - 1) * AdvanceStep)
    // never exceeds MaxAdvance, or refuses the combination outright when no clamp can produce a
    // meaningful sweep. Each individual field is already bounds-checked in isolation by
    // ReadOrDefault, but that alone does not stop e.g. StartAdvance=2.0, AdvanceStep=0.5,
    // BandCount=50 from compounding to an out-of-range 26.5 on the topmost band -- a real
    // hardware-safety gap, since this value is embedded directly into a SET_PRESSURE_ADVANCE/M900
    // K gcode command sent to the printer.
    private static CalibrationParameters BuildPressureAdvanceTowerParameters(CalibrationParameters defaults, Dictionary<string, double> values)
    {
        double startAdvance = ReadOrDefault(values, "start_advance", PressureAdvanceTowerDefaults.StartAdvance, MinAdvance, MaxAdvance);
        double advanceStep = ReadOrDefault(values, "advance_step", PressureAdvanceTowerDefaults.AdvanceStep, MinAdvanceStep, MaxAdvanceStep);
        double bandHeightMm = ReadOrDefault(values, "band_height_mm", PressureAdvanceTowerDefaults.BandHeightMm, MinBandHeightMm, MaxBandHeightMm);
        int bandCount = (int)ReadOrDefault(values, "band_count", PressureAdvanceTowerDefaults.BandCount, MinBandCount, MaxBandCount);

        if (bandCount > 1)
        {
            // The headroom between StartAdvance and MaxAdvance, spread across BandCount - 1
            // steps, is the largest step that keeps the topmost band in bounds.
            double maxStepForBounds = (MaxAdvance - startAdvance) / (bandCount - 1);
            if (maxStepForBounds < MinAdvanceStep)
            {
                // There is no room for even the smallest meaningful step: silently clamping here
                // would produce a "tower" whose bands all emit (near-)identical advance values --
                // a calibration print that runs to completion and reports success while measuring
                // nothing, exactly the silent-no-op failure mode this method must refuse instead
                // of hiding. Reject explicitly so the caller sees a clear reason instead of a
                // seemingly successful but useless print.
                throw new InvalidOperationException(
                    $"Pressure advance tower parameters cannot produce a meaningful sweep: start_advance " +
                    $"({startAdvance}) leaves no room for a distinguishable advance_step across " +
                    $"band_count ({bandCount}) bands within the supported [{MinAdvance}, {MaxAdvance}] range. " +
                    $"Lower start_advance or band_count.");
            }

            // Only ever shrink the step, never grow it back up to a value the client didn't ask
            // for: the invariant that the topmost band never exceeds MaxAdvance takes priority
            // over honouring a client-requested step that would overflow it.
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

    // Matches Farm.Modules.Calibration.Services.Calibration.CalibrationMeasurementRanges.PressureAdvance
    // (0.0-2.0) so the worker's own bounds-checking never diverges from the saga layer's.
    private const double MinAdvance = 0.0;
    private const double MaxAdvance = 2.0;
    private const double MinAdvanceStep = 0.0001;
    private const double MaxAdvanceStep = 0.5;

    // Upstream's constant ceiling is 50mm³/s; the bounds below give a little headroom for
    // unusual filaments/nozzles while still rejecting adversarial or malformed input.
    private const double MinVolumetricSpeedCeilingBoundMm3s = 1;
    private const double MaxVolumetricSpeedCeilingBoundMm3s = 100;

    // Upstream's constant ceiling for VFA specifically is 200mm³/s (CalibUtils::calib_VFA), a
    // separate, higher value than MaximumVolumetricSpeed's own 50mm³/s ceiling above; the bounds
    // below give a little headroom while still rejecting adversarial or malformed input.
    private const double MinVfaVolumetricSpeedCeilingBoundMm3s = 1;
    private const double MaxVfaVolumetricSpeedCeilingBoundMm3s = 250;

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

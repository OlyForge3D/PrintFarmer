using Farm.Slicer.Module.Models;

namespace Farm.Modules.Calibration.Services.Calibration;

/// <summary>
/// Saga-specific domain behavior keyed by the shared <see cref="CalibrationMethod"/> vocabulary
/// (<c>Farm.Slicer.Module.Models</c>) - step sequencing, calibration "kind" classification, and
/// plausible measurement ranges for the printer/toolhead <c>/api/calibration-projects</c> saga.
/// </summary>
/// <remarks>
/// Before issue #2161's unification, this file's predecessor (<c>CalibrationMethodNames.cs</c>)
/// also defined the saga's own, separately-evolving <c>CalibrationMethod</c> enum and wire-name
/// dictionary, which disagreed with the slicer's own <see cref="CalibrationMethods"/> for 9 of 15
/// wire names - a live bug, since <see cref="CalibrationOrchestrationSagaService.BuildSliceSubmissionBody"/>
/// posted the saga's own name straight to the real <c>POST /api/slice</c>, which
/// <c>SliceJobController</c> parses against <see cref="CalibrationMethods"/>. That duplicate
/// vocabulary is deleted; this file keeps only the saga-specific behavior (sequencing,
/// classification, ranges) that isn't vocabulary at all, retargeted onto the one canonical type.
/// See the mapping table on <see cref="CalibrationMethod"/>'s own remarks for the full
/// old-name-to-new-name correspondence.
/// </remarks>
public static class CalibrationMethodKinds
{
    /// <summary>
    /// Maps a method to the calibration kind recorded on the immutable attempt.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>The stable calibration kind.</returns>
    public static string ToKind(CalibrationMethod method) => method switch
    {
        CalibrationMethod.TemperatureTower => "temperature",
        CalibrationMethod.FlowRatePass1 or
        CalibrationMethod.FlowRatePass2 or
        CalibrationMethod.FlowRateYoloRecommended or
        CalibrationMethod.FlowRateYoloPerfectionist or
        CalibrationMethod.FlowVerification => "flow",
        CalibrationMethod.PressureAdvanceTower or
        CalibrationMethod.PressureAdvanceLine or
        CalibrationMethod.PressureAdvancePattern => "pressure_advance",
        CalibrationMethod.Retraction => "retraction",
        CalibrationMethod.MaximumVolumetricSpeed => "max_volumetric_speed",
        CalibrationMethod.Shrinkage => "shrinkage",
        CalibrationMethod.FinalVerification => "verification",
        CalibrationMethod.Cornering => "cornering",
        CalibrationMethod.InputShaping => "input_shaping",
        CalibrationMethod.Vfa => "resonance_speed",
        _ => throw new ArgumentOutOfRangeException(
            nameof(method),
            method,
            "Unknown calibration method."),
    };
}

/// <summary>
/// Canonical, server-authoritative per-method step sequences (D8). Every recognized
/// <see cref="CalibrationMethod"/> currently advances through the same fixed wizard
/// steps; the sequence is keyed per method (rather than a single global constant) so a
/// future method can diverge without an API shape change.
/// </summary>
public static class CalibrationMethodSteps
{
    /// <summary>Operator configures the run (filament, printer, parameters).</summary>
    public const string Setup = "setup";

    /// <summary>The calibration test object is sliced and printed.</summary>
    public const string Print = "print";

    /// <summary>The operator captures or enters a measurement.</summary>
    public const string Measure = "measure";

    /// <summary>The operator reviews and selects the accepted result.</summary>
    public const string Select = "select";

    private static readonly IReadOnlyList<string> DefaultSequence = [Setup, Print, Measure, Select];

    /// <summary>Gets the canonical, ordered step sequence for a calibration method.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The ordered step ids the method must advance through.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The method is not declared.</exception>
    public static IReadOnlyList<string> GetSequence(CalibrationMethod method) => method switch
    {
        CalibrationMethod.TemperatureTower or
        CalibrationMethod.FlowRatePass1 or
        CalibrationMethod.FlowRatePass2 or
        CalibrationMethod.FlowRateYoloRecommended or
        CalibrationMethod.FlowRateYoloPerfectionist or
        CalibrationMethod.PressureAdvanceTower or
        CalibrationMethod.PressureAdvanceLine or
        CalibrationMethod.PressureAdvancePattern or
        CalibrationMethod.FlowVerification or
        CalibrationMethod.Retraction or
        CalibrationMethod.MaximumVolumetricSpeed or
        CalibrationMethod.Shrinkage or
        CalibrationMethod.FinalVerification or
        CalibrationMethod.Cornering or
        CalibrationMethod.InputShaping or
        CalibrationMethod.Vfa => DefaultSequence,
        _ => throw new ArgumentOutOfRangeException(
            nameof(method),
            method,
            "Unknown calibration method."),
    };

    /// <summary>Gets the zero-based index of a step id within a method's canonical sequence.</summary>
    /// <param name="method">The method.</param>
    /// <param name="stepId">The candidate step id.</param>
    /// <returns>The zero-based index, or -1 when the step id is not part of the method's sequence.</returns>
    public static int IndexOf(CalibrationMethod method, string stepId)
    {
        IReadOnlyList<string> sequence = GetSequence(method);
        for (int index = 0; index < sequence.Count; index++)
        {
            if (string.Equals(sequence[index], stepId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>Inclusive plausible value range for one semantic calibration measurement kind.</summary>
/// <param name="MeasurementKey">The property name expected under an observation's <c>measurements</c> payload.</param>
/// <param name="Minimum">The inclusive minimum plausible value.</param>
/// <param name="Maximum">The inclusive maximum plausible value.</param>
public sealed record CalibrationMeasurementRange(string MeasurementKey, decimal Minimum, decimal Maximum);

/// <summary>
/// Canonical per-kind plausible measurement ranges (D8), used to reject physically
/// implausible submitted values server-side. Keyed by the stable calibration kind
/// (<see cref="CalibrationMethodKinds.ToKind(CalibrationMethod)"/>) rather than by method,
/// since every method sharing a kind measures the same physical quantity.
/// </summary>
public static class CalibrationMeasurementRanges
{
    /// <summary>Plausible nozzle/bed temperature range in degrees Celsius.</summary>
    public static readonly CalibrationMeasurementRange Temperature = new("temperature_c", 150m, 320m);

    /// <summary>Plausible flow ratio (extrusion multiplier) range.</summary>
    public static readonly CalibrationMeasurementRange FlowRatio = new("flow_ratio", 0.5m, 1.5m);

    /// <summary>Plausible pressure advance (linear advance) coefficient range.</summary>
    public static readonly CalibrationMeasurementRange PressureAdvance = new("pressure_advance", 0.0m, 2.0m);

    /// <summary>Plausible retraction length range in millimetres.</summary>
    public static readonly CalibrationMeasurementRange RetractionLength = new("retraction_length_mm", 0.0m, 10.0m);

    /// <summary>
    /// Plausible maximum volumetric speed range in mm³/s for a user-reported calibration
    /// <em>observation</em> — the value the user settles on after inspecting the printed tower,
    /// not the ceiling written into the filament profile before slicing (issue #2135). Upstream's
    /// <c>CalibUtils::calib_max_vol_speed</c> uses a 50mm³/s slicing-time ceiling (see the
    /// orcaslicer-worker's <c>CalibrationParameters.MaxVolumetricSpeedCeilingMm3s</c>) purely so the print
    /// isn't clamped below the sweep the tower geometry produces; a well-tuned filament can then
    /// legitimately report an observed ceiling anywhere up to that value, so the upper bound here
    /// is intentionally set at 60 — a little above the slicing ceiling — to admit filaments/
    /// nozzles that tolerate slightly more without accepting physically implausible submissions.
    /// </summary>
    public static readonly CalibrationMeasurementRange MaximumVolumetricSpeed = new("max_volumetric_speed_mm3_s", 1m, 60m);

    /// <summary>Gets the canonical measurement range for a calibration kind, if one is defined.</summary>
    /// <param name="kind">The stable calibration kind, e.g. from <see cref="CalibrationMethodKinds.ToKind(CalibrationMethod)"/>.</param>
    /// <returns>The range, or <see langword="null"/> when the kind has no defined semantic range.</returns>
    public static CalibrationMeasurementRange? ForKind(string kind) => kind switch
    {
        "temperature" => Temperature,
        "flow" => FlowRatio,
        "pressure_advance" => PressureAdvance,
        "retraction" => RetractionLength,
        "max_volumetric_speed" => MaximumVolumetricSpeed,
        _ => null,
    };
}

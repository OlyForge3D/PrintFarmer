using System.Text.Json;
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
        CalibrationMethod.FinalVerification => DefaultSequence,
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

/// <summary>
/// A required <c>setup</c>-step input declared for a calibration method (issue #2180, gap 4),
/// e.g. a temperature tower's start/end range or a max-volumetric-speed sweep range. Distinct
/// from <see cref="CalibrationMeasurementRange"/>: setup inputs bound what the operator supplies
/// before printing, not what is measured afterward.
/// </summary>
/// <param name="Key">The property name expected under an attempt's <c>specification</c> payload.</param>
/// <param name="Label">A short, human-readable display label for the input.</param>
/// <param name="Unit">The unit the value is expressed in.</param>
/// <param name="Minimum">The inclusive minimum plausible value.</param>
/// <param name="Maximum">The inclusive maximum plausible value.</param>
public sealed record CalibrationSetupInput(string Key, string Label, string Unit, decimal Minimum, decimal Maximum);

/// <summary>
/// Server-owned, per-method guided-session metadata (issue #2180, gap 3): display title,
/// purpose, wiki reference, and the <c>setup</c> step's required inputs. Previously this lived
/// only in the desktop client (<c>FILAMENT_METHOD_META</c>), which drifted from the server; this
/// catalog is now the single source of truth served to every client.
/// </summary>
/// <param name="Title">A short display title for the method.</param>
/// <param name="Purpose">A short explanation of what the method calibrates and why.</param>
/// <param name="WikiUrl">A reference URL with detailed guidance for the method.</param>
/// <param name="SetupInputs">The inputs the <c>setup</c> step must collect for this method.</param>
public sealed record CalibrationMethodGuidance(
    string Title,
    string Purpose,
    string WikiUrl,
    IReadOnlyList<CalibrationSetupInput> SetupInputs);

/// <summary>
/// Canonical per-method guidance catalog (issue #2180, gaps 3 and 4). Server-authoritative so
/// clients never duplicate or drift from this metadata, and so <c>setup</c> inputs are validated
/// the same way regardless of which client submitted them.
/// </summary>
public static class CalibrationMethodGuidanceCatalog
{
    private const string WikiBase = "https://github.com/OlyForge3D/PrintFarmer/wiki/Calibration-";

    private static readonly IReadOnlyList<CalibrationSetupInput> NoSetupInputs = [];

    private static readonly IReadOnlyList<CalibrationSetupInput> TemperatureTowerInputs =
    [
        new("start_temperature_c", "Tower start temperature", "°C", 150m, 320m),
        new("end_temperature_c", "Tower end temperature", "°C", 150m, 320m),
    ];

    private static readonly IReadOnlyList<CalibrationSetupInput> MaximumVolumetricSpeedInputs =
    [
        new("sweep_start_mm3_s", "Sweep start speed", "mm3/s", 1m, 60m),
        new("sweep_end_mm3_s", "Sweep end speed", "mm3/s", 1m, 60m),
    ];

    /// <summary>Gets the declared guidance for a calibration method.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The method's title, purpose, wiki reference, and required setup inputs.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The method is not declared.</exception>
    public static CalibrationMethodGuidance ForMethod(CalibrationMethod method) => method switch
    {
        CalibrationMethod.TemperatureTower => new(
            "Temperature Tower",
            "Finds the best nozzle temperature for this filament by printing a tower of segments at descending temperatures.",
            WikiBase + "Temperature-Tower",
            TemperatureTowerInputs),
        CalibrationMethod.FlowRatePass1 or
        CalibrationMethod.FlowRatePass2 or
        CalibrationMethod.FlowRateYoloRecommended or
        CalibrationMethod.FlowRateYoloPerfectionist => new(
            "Flow Rate",
            "Finds the flow ratio (extrusion multiplier) that produces accurate wall thickness.",
            WikiBase + "Flow-Rate",
            NoSetupInputs),
        CalibrationMethod.FlowVerification => new(
            "Flow Verification",
            "Verifies a previously calibrated flow ratio still produces accurate wall thickness.",
            WikiBase + "Flow-Rate",
            NoSetupInputs),
        CalibrationMethod.PressureAdvanceTower or
        CalibrationMethod.PressureAdvanceLine or
        CalibrationMethod.PressureAdvancePattern => new(
            "Pressure Advance",
            "Finds the pressure/linear advance coefficient that minimizes corner bulging and stringing.",
            WikiBase + "Pressure-Advance",
            NoSetupInputs),
        CalibrationMethod.Retraction => new(
            "Retraction",
            "Finds the retraction length that minimizes stringing without under-extrusion.",
            WikiBase + "Retraction",
            NoSetupInputs),
        CalibrationMethod.MaximumVolumetricSpeed => new(
            "Maximum Volumetric Speed",
            "Finds the highest volumetric flow this filament/nozzle combination can sustain without under-extrusion.",
            WikiBase + "Maximum-Volumetric-Speed",
            MaximumVolumetricSpeedInputs),
        CalibrationMethod.Shrinkage => new(
            "Shrinkage",
            "Measures dimensional shrinkage so parts can be scaled to compensate.",
            WikiBase + "Shrinkage",
            NoSetupInputs),
        CalibrationMethod.FinalVerification => new(
            "Final Verification",
            "Prints a combined verification model to confirm the full calibrated profile.",
            WikiBase + "Final-Verification",
            NoSetupInputs),
        _ => throw new ArgumentOutOfRangeException(
            nameof(method),
            method,
            "Unknown calibration method."),
    };

    /// <summary>Gets the measurement quantity the <c>measure</c> step expects, if this method's kind defines one.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The expected measurement range, or <see langword="null"/> when the kind has none defined.</returns>
    public static CalibrationMeasurementRange? MeasureQuantityFor(CalibrationMethod method) =>
        CalibrationMeasurementRanges.ForKind(CalibrationMethodKinds.ToKind(method));

    /// <summary>
    /// Validates a submitted <c>setup</c>/<c>specification</c> payload against the method's
    /// declared required inputs (issue #2180, gap 4), mirroring
    /// <see cref="CalibrationMeasurementRanges"/>'s server-side range enforcement for measurements.
    /// </summary>
    /// <param name="method">The attempt's recognized method.</param>
    /// <param name="specification">The submitted <c>specification</c> JSON payload.</param>
    /// <returns>A validation error code, or <see langword="null"/> when the method has no required inputs or all are present and in range.</returns>
    public static string? ValidateSetupInputs(CalibrationMethod method, JsonElement specification)
    {
        IReadOnlyList<CalibrationSetupInput> inputs = ForMethod(method).SetupInputs;
        if (inputs.Count == 0)
        {
            return null;
        }

        if (specification.ValueKind != JsonValueKind.Object)
        {
            return "setup_input_invalid";
        }

        foreach (CalibrationSetupInput input in inputs)
        {
            if (!specification.TryGetProperty(input.Key, out JsonElement valueElement))
            {
                return "setup_input_missing";
            }

            if (valueElement.ValueKind != JsonValueKind.Number || !valueElement.TryGetDecimal(out decimal value))
            {
                return "setup_input_invalid";
            }

            if (value < input.Minimum || value > input.Maximum)
            {
                return "setup_input_out_of_range";
            }
        }

        return null;
    }
}

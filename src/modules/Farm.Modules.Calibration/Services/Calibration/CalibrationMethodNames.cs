namespace Farm.Modules.Calibration.Services.Calibration;

/// <summary>
/// The calibration methods supported by the calibration domain.
/// </summary>
/// <remarks>
/// Relocated from the deleted generator subtree (<c>Services/Calibration/Generation/</c>) so this
/// catalogue survives the generator deletion. D7 (saga service) and D8 (step sequences/validation)
/// depend on this catalogue.
/// </remarks>
public enum CalibrationMethod
{
    /// <summary>
    /// No method. This is the default value and is never a valid request; it exists so an
    /// unparsed method is an explicitly undefined value rather than an accidental alias for a
    /// real method.
    /// </summary>
    Unspecified = 0,

    /// <summary>Nozzle temperature tower.</summary>
    Temperature = 1,

    /// <summary>Coarse flow ratio pass.</summary>
    FlowRatioCoarse = 2,

    /// <summary>Fine flow ratio pass.</summary>
    FlowRatioFine = 3,

    /// <summary>Wide-range flow ratio pass, commonly called the YOLO method.</summary>
    FlowRatioHighRange = 4,

    /// <summary>Pressure advance tower.</summary>
    PressureAdvanceTower = 5,

    /// <summary>Trusted server-generated pressure advance line.</summary>
    PressureAdvanceLine = 6,

    /// <summary>Trusted server-generated pressure advance pattern.</summary>
    PressureAdvancePattern = 7,

    /// <summary>Single-value flow verification print.</summary>
    FlowVerification = 8,

    /// <summary>Retraction length sweep.</summary>
    Retraction = 9,

    /// <summary>Maximum volumetric speed sweep.</summary>
    MaximumVolumetricSpeed = 10,

    /// <summary>Shrinkage compensation bars.</summary>
    Shrinkage = 11,

    /// <summary>Final verification against a linked imported asset or normal model.</summary>
    FinalVerification = 12,

    /// <summary>
    /// Cornering (jerk / junction deviation / Klipper square corner velocity) sweep. Unlike
    /// every other catalogued method, this measures the printer's motion system rather than a
    /// filament property, so it is report-only: see <see cref="CalibrationMethodNames"/> remarks
    /// and issue #2138 for the write-back model.
    /// </summary>
    Cornering = 13,
}

/// <summary>Canonical wire names for <see cref="CalibrationMethod"/>.</summary>
public static class CalibrationMethodNames
{
    /// <summary>Nozzle temperature tower.</summary>
    public const string Temperature = "temperature";

    /// <summary>Coarse flow ratio pass.</summary>
    public const string FlowRatioCoarse = "flow_ratio_coarse";

    /// <summary>Fine flow ratio pass.</summary>
    public const string FlowRatioFine = "flow_ratio_fine";

    /// <summary>Wide-range flow ratio pass.</summary>
    public const string FlowRatioHighRange = "flow_ratio_high_range";

    /// <summary>Pressure advance tower.</summary>
    public const string PressureAdvanceTower = "pressure_advance_tower";

    /// <summary>Trusted pressure advance line.</summary>
    public const string PressureAdvanceLine = "pressure_advance_line";

    /// <summary>Trusted pressure advance pattern.</summary>
    public const string PressureAdvancePattern = "pressure_advance_pattern";

    /// <summary>Flow verification print.</summary>
    public const string FlowVerification = "flow_verification";

    /// <summary>Retraction sweep.</summary>
    public const string Retraction = "retraction";

    /// <summary>Maximum volumetric speed sweep.</summary>
    public const string MaximumVolumetricSpeed = "max_volumetric_speed";

    /// <summary>Shrinkage compensation bars.</summary>
    public const string Shrinkage = "shrinkage";

    /// <summary>Final verification from a linked asset.</summary>
    public const string FinalVerification = "final_verification";

    /// <summary>
    /// Cornering (jerk / junction deviation / Klipper square corner velocity) sweep. Report-only:
    /// see the type-level <see cref="CalibrationMethod"/> remarks and issue #2138.
    /// </summary>
    public const string Cornering = "cornering";

    private static readonly Dictionary<string, CalibrationMethod> ByName =
        new(StringComparer.Ordinal)
        {
            [Temperature] = CalibrationMethod.Temperature,
            [FlowRatioCoarse] = CalibrationMethod.FlowRatioCoarse,
            [FlowRatioFine] = CalibrationMethod.FlowRatioFine,
            [FlowRatioHighRange] = CalibrationMethod.FlowRatioHighRange,
            [PressureAdvanceTower] = CalibrationMethod.PressureAdvanceTower,
            [PressureAdvanceLine] = CalibrationMethod.PressureAdvanceLine,
            [PressureAdvancePattern] = CalibrationMethod.PressureAdvancePattern,
            [FlowVerification] = CalibrationMethod.FlowVerification,
            [Retraction] = CalibrationMethod.Retraction,
            [MaximumVolumetricSpeed] = CalibrationMethod.MaximumVolumetricSpeed,
            [Shrinkage] = CalibrationMethod.Shrinkage,
            [FinalVerification] = CalibrationMethod.FinalVerification,
            [Cornering] = CalibrationMethod.Cornering,
        };

    /// <summary>Gets every supported canonical method name, in stable order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Temperature,
        FlowRatioCoarse,
        FlowRatioFine,
        FlowRatioHighRange,
        PressureAdvanceTower,
        PressureAdvanceLine,
        PressureAdvancePattern,
        FlowVerification,
        Retraction,
        MaximumVolumetricSpeed,
        Shrinkage,
        FinalVerification,
        Cornering,
    ];

    /// <summary>Maps a method to its canonical wire name.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The canonical name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The method is not declared.</exception>
    public static string ToName(CalibrationMethod method) => method switch
    {
        CalibrationMethod.Temperature => Temperature,
        CalibrationMethod.FlowRatioCoarse => FlowRatioCoarse,
        CalibrationMethod.FlowRatioFine => FlowRatioFine,
        CalibrationMethod.FlowRatioHighRange => FlowRatioHighRange,
        CalibrationMethod.PressureAdvanceTower => PressureAdvanceTower,
        CalibrationMethod.PressureAdvanceLine => PressureAdvanceLine,
        CalibrationMethod.PressureAdvancePattern => PressureAdvancePattern,
        CalibrationMethod.FlowVerification => FlowVerification,
        CalibrationMethod.Retraction => Retraction,
        CalibrationMethod.MaximumVolumetricSpeed => MaximumVolumetricSpeed,
        CalibrationMethod.Shrinkage => Shrinkage,
        CalibrationMethod.FinalVerification => FinalVerification,
        CalibrationMethod.Cornering => Cornering,
        _ => throw new ArgumentOutOfRangeException(
            nameof(method),
            method,
            "Unknown calibration method."),
    };

    /// <summary>Parses a canonical method name without any case-insensitive or alias fallback.</summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="method">The parsed method when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the value is an exact canonical name.</returns>
    public static bool TryParse(string? value, out CalibrationMethod method)
    {
        method = default;
        return value is not null && ByName.TryGetValue(value, out method);
    }

    /// <summary>
    /// Maps a method to the calibration kind recorded on the immutable attempt.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>The stable calibration kind.</returns>
    public static string ToKind(CalibrationMethod method) => method switch
    {
        CalibrationMethod.Temperature => "temperature",
        CalibrationMethod.FlowRatioCoarse or
        CalibrationMethod.FlowRatioFine or
        CalibrationMethod.FlowRatioHighRange or
        CalibrationMethod.FlowVerification => "flow",
        CalibrationMethod.PressureAdvanceTower or
        CalibrationMethod.PressureAdvanceLine or
        CalibrationMethod.PressureAdvancePattern => "pressure_advance",
        CalibrationMethod.Retraction => "retraction",
        CalibrationMethod.MaximumVolumetricSpeed => "max_volumetric_speed",
        CalibrationMethod.Shrinkage => "shrinkage",
        CalibrationMethod.FinalVerification => "verification",
        CalibrationMethod.Cornering => "cornering",
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
        CalibrationMethod.Temperature or
        CalibrationMethod.FlowRatioCoarse or
        CalibrationMethod.FlowRatioFine or
        CalibrationMethod.FlowRatioHighRange or
        CalibrationMethod.PressureAdvanceTower or
        CalibrationMethod.PressureAdvanceLine or
        CalibrationMethod.PressureAdvancePattern or
        CalibrationMethod.FlowVerification or
        CalibrationMethod.Retraction or
        CalibrationMethod.MaximumVolumetricSpeed or
        CalibrationMethod.Shrinkage or
        CalibrationMethod.FinalVerification or
        CalibrationMethod.Cornering => DefaultSequence,
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
/// (<see cref="CalibrationMethodNames.ToKind(CalibrationMethod)"/>) rather than by method,
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
    /// <param name="kind">The stable calibration kind, e.g. from <see cref="CalibrationMethodNames.ToKind(CalibrationMethod)"/>.</param>
    /// <returns>The range, or <see langword="null"/> when the kind has no defined semantic range.</returns>
    public static CalibrationMeasurementRange? ForKind(string kind) => kind switch
    {
        "temperature" => Temperature,
        "flow" => FlowRatio,
        "pressure_advance" => PressureAdvance,
        "max_volumetric_speed" => MaximumVolumetricSpeed,
        _ => null,
    };
}

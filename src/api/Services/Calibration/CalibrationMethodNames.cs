namespace Farm.Web.Api.Services.Calibration;

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
        _ => throw new ArgumentOutOfRangeException(
            nameof(method),
            method,
            "Unknown calibration method."),
    };
}

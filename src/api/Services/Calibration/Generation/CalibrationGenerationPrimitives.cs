using System.Text;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// A single structured, machine-readable rejection reason produced by a generation service.
/// </summary>
/// <param name="Code">Stable snake_case reason code, safe to expose to clients.</param>
/// <param name="Field">Dotted path of the offending input, for example <c>options.startCelsius</c>.</param>
/// <param name="Message">Operator-facing explanation that never contains secrets, paths or hosts.</param>
public sealed record CalibrationGenerationProblem(string Code, string Field, string Message);

/// <summary>
/// The outcome of a generation step: either an immutable value or an ordered problem list.
/// </summary>
/// <typeparam name="T">The produced value type.</typeparam>
/// <param name="Value">The produced value, or <see langword="null"/> when the step was rejected.</param>
/// <param name="Problems">The ordered rejection reasons; empty on success.</param>
/// <remarks>
/// Generation is fail closed. A caller must treat any non-empty problem list as a rejection and must
/// never fall back to a synthesized default.
/// </remarks>
public sealed record CalibrationGenerationResult<T>(
    T? Value,
    IReadOnlyList<CalibrationGenerationProblem> Problems)
    where T : class
{
    /// <summary>Gets a value indicating whether the step produced a usable value.</summary>
    public bool IsValid => Value is not null && Problems.Count == 0;
}

/// <summary>Non-generic factories for <see cref="CalibrationGenerationResult{T}"/>.</summary>
/// <example>
/// <code>
/// CalibrationGenerationResult&lt;CalibrationSpecification&gt; result =
///     CalibrationGenerationResults.Success(specification);
/// </code>
/// </example>
public static class CalibrationGenerationResults
{
    /// <summary>Creates a successful result.</summary>
    /// <typeparam name="T">The produced value type.</typeparam>
    /// <param name="value">The produced value.</param>
    /// <returns>A result carrying <paramref name="value"/> and no problems.</returns>
    public static CalibrationGenerationResult<T> Success<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return new CalibrationGenerationResult<T>(value, []);
    }

    /// <summary>Creates a rejected result.</summary>
    /// <typeparam name="T">The produced value type.</typeparam>
    /// <param name="problems">The ordered rejection reasons; must not be empty.</param>
    /// <returns>A result carrying only problems.</returns>
    public static CalibrationGenerationResult<T> Failure<T>(
        IReadOnlyList<CalibrationGenerationProblem> problems)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (problems.Count == 0)
        {
            throw new ArgumentException(
                "A rejected calibration generation result requires at least one problem.",
                nameof(problems));
        }

        return new CalibrationGenerationResult<T>(null, problems);
    }

    /// <summary>Creates a rejected result from a single reason.</summary>
    /// <typeparam name="T">The produced value type.</typeparam>
    /// <param name="code">Stable snake_case reason code.</param>
    /// <param name="field">Dotted path of the offending input.</param>
    /// <param name="message">Operator-facing explanation.</param>
    /// <returns>A result carrying exactly one problem.</returns>
    public static CalibrationGenerationResult<T> Failure<T>(
        string code,
        string field,
        string message)
        where T : class =>
        Failure<T>([new CalibrationGenerationProblem(code, field, message)]);
}

/// <summary>The calibration methods this generator supports.</summary>
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

/// <summary>Units used by calibration segment values. Every unit is explicit; none are implied.</summary>
public static class CalibrationUnits
{
    /// <summary>Degrees Celsius.</summary>
    public const string Celsius = "celsius";

    /// <summary>Dimensionless multiplier.</summary>
    public const string Ratio = "ratio";

    /// <summary>Millimetres.</summary>
    public const string Millimeters = "mm";

    /// <summary>Millimetres per second.</summary>
    public const string MillimetersPerSecond = "mm/s";

    /// <summary>Cubic millimetres per second.</summary>
    public const string CubicMillimetersPerSecond = "mm3/s";

    /// <summary>Klipper pressure advance, expressed in seconds.</summary>
    public const string Seconds = "s";

    /// <summary>A discrete count with no physical dimension.</summary>
    public const string Count = "count";
}

/// <summary>
/// Canonical JSON and digest helpers shared by every calibration generation service.
/// </summary>
/// <remarks>
/// All generation digests come from this one canonicalizer so a specification, plan and manifest
/// that are structurally equal always hash equal, independent of member declaration order.
/// </remarks>
public static class CalibrationCanonicalJson
{
    /// <summary>Serializes a value to canonical JSON text.</summary>
    /// <param name="value">The value to serialize.</param>
    /// <returns>Canonical JSON with ordinally ordered object members.</returns>
    public static string Serialize(object value) =>
        Encoding.UTF8.GetString(CalibrationSnapshotBuilder.CanonicalizeToUtf8Bytes(value));

    /// <summary>Computes the lowercase hexadecimal SHA-256 of the canonical JSON form of a value.</summary>
    /// <param name="value">The value to digest.</param>
    /// <returns>The lowercase hexadecimal digest.</returns>
    public static string ComputeSha256(object value) =>
        CalibrationSnapshotBuilder.ComputeSha256(value);

    /// <summary>Computes the lowercase hexadecimal SHA-256 of a UTF-8 encoded text payload.</summary>
    /// <param name="value">The text payload.</param>
    /// <returns>The lowercase hexadecimal digest.</returns>
    public static string ComputeTextSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    /// <summary>Computes the lowercase hexadecimal SHA-256 of a byte payload.</summary>
    /// <param name="value">The byte payload.</param>
    /// <returns>The lowercase hexadecimal digest.</returns>
    public static string ComputeBytesSha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(value)).ToLowerInvariant();

    /// <summary>
    /// Compares two digests without regard to hexadecimal casing or surrounding whitespace.
    /// </summary>
    /// <param name="left">The first digest.</param>
    /// <param name="right">The second digest.</param>
    /// <returns><see langword="true"/> when both digests are present and equal.</returns>
    public static bool DigestsMatch(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}

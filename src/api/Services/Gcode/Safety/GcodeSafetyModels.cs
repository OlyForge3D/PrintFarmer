namespace Farm.Web.Api.Services.Gcode.Safety;

/// <summary>The lifecycle point a static g-code safety pass is being performed for.</summary>
public enum GcodeSafetyCheckpoint
{
    /// <summary>Unknown. Never a valid value.</summary>
    Unspecified = 0,

    /// <summary>Before a worker artifact is accepted as complete.</summary>
    BeforeArtifactCompletion = 1,

    /// <summary>Before an artifact is promoted into the G-code library.</summary>
    BeforePromotion = 2,

    /// <summary>Before a print job is queued.</summary>
    BeforeQueueing = 3,

    /// <summary>Before a print job is started.</summary>
    BeforeStart = 4,

    /// <summary>Before an artifact is streamed to a physical printer.</summary>
    BeforeSendToPrinter = 5,
}

/// <summary>A bed coordinate in printer space, in millimetres.</summary>
/// <param name="X">X coordinate, in millimetres.</param>
/// <param name="Y">Y coordinate, in millimetres.</param>
public sealed record GcodeSafetyPoint(decimal X, decimal Y);

/// <summary>A named region the printer must never enter.</summary>
/// <param name="Name">Operator-facing region name.</param>
/// <param name="Polygon">Closed polygon vertices, in millimetres, in authored order.</param>
public sealed record GcodeSafetyExcludedRegion(
    string Name,
    IReadOnlyList<GcodeSafetyPoint> Polygon);

/// <summary>Authoritative build volume, origin, printable polygon and exclusions.</summary>
/// <param name="SizeXMillimeters">Build volume X extent, in millimetres.</param>
/// <param name="SizeYMillimeters">Build volume Y extent, in millimetres.</param>
/// <param name="SizeZMillimeters">Build volume Z extent, in millimetres.</param>
/// <param name="OriginXMillimeters">Bed origin X offset, in millimetres.</param>
/// <param name="OriginYMillimeters">Bed origin Y offset, in millimetres.</param>
/// <param name="PrintablePolygon">Authoritative printable polygon, in millimetres.</param>
/// <param name="ExcludedRegions">Authoritative excluded regions, in millimetres.</param>
public sealed record GcodeSafetyBedLimits(
    decimal? SizeXMillimeters,
    decimal? SizeYMillimeters,
    decimal? SizeZMillimeters,
    decimal? OriginXMillimeters,
    decimal? OriginYMillimeters,
    IReadOnlyList<GcodeSafetyPoint> PrintablePolygon,
    IReadOnlyList<GcodeSafetyExcludedRegion> ExcludedRegions)
{
    /// <summary>Bed limits with no printable polygon or excluded regions configured.</summary>
    public static GcodeSafetyBedLimits Empty { get; } = new(null, null, null, null, null, [], []);
}

/// <summary>The toolhead ceilings that bound a commanded program.</summary>
/// <param name="NozzleMaxTemperatureCelsius">Nozzle temperature ceiling, in degrees Celsius.</param>
/// <param name="HotendMaxTemperatureCelsius">Hotend temperature ceiling, in degrees Celsius.</param>
/// <param name="IsDirectDrive">Whether the toolhead is direct drive.</param>
public sealed record GcodeSafetyToolheadLimits(
    int? NozzleMaxTemperatureCelsius,
    int? HotendMaxTemperatureCelsius,
    bool? IsDirectDrive)
{
    /// <summary>Toolhead limits with no configured ceilings.</summary>
    public static GcodeSafetyToolheadLimits Empty { get; } = new(null, null, null);
}

/// <summary>Authoritative machine motion and thermal ceilings.</summary>
/// <param name="MaxBedTemperatureCelsius">Bed temperature ceiling.</param>
/// <param name="HasHeatedChamber">Whether a heated chamber exists.</param>
/// <param name="MaxChamberTemperatureCelsius">Chamber temperature ceiling when a chamber exists.</param>
/// <param name="MaxPrintSpeedMillimetersPerSecond">Print speed ceiling.</param>
/// <param name="MaxTravelSpeedMillimetersPerSecond">Travel speed ceiling.</param>
/// <param name="MaxAcceleration">Print/travel acceleration ceiling, in mm/s².</param>
public sealed record GcodeSafetyMachineLimits(
    int? MaxBedTemperatureCelsius,
    bool? HasHeatedChamber,
    int? MaxChamberTemperatureCelsius,
    int? MaxPrintSpeedMillimetersPerSecond,
    int? MaxTravelSpeedMillimetersPerSecond,
    int? MaxAcceleration)
{
    /// <summary>Machine limits with no configured ceilings.</summary>
    public static GcodeSafetyMachineLimits Empty { get; } = new(null, null, null, null, null, null);
}

/// <summary>The filament and flow ceilings a program's extrusion moves are checked against.</summary>
/// <param name="FilamentDiameterMillimeters">Filament diameter, in millimetres.</param>
/// <param name="MaxVolumetricFlow">Volumetric flow ceiling, in mm³/s.</param>
public sealed record GcodeSafetyPrintLimits(
    decimal? FilamentDiameterMillimeters,
    decimal? MaxVolumetricFlow)
{
    /// <summary>Print limits with no configured ceilings; volumetric flow is never checked.</summary>
    public static GcodeSafetyPrintLimits Empty { get; } = new(null, null);
}

/// <summary>The complete, calibration-independent authoritative safety envelope for one machine.</summary>
/// <param name="Toolhead">Toolhead ceilings.</param>
/// <param name="Bed">Bed geometry and exclusions.</param>
/// <param name="Machine">Machine motion and thermal ceilings.</param>
/// <param name="Print">Filament and flow ceilings.</param>
public sealed record GcodeSafetyLimits(
    GcodeSafetyToolheadLimits Toolhead,
    GcodeSafetyBedLimits Bed,
    GcodeSafetyMachineLimits Machine,
    GcodeSafetyPrintLimits Print)
{
    /// <summary>
    /// Limits with every ceiling unset. Every temperature/speed/acceleration/flow check is skipped
    /// when its ceiling is unset, so this is only appropriate when the caller cannot determine the
    /// authoritative machine envelope and has decided the redaction/structural checks alone are
    /// still worth running.
    /// </summary>
    public static GcodeSafetyLimits Empty { get; } = new(
        GcodeSafetyToolheadLimits.Empty,
        GcodeSafetyBedLimits.Empty,
        GcodeSafetyMachineLimits.Empty,
        GcodeSafetyPrintLimits.Empty);
}

/// <summary>One rejection reason from a static g-code safety validation.</summary>
/// <param name="Code">A stable, machine-readable problem code.</param>
/// <param name="Field">The field or location the problem was found at.</param>
/// <param name="Message">A human-readable description of the problem.</param>
public sealed record GcodeSafetyProblem(string Code, string Field, string Message);

/// <summary>A successful static validation record.</summary>
/// <param name="Checkpoint">The lifecycle point that was validated.</param>
/// <param name="GcodeSha256">The digest of the validated G-code.</param>
/// <param name="CommandCount">The number of interpreted commands.</param>
/// <param name="ValidatedAtUtc">When the validation ran.</param>
public sealed record GcodeSafetyReport(
    GcodeSafetyCheckpoint Checkpoint,
    string GcodeSha256,
    int CommandCount,
    DateTime ValidatedAtUtc);

/// <summary>The outcome of a static g-code safety validation: either a clean report or problems.</summary>
/// <typeparam name="T">The successful payload type.</typeparam>
public sealed class GcodeSafetyResult<T>
{
    private GcodeSafetyResult(bool isValid, T? value, IReadOnlyList<GcodeSafetyProblem> problems)
    {
        IsValid = isValid;
        Value = value;
        Problems = problems;
    }

    /// <summary>Whether the validation succeeded.</summary>
    public bool IsValid { get; }

    /// <summary>The successful payload, when <see cref="IsValid"/> is <see langword="true"/>.</summary>
    public T? Value { get; }

    /// <summary>The ordered rejection reasons, empty when <see cref="IsValid"/> is <see langword="true"/>.</summary>
    public IReadOnlyList<GcodeSafetyProblem> Problems { get; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The successful payload.</param>
    public static GcodeSafetyResult<T> Success(T value) => new(true, value, []);

    /// <summary>Creates a failed result from one or more problems.</summary>
    /// <param name="problems">The ordered rejection reasons.</param>
    public static GcodeSafetyResult<T> Failure(IReadOnlyList<GcodeSafetyProblem> problems) =>
        new(false, default, problems);

    /// <summary>Creates a failed result from a single problem.</summary>
    /// <param name="code">A stable, machine-readable problem code.</param>
    /// <param name="field">The field or location the problem was found at.</param>
    /// <param name="message">A human-readable description of the problem.</param>
    public static GcodeSafetyResult<T> Failure(string code, string field, string message) =>
        new(false, default, [new GcodeSafetyProblem(code, field, message)]);
}

/// <summary>
/// The stable, machine-readable problem codes shared with the calibration-scoped adapter, so that
/// existing consumers observe unchanged codes after the extraction.
/// </summary>
public static class GcodeSafetyProblemCodes
{
    /// <summary>A command outside the request's optional allowlist was encountered.</summary>
    public const string CommandNotAllowlisted = "gcode_command_not_allowlisted";

    /// <summary>A firmware tuning tower command was encountered.</summary>
    public const string TuningTowerForbidden = "gcode_tuning_tower_forbidden";

    /// <summary>A credential-bearing token was encountered.</summary>
    public const string ContainsCredential = "gcode_contains_credential";

    /// <summary>A URL was encountered.</summary>
    public const string ContainsPrivateUrl = "gcode_contains_private_url";

    /// <summary>An absolute filesystem path was encountered.</summary>
    public const string ContainsFilesystemPath = "gcode_contains_filesystem_path";

    /// <summary>A shell, host or network command was encountered.</summary>
    public const string ContainsHostCommand = "gcode_contains_host_command";

    /// <summary>A commanded move falls outside the authoritative build volume.</summary>
    public const string MotionOutsideBuildVolume = "gcode_motion_outside_build_volume";

    /// <summary>A commanded move falls outside the authoritative printable polygon.</summary>
    public const string MotionOutsidePrintablePolygon = "gcode_motion_outside_printable_polygon";

    /// <summary>A commanded move enters an authoritative excluded region.</summary>
    public const string MotionInsideExcludedRegion = "gcode_motion_inside_excluded_region";

    /// <summary>A commanded temperature exceeds an authoritative ceiling.</summary>
    public const string TemperatureAboveLimit = "gcode_temperature_above_limit";

    /// <summary>A commanded speed exceeds an authoritative ceiling.</summary>
    public const string SpeedAboveLimit = "gcode_speed_above_limit";

    /// <summary>A commanded acceleration exceeds an authoritative ceiling.</summary>
    public const string AccelerationAboveLimit = "gcode_acceleration_above_limit";

    /// <summary>A commanded move exceeds the authoritative volumetric flow ceiling.</summary>
    public const string VolumetricFlowAboveLimit = "gcode_volumetric_flow_above_limit";

    /// <summary>A commanded retraction exceeds the safe range.</summary>
    public const string RetractionAboveLimit = "gcode_retraction_above_limit";

    /// <summary>A commanded pressure advance value is outside the safe range.</summary>
    public const string PressureAdvanceOutOfRange = "gcode_pressure_advance_out_of_range";

    /// <summary>The program is unsafe at initialization (extrudes before homed/heated).</summary>
    public const string UnsafeInitialization = "gcode_unsafe_initialization";

    /// <summary>A segment transition marker was found unsafe.</summary>
    public const string UnsafeSegmentTransition = "gcode_unsafe_segment_transition";

    /// <summary>The program does not end with a safe heater and motor reset.</summary>
    public const string MissingFinalReset = "gcode_missing_final_reset";

    /// <summary>The supplied g-code is empty or otherwise malformed.</summary>
    public const string Malformed = "gcode_malformed";
}

/// <summary>Everything the general static validator needs, with no ambient state.</summary>
/// <param name="Limits">The authoritative safety envelope for the target machine.</param>
/// <param name="Gcode">The g-code program text to validate.</param>
/// <param name="Checkpoint">The lifecycle point being validated.</param>
/// <param name="AllowedCommands">
/// An optional command allowlist. When <see langword="null"/>, any command is accepted (the normal
/// case for real slicer-produced g-code, whose vocabulary is far wider than any narrow generator
/// allowlist). Calibration call sites pass their trusted generator allowlist here.
/// </param>
public sealed record GcodeSafetyRequest(
    GcodeSafetyLimits Limits,
    string Gcode,
    GcodeSafetyCheckpoint Checkpoint,
    IReadOnlyCollection<string>? AllowedCommands = null);

/// <summary>
/// General, calibration-independent reject-only static validation of g-code, extracted from the
/// calibration-only validator introduced by PR #947. Safe and intended to run at every slice-job
/// and send-to-printer lifecycle point where g-code content is not yet trusted.
/// </summary>
/// <remarks>
/// The validator never rewrites, repairs or normalizes g-code. It parses the program statefully and
/// either returns a clean report or the ordered reasons the program must be rejected. Provenance and
/// digest matching against calibration manifests, specifications, and plans is intentionally out of
/// scope here — that was calibration-generator-specific and lived in the calibration generation saga
/// removed by #1979.
/// </remarks>
public interface IGcodeSafetyValidator
{
    /// <summary>Validates g-code against an authoritative machine safety envelope.</summary>
    /// <param name="request">The complete validation request.</param>
    /// <returns>The clean report, or the ordered rejection reasons.</returns>
    GcodeSafetyResult<GcodeSafetyReport> Validate(GcodeSafetyRequest request);
}

namespace Farm.Slicer.Module.Models;

/// <summary>
/// Calibration methods supported by the OrcaSlicer-worker calibration mode (issue #1938).
/// </summary>
/// <remarks>
/// This is deliberately unrelated to the printer/toolhead <c>/api/calibration-projects</c> saga
/// (<see cref="Domain.SliceJob.CalibrationProjectId"/>/<see cref="Domain.SliceJob.CalibrationAttemptId"/>/
/// <see cref="Domain.SliceJob.CalibrationOrchestrationId"/>), which is being removed by a separate
/// epic. A calibration-mode slice job produces a calibrated <em>filament</em> profile via an
/// ordinary ad-hoc slice — it never sets those three fields, so
/// <c>SlicePrintBridgeController.IsCalibrationSlice()</c> stays false and send-to-printer keeps
/// working for these jobs.
/// <para>
/// PA Pattern (GPL-3.0 provenance concerns) and PA Line (Bambu-specific) are intentionally not
/// supported yet; see the issue for the licensing decision they still need.
/// </para>
/// </remarks>
public enum CalibrationMethod
{
    /// <summary>Flow rate calibration, pass 1 (coarse sweep).</summary>
    FlowRatePass1,

    /// <summary>Flow rate calibration, pass 2 (fine sweep).</summary>
    FlowRatePass2,

    /// <summary>Temperature tower calibration.</summary>
    TemperatureTower,
}

/// <summary>
/// Wire-name parsing/formatting for <see cref="CalibrationMethod"/>. The wire names are the
/// snake_case strings clients submit (for example <c>"flow_rate_pass_1"</c>, matching the shape in
/// the issue), which are deliberately distinct from the C# enum member names.
/// </summary>
public static class CalibrationMethods
{
    private static readonly IReadOnlyDictionary<string, CalibrationMethod> WireNameToMethod =
        new Dictionary<string, CalibrationMethod>(StringComparer.OrdinalIgnoreCase)
        {
            ["flow_rate_pass_1"] = CalibrationMethod.FlowRatePass1,
            ["flow_rate_pass_2"] = CalibrationMethod.FlowRatePass2,
            ["temperature_tower"] = CalibrationMethod.TemperatureTower,
        };

    private static readonly Dictionary<CalibrationMethod, string> MethodToWireName =
        new()
        {
            [CalibrationMethod.FlowRatePass1] = "flow_rate_pass_1",
            [CalibrationMethod.FlowRatePass2] = "flow_rate_pass_2",
            [CalibrationMethod.TemperatureTower] = "temperature_tower",
        };

    /// <summary>The wire names of every currently-supported calibration method.</summary>
    public static IReadOnlyList<string> SupportedWireNames { get; } = [.. WireNameToMethod.Keys];

    /// <summary>
    /// Attempts to parse a client-supplied calibration method name. Only the methods currently
    /// implemented by the worker are accepted — an unrecognized or not-yet-supported name (for
    /// example <c>"pa_pattern"</c> or <c>"pa_line"</c>) returns <see langword="false"/> so the
    /// caller can reject the request with a clear, actionable error instead of a generic slice
    /// failure surfacing later on the worker.
    /// </summary>
    /// <param name="wireName">The client-supplied method name.</param>
    /// <param name="method">The parsed method, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="wireName"/> names a supported method.</returns>
    public static bool TryParse(string? wireName, out CalibrationMethod method)
    {
        if (!string.IsNullOrWhiteSpace(wireName) && WireNameToMethod.TryGetValue(wireName.Trim(), out CalibrationMethod parsed))
        {
            method = parsed;
            return true;
        }

        method = default;
        return false;
    }

    /// <summary>Formats a <see cref="CalibrationMethod"/> back to its canonical wire name.</summary>
    public static string ToWireName(CalibrationMethod method) => MethodToWireName[method];

    /// <summary>
    /// A descriptive placeholder model file name for a calibration job, used in place of a
    /// client-supplied upload since the worker resolves the actual calibration model from its own
    /// bundled OrcaSlicer resources.
    /// </summary>
    public static string DefaultModelFileName(CalibrationMethod method) => method switch
    {
        CalibrationMethod.FlowRatePass1 => "flowrate-test-pass1.3mf",
        CalibrationMethod.FlowRatePass2 => "flowrate-test-pass2.3mf",
        CalibrationMethod.TemperatureTower => "temperature_tower.drc",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported calibration method."),
    };

    /// <summary>
    /// The path of the bundled OrcaSlicer calibration resource for <paramref name="method"/>,
    /// relative to the OrcaSlicer installation's <c>resources/calib/</c> directory.
    /// </summary>
    public static string RelativeResourcePath(CalibrationMethod method) => method switch
    {
        CalibrationMethod.FlowRatePass1 => Path.Combine("filament_flow", "flowrate-test-pass1.3mf"),
        CalibrationMethod.FlowRatePass2 => Path.Combine("filament_flow", "flowrate-test-pass2.3mf"),
        CalibrationMethod.TemperatureTower => Path.Combine("temperature_tower", "temperature_tower.drc"),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported calibration method."),
    };
}

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
/// <para>
/// Investigated as part of issue #2051: "max volumetric speed" and "retraction" calibration are
/// not blocked by anything (upstream OrcaSlicer already implements both —
/// <c>Plater::calib_max_vol_speed</c>/<c>Plater::calib_retraction</c> and their
/// <c>CalibMode::Calib_Vol_speed_Tower</c>/<c>CalibMode::Calib_Retraction_tower</c> modes exist
/// in <c>calib_dlg.cpp</c>); they simply have no resource resolver, configurator, or pipeline
/// wiring in this repo yet, unlike temperature-tower and flow-rate.
/// </para>
/// <para>
/// <see cref="FlowRateYoloRecommended"/>/<see cref="FlowRateYoloPerfectionist"/>'s bundled 3MF
/// resources (<c>Orca-LinearFlow.3mf</c>/<c>Orca-LinearFlow_fine.3mf</c>) encode per-object flow
/// ratios as baseline-relative deltas (for example <c>flowrate_0.01</c>, <c>flowrate_m0.01</c>),
/// not the absolute percentages (<c>flowrate_95</c>) that <c>FlowRateCalibrationConfigurator</c>
/// parses for <see cref="FlowRatePass1"/>/<see cref="FlowRatePass2"/>. Until a delta-aware
/// configurator exists, the worker deliberately fails these two methods loudly (see
/// <c>OrcaSlicingPipelineService.PrepareCalibrationModel</c>) rather than silently reusing the
/// pass1/2 parser and producing near-identical, uncalibrated G-code for every block.
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

    /// <summary>
    /// Flow rate calibration using OrcaSlicer's linear-regression "YOLO (Recommended)" method
    /// (coarse pass). See the type-level remarks for why the worker does not yet slice this
    /// method's per-object overrides.
    /// </summary>
    FlowRateYoloRecommended,

    /// <summary>
    /// Flow rate calibration using OrcaSlicer's linear-regression "YOLO (Perfectionist)" method
    /// (fine pass). See the type-level remarks for why the worker does not yet slice this
    /// method's per-object overrides.
    /// </summary>
    FlowRateYoloPerfectionist,
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
            ["flow_rate_yolo_recommended"] = CalibrationMethod.FlowRateYoloRecommended,
            ["flow_rate_yolo_perfectionist"] = CalibrationMethod.FlowRateYoloPerfectionist,
        };

    private static readonly Dictionary<CalibrationMethod, string> MethodToWireName =
        new()
        {
            [CalibrationMethod.FlowRatePass1] = "flow_rate_pass_1",
            [CalibrationMethod.FlowRatePass2] = "flow_rate_pass_2",
            [CalibrationMethod.TemperatureTower] = "temperature_tower",
            [CalibrationMethod.FlowRateYoloRecommended] = "flow_rate_yolo_recommended",
            [CalibrationMethod.FlowRateYoloPerfectionist] = "flow_rate_yolo_perfectionist",
        };

    /// <summary>
    /// The wire names of every calibration method <see cref="TryParse"/> recognizes, including
    /// <see cref="CalibrationMethod.FlowRateYoloRecommended"/>/<see cref="CalibrationMethod.FlowRateYoloPerfectionist"/>,
    /// which parse successfully but are not yet slicer-supported (see <see cref="IsSlicerSupported"/>).
    /// Do not surface this list as "supported methods" in a client-facing error message — use
    /// <see cref="ClientAcceptedWireNames"/> for that.
    /// </summary>
    public static IReadOnlyList<string> SupportedWireNames { get; } = [.. WireNameToMethod.Keys];

    /// <summary>
    /// The wire names of calibration methods a client can actually submit a job for today — the
    /// subset of <see cref="SupportedWireNames"/> for which <see cref="IsSlicerSupported"/> is
    /// <see langword="true"/>. Use this (not <see cref="SupportedWireNames"/>) when listing
    /// accepted methods in a client-facing error message, so the list never advertises a method
    /// the same request would otherwise be rejected for.
    /// </summary>
    public static IReadOnlyList<string> ClientAcceptedWireNames { get; } =
        [.. WireNameToMethod.Where(pair => IsSlicerSupported(pair.Value)).Select(pair => pair.Key)];

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
    /// Whether the worker can actually slice a job for <paramref name="method"/> today.
    /// <see cref="CalibrationMethod.FlowRateYoloRecommended"/> and
    /// <see cref="CalibrationMethod.FlowRateYoloPerfectionist"/> are catalogued (issue #2051) so
    /// their wire names round-trip and their resource metadata is available, but the worker
    /// cannot yet apply their delta-based per-object flow-ratio overrides (see the
    /// <see cref="CalibrationMethod"/> type remarks) and would only fail late, after dispatch.
    /// Callers that accept a client-supplied method — chiefly the slice-job submission
    /// endpoint — must reject these methods with <see langword="false"/> here, at the API
    /// boundary, instead of letting <see cref="TryParse"/> alone gate acceptance.
    /// </summary>
    /// <param name="method">A method that already parsed successfully via <see cref="TryParse"/>.</param>
    /// <returns><see langword="true"/> when the worker can slice this method today.</returns>
    public static bool IsSlicerSupported(CalibrationMethod method) => method is not (
        CalibrationMethod.FlowRateYoloRecommended or CalibrationMethod.FlowRateYoloPerfectionist);

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
        CalibrationMethod.FlowRateYoloRecommended => "Orca-LinearFlow.3mf",
        CalibrationMethod.FlowRateYoloPerfectionist => "Orca-LinearFlow_fine.3mf",
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
        CalibrationMethod.FlowRateYoloRecommended => Path.Combine("filament_flow", "Orca-LinearFlow.3mf"),
        CalibrationMethod.FlowRateYoloPerfectionist => Path.Combine("filament_flow", "Orca-LinearFlow_fine.3mf"),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported calibration method."),
    };
}

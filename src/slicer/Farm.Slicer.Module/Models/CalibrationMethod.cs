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
/// in <c>calib_dlg.cpp</c>); both are now built — see <see cref="Retraction"/> and
/// <see cref="MaximumVolumetricSpeed"/>.
/// </para>
/// <para>
/// <see cref="Retraction"/> (issue #2137): the bundled <c>retraction_tower.drc</c> resource is a
/// single raw Draco mesh with no per-object names or metadata — unlike the flow-rate towers'
/// 3MF resources, there is nothing here for a <c>FlowRateCalibrationConfigurator</c>-style
/// per-object rewrite to target. Upstream's native sweep
/// (<c>CalibMode::Calib_Retraction_tower</c> in <c>GCode.cpp</c>) mutates the slicing engine's
/// internal <c>retraction_length</c> config directly per layer, a hook the CLI-driven worker
/// cannot reach. The worker instead reimplements the sweep via a <c>layer_change_gcode</c>
/// injection (mirroring <see cref="TemperatureTower"/>'s <c>M104</c> approach) that issues
/// <c>M207 S...</c> once per Z-band, and forces the machine profile's
/// <c>use_firmware_retraction</c> setting on for these jobs — <c>M207</c> only takes effect when
/// firmware retraction is enabled, since software retraction bakes the retraction length into
/// <c>G1 E...</c> moves at slice time instead of reading it live. Enabling firmware retraction
/// also forces every extruder's <c>wipe</c> setting off: upstream's <c>PrintConfig::validate()</c>
/// hard-rejects <c>use_firmware_retraction=1</c> combined with any extruder's <c>wipe=1</c>
/// (real vendor profiles commonly ship wipe enabled), and that check runs even in CLI mode, so
/// leaving it untouched would fail slicing outright rather than merely producing a bad result.
/// This produces a per-band
/// <em>retraction length</em> result; write-back into a filament profile (as opposed to a
/// machine profile) is the calibration-consumer's job and is intentionally out of scope for the
/// worker — see the issue for the recommendation that a future desktop-side workflow store the
/// selected band's length as a filament override, analogous to how flow-rate results are
/// consumed today.
/// </para>
/// <para>
/// <see cref="MaximumVolumetricSpeed"/> (issue #2135): upstream's <c>CalibUtils::calib_max_vol_speed</c>
/// loads <c>resources/calib/volumetric_speed/SpeedTestStructure.drc</c> — an opaque, proprietary
/// binary (magic bytes <c>44 52 41 43</c>/"DRAC", not a ZIP/3MF archive), confirmed against a local
/// OrcaSlicer install. Unlike the flow-rate resources, this cannot be parsed and rewritten the way
/// <c>FlowRateCalibrationConfigurator</c> rewrites 3MF metadata, so the worker copies it unmodified
/// — the same treatment as <see cref="TemperatureTower"/>'s <c>.drc</c> resource. Upstream's C++
/// also sets a permissive <c>filament_max_volumetric_speed</c> ceiling (a constant 50mm³/s,
/// <c>src/slicer/Utils/CalibUtils.cpp</c>: <c>filament_config.set_key_value(
/// "filament_max_volumetric_speed", new ConfigOptionFloats{50})</c>) before slicing, purely so the
/// slicer's own auto speed-limiting does not clamp the print below the range the calibration
/// tower's built-in, width-increasing geometry is designed to sweep through. The worker reproduces
/// that: <c>OrcaSlicingPipelineService.ApplyMaxVolumetricSpeedCeilingAsync</c> sets the filament
/// profile's <c>filament_max_volumetric_speed</c> to the ceiling resolved from
/// <c>CalibrationParameters.MaxVolumetricSpeedCeilingMm3s</c> before the slice.
/// </para>
/// <para>
/// <strong>Known, more significant limitation (corrected after adversarial review — an earlier
/// revision of this comment claimed the ceiling alone was upstream's entire sweep mechanism; that
/// claim was checked against upstream source and was wrong, see below):</strong> upstream's actual
/// per-layer speed variation is produced by <c>GCode.cpp</c>'s <c>calib_mode()</c> switch
/// (<c>case CalibMode::Calib_Vol_speed_Tower: auto _speed = print.calib_params().start + print_z *
/// print.calib_params().step; m_calib_config.set_key_value("outer_wall_speed", ...)</c>) — a live,
/// in-process override of the wall speed applied while <em>that same run's</em> gcode is being
/// generated, driven by <c>Print::calib_params()</c>/<c>calib_mode()</c>. Those are set only by
/// <c>Print::set_calib_params</c>, which is called only from <c>CalibUtils::process_and_store_3mf</c>
/// (GUI code, <c>src/slic3r/Utils/CalibUtils.cpp</c>) immediately before slicing in the same GUI
/// process. <c>calib_mode</c>/<c>calib_params</c> are never persisted into the 3MF/project file and
/// have no OrcaSlicer CLI flag — confirmed by exhaustive search of the upstream tree — so, unlike
/// <see cref="TemperatureTower"/>'s per-layer temperature step (a real, standalone <c>M104</c>
/// command any inserted <c>layer_change_gcode</c> can emit with full physical effect), this
/// specific mechanism is not reachable at all from a separate-process, CLI-driven pipeline: an
/// injected custom-gcode "set speed" command would be silently overwritten the moment the slicer's
/// own wall-generation code emits its own <c>F</c> parameter on the next extrusion move, which it
/// always does. This worker's implementation therefore only applies the permissive
/// <c>filament_max_volumetric_speed</c> ceiling and slices the bundled tower geometry with the
/// client-selected process profile's own (constant) wall speed; it does not reproduce upstream's
/// deliberate per-layer speed ramp, and — given the architecture above — cannot do so today without
/// a new worker capability such as authoring OrcaSlicer/PrusaSlicer's 3MF-native "height range
/// modifier" per-object speed overrides (a legitimate, project-format-persisted mechanism that,
/// unlike <c>calib_mode</c>, the CLI slicer does read normally), tracked as follow-up work rather
/// than attempted here. The wire name still submits, slices, and returns gcode end to end per the
/// issue's acceptance criteria; what is not yet delivered is upstream's full calibration fidelity.
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

    /// <summary>
    /// Retraction tower calibration (issue #2137). See the type-level remarks for how the worker
    /// reimplements upstream's native per-band sweep via injected <c>layer_change_gcode</c> plus
    /// a forced <c>use_firmware_retraction</c> machine-profile setting, and for the deliberate
    /// out-of-scope note on filament-profile write-back.
    /// </summary>
    Retraction,

    /// <summary>
    /// Maximum volumetric speed calibration (issue #2135). See the type-level remarks for the
    /// resource format and the permissive-ceiling configurator this method needs.
    /// </summary>
    MaximumVolumetricSpeed,

    /// <summary>
    /// Pressure advance tower calibration (issue #2136). Scope is deliberately Tower-only; PA
    /// Pattern and PA Line remain unsupported per the type-level remarks above. Emits per-band
    /// <c>layer_change_gcode</c> for Klipper (<c>SET_PRESSURE_ADVANCE</c>) or Marlin/Marlin2
    /// (<c>M900 K</c>) — see <c>PressureAdvanceTowerGcodeBuilder</c> in the OrcaSlicer worker.
    /// </summary>
    PressureAdvanceTower,
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
            ["retraction"] = CalibrationMethod.Retraction,
            ["max_volumetric_speed"] = CalibrationMethod.MaximumVolumetricSpeed,

            // Matches Farm.Modules.Calibration.Services.Calibration.CalibrationMethodNames.PressureAdvanceTower
            // so the two catalogues do not diverge further (issue #2136).
            ["pressure_advance_tower"] = CalibrationMethod.PressureAdvanceTower,
        };

    private static readonly Dictionary<CalibrationMethod, string> MethodToWireName =
        new()
        {
            [CalibrationMethod.FlowRatePass1] = "flow_rate_pass_1",
            [CalibrationMethod.FlowRatePass2] = "flow_rate_pass_2",
            [CalibrationMethod.TemperatureTower] = "temperature_tower",
            [CalibrationMethod.FlowRateYoloRecommended] = "flow_rate_yolo_recommended",
            [CalibrationMethod.FlowRateYoloPerfectionist] = "flow_rate_yolo_perfectionist",
            [CalibrationMethod.Retraction] = "retraction",
            [CalibrationMethod.MaximumVolumetricSpeed] = "max_volumetric_speed",
            [CalibrationMethod.PressureAdvanceTower] = "pressure_advance_tower",
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
    /// Attempts to parse a client-supplied calibration method name against the full calibration
    /// catalogue. A name that isn't catalogued at all (for example <c>"pa_pattern"</c> or
    /// <c>"pa_line"</c>, both intentionally excluded — see the licensing note on
    /// <see cref="CalibrationMethod"/>) returns <see langword="false"/>. Note this does
    /// <em>not</em> mean the parsed method is ready for the worker to slice today: two
    /// catalogued methods (<see cref="CalibrationMethod.FlowRateYoloRecommended"/> and
    /// <see cref="CalibrationMethod.FlowRateYoloPerfectionist"/>) parse successfully but are
    /// not yet slicer-supported — see <see cref="IsSlicerSupported"/> and
    /// <see cref="ClientAcceptedWireNames"/> for the check that actually gates client
    /// submission.
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
    /// <remarks>
    /// This check is opt-out: any method not explicitly excluded above is considered supported.
    /// <see cref="CalibrationMethod.PressureAdvanceTower"/> (issue #2136) is therefore already
    /// slicer-supported without an entry here — the worker's
    /// <c>OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync</c> implements it.
    /// </remarks>
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
        CalibrationMethod.Retraction => "retraction_tower.drc",
        CalibrationMethod.MaximumVolumetricSpeed => "SpeedTestStructure.drc",
        CalibrationMethod.PressureAdvanceTower => "tower_with_seam.drc",
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
        CalibrationMethod.Retraction => Path.Join("retraction", "retraction_tower.drc"),
        CalibrationMethod.MaximumVolumetricSpeed => Path.Combine("volumetric_speed", "SpeedTestStructure.drc"),
        CalibrationMethod.PressureAdvanceTower => Path.Combine("pressure_advance", "tower_with_seam.drc"),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported calibration method."),
    };
}

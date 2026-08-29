namespace Farm.Slicer.Module.Models;

/// <summary>
/// Calibration methods supported by the OrcaSlicer-worker calibration mode (issue #1938).
/// </summary>
/// <remarks>
/// This is the single canonical calibration-method vocabulary for PrintFarmer, per Dallas's
/// amended architecture decision on issue #2151/#2161: it is used both for the OrcaSlicer-worker
/// calibration mode described below and for the printer/toolhead <c>/api/calibration-projects</c>
/// saga (<see cref="Domain.SliceJob.CalibrationProjectId"/>/<see cref="Domain.SliceJob.CalibrationAttemptId"/>/
/// <see cref="Domain.SliceJob.CalibrationOrchestrationId"/>), which was retained/reshaped rather
/// than removed (issue #1940). A calibration-mode slice job produces a calibrated <em>filament</em>
/// profile via an ordinary ad-hoc slice — it never sets those three fields, so
/// <c>SlicePrintBridgeController.IsCalibrationSlice()</c> stays false and send-to-printer keeps
/// working for these jobs.
/// <para>
/// <strong>Unified vocabulary mapping table (issue #2161):</strong> before this change, the saga
/// (<c>Farm.Modules.Calibration</c>) maintained its own, separately-evolving 15-member
/// <c>CalibrationMethod</c> enum and wire-name dictionary, which agreed with this type's wire
/// names for only 6 of its 15 members - a live bug, since
/// <c>CalibrationOrchestrationSagaService.BuildSliceSubmissionBody</c> posted the saga's own wire
/// name straight to the real <c>POST /api/slice</c>, which this type's <see cref="CalibrationMethods"/>
/// parses against. The table below is the full old-name-to-new-name correspondence; the saga's
/// duplicate type has been deleted and every consumer now uses this one directly.
/// <list type="table">
/// <listheader><term>Old saga wire name</term><description>Unified wire name / disposition</description></listheader>
/// <item><term><c>temperature</c></term><description><c>temperature_tower</c> (<see cref="TemperatureTower"/>)</description></item>
/// <item><term><c>flow_ratio_coarse</c></term><description><c>flow_rate_pass_1</c> (<see cref="FlowRatePass1"/>)</description></item>
/// <item><term><c>flow_ratio_fine</c></term><description><c>flow_rate_pass_2</c> (<see cref="FlowRatePass2"/>)</description></item>
/// <item><term><c>flow_ratio_high_range</c></term><description>
/// Retired (see the audit note on <see cref="FlowRatePass1"/>/<see cref="FlowRateYoloRecommended"/>
/// below) - superseded by <c>flow_rate_yolo_recommended</c> (<see cref="FlowRateYoloRecommended"/>).
/// Still parses as a legacy alias of that method (never as a distinct canonical value) so a
/// previously-stored attempt is not orphaned.</description></item>
/// <item><term><c>pressure_advance_tower</c></term><description>unchanged (<see cref="PressureAdvanceTower"/>)</description></item>
/// <item><term><c>pressure_advance_line</c></term><description>unchanged, now catalogued here too (<see cref="PressureAdvanceLine"/>); not yet slicer-supported</description></item>
/// <item><term><c>pressure_advance_pattern</c></term><description>unchanged, now catalogued here too (<see cref="PressureAdvancePattern"/>); not yet slicer-supported</description></item>
/// <item><term><c>flow_verification</c></term><description>unchanged, now catalogued here too (<see cref="FlowVerification"/>); not yet slicer-supported</description></item>
/// <item><term><c>retraction</c></term><description>unchanged (<see cref="Retraction"/>)</description></item>
/// <item><term><c>max_volumetric_speed</c></term><description>unchanged (<see cref="MaximumVolumetricSpeed"/>)</description></item>
/// <item><term><c>shrinkage</c></term><description>unchanged, now catalogued here too (<see cref="Shrinkage"/>); not yet slicer-supported</description></item>
/// <item><term><c>final_verification</c></term><description>unchanged, now catalogued here too (<see cref="FinalVerification"/>); not yet slicer-supported</description></item>
/// <item><term><c>cornering</c></term><description>unchanged (<see cref="Cornering"/>)</description></item>
/// <item><term><c>input_shaping</c></term><description>unchanged (<see cref="InputShaping"/>)</description></item>
/// <item><term><c>vfa</c></term><description>unchanged (<see cref="Vfa"/>)</description></item>
/// <item><term><em>(slicer-only, now saga-visible too)</em></term><description><c>flow_rate_yolo_recommended</c>/<c>flow_rate_yolo_perfectionist</c> (<see cref="FlowRateYoloRecommended"/>/<see cref="FlowRateYoloPerfectionist"/>)</description></item>
/// </list>
/// </para>
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
/// ratios as baseline-relative deltas (for example <c>flowrate_0.01</c>, <c>flowrate_m0.01</c> for
/// Recommended's coarser 0.01 steps, or <c>flowrate_0.005</c>, <c>flowrate_m0.035</c> for
/// Perfectionist's finer 0.005 steps), not the absolute percentages (<c>flowrate_95</c>) that
/// <c>FlowRateCalibrationConfigurator</c> parses for <see cref="FlowRatePass1"/>/
/// <see cref="FlowRatePass2"/>. Issue #2141 shipped <c>FlowRateDeltaCalibrationConfigurator</c>, a
/// dedicated delta-aware configurator that applies <c>baseline + delta</c> per object (baseline
/// being the source filament profile's current <c>filament_flow_ratio</c>), and wired it in for
/// <see cref="FlowRateYoloRecommended"/>. Issue #2142 confirmed the same regex-based parser
/// already tolerates Perfectionist's extra decimal place unmodified (it matches
/// <c>\d+(?:\.\d+)?</c>, not a fixed decimal count) and wired the same configurator in for
/// <see cref="FlowRateYoloPerfectionist"/> too — both methods are now slicer-supported.
/// </para>
/// <para>
/// <see cref="Cornering"/> (issue #2138): cornering calibrates jerk (classic Marlin), junction
/// deviation (Marlin 2's <c>M205 J</c>), or Klipper's <c>SQUARE_CORNER_VELOCITY</c> — three
/// firmware-specific motion-planner concepts, unlike every other catalogued method here, which
/// are filament properties. Per the architecture decision recorded on issue #2138 (and shared
/// with #2139/#2140), this method is <strong>report-only</strong>: the calibration saga never
/// carries it into a filament-profile-clone/patch step, and the operator may separately, and
/// explicitly, record the resulting value onto the printer's own
/// <c>MaxJerk</c>/<c>JunctionDeviation</c>/<c>SquareCornerVelocity</c> fields (mirroring
/// <c>MaxAcceleration</c>) through the ordinary admin-gated printer update endpoint — never
/// automatically from this calibration flow. The bundled resource
/// (<c>resources/calib/cornering/SCV-V2.drc</c>) is, like <see cref="TemperatureTower"/>'s and
/// <see cref="MaximumVolumetricSpeed"/>'s <c>.drc</c> resources, an opaque OrcaSlicer binary
/// format that is copied unmodified rather than parsed and rewritten. Because jerk/junction
/// deviation and square corner velocity are meaningless outside Marlin/Marlin-2/Klipper
/// firmware, <c>OrcaSlicingPipelineService</c> validates the target printer's <c>gcode_flavor</c>
/// before slicing and refuses explicitly — rather than silently slicing a test result the
/// operator's firmware cannot even apply — for any other flavor (reprapfirmware, smoothie,
/// sprinter, etc.).
/// </para>
/// <para>
/// <see cref="InputShaping"/> (issue #2139): report-only per the architecture decision recorded
/// on that issue (Dallas). Input shaping compensates for mechanical resonance in the printer
/// frame, and the result is applied to <em>firmware</em> — Klipper's <c>[input_shaper]</c>
/// (typically measured via <c>SHAPER_CALIBRATE</c>/<c>TEST_RESONANCES</c> with an accelerometer,
/// or read off a printed ringing tower) or Marlin's <c>M593</c> ZV input shaping — never to a
/// slicer or filament setting PrintFarmer can write. There is deliberately no settable field for
/// this method anywhere (no <c>Printer</c> column, no filament-profile-clone patch step): the
/// worker only slices the bundled ringing-tower resource so the operator can print and measure
/// it, and the result is captured purely as a <c>CalibrationObservation</c> the operator
/// acts on themselves in their firmware config. Upstream's bundled resource
/// (<c>resources/calib/input_shaping/ringing_tower.drc</c>, confirmed against a local OrcaSlicer
/// install: DRAC magic bytes, not a ZIP/3MF archive) is opaque like <see cref="TemperatureTower"/>
/// and <see cref="MaximumVolumetricSpeed"/>'s <c>.drc</c> resources, so the worker copies it
/// unmodified rather than attempting to parse and rewrite it. Because the measured result is
/// firmware-specific (a Klipper shaper frequency/damping-ratio pair vs. Marlin's coarser
/// <c>M593</c> parameters), a calibration job for this method must name the firmware flavor it
/// targets (<c>"klipper"</c> or <c>"marlin"</c>, via <c>CalibrationParameters.FirmwareFlavor</c>);
/// an unsupported or missing flavor is refused explicitly by
/// <c>OrcaSlicingPipelineService.PrepareCalibrationModel</c> rather than silently slicing a tower
/// the operator has no firmware-specific guidance to act on.
/// </para>
/// <para>
/// <see cref="Vfa"/> (Vertical Fine Artifacts / resonance speed, issue #2140): VFA sweeps outer
/// wall speed across a tall tower to find the printer's resonance band (the range that produces
/// visible ringing/banding), so — like <see cref="Cornering"/> — it measures the printer's motion
/// system rather than a filament property, and the architecture decision recorded on issue #2140
/// makes it <strong>report-only</strong>: the calibration saga never carries it into a
/// filament-profile-clone/patch step, and there is no settable field to write back at all (the
/// operator's takeaway is a speed range to avoid, not a value to apply). The bundled resource
/// (<c>resources/calib/vfa/vfa.drc</c>, verified against the upstream OrcaSlicer resource tree)
/// is, like <see cref="MaximumVolumetricSpeed"/>'s and <see cref="Cornering"/>'s <c>.drc</c>
/// resources, an opaque binary format copied unmodified rather than parsed and rewritten.
/// Upstream's actual per-layer speed ramp for this tower uses the exact same in-process
/// mechanism documented above for <see cref="MaximumVolumetricSpeed"/> —
/// <c>GCode.cpp</c>'s <c>calib_mode()</c> switch (<c>case CalibMode::Calib_VFA_Tower</c>) drives
/// <c>m_calib_config.set_key_value("outer_wall_speed", ...)</c> from <c>Print::calib_params()</c>,
/// which is set only by the GUI's <c>CalibUtils::calib_VFA</c>/<c>process_and_store_3mf</c>
/// immediately before slicing, is never persisted to the 3MF project file, and has no CLI flag —
/// so, exactly as for <see cref="MaximumVolumetricSpeed"/>, it is not reachable from this
/// separate-process, CLI-driven worker. (This is why this method's implementation is closer in
/// shape to <see cref="MaximumVolumetricSpeed"/>'s permissive-ceiling configurator than to
/// <c>FlowRateCalibrationConfigurator</c>'s per-object 3MF rewriting — the bundled resource here
/// is an opaque tower, not a 3MF with per-object metadata to patch.) Upstream's
/// <c>CalibUtils::calib_VFA</c> also raises <c>filament_max_volumetric_speed</c> to 200 (versus
/// <see cref="MaximumVolumetricSpeed"/>'s 50) so the sweep's higher wall speeds are never
/// throttled by the slicer's own volumetric-speed limiter; the worker reproduces that ceiling via
/// <c>OrcaSlicingPipelineService.ApplyVfaMaxVolumetricSpeedCeilingAsync</c>, gated by
/// <c>CalibrationParameters.VfaMaxVolumetricSpeedCeilingMm3s</c>, and otherwise slices the bundled
/// tower geometry with the client-selected process profile's own constant wall speed — the wire
/// name still submits, slices, and returns gcode end to end per the issue's acceptance criteria,
/// while upstream's per-layer speed ramp itself remains unreproduced for the same structural
/// reason as <see cref="MaximumVolumetricSpeed"/>.
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
    /// (coarse pass). Slicer-supported via <c>FlowRateDeltaCalibrationConfigurator</c> (issue
    /// #2141); see the type-level remarks.
    /// </summary>
    FlowRateYoloRecommended,

    /// <summary>
    /// Flow rate calibration using OrcaSlicer's linear-regression "YOLO (Perfectionist)" method
    /// (fine pass). Slicer-supported via <c>FlowRateDeltaCalibrationConfigurator</c> (issue
    /// #2142); see the type-level remarks.
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
    /// Input shaping / resonance-compensation calibration (issue #2139). Report-only: see the
    /// type-level remarks for why this method never writes a filament profile, a
    /// <c>Printer</c> field, or firmware configuration, and requires a firmware flavor.
    /// </summary>
    InputShaping,

    /// <summary>
    /// Pressure advance tower calibration (issue #2136). Scope is deliberately Tower-only; PA
    /// Pattern and PA Line remain unsupported per the type-level remarks above. Emits per-band
    /// <c>layer_change_gcode</c> for Klipper (<c>SET_PRESSURE_ADVANCE</c>) or Marlin/Marlin2
    /// (<c>M900 K</c>) — see <c>PressureAdvanceTowerGcodeBuilder</c> in the OrcaSlicer worker.
    /// </summary>
    PressureAdvanceTower,

    /// <summary>
    /// Cornering (jerk / junction deviation / Klipper square corner velocity) calibration
    /// (issue #2138). See the type-level remarks for the report-only write-back model and the
    /// firmware-flavor gate this method needs.
    /// </summary>
    Cornering,

    /// <summary>
    /// VFA (Vertical Fine Artifacts / resonance speed) calibration (issue #2140). See the
    /// type-level remarks for the report-only write-back model and why this method's
    /// permissive-ceiling implementation mirrors <see cref="MaximumVolumetricSpeed"/> rather than
    /// <c>FlowRateCalibrationConfigurator</c>'s per-object 3MF rewriting.
    /// </summary>
    Vfa,

    /// <summary>
    /// Trusted server-generated pressure advance line (issue #2161 unification). Catalogued here
    /// so the calibration saga's own wire name agrees with what <c>SliceJobController</c>
    /// parses; intentionally not yet slicer-supported — no worker configurator exists for this
    /// Bambu-specific method today (see <see cref="CalibrationMethods.IsSlicerSupported"/>).
    /// </summary>
    PressureAdvanceLine,

    /// <summary>
    /// Trusted server-generated pressure advance pattern (issue #2161 unification). Catalogued
    /// here so the calibration saga's own wire name agrees with what <c>SliceJobController</c>
    /// parses; intentionally not yet slicer-supported — no worker configurator exists for this
    /// GPL-3.0-provenance-constrained method today (see <see cref="CalibrationMethods.IsSlicerSupported"/>).
    /// </summary>
    PressureAdvancePattern,

    /// <summary>
    /// Single-value flow verification print (issue #2161 unification). Catalogued here so the
    /// calibration saga's own wire name agrees with what <c>SliceJobController</c> parses;
    /// intentionally not yet slicer-supported — no worker configurator exists for this method
    /// today (see <see cref="CalibrationMethods.IsSlicerSupported"/>).
    /// </summary>
    FlowVerification,

    /// <summary>
    /// Shrinkage compensation bars (issue #2161 unification). Catalogued here so the calibration
    /// saga's own wire name agrees with what <c>SliceJobController</c> parses; intentionally
    /// not yet slicer-supported — no worker configurator exists for this method today (see
    /// <see cref="CalibrationMethods.IsSlicerSupported"/>).
    /// </summary>
    Shrinkage,

    /// <summary>
    /// Final verification against a linked imported asset or normal model (issue #2161
    /// unification). Catalogued here so the calibration saga's own wire name agrees with what
    /// <c>SliceJobController</c> parses; intentionally not yet slicer-supported — no worker
    /// configurator exists for this method today (see
    /// <see cref="CalibrationMethods.IsSlicerSupported"/>).
    /// </summary>
    FinalVerification,
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
            ["input_shaping"] = CalibrationMethod.InputShaping,

            // Matches the calibration saga's own wire names (issue #2161 unification) so the two
            // no longer diverge; #2136 already established this for pressure_advance_tower.
            ["pressure_advance_tower"] = CalibrationMethod.PressureAdvanceTower,
            ["cornering"] = CalibrationMethod.Cornering,
            ["vfa"] = CalibrationMethod.Vfa,

            // The five saga-only methods added by issue #2161's unification. None has a worker
            // configurator yet - see IsSlicerSupported.
            ["pressure_advance_line"] = CalibrationMethod.PressureAdvanceLine,
            ["pressure_advance_pattern"] = CalibrationMethod.PressureAdvancePattern,
            ["flow_verification"] = CalibrationMethod.FlowVerification,
            ["shrinkage"] = CalibrationMethod.Shrinkage,
            ["final_verification"] = CalibrationMethod.FinalVerification,
        };

    /// <summary>
    /// Legacy wire names the pre-unification calibration saga (<c>Farm.Modules.Calibration</c>)
    /// used to post before issue #2161. These are accepted by <see cref="TryParse"/> only - never
    /// by <see cref="ToWireName"/>, <see cref="SupportedWireNames"/>, or
    /// <see cref="ClientAcceptedWireNames"/> - so a previously-persisted
    /// <c>CalibrationAttempt.Method</c> value still parses (no data migration required, per the
    /// issue's persisted-data-compatibility audit: no seed/migration data was found under these
    /// names, and any pre-existing attempt using them always failed at the slicing step anyway,
    /// since the two catalogues disagreed on 9 of 15 names), while new callers are steered onto
    /// the one canonical name going forward. <c>flow_ratio_high_range</c> is a retirement alias:
    /// it never had a working slicer counterpart and is superseded by the newer, delta-aware
    /// <see cref="CalibrationMethod.FlowRateYoloRecommended"/>/<see cref="CalibrationMethod.FlowRateYoloPerfectionist"/>
    /// methods (issues #2141/#2142).
    /// </summary>
    private static readonly Dictionary<string, CalibrationMethod> LegacyWireNameAliases =
        new Dictionary<string, CalibrationMethod>(StringComparer.OrdinalIgnoreCase)
        {
            ["temperature"] = CalibrationMethod.TemperatureTower,
            ["flow_ratio_coarse"] = CalibrationMethod.FlowRatePass1,
            ["flow_ratio_fine"] = CalibrationMethod.FlowRatePass2,
            ["flow_ratio_high_range"] = CalibrationMethod.FlowRateYoloRecommended,
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
            [CalibrationMethod.InputShaping] = "input_shaping",
            [CalibrationMethod.PressureAdvanceTower] = "pressure_advance_tower",
            [CalibrationMethod.Cornering] = "cornering",
            [CalibrationMethod.Vfa] = "vfa",
            [CalibrationMethod.PressureAdvanceLine] = "pressure_advance_line",
            [CalibrationMethod.PressureAdvancePattern] = "pressure_advance_pattern",
            [CalibrationMethod.FlowVerification] = "flow_verification",
            [CalibrationMethod.Shrinkage] = "shrinkage",
            [CalibrationMethod.FinalVerification] = "final_verification",
        };

    /// <summary>
    /// The wire names of every calibration method <see cref="TryParse"/> recognizes, excluding
    /// legacy aliases (see <see cref="LegacyWireNameAliases"/>). Do not surface this list as
    /// "supported methods" in a client-facing error message — use <see cref="ClientAcceptedWireNames"/>
    /// for that.
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
    /// <em>not</em> mean the parsed method is ready for the worker to slice today — see
    /// <see cref="IsSlicerSupported"/> and <see cref="ClientAcceptedWireNames"/> for the check
    /// that actually gates client submission.
    /// </summary>
    /// <remarks>
    /// Also accepts the pre-unification calibration saga's legacy wire names (see
    /// <see cref="LegacyWireNameAliases"/>) so a previously-persisted <c>CalibrationAttempt.Method</c>
    /// value keeps parsing after issue #2161's unification. Legacy names are intentionally never
    /// produced by <see cref="ToWireName"/> or listed in <see cref="SupportedWireNames"/>/
    /// <see cref="ClientAcceptedWireNames"/>, so new callers are steered onto the canonical name.
    /// </remarks>
    /// <param name="wireName">The client-supplied method name.</param>
    /// <param name="method">The parsed method, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="wireName"/> names a supported method.</returns>
    public static bool TryParse(string? wireName, out CalibrationMethod method)
    {
        if (string.IsNullOrWhiteSpace(wireName))
        {
            method = default;
            return false;
        }

        string trimmed = wireName.Trim();
        if (WireNameToMethod.TryGetValue(trimmed, out CalibrationMethod parsed) ||
            LegacyWireNameAliases.TryGetValue(trimmed, out parsed))
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
    /// Both <see cref="CalibrationMethod.FlowRateYoloRecommended"/> (issue #2141) and
    /// <see cref="CalibrationMethod.FlowRateYoloPerfectionist"/> (issue #2142) now have a
    /// delta-aware configurator (<c>FlowRateDeltaCalibrationConfigurator</c>) and are
    /// slicer-supported. Callers that accept a client-supplied method — chiefly the slice-job
    /// submission endpoint — must still check this at the API boundary rather than letting
    /// <see cref="TryParse"/> alone gate acceptance, so a future catalogued-but-not-yet-supported
    /// method fails fast instead of only failing late, after dispatch.
    /// </summary>
    /// <param name="method">A method that already parsed successfully via <see cref="TryParse"/>.</param>
    /// <returns><see langword="true"/> when the worker can slice this method today.</returns>
    /// <remarks>
    /// This check is opt-out: any method not explicitly excluded is considered supported.
    /// <see cref="CalibrationMethod.PressureAdvanceTower"/> (issue #2136) is therefore already
    /// slicer-supported without an entry here — the worker's
    /// <c>OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync</c> implements it.
    /// <see cref="CalibrationMethod.PressureAdvanceLine"/>, <see cref="CalibrationMethod.PressureAdvancePattern"/>,
    /// <see cref="CalibrationMethod.FlowVerification"/>, <see cref="CalibrationMethod.Shrinkage"/>,
    /// and <see cref="CalibrationMethod.FinalVerification"/> (issue #2161 unification) are the
    /// current opt-out entries: they are catalogued (parseable, with a defined wire name) but no
    /// worker configurator exists for any of them yet - do not flip any of these to
    /// <see langword="true"/> until a real slicing implementation lands for it.
    /// </remarks>
    public static bool IsSlicerSupported(CalibrationMethod method) => method switch
    {
        CalibrationMethod.PressureAdvanceLine
            or CalibrationMethod.PressureAdvancePattern
            or CalibrationMethod.FlowVerification
            or CalibrationMethod.Shrinkage
            or CalibrationMethod.FinalVerification => false,
        _ => true,
    };

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
        CalibrationMethod.InputShaping => "ringing_tower.drc",
        CalibrationMethod.PressureAdvanceTower => "tower_with_seam.drc",
        CalibrationMethod.Cornering => "SCV-V2.drc",
        CalibrationMethod.Vfa => "vfa.drc",
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
        CalibrationMethod.InputShaping => Path.Combine("input_shaping", "ringing_tower.drc"),
        CalibrationMethod.PressureAdvanceTower => Path.Combine("pressure_advance", "tower_with_seam.drc"),
        CalibrationMethod.Cornering => Path.Combine("cornering", "SCV-V2.drc"),
        CalibrationMethod.Vfa => Path.Combine("vfa", "vfa.drc"),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported calibration method."),
    };
}

/// <summary>
/// Canonical firmware flavors <see cref="CalibrationMethod.InputShaping"/> calibration (issue
/// #2139) supports. Defined here — in <c>Farm.Slicer.Module</c>, referenced by both the API
/// project (<c>SliceJobController</c>, which validates a client's request) and the worker project
/// (<c>CalibrationParameters</c>, which re-validates defense-in-depth before slicing) — so the two
/// layers share one source of truth instead of maintaining duplicate lists that could drift.
/// </summary>
public static class InputShapingFirmwareFlavors
{
    /// <summary>
    /// Klipper: input shaping is tuned via the <c>SHAPER_CALIBRATE</c>/<c>TEST_RESONANCES</c>
    /// macros (typically with an accelerometer, or by measuring a printed ringing tower) and
    /// applied through the <c>[input_shaper]</c> config section.
    /// </summary>
    public const string Klipper = "klipper";

    /// <summary>
    /// Marlin: input shaping is configured via the <c>M593</c> ZV input shaping G-code command.
    /// </summary>
    public const string Marlin = "marlin";

    /// <summary>The full set of supported firmware flavor wire values, in stable order.</summary>
    public static readonly IReadOnlyList<string> Supported = [Klipper, Marlin];

    /// <summary>
    /// Whether <paramref name="firmwareFlavor"/> names a firmware flavor input shaping
    /// calibration supports. Comparison is case-insensitive and trims surrounding whitespace; a
    /// null, blank, or unrecognized value returns <see langword="false"/> — callers must refuse
    /// those explicitly rather than silently defaulting to either supported flavor.
    /// </summary>
    public static bool IsSupported(string? firmwareFlavor) =>
        !string.IsNullOrWhiteSpace(firmwareFlavor)
        && Supported.Contains(firmwareFlavor.Trim(), StringComparer.OrdinalIgnoreCase);
}

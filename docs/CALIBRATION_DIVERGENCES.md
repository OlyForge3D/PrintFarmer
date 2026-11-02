## Ordinary slicer G-code checks and historical calibration policy

The deterministic calibration generator and its adapter were removed in #1979.
This document previously described that removed pipeline as if it still ran.
`GcodeSafetyValidator` now checks ordinary slicer output; it is not a controlled
calibration-program verifier.

### Removed historical assumptions

Ordinary files need not contain literal `G28`, heating before extrusion,
`TURN_OFF_HEATERS`, `M84`/`M18`, or balanced `PF_SEG_*` markers. Firmware macros such
as `PRINT_START`/`PRINT_END` may own those operations. The old command allowlist,
`TUNING_TOWER` prohibition, pressure-advance ceilings (2.0 / 0.5 direct drive),
and retraction ceilings (10 / 3 mm direct drive) no longer apply. None was a
configured ordinary-file limit.

The former vendor-profile hook neutralization and calibration flow-rate clamping
also belonged to removed calibration compilers, not the current ordinary-file
validator. They must not be presented as current protections.

### Retained static checks

All checks below run in
[`GcodeSafetyValidator`](../src/modules/Farm.Modules.Gcode/Services/Gcode/Safety/GcodeSafetyValidator.cs).
It rejects rather than rewrites the file and reports a code and first line per
distinct rejection code.

| Check | Recognized input and application |
| --- | --- |
| Nozzle temperature | `M104`/`M109 S`: nozzle maximum, falling back to hotend maximum when nozzle maximum is unset. |
| Bed temperature | `M140`/`M190 S`: configured bed maximum. |
| Chamber temperature | `M141`/`M191 S`: nonzero values require `HasHeatedChamber == true` and a configured maximum, then must not exceed it. Zero is accepted. |
| Acceleration | `M204 S` and `SET_VELOCITY_LIMIT ACCEL=` against configured maximum acceleration. |
| Speed | `SET_VELOCITY_LIMIT VELOCITY=` against travel maximum; explicit `G0`/`G1 F` (mm/min divided by 60) against print maximum for positive extrusion, travel maximum otherwise. |
| XY volume | Absolute-position `G0`/`G1` XY endpoints against origin plus size, when both X and Y sizes exist; 0.01 mm tolerance. |
| Printable polygon / exclusions | Absolute-position XY endpoints must lie in the configured printable polygon and outside configured excluded-region polygons. |
| Z volume | `G0`/`G1` with Z must not exceed the configured Z maximum. |
| Volumetric flow | Only if filament diameter **and** maximum flow are supplied. Uses positive extrusion, stored feed rate and XY Manhattan distance, with 0.05 mm3/s tolerance. Neither ordinary caller supplies these limits today. |
| Executable host commands | Exact first command token after an optional `N` line-number prefix, case-insensitive: `RUN_SHELL_COMMAND`, `/bin/sh`, `cmd.exe`, `powershell`, `curl`, `wget`, `nc`, `ssh`. Semicolon comments are stripped first. |
| Empty input | An empty program is rejected as `gcode_malformed`. |

Credential words, URLs, absolute paths and host-command words in full-line or
trailing semicolon comments are not executable instructions and are not scanned.
The host check does not reject a display message or a macro argument merely
because it contains one of those strings. This is a narrow recognized-command
check, **not** a credential scrubber or general shell-language analyzer.

### Where checks run and where they do not

| Path | Current static-validator coverage |
| --- | --- |
| Slice send-to-printer | [`SlicePrintBridgeController.SendToPrinterAsync`](../src/modules/Farm.Modules.PrintQueue/Controllers/SlicePrintBridgeController.cs) validates before **both upload-only and immediate start**. Failure returns HTTP 400 without uploading. The uploaded bytes are the same bytes read for validation. |
| Printer-file/catalog queue admission | [`PrinterFileQueueService.AdmitAsync`](../src/modules/Farm.Modules.Gcode/Services/Gcode/PrinterFileQueueService.cs) validates verified local content against the actual destination before creating a job; a rejection blocks the operation with its problem code. |
| Slice add-to-queue | `SlicePrintBridgeController.AddToQueueAsync` promotes the artifact and calls `IJobQueueService.AddJobToQueueAsync`; it does **not** invoke this validator. |
| Queue dispatch/start | Does **not** invoke this validator. Other dispatch checks below are not equivalent to inspecting the G-code commands. |

Checkpoint enum names are report metadata, not proof that every named lifecycle
point is wired. In particular there is no universal artifact-completion, promotion
or dispatch static-validation gate.

Both covered callers use
[`PrinterGcodeSafetyLimits.Build`](../src/infra/Services/Gcode/Safety/PrinterGcodeSafetyLimits.cs).
Limits come from persisted PrintFarmer `Printer` and primary (or first available)
`Toolhead` records, including the hotend-model fallback. They are **not fetched
live from firmware**. Unset nozzle/bed/speed/acceleration/volume/flow ceilings skip
their checks; unset XY origins default to zero. Chamber heating is the exception
described above. Keep the records accurate; a missing limit is not an implicit
firmware-derived ceiling.

Configured malformed polygons, null/missing points, fewer than three vertices,
and nonfinite/unrepresentable coordinates fail closed in the shared builder.
The bridge returns HTTP 400; printer-file admission blocks with
`destination_safety_geometry_invalid`. Empty/unconfigured geometry is not the
same as malformed configured geometry.

### Other gates are separate

These are verified call paths, not a claim that every possible start route
enforces every check:

- The slice bridge checks permissions/resource scope, job ownership and completed
  status, destination existence, durable artifact promotion and file availability
  before upload. Its immediate-start helper acquires the shared dispatch claim;
  upload-only does not start a print.
- Printer-file admission rechecks actor access, confirmation binding, local file
  digest/size, destination enabled/compatibility, and a destination revision fence
  before committing the pinned job. See `PrinterFileQueueService` above.
- [`AutoDispatchService`](../src/infra/Services/AutoDispatch/AutoDispatchService.cs)
  returns pre-cleared auto-start to PendingReady when filament evidence is not
  Compatible. Its ordinary ready-gate preflight requires an explicit override
  for Incompatible **or Unknown** evidence, binding that override to the exact
  job, printer and evidence versions. This is ready/start behavior, not proof
  that insufficient estimated quantity blocks ordinary queue admission.
- [`FilamentPreflightEvaluator`](../src/infra/Services/AutoDispatch/FilamentPreflightEvaluator.cs)
  returns Unknown for missing spool/service/material/estimate/remaining-weight
  evidence, and Incompatible for material mismatch or estimated shortage.
  [`DispatchClaimService`](../src/infra/Services/Queue/Dispatch/DispatchClaimService.cs)
  rechecks and fingerprints supplied reviewed filament evidence at claim time.
- Printer-file compatibility scoring explicitly disables `gramsAware` in
  [`DispatchScorer.PrinterFiles`](../src/infra/Services/Queue/Dispatch/DispatchScorer.PrinterFiles.cs).
  Quantity scoring is not an unconditional admission veto. This change does not
  modify queue, filament, authorization or claim policy.

### Static-analysis limits and risk

Unknown firmware macros are accepted but not expanded. Their heating, homing,
shutdown, shell execution and motion cannot be certified from the file. The
parser recognizes a limited whitespace-delimited command/parameter vocabulary,
semicolon comments, optional `N` line-number prefixes, `G90`/`G91`, `M82`/`M83`,
and `G92 E`. It is not a complete
firmware interpreter: arcs, compact command/parameter syntax, macro effects,
work-coordinate transforms, complete path/segment intersections, negative Z,
and inherited feed-rate ceiling enforcement are not comprehensively checked.
Relative XY moves are tracked but do not receive the absolute-XY endpoint checks.
Do not treat acceptance, a SHA-256 report, or a successful upload as proof that
printing is safe.

The change is reversible policy removal, not a firmware safety replacement.
A retained-envelope regression or an executable recognized host command passing
would falsify the intended contract; tests cover both rejection and acceptance.
If assumptions are wrong, operators and nearby people can face equipment,
thermal or motion hazards, potentially irreversible hardware damage. Keep
firmware limits, trusted macros and operator procedures in place; static checks
cannot substitute for them, especially on the documented uncovered queue paths.

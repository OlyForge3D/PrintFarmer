## Ordinary slicer G-code checks and historical calibration policy

The deterministic calibration generator and its adapter were removed in #1979.
This document previously described that removed pipeline as if it still ran.
`GcodeSafetyValidator` now rejects only null or empty input and records a report
for nonempty text. It does not interpret or certify ordinary slicer output.

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

### Retained input check and report

All checks below run in
[`GcodeSafetyValidator`](../src/modules/Farm.Modules.Gcode/Services/Gcode/Safety/GcodeSafetyValidator.cs).
Null and zero-length text return `gcode_malformed`, field `gcode`, message
`The g-code program is empty.` Whitespace-only and comment-only text are nonempty
and accepted. No content is rewritten.

Success retains the requested checkpoint, SHA-256 of the supplied UTF-8 text,
command count (nonempty lines after stripping semicolon comments), and UTC
timestamp. The count is metadata, not parsed command validation. Temperature,
motion, printable/excluded geometry, speed, acceleration, extrusion/flow,
host-command, credential/URL/path and historical calibration checks do not run.
Even malformed or unknown nonempty commands are accepted.

### Where checks run and where they do not

| Path | Current input-check coverage |
| --- | --- |
| Slice send-to-printer | [`SlicePrintBridgeController.SendToPrinterAsync`](../src/modules/Farm.Modules.PrintQueue/Controllers/SlicePrintBridgeController.cs) checks decoded text before **both upload-only and immediate start**. Empty input returns HTTP 400 without uploading. A leading UTF-8 BOM is excluded from the text check, so a BOM-only file is empty. Uploaded bytes retain the original BOM and otherwise remain identical to those read. |
| Printer-file/catalog queue admission | [`PrinterFileQueueService.AdmitAsync`](../src/modules/Farm.Modules.Gcode/Services/Gcode/PrinterFileQueueService.cs) checks verified local text before creating a job; empty input blocks the operation with `gcode_malformed`. |
| Slice add-to-queue | `SlicePrintBridgeController.AddToQueueAsync` promotes the artifact and calls `IJobQueueService.AddJobToQueueAsync`; it does **not** invoke this validator. |
| Queue dispatch/start | Does **not** invoke this validator. Other dispatch checks below are not equivalent to inspecting the G-code commands. |

Checkpoint enum names are report metadata, not proof that every named lifecycle
point is wired. There is no universal artifact-completion, promotion or dispatch
static-validation gate. Neither covered caller constructs command limits from
printer/toolhead records. Missing, malformed or nonfinite stored geometry does
not veto these input checks. Database geometry and firmware configuration remain
unchanged and can still serve other features.

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

### Responsibility and risk

Firmware configuration, trusted programs/macros and operator procedures own
command safety, including heating, homing, shutdown, shell execution and motion.
PrintFarmer no longer attempts static command-content policing on these paths.
Do not treat acceptance, a SHA-256 report, or a successful upload as proof that
printing is safe.

The application policy change is reversible; executing a hazardous accepted
program may not be. A nonempty program rejected by these input checks, an empty
program accepted, or changed report/hash/upload bytes would falsify the intended
contract; regression tests cover acceptance, rejection and byte preservation.
If assumptions are wrong, operators and nearby people can face equipment,
thermal or motion hazards, potentially irreversible hardware damage. Keep
firmware limits, trusted macros and operator procedures in place.

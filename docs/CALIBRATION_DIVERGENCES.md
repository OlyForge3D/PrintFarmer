# Calibration Divergences From OrcaSlicer

> **Purpose**: Distinguish PFD's *intentional* design decisions from *regressions* or *silent
> drift* when calibration output is compared against OrcaSlicer-derived references (see the
> golden/parity fixture work in #1929, part of #1926).

PFD's calibration generation pipeline (`src/api/Services/Calibration/Generation/`) reuses
OrcaSlicer's native profile format and slicing engine, but it is not a byte-for-byte
reimplementation of OrcaSlicer's calibration wizard. A handful of behaviors are deliberately
different, almost always because PFD runs calibration **unattended against real, remote
hardware**, whereas OrcaSlicer's calibration wizard runs interactively on a desktop where a
human reviews the G-code (or the print) before it reaches a printer.

If a future golden/parity test (#1929) reports a mismatch against an OrcaSlicer-generated
reference, check this list first. A match to an entry below means the divergence is expected;
add a code comment linking back to this document if the test needs to special-case it. If the
mismatch isn't listed here, treat it as a genuine bug or drift, not an intentional divergence.

## 1. Safety ceilings reject values OrcaSlicer would slice unmodified

**Method(s)**: all — enforced pipeline-wide as a final, reject-only checkpoint.

**What differs**: `GcodeSafetyValidator` statically interprets every emitted
calibration program and rejects it outright if any commanded nozzle/bed/chamber temperature,
acceleration, feed rate, retraction distance, pressure advance value, or volumetric flow rate
exceeds an authoritative ceiling (drawn from the printer's own profile limits, plus a small set
of absolute, hardcoded ceilings — see below). OrcaSlicer's calibration wizard has no equivalent
static pass: it will happily slice and hand over a tuning-tower program that commands values
outside a printer's configured limits, trusting the firmware or the operator to catch it.

**Why**: PFD calibration jobs are queued and executed without a human present to watch the
first layer or abort a runaway heat/speed command. The validator exists specifically to fail
closed before a job reaches a worker, rather than relying on firmware limits (which may not be
configured, or may be wrong) or an operator (who isn't there). This is documented as a design
invariant in the validator's own remarks: *"The validator never rewrites, repairs or normalizes
G-code... it either returns a clean report or the ordered reasons the program must not be
completed, promoted, queued or started."*

**Where in code**:

- `src/api/Services/Gcode/Safety/GcodeSafetyValidator.cs` (general, calibration-independent
  checks) —
  `ApplyNozzleTemperature`, `ApplyBedTemperature`, `ApplyChamberTemperature`,
  `ApplyAcceleration`, `ApplyVelocityLimit`, `ApplyMove` (feed rate, retraction, volumetric flow),
  `ApplyPressureAdvance`.
- Tested by `src/tests/Farm.Web.Api.Tests/Services/Gcode/Safety/GcodeSafetyValidatorTests.cs`
  and `src/tests/Farm.Web.Api.Tests/Services/Calibration/Generation/CalibrationGcodeProgramValidatorTests.cs`.
- A related but separate mechanism proactively *clamps* (rather than rejects) the printed feed
  rate to the volumetric-flow ceiling during specification compilation, specifically so a wide
  nozzle's calibration program is resolved safely instead of being rejected later by the
  validator above — see `CalibrationSpecificationCompiler.cs` (search for the `Clamp the printed
  feed rate` comment).

## 2. Direct-drive extruders get stricter pressure-advance and retraction ceilings

**Method(s)**: `PressureAdvanceTower`, `PressureAdvanceLine`, `PressureAdvancePattern`,
`Retraction`.

**What differs**: The safety validator's pressure-advance ceiling is `min(2.0,
0.5 if direct-drive else 2.0)`, and its retraction ceiling is `min(10.0mm,
3.0mm if direct-drive else 10.0mm)`. OrcaSlicer's own calibration test generators apply no
extruder-topology-aware ceiling at all — a user can dial in any pressure advance or retraction
distance the test wants to sweep, regardless of whether the toolhead is direct-drive or Bowden.

**Why**: Pressure-advance and retraction values that are safe on a long Bowden path can strip a
direct-drive extruder's gear or slam it against its stop on a short, stiff drive path. Because
PFD calibration is unattended, the printer's own hardware topology (`IsDirectDrive`) is used to
pick a tighter, hardware-appropriate absolute ceiling rather than trusting the sweep range the
calibration method itself would otherwise request.

**Where in code**:

- `src/api/Services/Gcode/Safety/GcodeSafetyValidator.cs` —
  `ApplyPressureAdvance` (pressure-advance ceiling), `ApplyMove` (retraction ceiling), and the
  `AbsolutePressureAdvanceCeiling` / `AbsoluteRetractionCeiling` constants.
- Tested by `src/tests/Farm.Web.Api.Tests/Services/Calibration/Generation/CalibrationGcodeProgramValidatorTests.cs`
  in the calibration test suite.

## 3. Custom G-code hooks and post-processing scripts are neutralized in vendor profiles

**Method(s)**: all — applied whenever a machine/process/filament profile is compiled into a
calibration plan.

**What differs**: Official OrcaSlicer vendor profiles routinely populate custom G-code hook
keys (any native key ending in `_gcode`, e.g. `before_layer_gcode`, `machine_start_gcode`) and
`post_process` / `printer_notes`. OrcaSlicer trusts these keys and will execute or emit them
verbatim when slicing normally. PFD's calibration plan compiler neutralizes every such key
before a worker ever sees the effective profile: the immutable baseline document keeps its
original bytes and digest for provenance, but the document sent downstream carries none of
their values.

**Why**: These keys are command-bearing by construction (arbitrary G-code, or scripts invoked
by the desktop OrcaSlicer app). A calibration job compiled from a third-party or user-imported
vendor profile must not be able to smuggle arbitrary commands into an automated, unattended
print pipeline. The rule is stated by key *shape* (suffix), not by an enumerated list, so a
future upstream hook this build has never heard of is neutralized on sight rather than
surviving as an unrecognized field.

**Where in code**:

- `src/api/Services/Calibration/Generation/OrcaCalibrationPlanCompiler.cs` —
  `OrcaProfileCommandKeys` (the forbidden-key rule) and `OrcaEffectiveProfileFactory` (where
  neutralization is applied).

## 4. Calibration G-code is restricted to a trusted command allowlist and forbids `TUNING_TOWER`

**Method(s)**: all Klipper-targeted calibration methods.

**What differs**: Klipper's own tuning workflow — and the macros OrcaSlicer's community guides
often point users at — frequently drives a live-adjusted parameter with the `TUNING_TOWER`
macro (e.g. sweeping pressure advance mid-print via `SET_PRESSURE_ADVANCE`). PFD's generator
never emits `TUNING_TOWER`, and the safety validator explicitly rejects it if found (along with
any other command outside a fixed, trusted allowlist — see `gcode_command_not_allowlisted` and
`gcode_tuning_tower_forbidden`).

**Why**: `TUNING_TOWER` and free-form macro commands are open-ended by design — that is the
point of the macro in an interactive workflow. PFD instead generates discrete, pre-computed test
points up front so the resulting program is deterministic and reproducible for golden/parity
comparison, and so the safety validator can reason about a small, closed command allowlist
instead of an open-ended macro surface. This also closes an injection vector: an unallowlisted
command (`RUN_SHELL_COMMAND`, `SAVE_CONFIG`, host-command markers such as `curl`/`ssh`, etc.) is
rejected outright rather than executed against real hardware.

**Where in code**:

- `src/api/Services/Gcode/Safety/GcodeSafetyValidator.cs` — the general command allowlist
  check and `TUNING_TOWER` rejection in the G-code interpretation loop. The allowlist itself
  is optional and calibration-specific: `CalibrationGcodeProgramValidator` is the only caller
  that passes one (`KlipperCalibrationCommands.Allowlist`); the send-to-printer path leaves it
  unset so ordinary slicer g-code is not constrained to a calibration vocabulary.
- Tested by `src/tests/Farm.Web.Api.Tests/Services/Calibration/Generation/CalibrationGcodeProgramValidatorTests.cs`
  (`Validate_WithInjectedTuningTower_RejectsExplicitly`,
  `Validate_WithNonAllowlistedCommand_Rejects`) and
  `src/tests/Farm.Web.Api.Tests/Services/Gcode/Safety/GcodeSafetyValidatorTests.cs`
  (`Validate_WithAllowlistAndDisallowedCommand_Rejects`,
  `Validate_WithNoAllowlist_AcceptsArbitrarySlicerCommands`).

## Adding a new entry

When you discover or introduce another deliberate PFD/OrcaSlicer divergence in the calibration
pipeline, add a numbered entry above with the same four fields: **Method(s)**, **What
differs**, **Why**, and **Where in code**. Keep entries in the order they were added rather than
renumbering existing ones, so code comments and PR discussions that cite an entry number stay
valid.

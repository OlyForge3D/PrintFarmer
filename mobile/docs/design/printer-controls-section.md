# Printer Controls Section — UX Spec

**Status:** v1 locked
**Issue:** #283
**Implementers:** #284 (Preheat), #285 (Home), #286 (Jog)
**Owner:** Newt (UX) → Hudson (iOS)
**Last updated:** 2026-09-09

This spec defines the visual hierarchy, component anatomy, interaction model, accessibility, and edge cases for the **Printer Controls** section that lives inside `PrinterDetailView`. Three subgroups in fixed order: **Preheat → Home → Jog**.

## Embedding contract (#2521)

`Views/PrinterControls/PrinterSetupControlsContent.swift` exports
`PrinterSetupControlsContent(printer:viewModel:)`. It observes an externally
owned `PrinterControlsViewModel` and renders thermal/motion, lockout and
command outcome presentation. It does not create services/models or subscribe to
SignalR. Production hosts explicitly opt into `observesSafety`, which asks the same
owner to refresh read-only evidence every five seconds while foregrounded; default
embedded content performs no automatic reads. `JogSubgroup` no longer loads capabilities;
its initial capability observation normalizes selection for preloaded limited axes.

`PrinterControlsSection(printer:composition:)` remains the standalone owner,
with lazy construction directly inside `StateObject(wrappedValue:)`. The
`printer:viewModel:` wrapper initializer is test-only. The wrapper loads
capabilities and forwards `PrinterControlsUpdateSignal` changes outside
online/offline content, so hiding controls cannot strand an acknowledgement.

**Essential integration (#2594):** `PrinterDetailView` embeds the presentation-only
`PrinterSetupControlsContent` in an always-discoverable Overview/Controls pager.
`AdvancedPrinterControlsAccess.isEntryVisible` gates command content and owner
construction, not destination existence. An offline/disabled visit explains
the restriction without loading capabilities, enabling a setting or dispatching
commands. The settings link uses the existing `AppDestination.settings` route.

For embedding, retain one owner above page visibility, scoped by registered server
and printer UUID. The host supplies the scoped service, loads capabilities once,
and forwards meaningful updates while the Controls page is hidden. Do not use
the test-only wrapper initializer or create an owner per page. Apply
`AdvancedPrinterControlsAccess` before exposing command content; this observer does not
read feature flags. Replace the owner when the real server/printer target changes.

### Effective native operations and gates

| Task | Existing dispatch | Preserved gate/confirmation |
| --- | --- | --- |
| Preheat PLA/PETG/ABS | `setTemperatures` | Temperature capability and valid reported maxima for every included heater; omit unsupported bed |
| Cool Down | Same preheat command, 0/0 preset | Shown only with temperature subgroup; unsupported confirmation targets ignored |
| Home All/XY/Z | `home`, `homeXY`, `homeZ` | Independent capability; fresh requested-axis acknowledgement, or acceptance-only when already homed |
| Jog | `move` | Movement + supported axes; matching-axis position update; existing distances/feedrates |
| Individual hotend/bed | `setTemperatures` | Specific heater support; other heater omitted; exact setpoint acknowledgement |
| Absolute XYZ | `moveTo` | Absolute support + supported axes; every requested coordinate must match |
| Disable motors | `disableMotors` | Specific motor-release support + explicit confirmation; acceptance only, no motor telemetry |

All operations retain shared transport single-flight and online/idle gating. Printing
and paused printers keep the explanatory lockout. Unrelated telemetry and
other-printer updates do not acknowledge pending commands. Becoming offline or
entering an unsafe state invalidates physical confirmation, not an outstanding
HTTP response. Network/server failures can have uncertain physical outcomes;
inspect the printer before another request. Z-offset, physical filament and
console were not part of #2598; material/calibration extensions are described below.
Earlier design proposals below are not evidence of additional command support
or per-subgroup concurrency.

The detail host supplies `usesColumns` from its actual available width (at least
760 points), not device identity. Thermal work occupies the leading column and
Home/Jog share the supporting column; this leaves a composition seam for later
material tools without introducing a new command owner. Narrow layouts stack.
Accessibility text sizes use a single outer column, including on iPad. Error
dismissal remains a separately accessible button with a minimum 44-point target.
The controls snapshot suite includes hosted observer-remount, wrapper-offline,
retained-owner, preloaded-axis and large-text regressions; model/correlation
suites continue to exercise native dispatch through mock services.

### Guarded physical material and calibration (#2599)

`PrinterMaterialControls` occupies the thermal/material column;
`PrinterZOffsetCalibrationControls` occupies the motion column. Both observe
the existing owner. The detail host passes its existing
`PrinterFilamentPresentation` to the thermal/material column, which reuses
`PrinterFilamentSection` passively, without assignment callbacks. Assign/Clear,
NFC and Eject remain on Overview. Calibration is inline, not a second modal control owner,
so the detail host's independently confirmed Emergency Stop remains reachable.
Narrow and accessibility-text layouts stack. Buttons remain at least 44 points.

The extrusion choices are signed 10/25/50/100 mm and 1/5/10 mm/s. The owner
converts speed to mm/min once before the typed service boundary. Availability
and dispatch both require `verifiedSafety` v1 from `backend-capabilities` and
`safetyTelemetry` from `status` (#2613/#2617). The minimum must be Verified,
finite and sourced; measured hotend temperature must meet it and pass its own
`observedAtUtc` / `staleAfterSeconds` policy. Future dates, absent provenance,
unknown versions and missing/nonfinite samples fail closed. Partial discovery can
authorize individually verified facts. Targets, preheat completion, catalog maxima
and assigned-spool metadata are never substitutes.

Refresh safety checks performs reads only. A native status read expires after
15 seconds; receipt of a new response never renews an old fact's timestamp.
The server independently re-probes and preflights every physical dispatch.
Backgrounding, reconnect/configuration changes and owner/access changes invalidate
client evidence. Static discovery timestamps are provenance, not a heater clock;
configuration revision and the discovered movement frame fence calibration.

Load, Unload and Change filament require their own explicit operation flags,
Supported per-operation discovery, the same measured-temperature guard, and
a native confirmation. The unload path consumes `FilamentUnloadResult`;
its residual weight and spool ID are not physical-loaded state. Successful
results say **request accepted**, preserve server guidance and ask the operator
to verify completion. False/failed/uncertain responses never become success.
No physical action binds or clears a spool, and no action supplies a toolhead
index. Existing Assign/Change spool, Clear assignment, NFC and combined Eject
remain unchanged under their original owners.

Calibration exposes Introduction → Home → Position → Adjust → Review/Save → Done.
Starting reads the existing stored offset without assuming zero. Fresh
matching homing telemetry, not HTTP acceptance or a cached homed flag,
is necessary to advance Home. Position requires verified origin, travel envelope
and minimum clearance, freshly homed XYZ, fresh matching origin-offset telemetry,
and finite reported XYZ. It lifts vertically first when needed, then centers within
the verified envelope after converting through the actual frame. No guessed bed
size, zero baseline or paper-test height is used. Adjustments preserve XY and send
all XYZ coordinates, respecting the server's effective-coordinate bounds and
clearance. A reported matching position after dispatch is required before changing
the draft; HTTP acceptance or unrelated legacy telemetry cannot advance it.
Clearance may prevent reaching a useful paper-test height on some hardware; the
app explains this rather than overriding it.

The guarded downstream semantics use 0.01/0.05/0.1 mm increments (negative is
closer), -5…5 mm save bounds, a freshly reviewed `PrinterDetails.rowVersion`,
and `saveToFirmware: true`. A review is consumed on any save attempt, including
412/428 or an uncertain outcome; refresh/review never automatically retries.
Done describes firmware-request acceptance, not a measured gap or first-layer
quality. The full conditional flow is exercised with a synthetic shared-contract
fixture; it is not a claim that any current production backend proves every fact.

Cancellation/dismissal, loss of access and server-epoch changes invalidate the
flow's session token. Outstanding physical responses retain the existing
single-flight lease until settled; late replies cannot advance canceled steps.
A workflow lease also prevents other controls owners on the same server/printer
from issuing routine commands between calibration steps. Baseline-offset changes,
frame/configuration changes and observed stale revisions require a new review;
there is no physical-save retry, including after 412/428 or uncertain firmware results.
Unrelated temperature/position changes never acknowledge a filament operation.

**Shared-contract exclusions, verified against source:**

| Operation | Current production availability / missing evidence |
| --- | --- |
| Extrude / retract | Conditional native path complete; current Moonraker discovery deliberately leaves the material-safe minimum Unknown |
| Load / unload / change | Conditional native paths complete; Moonraker now probes installed LOAD_FILAMENT / UNLOAD_FILAMENT / M600 macros, but still requires a verified material-safe minimum |
| Calibration home | Existing ordinary Home controls remain available by their own flags; the calibration flow additionally requires firmware/movement support |
| Calibration position / adjust | Conditional native path complete; Moonraker now supplies verified geometry and separate G90/G0 support, but its clearance remains Unknown |
| Firmware save | Conditional reviewed native save complete; current Moonraker discovery explicitly reports Unsupported firmware persistence |
| Assign / clear / NFC / combined Eject | Existing paths unchanged; not replaced by these physical controls |

Sources: `src/infra/Services/Printers/PrinterBackendCapabilitiesService.cs`,
`src/infra/Models/PrinterBackendCapabilitiesDto.cs`,
`src/modules/Farm.Modules.Printers/Controllers/PrintersController.cs`
(`extrude`, `filament-*`, `z-offset`), and native
`src/infra/Models/PrinterSafetyContracts.cs`, `PrinterSafetyGuard.cs`, and native
`Models/PrinterBackendCapabilities.swift` / `Models/Models.swift`. Native types
mirror the shared camelCase/string-enum DTOs; no backend routes, schema changes or
production safety values are added by #2599.

Integration evidence lives in `mobile/build/guarded-safety-iPhone/` and
`mobile/build/guarded-safety-iPad/`, including native view attachments at
390/1024/320-point widths, accessibility text, and separate XCUI evidence.
Earlier `guarded-filament-*` evidence predates this integration.
These tests prove native guards and typed synthetic shared-contract behavior,
**not actual hardware support or a completed physical calibration**.

### Individual thermal and motion controls (#2598)

- Thermal controls use compact native Hotend/Bed rows: **Current** measurement,
  reported **Target**, new-target input, **Set** and per-heater **Off**. Standard
  phone widths keep the input and both actions together; insufficient width or
  accessibility text stacks the editor without discarding its draft. The same
  rows fit the iPad thermal column. Native fields and actions retain a 44-point
  minimum and system Dynamic Type. Range guidance is in the field's VoiceOver
  hint and specific validation errors, not repeated permanent range paragraphs.
  Missing limits remain an explicit actionable state.
- Presets remain PLA **200/60**, PETG **240/80**, ABS **240/100** and
  Cool Down **0/0** °C. Presets require proven hotend support; unsupported or
  explicitly bed-less hardware omits the bed, including during Cool Down.
- Separate target editors send only the chosen heater. Zero explicitly turns
  that heater off; blank is not zero. Actual temperature and reported setpoint
  are separate labels; missing/nonfinite measurements read **Unknown**.
- Targets must be finite, nonnegative **whole degrees Celsius**, and may not exceed a known configured
  `maxHotendTemp`/`maxBedTemp` from the typed details contract. Missing maxima
  remain unknown, not fabricated limits: **positive heating is blocked** when
  the heater's maximum is missing, zero/negative, loading or failed to load.
  A supported zero-off command remains available under the existing online,
  authority and idle gates. Presets dispatch neither heater unless every
  included positive target is within its reported maximum. Capability
  loading also reads these optional hardware details; failed reads add no proof.
  Read results survive normal online/state changes (including initial unknown
  to idle). Deactivation or authority changes fence both successful and failed
  late reads. Missing/failed limits are explained in the shared group with a
  **Retry heater limits** read-only action. Missing fields can also be retried;
  retry never replays a target. Reopening can retry missing hardware with cached capabilities.
- Absolute coordinates are signed millimetres, with blank axes omitted and
  zero preserved, and accept **at most three decimal places**. **Custom feedrate
  input is disabled** because the shared contract provides no authoritative
  feedrate maximum. Both the editor and VM reject any custom value, including
  otherwise reasonable rates and extreme integers. An omitted custom rate
  selects and explicitly sends the existing relative-jog rate: **3000 mm/min**
  for XY-only moves, **600 mm/min** for any move including Z (even Z=0).
  These are the established native axis-specific rates, not a newly invented
  custom range or an unbounded server default. No mm/s conversion or relative-move fallback
  occurs. Build-volume dimensions are not firmware travel limits or proof of
  a zero origin. Position and homing labels preserve unknown telemetry.
- Precision is checked in both the editor and command owner before dispatch:
  shared Moonraker/FlashForge temperature formatting emits whole degrees, and
  Moonraker movement formatting emits at most three decimal millimetres.
  Excess precision is rejected with explicit inline guidance and native
  VoiceOver hints, not silently rounded. The original valid value is sent
  unchanged, including signed coordinates and zero; no firmware tolerance is
  invented. Decimal validation also rejects tiny entered fractions that would
  otherwise disappear during numeric parsing.
- Relative jog retains **0.1/1/10/100 mm**, XY **3000 mm/min**, Z **600 mm/min**.
  Unknown/false specific movement flags hide movement controls. The #2597
  handoff currently proves neither relative nor absolute motion on production
  backends; synthetic UI fixtures are not capability evidence.
- Native input editors and action buttons have measured native hit bounds
  of at least **44 × 44 points**, not just taller outer containers. The numeric
  editor retains standard UIKit text entry, signed/decimal input and Done.
  Axis/step choices stack at accessibility text sizes. Hosted tests measure
  every new control's bounds, enabled state and hit testing (without depending
  on the simulator's global accessibility-service activation), and retain
  full-content phone, regular and narrow-split
  screenshots independently on the approved iPhone and iPad hosts.
- Motor release warns about loss of holding force, gravity, re-homing, and
  heaters remaining on. `success:false` is an error. `success:true` means the
  request was accepted, not that motors are physically released.
- The persistent command owner uses invocation UUIDs and keeps single-flight
  until the request returns even when matching telemetry arrives first.
  Other-axis, measured-temperature and unrelated setpoint noise do not
  acknowledge an individual target or absolute move.
- Only a matching **post-dispatch** update permits “Matching telemetry received.”
  If cached heater setpoints or absolute coordinates already match, successful
  HTTP acceptance ends waiting with an explicit notice that fresh physical
  completion is **not confirmed**. This applies equally to repeated presets,
  already-zero Cool Down, individual heaters and already-matching destinations.
  A fresh update arriving before the HTTP response retains telemetry wording,
  but never releases the response's single-flight slot early.
- Home All/XY/Z on axes already reported homed completes local waiting after
  successful request acceptance, explicitly **without confirming fresh physical
  completion**. Otherwise homing waits for a fresh requested-axis report.
  Cached homing or unrelated noise never releases an outstanding HTTP request;
  a server rejection still wins over telemetry received before its response.
- The access-lifecycle modifier belongs on the **detail host**, not a pager
  child. It fences server generation, signed-in user, per-server preference,
  readiness and Queue.Start permission. Leaving the detail or losing authority
  invalidates observation and fences late results. Benign printer-state churn does not
  cancel work. Going offline or entering an unsafe state blocks new actions but
  preserves the in-flight response and single-flight slot: a rejection remains
  visible, while acceptance is reported with an unknown physical outcome even
  if the printer reconnects first. With no outstanding response, unsafe state
  ends telemetry waiting with an uncertainty warning.
  **Stop waiting**, caller cancellation, access revocation and deactivation
  never cancel a dispatched transport task or free its unresolved response's
  single-flight slot. Routine actions remain locked even if an owner is
  recreated, reactivated or permission is restored. Matching telemetry cannot release this
  lock early. The eventual response releases its own slot: acceptance after
  stopping observation reports an unknown physical outcome, while rejection
  remains visible when authority is unchanged. After a lifecycle change,
  neither a stale success nor failure becomes a new lifecycle's result; a
  notice instead directs the operator to check the original printer.
  Cancellation proven to precede dispatch releases safely without sending
  anything. After an HTTP response has already returned, Stop waiting may end
  telemetry observation, but cannot undo printer execution. Nothing is replayed.
  Disabled editors expose the preference/permission reason in the
  shared group for sighted and VoiceOver users. New failures clear stale
  success/acceptance notices. Offline controls remain hidden, with the
  explanation supplied by the detail host.
- Emergency Stop remains shell-owned above both tabs, with its own confirmation
  and no dependency on pending setup commands. The shared owner/composition
  seam remains available for #2599; no filament or Z-offset transport is added.

**Owner-lifetime contract:** a MainActor registry in
`PrinterControlsViewModel.swift` owns invocation-token leases keyed by immutable
**registered server UUID + printer UUID**, never by a service object's identity,
the current user, or a transient service generation. The existing
`ServiceContainer.printerControlsComposition` exposes an immutable MainActor
bundle of the **actual composed** registered UUID, generation, transition
revision and exact printer service. It is unavailable during reconciliation or
when eager registry selection differs from the composed server, including
pending demo transitions. The private composition ID, target and worker handle
are observed so read-only availability updates on settlement and restoration
even without a generation change; service initialization and switch ordering
are unchanged.

The detail host captures that bundle alongside its detail-data service after
the existing settlement barrier, checks cancellation/generation, and retains
it for controls-owner construction. A registry change is never used to relabel
an earlier captured service. A captured composition identity change retries
owner creation when a child page mounted before settlement. The retry uses
the same current-composition/access guards and retains any existing owner;
nil or stale contexts do not create owners or refetch capabilities. These
tasks participate in the detail host's existing disappearance cleanup.
The standalone owner also receives the whole
bundle. `PrinterControlsAccessLifecycle` only checks the immutable binding
against current registry/composition and existing user/permission gates; it
never assigns identity. A bare-service model remains unable to dispatch even
if later lifecycle code supplies a UUID. Tests and previews construct explicit
synthetic bundles; production has no permissive missing/mock-context default.

The transition revision additionally fences same-generation service
replacement (`switchToReal`). Neither revision nor generation enters the shared
lease key, so returning to the same registered server cannot bypass an earlier
unresolved request. Delayed-disconnect and delayed-configuration tests exercise
the real container and mock HTTP transport, not separately invented identity
fixtures.

All thermal/motion owners acquire the same lease in the existing command
pipeline. It survives detail dismissal, replacement view models, and service
reconstruction until the original response settles. Different servers/printers
remain independent. Every terminal path, including failed validation and proven
pre-dispatch cancellation, releases only its matching invocation token. Old
telemetry cancellation cannot release a replacement's lease. An outstanding call
retains its cleanup owner, but the registry stores only UUIDs and observes views
weakly, so settled owners are not leaked.

A replacement explains the shared lock and disables preset, home, jog and other routine inputs; it does
not offer Stop waiting for another owner's request. Shared-state observation
updates the replacement UI when the lease is released. Local post-response
telemetry waiting remains separate from transport ownership. This is a
**process-local** guarantee, not cross-device/server-side serialization or proof
of physical completion; Emergency Stop remains independent.

---

## 1. Visual Hierarchy

The command section is conditionally rendered: offline or preference-disabled
detail pages show an explanation instead. The following original subgroup
specification governs controls, not destination discoverability. Overview now
places identity, paired measured/target temperatures and material/current job
first. The compact labeled Emergency Stop is above both pages, independent of
routine actions beside Current Job, and retains confirmation and a 44-point floor.

```
PrinterDetailView (existing)
├── Status header (existing)
├── Camera preview (existing)
├── Controls Section ← NEW (this spec)
│   ├── Section header  "Controls"  .title3 .semibold  pfTextPrimary
│   ├── Mid-print lockout banner (only when state == .printing | .paused)
│   ├── PreheatSubgroup
│   ├── Divider (Color.pfBorder, 1pt)
│   ├── HomeSubgroup
│   ├── Divider (Color.pfBorder, 1pt)
│   └── JogSubgroup
├── Predictive insights (existing)
└── Auto dispatch (existing)
```

**Spacing tokens (reuse existing iOS patterns):**

| Slot | Value | Notes |
| --- | --- | --- |
| Section outer padding | 16pt horizontal, 12pt vertical | Matches existing `PrinterDetailView` cards |
| Inter-subgroup gap | 16pt | `VStack(spacing: 16)` |
| Subgroup header → controls | 8pt | |
| Inter-control gap (within a subgroup) | 8pt | Matches `ActionButtonStyle` stack convention |
| Divider thickness | 1pt | `Color.pfBorder` |

**Typography (existing tokens):**

| Element | Token |
| --- | --- |
| Section header "Controls" | `.title3.weight(.semibold)` `.pfTextPrimary` |
| Subgroup label (Preheat / Home / Jog) | `.headline` `.pfTextPrimary` |
| Helper text (e.g., "Disabled while printing") | `.footnote` `.pfTextSecondary` |
| Button label | `.subheadline.weight(.medium)` |
| Inline values (temps, step) | `.subheadline.monospacedDigit()` |

**Color tokens (from `Color+Pf` in `Theme/ThemeColors.swift`):**

| Purpose | Token |
| --- | --- |
| Subgroup container background | `Color.pfCard` |
| Container border | `Color.pfBorder` |
| Primary CTA fill | `Color.pfButtonPrimary` |
| Primary CTA text | `Color.pfButtonPrimaryText` |
| Secondary action fill | `Color.pfBackgroundTertiary` |
| Secondary action text | `Color.pfTextPrimary` |
| Pending tint | `Color.pfAssigned` (teal) |
| Error banner fill | `Color.pfError.opacity(0.12)` |
| Error banner stroke / icon / text | `Color.pfError` |
| Disabled overlay | `Color.pfTextTertiary.opacity(0.30)` plus pattern (see §2.4) |
| Hot bed/hotend cue (Cool Down) | `Color.pfSecondaryAccent` |

**Iconography (SF Symbols only — no MDI on iOS):**

| Action | SF Symbol |
| --- | --- |
| Preheat / Cool Down | Text labels and temperature pairs (compact thermal row) |
| Home All | `house.fill` |
| Home XY | `move.3d` (fallback `arrow.up.left.and.arrow.down.right`) |
| Home Z | `arrow.up.and.down` |
| Jog + | `plus.circle.fill` |
| Jog − | `minus.circle.fill` |
| Pending | `progressView` (SwiftUI built-in spinner) |
| Error | `exclamationmark.triangle.fill` |
| Capability missing | (no icon — control is hidden, not flagged) |

---

## 2. Component Anatomy

### 2.1 Phone (single column)

```
┌─────────────────────────────────────┐
│ Controls                            │ ← .title3.semibold
├─────────────────────────────────────┤
│ ⓘ Controls disabled while printing. │ ← lockout banner (only when state==printing|paused)
├─────────────────────────────────────┤
│ Preheat                             │
│ [PLA] [PETG] [ABS] [Cool]            │  ← temperature pair under each label
│ Hotend        Target: 215 °C         │
│ Current:192° [New °C] [Set] [Off]    │
│ Bed           Target: 60 °C          │
│ Current:37°  [New °C] [Set] [Off]    │
├─────────────────────────────────────┤
│ Home                                │
│ ┌─────────────────────────────────┐ │
│ │ 🏠 Home All                     │ │  ← full-width prominent
│ └─────────────────────────────────┘ │
│ ┌──────────────┐  ┌──────────────┐ │
│ │ ⤢ Home XY    │  │ ↕ Home Z     │ │  ← 2-column standard
│ └──────────────┘  └──────────────┘ │
├─────────────────────────────────────┤
│ Jog                                 │
│ Axis  [ X ][ Y ][ Z ]               │  ← segmented Picker
│ Step  [0.1][ 1 ][10 ][100] mm       │  ← segmented Picker
│ ┌──────────────┐  ┌──────────────┐ │
│ │     −        │  │      +       │ │  ← 60pt height, side-by-side
│ └──────────────┘  └──────────────┘ │
└─────────────────────────────────────┘
```

### 2.2 iPad (≥ regular width — `horizontalSizeClass == .regular`)

Two columns:

```
┌─────────────────────────────────┬─────────────────────────────────┐
│ Preheat                         │ Home                            │
│ ┌──────┬──────┬──────┬────────┐ │ ┌─────────────────────────────┐ │
│ │ PLA  │ PETG │ ABS  │ Cool   │ │ │   🏠 Home All               │ │
│ └──────┴──────┴──────┴────────┘ │ └─────────────────────────────┘ │
│  (4-up row, all visible)        │ ┌──────────────┬──────────────┐ │
│                                 │ │ Home XY      │ Home Z       │ │
│                                 │ └──────────────┴──────────────┘ │
├─────────────────────────────────┴─────────────────────────────────┤
│ Jog (full width)                                                  │
│ Axis [ X ][ Y ][ Z ]    Step [0.1][1][10][100] mm                 │
│ ┌────────────────────────┬────────────────────────┐               │
│ │           −            │           +            │               │
│ └────────────────────────┴────────────────────────┘               │
└───────────────────────────────────────────────────────────────────┘
```

Use `ViewThatFits` or `horizontalSizeClass` to switch layouts. No new breakpoints introduced.

### 2.3 Subgroup specifications

#### Preheat

- Four buttons, fixed order: **PLA, PETG, ABS, Cool Down**.
- Each button shows: icon, material label (`.subheadline.weight(.medium)`), temperatures `H°/B°` (`.caption.monospacedDigit()`).
- Cool Down uses `pfSecondaryAccent` tint for icon + label to differentiate from heat actions.
- Tap → calls `PrinterService.setTemperatures(printerId:hotend:bed:)` with the locked preset values.
- Presets use a compact four-button row on standard phone and iPad text sizes,
  two columns at XX Large/XXX Large, and one column at accessibility sizes.
  Labels and temperature pairs remain visible; the compact **Cool** label keeps
  the full **Cool down** VoiceOver name. Hit targets remain at least 44pt.
- **No custom temp input. No long-press. No swipe.**

#### Home

- Three buttons in fixed order: **Home All, Home XY, Home Z**.
- Home All is `.prominent` (50pt) and full-width — it is the primary action.
- Home XY and Home Z are `.standard` (44pt), 2-up row.
- Tap → `PrinterService.home(printerId:axes:)` with `["X","Y","Z"]`, `["X","Y"]`, or `["Z"]`.

#### Jog

- Axis picker: SwiftUI `Picker(.segmented)` with X / Y / Z. Default selection: `X`.
- Step picker: segmented picker with `0.1`, `1`, `10`, `100` (display label `mm` outside the picker). Default: `1`.
- `−` and `+` buttons: 60pt height (taller than `.prominent`) because they are the most-tapped controls and benefit from generous targets. Side-by-side, equal width, 8pt gap.
- Tap `+` → `move(printerId, axis: selectedAxis, distanceMm: +selectedStep, feedrateMmMin: feedrate)`.
- Tap `−` → same with negated distance.
- Feedrate is selected by axis: 3000 for X/Y, 600 for Z. Caller-side constants — never shown in UI.

### 2.4 State variants per control

Every button supports five states. Visual treatment:

| State | Visual | Interaction |
| --- | --- | --- |
| **Default** | Full-color fill, label and icon at full opacity | Enabled, accepts taps |
| **Disabled (mid-print)** | Greyscale fill (`pfBackgroundTertiary`), label at 50% opacity, **diagonal stripe pattern** at 8% opacity overlay (color-blind cue per #15) | Not tappable; tap is swallowed silently. Lockout banner explains why. |
| **Capability missing** | **Control is removed from the layout entirely.** | n/a — graceful absence, no greyed slot, no tooltip. Surrounding controls reflow. |
| **Pending** | Label hidden, `ProgressView()` (small) centered. Button stays at full size. Tint: `pfAssigned`. Border thickens to 1.5pt. | Not tappable. All sibling buttons in the subgroup also disable for the duration to prevent burst-spam. |
| **Error** | Reverts to default appearance, but a 1.5pt `pfError` border is applied for 4 seconds, plus an inline banner appears below the subgroup. | Tappable (retry by tapping the same control again, or tap "Retry" in the banner). |

The **diagonal stripe pattern** on disabled state is the color-blind cue called out by #15. Implement as a subtle `LinearGradient` or `Canvas` overlay with 8% white-on-charcoal stripes at 45°. Greyscale alone is not enough — printing red/green colorblind users can mistake greyed for active.

---

## 3. Interaction Model

### 3.1 Single-flight queue (per subgroup)

Each subgroup has its own **single-flight in-flight slot**. While one command from a subgroup is pending, all other buttons in *that subgroup* are disabled. Other subgroups remain interactive. This prevents "Preheat PLA + Preheat ABS" stacked commands without freezing the whole panel.

Example: tapping **Preheat PLA** disables PETG/ABS/Cool Down until the command resolves; Home and Jog remain live.

**Why per-subgroup, not global?** Operators commonly preheat while jogging the bed for tramming. Global locking would feel slower than the printer.

### 3.2 Lifecycle (per command)

```
   tap
    │
    ▼
[ debounce 250ms ]   ← swallow accidental double-taps
    │
    ▼
[ optimistic? NO — UI does not show the new temp/position ]
    │
    ▼
[ button → Pending state, sibling buttons in subgroup disabled ]
    │
    ▼
[ POST /api/printers/{id}/{temps|home|move} ]
    │
    ├── 4xx/5xx ────────► [ Error state + banner + auto-clear pending ]
    ├── network failure ─► [ Error state + banner ]
    └── 200 OK
         │
         ▼
   [ wait for printerupdated SignalR event matching printerId ]
         │
         ├── event arrives within 5s ────► [ Pending → Default, banner clears ]
         └── timeout (5s, no event) ─────► [ Pending → Default with subtle toast
                                            "Sent. Awaiting printer." — not an error,
                                            because the HTTP call succeeded ]
```

**Debounce window:** 250ms (single trailing-edge debounce). Below 250ms feels sticky on iOS 17 button presses; above 400ms users start re-tapping.

**Pending → Default transition:** crossfade 150ms.

**Pending timeout:** 5 seconds. After 5s with no `printerupdated`, the button returns to Default and a non-blocking toast says "Sent. Awaiting printer." This is **not** an error — the API accepted the command, the printer just hasn't echoed state yet.

### 3.3 Error banner

Position: **directly below the affected subgroup**, full width, slides down 200ms. Anchored to the subgroup so the operator always sees which command failed.

```
┌─────────────────────────────────────┐
│ Home                                │
│ [Home All] [Home XY] [Home Z]       │
│ ┌───────────────────────────────┐   │
│ │ ⚠ Home Z failed: printer busy │ ← .pfError.opacity(0.12) fill,
│ │   [ Retry ]              [ × ]│    1pt .pfError stroke
│ └───────────────────────────────┘   │
└─────────────────────────────────────┘
```

- Banner shows the user-friendly server error (truncated to 80 chars; full text via VoiceOver).
- `Retry` re-issues the same command with the same payload.
- `×` dismisses the banner without retrying.
- Banner auto-dismisses after **8 seconds** if untouched.
- Only **one banner per subgroup** at a time; a new error replaces the previous.

### 3.4 Mid-print lockout

When `printer.state` is `.printing` or `.paused`:

- A single banner appears at the top of the section (above Preheat):
  ```
  ⓘ Controls disabled while printing. Pause and stop the job to regain control.
  ```
  Tone: `.pfWarning.opacity(0.10)` fill, `.pfWarning` icon, `.pfTextPrimary` body.
- All buttons enter **Disabled** visual state (greyscale + stripe pattern).
- Taps are absorbed silently — no toast spam.
- VoiceOver announces "Controls locked" once on focus entry.

The mid-print state is read from the same `printer.state` already wired into `PrinterDetailView`.

### 3.5 Capability gating

Capability is provided per printer (e.g., FlashForge omits `bedTemp`). When a capability is missing, the control is **removed from the layout**, not disabled. Implementation:

```swift
if printer.capabilities.contains(.bedTemp) {
    PreheatButton(.pla, hotend: 200, bed: 60)
}
```

If the entire **Preheat** subgroup loses all capabilities (no hotend, no bed), the Preheat header and its container are also hidden — surrounding subgroups reflow. The same logic applies to Home and Jog.

If all three subgroups are empty, the whole Controls section hides (same as offline). This is the only case where a printer is online but Controls is invisible — extremely rare and acceptable.

---

## 4. Accessibility

All controls must satisfy:

- **Touch target ≥ 44×44pt.** Already enforced by `ActionButtonStyle.standard` / `.prominent`. Jog `±` use 60pt.
- **Dynamic Type.** All labels use system text styles (`.subheadline`, `.caption`, etc.). Accessibility sizes collapse Preheat to one column and stack each heater's reading, target editor and Set/Off row. `ViewThatFits` also stacks heater editors when the inline row cannot fit.
- **VoiceOver labels and hints** on every control.
- **Color contrast ≥ 4.5:1** for text, ≥ 3:1 for icon-only. The dark theme `pfButtonPrimary` (#047857) on `pfButtonPrimaryText` (#fff) measures 4.6:1 — passes.
- **Reduce Motion** honored: pending crossfade and banner slide become instant when `accessibilityReduceMotion == true`.

### 4.1 VoiceOver script per control

| Control | Label | Hint | Traits | State announcement |
| --- | --- | --- | --- | --- |
| Preheat PLA | "Preheat for PLA" | "Sets hotend to 200 degrees, bed to 60 degrees." | `.button` | "Pending" / "Failed: <reason>. Double-tap to retry." |
| Preheat PETG | "Preheat for PETG" | "Sets hotend to 240 degrees, bed to 80 degrees." | `.button` | (same pattern) |
| Preheat ABS | "Preheat for ABS" | "Sets hotend to 240 degrees, bed to 100 degrees." | `.button` | (same pattern) |
| Cool Down | "Cool down" | "Sets hotend and bed to 0 degrees." | `.button` | (same pattern) |
| Home All | "Home all axes" | "Homes X, Y, and Z." | `.button` | (same pattern) |
| Home XY | "Home X and Y" | "Homes X and Y axes only." | `.button` | (same pattern) |
| Home Z | "Home Z" | "Homes Z axis only." | `.button` | (same pattern) |
| Axis picker | "Jog axis" | "Choose X, Y, or Z axis to move." | (segmented `Picker` defaults) | Reads selected value |
| Step picker | "Jog step distance" | "Choose how many millimeters each tap moves." | (segmented `Picker` defaults) | Reads selected value with "millimeters" suffix |
| `+` button | "Jog forward" | "Moves <axis> positive <step> millimeters." (label is dynamic) | `.button` | (same pattern) |
| `−` button | "Jog backward" | "Moves <axis> negative <step> millimeters." | `.button` | (same pattern) |
| Lockout banner | "Controls locked while printing" | "Pause and stop the job to regain control." | `.staticText`, `.updatesFrequently` removed | Read once on focus |
| Error banner | "Error: <full server message>" | "Double-tap Retry to send the command again." | `.staticText` + adjacent `.button` for Retry | Read on appearance |

**Disabled controls** keep their label, append "disabled" trait, and the hint changes to "Disabled while printing."

**Hidden controls** (capability missing) are not in the accessibility tree — they don't read at all.

### 4.2 Focus order

VoiceOver swipe order: section header → lockout banner (if any) → Preheat header → 4 preheat buttons → Home header → 3 home buttons → Jog header → axis picker → step picker → `−` → `+` → error banner (if any).

---

## 5. Edge Cases

| Case | Behavior |
| --- | --- |
| Printer goes offline mid-pending | Pending button reverts to Default. Toast: "Connection lost." Section hides on next render once `isOnline == false`. |
| Printer transitions idle → printing while user is in Jog | All Jog controls flip to Disabled with stripe overlay; lockout banner slides in. Pending Jog command (if any) shows error "Job started — control released." |
| `printerupdated` event arrives but temps don't match request (printer rejected) | Treat as silent success — the operator may have changed presets manually; we don't fight the printer's reality. No banner. |
| Two operators control same printer | Server is single source of truth via `printerupdated`. UI only reflects events; no client-side merge logic. |
| User taps `+` 10 times quickly | First tap → Pending; taps 2–10 are debounced/dropped. After Pending clears, taps resume. **No queueing of jog deltas in v1.** |
| Capability list empty | Subgroup hidden. If all three subgroups empty, whole section hidden. |
| Section opens while command still pending from a previous detail-view visit | Pending state restored from in-memory `ControlsViewModel`. If app was backgrounded > 60s, pending is cleared (assume timed out). |
| Network 401 / token expired | Standard app-wide auth interceptor handles this; banner shows "Sign in again" with deep link to settings. |

---

## 6. SwiftUI Skeleton (for Hudson)

This is structure only — no business logic. Use existing services and view models.

```swift
struct PrinterControlsSection: View {
    @ObservedObject var vm: ControlsViewModel
    let printer: Printer

    var body: some View {
        guard printer.isOnline else { return AnyView(EmptyView()) }
        return AnyView(
            VStack(alignment: .leading, spacing: 16) {
                Text("Controls")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(Color.pfTextPrimary)

                if printer.isLocked {
                    LockoutBanner()
                }

                PreheatSubgroup(vm: vm, capabilities: printer.capabilities)
                Divider().background(Color.pfBorder)
                HomeSubgroup(vm: vm, capabilities: printer.capabilities)
                Divider().background(Color.pfBorder)
                JogSubgroup(vm: vm, capabilities: printer.capabilities)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .background(Color.pfCard)
        )
    }
}

struct PreheatSubgroup: View {
    @ObservedObject var vm: ControlsViewModel
    let capabilities: PrinterCapabilities

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Preheat").font(.headline).foregroundStyle(Color.pfTextPrimary)
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 8),
                                 GridItem(.flexible(), spacing: 8)],
                      spacing: 8) {
                if capabilities.contains(.hotend) || capabilities.contains(.bed) {
                    PreheatButton(preset: .pla, vm: vm)
                    PreheatButton(preset: .petg, vm: vm)
                    PreheatButton(preset: .abs, vm: vm)
                    PreheatButton(preset: .coolDown, vm: vm)
                }
            }
            if let err = vm.preheatError { ErrorBanner(error: err, onRetry: vm.retryPreheat) }
        }
    }
}

struct JogSubgroup: View {
    @ObservedObject var vm: ControlsViewModel
    let capabilities: PrinterCapabilities

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Jog").font(.headline).foregroundStyle(Color.pfTextPrimary)
            Picker("Jog axis", selection: $vm.axis) {
                ForEach(JogAxis.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)

            Picker("Jog step distance", selection: $vm.step) {
                ForEach(JogStep.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)

            HStack(spacing: 8) {
                JogButton(direction: .negative, vm: vm)
                JogButton(direction: .positive, vm: vm)
            }
            .frame(minHeight: 60)

            if let err = vm.jogError { ErrorBanner(error: err, onRetry: vm.retryJog) }
        }
    }
}
```

**Helper enums** (Hudson decides exact ownership — model layer or view-local):

```swift
enum PreheatPreset { case pla, petg, abs, coolDown
    var hotend: Double { ... }   // 200, 240, 240, 0
    var bed: Double { ... }      // 60, 80, 100, 0
    var label: String { ... }
}
enum JogAxis: String, CaseIterable, Identifiable { case x, y, z; var id: String { rawValue } }
enum JogStep: Double, CaseIterable, Identifiable { case p1 = 0.1, one = 1, ten = 10, hundred = 100; var id: Double { rawValue } }
```

---

## 7. Design tokens — quick reference

| Token | Value (dark) | Used for |
| --- | --- | --- |
| `pfCard` | `#0f172a` | Section + subgroup container |
| `pfBackgroundTertiary` | `#111827` | Disabled fill, secondary button fill |
| `pfBorder` | `#243145` | Divider, container border |
| `pfButtonPrimary` | `#047857` | Home All, primary CTAs |
| `pfButtonPrimaryText` | `#ffffff` | Primary CTA text |
| `pfTextPrimary` | `#e5e7eb` | Labels |
| `pfTextSecondary` | `#9ca3af` | Helper text, temp values |
| `pfTextTertiary` | `#6b7280` | Disabled text base |
| `pfAssigned` | `#22d3ee` | Pending tint |
| `pfError` | `#dc2626` | Error border, banner stroke |
| `pfWarning` | `#d97706` | Lockout banner |
| `pfSecondaryAccent` | `#1d4ed8` | Cool Down accent |

All listed tokens already exist in `mobile/PrintFarmer/Theme/ThemeColors.swift` — **no new tokens introduced**.

---

## 8. Open questions for follow-up issues

These are out of scope for v1 but worth filing now:

- Custom temperature input (deferred — see #283 "Out of Scope").
- Hold-to-jog (long-press auto-repeat) — would need feedrate UX and runaway protection.
- Macro buttons (e.g., "Tram bed", "Belt test") — separate epic.
- iPad-specific large-jog gesture controls — separate epic.

---

**Implementation handoff:** Hudson can build #284 (Preheat), #285 (Home), #286 (Jog) directly from §2, §3, §4, and §6. The `ControlsViewModel` shape is implied but Hudson owns its API.

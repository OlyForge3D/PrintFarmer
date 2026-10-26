## Reduced pixel-baseline policy (#3526)

Keep pixels for stable, reachable shared visual treatments that cannot be
protected adequately by behavioral or geometric assertions. Do not add a
golden for every backend, wording change, job state or evolving whole screen.
A new golden needs a documented production caller, unique visual failure it
detects, stability rationale, and why existing focused assertions are not enough.
Test fixture reachability is not production reachability.

The complete strict-image inventory at the start of #3526 was **8 cases /
16 PNGs** (each case had one iPhone and one iPad reference). It is now **1 case /
2 PNGs**, an **87.5% reduction** in both mandatory comparisons and stored images.
Across both host-family runs that is 16 -> 2 comparison executions. No reference
was re-recorded and no precision was changed.

| Previous case | Reachability / stability | Unique regression value and decision |
| --- | --- | --- |
| `test_snapshot_moonrakerProfile` | Standalone `PrinterControlsSection`, no production mount; evolving setup layout. | **Replace** pixels with `test_instrumentPanel_backendProfilesGateNativeHeaterEditors`: wire-profile gating, named native editors, no incidental dispatch. |
| `test_snapshot_flashForgeProfile` | Same unreachable owner, unmatched-route-port fixture; volatile explanatory copy. | **Replace** with the same production-panel matrix: unsupported hotend/bed remain disabled. |
| `test_snapshot_sdcpProfile` | Same unreachable owner; duplicates the unsupported layout. | **Replace** with the production-panel matrix: SDCP has no advertised motion/heater actions. |
| `test_snapshot_loadingState_capabilitiesNil` | Unreachable owner; transient async state, not a stable visual contract. | **Replace** with `test_instrumentPanel_loadingCapabilitiesCannotDispatchAndResolvesWithoutRemount`: barrier-held capability discovery, disabled editors, in-place enabling, no command replay. |
| `test_snapshot_disabledState_printerStarting` | Unreachable setup banner, not current Control/Tune; volatile copy. | **Replace** with `test_instrumentPanel_activeJobsSelectTuneAndGateTemperaturesByCapability`, covering starting, printing and paused, with/without active-print heat support. |
| `test_snapshot_lockoutBanner_printingState` | Same unreachable banner; redundant disabled-state pixels. | **Replace** with the active-job matrix plus production native Move/Heat/Tune selection and enablement assertions. No compatibility requirement justifies keeping either legacy banner golden. |
| `test_controlSensorRows` | `PrinterSensorControlRows` is mounted by production Tune; actively evolving composition. | **Replace** pixels with named 44pt targets, bounds, accessible names, fan/read-only exclusion, exact chamber target dispatch, and absent actions for unsupported/offline/missing readings. |
| `test_snapshot_printerDetailBorderedDestructiveControls_darkMode` | Shared bordered destructive style; `.eject` is mounted by `PrinterDetailView`'s filament action; stable, self-contained chrome. | **Retain** unchanged: dark theme contrast, destructive tint, icon/title/border and sibling spacing are visual properties. `.cancel` is a synthetic sibling style variant, not a claim that current Cancel uses this component. State/dispatch evidence comes from action tests, not this image. |

`PrinterDetailView` mounts `PrinterInstrumentPanelControls`, not
`PrinterControlsSection` or `PrinterSetupControlsContent`. The legacy ownership,
layout and command tests are retained for compatibility; they do not justify
mandatory pixels or represent the current Control tab. New coverage renders the
actual instrument panel and observes public accessibility IDs, native actions
and geometry, not SwiftUI type strings or view-tree shapes.

`test_instrumentPanel_nativeMoveTargetsFitAndDispatchAcrossAdaptiveLayouts`
checks bounds and 44pt targets at 320pt, 390pt accessibility text, and 1024pt,
then selects 10mm and sends a Z jog with the exact printer, axis, distance and
existing rate. Existing `PrinterDetailPanelsTests` additionally exercises the
real detail host, server identity, Tune sensors/factors and authenticated API
dispatch. `PreheatSubgroupTests`, `PrinterControlsViewModelTests` and
`PrinterRunActionBarTests` retain validation, request locking and action gates.

## Review captures are not goldens

Volatile Control/Tune scenarios attach `review-only-production-
PrinterInstrumentPanelControls-mock-...` images to `.xcresult`. Names identify
the production component, mock backend/state, viewport/type size when varied,
and host family. Sensor captures similarly identify the production row and mock
chamber target. These are component fixtures, **not full printer-detail screens,
live backend/firmware proof, or exact-pixel gates**. Screenshot differences alone
never fail them; focused state/geometry/interaction assertions still can.

Existing card/legacy setup evidence in the same source file is also
attachment-only. Those legacy captures must not be presented as the current
Control screen. For full-screen provenance use the existing
`PrinterDetailPanelsUITests`/`Issue3259ControlIdleUITests` capture methods in
`PrintFarmerUITests/PrinterDetailPanelsUITests.swift`; they navigate the real
detail host with demo/mock data and do not use PNG comparisons.

SnapshotTesting derives the retained reference path from its source file:

```text
mobile/PrintFarmerTests/Views/__Snapshots__/PrinterControlsSectionSnapshotTests/
```

The component fixtures use the same iPhone 13 layout on both test hosts.
iPhone references use the default `.1.png` suffix (1170×2532 pixels at 3×);
iPad references use `.iPad.png` (780×1688 pixels at 2×). UIKit/SwiftUI
rasterization and safe-area behavior differ by host family even when the
snapshot layout and display-scale traits are overridden, so each family needs
its own strict pixel reference. These are component tests, not iPad full-screen
layout or UI tests.

`PrinterSensorsSnapshotTests` is a historical class name, now containing focused
sensor contracts only. It has no reference directory.

## Regenerating

Baselines are SDK-sensitive. Generate them with Xcode 26.6 build 17F113 and the approved
iOS 26.5 (23F77) simulator runtime. CI uses the `macos-26` arm64 runner image and
installs iOS 26.5 when it is not preinstalled. Select the simulator with the
same workflow steps and preference variables used by
`.github/workflows/ios-pr-ci.yml`, then run only the affected snapshot test
class (`PrinterControlsSectionSnapshotTests`).
The shared host helper forces `.light`; the dedicated destructive-button
snapshot selects `.dark` explicitly, so host appearance cannot change the
Controls references.

1. Run the full affected snapshot class and inspect reference/actual/difference
   attachments. Identify which baselines reflect an intentional approved change,
   rather than an unexpected rendering or runtime difference.
2. Temporarily pass `record: true` only to those `assertSnapshot` calls and run
   their selectors against the resolver-selected simulator.
3. Inspect the regenerated PNGs for that device family in the directory above.
   Recording deliberately fails with a reminder to turn recording off.
4. Remove `record: true`.
5. Run the affected golden and contract selectors again; the single golden and
   relevant layout/lifecycle assertions must pass with zero skips before
   committing the PNGs.

Do not accept baselines generated with a different Xcode, runtime, device
resolver preference, display scale, or locale.

Validate both an iPhone and an iPad host when changing these tests. Do not
overwrite the other family's references while recording.

## Historical baseline revisions (superseded by #3526)

The entries below record previous image changes, not the current inventory or
permission to restore retired goldens.

### Essential Controls baseline revision (#2599)

The six Controls goldens were intentionally updated for the owner-selected
Essential concept 1 in [the Controls design contract](../../../docs/design/printer-controls-section.md),
using Xcode 26.6 (17F113), iOS 26.5 (23F77), iPhone 15 at 3x and
iPad Pro 13-inch (M5) at 2x, with the existing locale and `.iPhone13` component
configuration unchanged. The shared destructive-button dark-mode golden was
not changed.

| Cases | Intentional change from the legacy references |
| --- | --- |
| Moonraker | Paired readings, one Heat group with side-by-side targets and Set targets, compact presets/Cool down, grouped Move & home, guarded calibration entry. |
| Starting / printing | The same Essential composition with the retained lockout banner and disabled physical controls, not the old vertical Preheat/Home/Jog cards. |
| FlashForge / SDCP / capabilities loading | Replace the old empty panel with persistent measured/target readings and guarded Move & home / Filament tools, with truthful unavailable-support guidance. |

These are not full-screen iPad layout references. The same class separately
captures the approved 386x612 phone and 1068x650 tablet scroll viewports,
including lower content and accessibility layouts, and asserts control geometry.
The component snapshots retain unsupported states instead of copying the
prototype's illustrative hardware readiness.

## Web-style homing glyph revision (#2629)

The same six Controls goldens now reflect the owner's web UI reference:
Home All at the upper-left of the directional pad, Home XY in its center,
and Home Z between Z+ and Z-. Filled house glyphs replace the text row.
Unsupported controls remain disabled rather than removing Heat fields/presets,
the spatial homing layout, motor release, absolute movement or calibration.
Idle capability explanations live in Details & safety. Backend command,
offline and print-state gates are unchanged.
The independent destructive-button dark-mode reference remains unchanged.

References were regenerated on task-isolated iPhone 17 (3x) and iPad Pro
13-inch M5 (2x) hosts using Xcode 26.6 (17F113), iOS 26.5 (23F77),
the shared approved-runtime resolver and the unchanged `.iPhone13` component
configuration. Full-page identity/selector captures and homing dispatch tests
supplement these component goldens.

The follow-up references remove Heat's trailing caption and place a compact
Go beside the target inputs. Native geometry assertions require matching
temperature, Go, and extrusion-selector heights, including accessibility sizes.

## Absolute-move and Cool-down polish revision (#2643)

The same six Controls goldens now reflect three deliberate changes:

- **Cool down** is a single enlarged snowflake glyph with no text label. It is
  marked `accessibilityHidden` on the inner glyph so the button keeps its own
  accessible name. The PLA/PETG/ABS presets keep their two-line label.
- **Preset button height** derives from type metrics only
  (`max(54, presetFontSize * 2 + 14)`) rather than rendered content. Previously
  the tallest cell decided the row height, so a capability-dependent second line
  (`200/60°` with a bed, `200°` without) shifted the whole Heat card by ~10pt
  between supported and unsupported capability sets. The geometry assertion in
  `test_unsupportedControlsStayDisabledInTheSamePositions` covers this.
- **Numeric field placeholders** render with `pfTextSecondary` via
  `attributedPlaceholder`, so the X/Y/Z axis hints in the absolute-move row read
  clearly instead of the washed-out system placeholder grey.

Absolute move no longer requires all three coordinates — any single axis is a
valid destination — and per-axis current positions render `—` when that axis is
not homed.

References were regenerated on the resolver-selected iPhone 15 (3x) and iPad Pro
13-inch M5 (2x) hosts using Xcode 26.6 (17F113) and iOS 26.5 (23F77), with the
unchanged `.iPhone13` component configuration.

## Approved printer-detail control updates (#3259)

The six iPhone goldens were refreshed from the reviewed CI test captures at
`564d7531885aff0821b1597d5cc1fd4b2ce1ae98`, produced by Xcode 26.6 on iOS
26.5 (23F77), using the iPhone snapshot configuration at 3x. Only the six
changed cases below were updated; the destructive-button golden and all iPad
goldens remain unchanged.

| Cases | Intentional change |
| --- | --- |
| Starting / printing | Keep the lockout visible and physical controls disabled while using the updated control layout and explanatory copy. |
| FlashForge / Moonraker / SDCP | Reflect the current capability-wire profiles while preserving supported and unsupported control states. |
| Capabilities loading | Show heater maxima as unknown until a valid hardware-limits read completes; do not reuse stale fixture limits. |

These are component renderings of explicit test fixtures, not backend or
physical-printer command evidence. Capability assertions and command tests
remain the behavioral evidence; strict image comparison is unchanged.

## Xcode 27.0 toolchain baseline (#3285)

The six Controls snapshots were re-baselined against Xcode 27.0 build 27A266a and
iOS 26.5 (23F77). The references were regenerated with the strict existing
snapshot assertions on the resolver-selected iPhone and iPad hosts, with
appearance pinned to light. The six iPhone references were byte-identical to
their existing goldens; only the six iPad references changed. The dark-mode
destructive-button reference is independent and remains unchanged.

## Xcode 26.6 containment rollback

The active toolchain is again Xcode 26.6 (17F113), iOS 26.5 (23F77).
The six iPad PNGs changed by #3286 are retained pending matching-environment
validation; this rollback does not certify those PNGs under Xcode 26.6.
Do not restore historical images or re-record without comparing actual output
at the exact approved Xcode/runtime, host family, scale, locale and appearance.
Strict assertions, iPhone references and the destructive-button reference stay
unchanged. The selected-toolchain iPad snapshot run remains required evidence.

## Mainsail sensor presentation revision (#3451)

The two sensor fixtures intentionally capitalize sensor-name words and remove
the writable rows' dividers. Full sensor IDs, readings, disabled-state guidance,
and the native 44-point control targets are unchanged.

The iPhone references were updated in `2d4f23dac` without generation metadata.
They were subsequently verified unchanged with recording disabled on a
task-isolated iPhone 15 (3x), Xcode 26.6 (17F113), and iOS 26.5 (23F77).
The full unit run executed 2,469 tests with zero failures; only the opt-in live
HTTPS login diagnostic was skipped. Both sensor snapshots passed with no skips.

The stale iPad sensor references failed strict comparison on the same approved
toolchain/runtime and were regenerated on a task-isolated iPad Pro 13-inch (M5)
host (2x). Reference/actual images were inspected before recording: the changes
are the intended name capitalization and divider removal. The `.iPhone13`
component configuration, light appearance, default simulator locale, fixture
data, and strict assertions remain unchanged. Only the two sensor iPad PNGs
were recorded; neither iPhone nor Controls references were re-recorded.
Both iPad sensor cases then passed with recording disabled and zero skips.

## Status sensor readout without the Temperatures group

The printer detail Status page already shows nozzle and bed in its temperature
strip, so `PrinterSensorsReadout` no longer renders the duplicate Temperatures
group. `test_statusSensorReadout` keeps its fixture (nozzle, bed, chamber, MCU
and part fan) and now renders only the Fans group.

Both `test_statusSensorReadout` references were replaced with the strict
comparison's actual output, after reference and actual images were inspected:
iPhone 15 (3x) and iPad Pro 13-inch (M5) (2x), Xcode 26.6 (17F113), iOS 26.5
(23F77), light appearance, default locale. The only difference is the removed
Temperatures group. `test_controlSensorRows` and every Controls reference were
not re-recorded and pass unchanged on both families.

## Status sensor readout and temperature strip removed

The printer detail Status page no longer shows the sensor/fan readout or the
nozzle/bed temperature strip. Temperatures remain on the Control tab's Heat
section, and writable fans and sensors remain in Tune. `PrinterSensorsReadout`
and `test_statusSensorReadout` were deleted together with both
`test_statusSensorReadout` references (iPhone and iPad). `test_controlSensorRows`
and every Controls reference are unchanged; no PNGs were re-recorded.

## Cool down follows heater-shutdown support (#3460)

Cool down now calls `POST /heaters/off` and is hidden unless the backend
reports `supportsHeaterShutdown`; the remaining PLA/PETG/ABS presets fill the
row. The Moonraker fixture now mirrors the real backend
(`supportsHeaterShutdown` and `supportsTemperatureControlDuringPrint` true), so
`test_snapshot_moonrakerProfile` still shows Cool down and was not re-recorded.

The iPhone references for `test_snapshot_flashForgeProfile`,
`test_snapshot_sdcpProfile` and `test_snapshot_loadingState_capabilitiesNil`
were replaced with the strict comparison's actual output after reference and
actual images were inspected: iPhone 15 (3x), Xcode 26.6 (17F113), iOS 26.5
(23F77), light appearance, default locale. The only difference is the omitted
Cool down button and the three-column preset row. These cases have no iPad
references.

## Tune drops fans; Cool down always shown

Tune no longer renders fan controls, and Cool down is shown for every printer
with hotend control (heater shutdown when supported, otherwise a 0/0 idle
fallback). The lockout banner no longer mentions fan adjustments. The iPhone
references for `test_snapshot_flashForgeProfile`, `test_snapshot_sdcpProfile`,
`test_snapshot_loadingState_capabilitiesNil` (Cool down restored, four-column
preset row), `test_snapshot_disabledState_printerStarting`,
`test_snapshot_lockoutBanner_printingState` (banner copy) and
`PrinterSensorsSnapshotTests/test_controlSensorRows` (fan rows removed, chamber
row only) were replaced with the strict comparison's actual output after
inspection: iPhone 15 (3x), Xcode 26.6 (17F113), iOS 26.5 (23F77), light
appearance, default locale. The iPad references for
`test_snapshot_disabledState_printerStarting`,
`test_snapshot_lockoutBanner_printingState` and `test_controlSensorRows` were
replaced the same way on iPad Pro 13-inch (M5) (iOS 26.5, 23F77). The other
iPad references already matched and were not re-recorded.

## Cool down is idle-only

Cool down is disabled while a job is starting, printing or paused, whatever
`supportsHeaterShutdown` reports, and no longer asks for confirmation.
`test_snapshot_lockoutBanner_printingState` was re-recorded on iPhone 15 (3x)
and iPad Pro 13-inch (M5), Xcode 26.6 (17F113), iOS 26.5 (23F77), light
appearance, default locale, after inspecting both images: the snowflake is
disabled again, and both PNGs are byte-identical to the references before the
mid-print change. The Heat stepper and babystep restyle has no snapshot
coverage; it was checked with scratch renders only.

## Final preset temperatures

PETG and ABS presets changed to 230/80 and 245/100 (nozzle/bed °C), read from
`PreheatPreset.definition`. Their captions changed in
`test_snapshot_disabledState_printerStarting`, `test_snapshot_flashForgeProfile`,
`test_snapshot_loadingState_capabilitiesNil`,
`test_snapshot_lockoutBanner_printingState`, `test_snapshot_moonrakerProfile`
and `test_snapshot_sdcpProfile`. All six were re-recorded on iPhone 15 (3x) and
iPad Pro 13-inch (M5), Xcode 26.6 (17F113), iOS 26.5 (23F77), light
appearance, default locale, after inspecting the images. These snapshots
render the standalone `PrinterSetupControlsContent` (formerly hosted by the
removed Advanced screen), which keeps PLA/PETG/ABS + Cool down; the Control
tab's five-slot row with More has no snapshot coverage and is exercised by
`Issue3259ControlIdleUITests.testHeatPresetRowOffersMoreMenuWhileIdle`.

### Advanced printer controls opt-in removed

The Advanced printer controls screen, its Settings toggle and the onboarding
prompt were removed. No reference image changed: the blocked-editor evidence
test now covers the readiness gate instead of the retired Settings opt-in, and
it attaches screenshots without comparing reference images.

## Native-control compatibility fixture wording (#3512)

Only `test_snapshot_disabledState_printerStarting` and
`test_snapshot_lockoutBanner_printingState` were re-recorded for both families.
These tests mount the legacy standalone `PrinterControlsSection` setup
component, not the current printer-detail Control tab's
`PrinterInstrumentPanelControls`. The banner in these fixtures is not a
banner on the current Control or Tune screen.

Reference, actual and difference images were inspected before recording.
Every changed pixel is confined to the intentional lockout message, which
now describes temperature and Z-offset controls in Tune without the removed
verified-readback requirement. All control positions, readings and disabled
states are unchanged; this is not a toolchain-drift rebaseline.

Generation used task-isolated iPhone 15 (3x, 1170x2532) and iPad Pro 13-inch
(M5) (2x, 780x1688) hosts, Xcode 26.6 (17F113), iOS 26.5 (23F77), light
appearance, default locale and the unchanged `.iPhone13` component
configuration. All other Controls and sensor references, strict comparison
settings and test selectors remain unchanged; recording is disabled.
Both full snapshot classes passed on each host with zero skips.

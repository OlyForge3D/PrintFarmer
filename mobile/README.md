# PrintFarmer

iOS app for managing 3D printer farms.

## About

PrintFarmer is a SwiftUI-based iOS application for monitoring and managing
multiple 3D printers across one or more registered PrintFarmer servers. Features
include printer status monitoring, filament/spool management, job queue viewing,
same-printer and same-priority queue reordering for users with `Queue.Write`,
server switching, and real-time updates via SignalR.

## Tech Stack

- **Language:** Swift
- **UI Framework:** SwiftUI
- **Minimum Target:** iOS 17+
- **Concurrency:** Swift Concurrency (async/await)
- **Architecture:** MVVM with repository pattern
- **Backend:** [PrintFarmer API](https://github.com/OlyForge3D) (ASP.NET Core)

## Requirements

- Xcode 26+
- iOS 17+ deployment target

## Getting Started

1. Clone the repository:
   ```bash
   git clone https://github.com/OlyForge3D/PFarm-Ios.git
   cd PFarm-Ios
   ```

2. Open `PrintFarmer.xcodeproj` in Xcode.

3. Optional for development: set the `PRINTFARMER_API_URL` environment variable
   in your Xcode scheme to seed the initial server. For local PrintFarmer
   development, use `http://localhost:5245`.

4. Build and run on a simulator or device (iOS 17+).

## Testing

The iOS CI matrix selects shipping Login, three-tab shell, Scan, harvest,
printed-parts stock, printer/coverage and cached-Farm suites on both device
families, with extra regular-width Queue navigation on iPad. Printed-parts
coverage includes capability gating, reorder warnings/filtering and adjustment
sheets. Retired Attention/Tasks grouping and Two-modes/promotion screens have
no shipping entry points, so their selectors (including Attention-only Dynamic
Type assertions) are intentionally removed rather than mapped to empty suites.
`scripts/tests/test_run_tests.py` checks every selected class/method against
Swift sources and rejects selected classes without real test methods.

Use **iOS 26.5 (23F77)**, the unchanged-snapshot default supported by
[the original evidence](https://github.com/OlyForge3D/PrintFarmer/issues/2536#issuecomment-5573657441).
Install it in Xcode Settings > Components and create an available iPhone
simulator. The shared resolver requires Python 3, rejects beta/unapproved
builds even in fallbacks, and works without `GITHUB_ENV`.
From the repository root:

```bash
cd mobile
(
  set -euo pipefail
  mkdir -p build
  run_dir="$(mktemp -d "$PWD/build/snapshots.XXXXXX")"
  xcodebuild -version | tee "$run_dir/xcode.log"
  git rev-parse HEAD | tee "$run_dir/commit.log"
  xcrun simctl list runtimes -j > "$run_dir/runtimes.json"
  xcrun simctl list devices available -j > "$run_dir/devices.json"
  simulator_udid="$(../scripts/ci/resolve-ios-simulator.sh --udid 2>"$run_dir/destination.log")" ||
    { cat "$run_dir/destination.log" >&2; exit 1; }
  cat "$run_dir/destination.log"
  python3 scripts/run-tests.py -- test -scheme PrintFarmer \
    -destination "platform=iOS Simulator,id=$simulator_udid" \
    -only-testing:PrintFarmerTests/PrinterControlsSectionSnapshotTests \
    -parallel-testing-enabled NO \
    -resultBundlePath "$run_dir/Snapshots.xcresult" \
    2>&1 | tee "$run_dir/test.log"
)
```

The shared test plan enables XCTest watchdogs. Isolated timeout investigations
use a 60-second allowance without capping longer adjacent tests; see the
[XCUI timeout policy and evidence ledger](docs/xcui-timeout-diagnosis.md).
The shared local/CI runner separately bounds post-test finalization/restart at
120s and disables automatic verbose simulator diagnostics. Preserve the
`.events.jsonl` and `.timing.json` siblings with each result bundle and log;
reported test duration is distinct from collection/teardown overhead.

Use `-only-testing:PrintFarmerTests` for all unit tests, or explicitly select
`PrintFarmerUITests/<Suite>` for XCUI. See [agent testing guidance](AGENTS.md#simulator-testing)
for iPad-host runs; each family needs separate unchanged-reference evidence.
To diagnose environmental drift, compare identical tests/reference blobs under
identical pinned Xcode/runtime/build, family, scale and locale; do not assume
every failure is environmental or conflate XCUI failures with image drift.
Keep snapshot strictness, skip policy and PNGs unchanged. The
[snapshot guide](PrintFarmerTests/Views/__Snapshots__/README.md) is not
authorization to re-record baselines for #2536/#2572.

## Server Configuration

The app supports multiple registered PrintFarmer backend servers. Server
registrations are stored locally in UserDefaults on the device, and each server
keeps its own Keychain-stored credentials.

### Direct Motion Controls

Home All/XY/Z, relative jogs and absolute positioning use ordinary direct
command endpoints. Controls remain gated by authorization, printer readiness,
backend capabilities and movement safety. Absolute positioning allows blank
axes to remain unchanged; current telemetry is used only to validate safety.

A successful response means the backend accepted the command, not that physical
motion completed. The in-flight request disables conflicting actions.
Canceling observation, closing a screen or disconnecting cannot recall a command.
After a timeout or uncertain outcome, inspect the printer before another action.
Commands are never automatically retried or replayed on reconnect.

There are no motion journals, operation IDs, receipt polling, recovery panels
or admission-resubmission flows. Ordinary printer telemetry and safety refreshes
remain available; calibration still requires fresh safety evidence.

### Managing Servers

- On first launch, register a PrintFarmer server before signing in.
- After setup, open the **Account avatar** → **Manage Servers** to add, edit, or delete servers.
- The server editor normalizes URLs and rejects duplicates.
- Use **Check Connection** to verify reachability. The app checks `/health` and
  `/healthz`; network failures are shown in the editor and saved status appears
  in the server list.

### Switching Servers

- On iPhone and iPad, open the **Account avatar** → **Manage Servers**.
- Switching servers rebuilds the app's API, authentication, and SignalR services
  for the newly active server.

### Navigation

The phone is for the farm floor; the web is the full console. iPhone has
exactly three tabs: **Farm · Queue · Filament**. Regular-width iPad presents
those same items in a `NavigationSplitView` sidebar, without Floor/Oversight
sections or a duplicate Fleet destination.

A floating **Scan** button is available above the tab bar (or at the bottom of
the iPad detail column). It opens the existing barcode/QR and NFC flows for
printer and spool tags. Filament retains Add spool and continuous barcode
intake. The toolbar avatar opens **Account**, including **Settings**, **Manage
Servers**, notifications and offline activity.

When the server enables printed-parts inventory, the **Printed parts** toolbar
button in Filament opens stock browsing and quantity adjustment as a secondary
sheet. This does not add a tab or restore the old inventory segment picker.

Analytics, maintenance planning, reporting, locations and history belong on
the web. Retired analytical deep links open Farm; native attention
notifications still parse and apply Farm's **Needs attention** filter.
Server capabilities govern actions/data but do not remove the three tabs.

There is no shell preference or Two-modes upgrade promotion. Existing
`pf_navigation_established_shell*`, layout and promotion defaults are removed
once by `ServerRegistry`, without clearing server registrations or safety
preferences. Farm preserves the exact-owner read-only cached fleet and honest
last-confirmed timestamp when offline.

Farm's authorized offline snapshot is refreshed by the canonical Farm loader,
not by the live list's pull-to-refresh. Its displayed last-confirmed timestamp
is the stale bound; it must not be interpreted as the last live-list refresh.
The snapshot host and live list currently both fetch/subscribe on Farm
appearance. Moving between cached and live content can reset the active Farm
navigation stack. Consolidating that ownership without losing offline safety
is follow-on work in #3235.

### Printer Detail: Overview / Controls

Printer detail opens on **Overview**, with printer identity, paired measured and
target Hotend/Bed temperatures, filament and current work before supporting
information. Missing or offline readings are unavailable, not zero or ready;
missing bed telemetry does not establish whether a heated bed is installed.
At useful iPad widths, camera, queue, maintenance and history occupy a supporting
column. Narrow split views and accessibility text reflow to one reading column.
Navigation remains Farm grid → pushed printer detail.

Both **Overview** and **Controls** remain visible, through the segmented selector
or a swipe, even offline or with setup controls disabled. Controls explains the
restriction and links to the existing per-server Printer Safety settings.
Selection is retained when access changes; visibility does not grant permission
or trigger capability loading while setup controls are unavailable.

A compact labeled **Emergency Stop** stays above the selector on both pages,
with confirmation, its own pending guard, and an offline explanation. It never
requires a timed hold and is not disabled by an unrelated pending command.
Pause/Resume/Cancel/Stop stay with **Current Job** on Overview. Camera/live view,
queue, history, maintenance, Mainsail, auto-dispatch, failure detection,
NFC and spool assignment/Eject utilities remain available under their existing gates.

### Native control transport contract

The typed networking prerequisite for Essential controls is implemented in
`PrinterServiceProtocol` and `PrinterService`, together with safety corrections
to the existing Preheat and Home controls and a visible capability-read retry.
Controls includes absolute movement, motor release, guarded material controls
and an inline calibration review. Availability is limited by the safety
evidence described below. All paths below are relative to `/api/printers/{printerId}`.

Controls follows the owner-selected **Essential concept 1** (#2589/#2593):
paired measured/target readings above one **Heat** group, side-by-side target
inputs, **Set targets**, a compact PLA/PETG/ABS row and **Cool down**.
Blank inputs leave that heater unchanged; zero turns it off. One guarded
request applies entered targets only after every value passes validation.
**Move & home** combines directional XY, independent Z and compact home actions;
absolute movement is disclosed. Phone reads Heat / Move / Filament; iPad places
thermal/material work beside movement. Accessibility sizes stack without
discarding drafts. Runtime safety is real server evidence, never prototype data.
The recovered prototype is the visual target, including its divided temperature
surface, compact bordered controls, ruled motion rows, paired motor/calibration
entries and 1.1:1 iPad columns. The compact assignment shortcut reuses the existing
picker; **Details & safety** retains Clear/NFC, inventory details and safety reads.

| Native method | POST route | Request / response |
| --- | --- | --- |
| `setTemperatures` | `/temps` | Optional `hotend` / `bed` in Celsius; nil omitted, zero means off. Decodes `CommandResult` and throws on rejection. |
| `home`, `homeXY`, `homeZ` | `/home`, `/homexy`, `/homez` | No body. Only All, XY or Z; decodes `CommandResult` and throws on rejection. |
| `move` | `/move` | Single X/Y/Z delta in mm and `f` in mm/min; rejects an invalid axis. |
| `moveTo` | `/moveto` | Optional `x`, `y`, `z`, `f`; zero is a coordinate, nil omits it. Returns `CommandResult`. |
| `extrude` | `/extrude` | Signed `distanceMm` (-100...100, nonzero), `feedrateMmPerMinute` (1...6000); returns `CommandResult`. |
| `disableMotors` | `/disable-motors` | No body; returns `CommandResult`. |
| `loadFilament`, `changeFilament` | `/filament-load`, `/filament-change` | No body; returns `CommandResult`, unrelated to spool assignment. |
| `unloadFilament` | `/filament-unload` | Optional query `toolheadIndex`; detailed overload returns success/message/spoolId/material/residualWeightG. Printer-only overload preserves `CommandResult`. |
| `saveZOffset` | `/z-offset` | Required `offsetMm` (-5...5), `saveToFirmware`; quoted `If-Match` from `reviewedRowVersion`; returns `CommandResult`. |

Callers must check each operation's explicit server capability, Queue.Start
permission, current server/printer identity, user opt-in and physical readiness.
Generic control flags and backend-name fallbacks do not enable commands.
Missing fields or a missing capability endpoint fail closed. This is command
availability, not evidence that hardware is absent. Capabilities are fetched
fresh; the former permanent UUID-only cache is removed. APIClient's registered
server generation fence rejects stale in-flight responses.
Homing visibility and dispatch use independent All/XY/Z evidence, not jogging
support. A failed capability read remains unknown and shows a read-only retry
affordance; it is not cached as permanent unsupported state.
Preheat, including Cool Down, stays hidden and refuses dispatch until hotend
support is confirmed. Cool Down omits an unconfirmed bed just like heating
presets; unknown support never permits a speculative 0/0 command. Missing bed
support is described as unavailable control, not physically absent hardware.
Demo mode advertises no physical or persistence capabilities: its no-op commands
are not backend support evidence.

Control requests are never replayed automatically. Errors (including 409, 412,
428 and uncertain firmware-save 503) propagate through existing APIClient
semantics. A successful HTTP response must still contain a valid command result;
`success: false` is not physical success. Callers of result-returning methods
must inspect it. Neither acceptance nor inventory residual weight proves final
physical telemetry. After an offset save, refresh details for the next reviewed
rowVersion; never fetch a newer revision merely to retry an old confirmation.

`Printer` already exposes optional measured temperatures, targets, XYZ position
and `homedAxes`. `getDetails` now also projects optional `zOffsetMm`,
`lastZOffsetCalibrationAt`, `rowVersion` and nested `capabilities` with optional
catalog/configuration maxima (`maxBuildVolumeX/Y/Z`, `maxHotendTemp`,
`maxBedTemp`, `hasHeatedBed`). These values stay unknown when absent. Build
volume is not live firmware travel bounds or proof of a zero-based origin;
catalog heater maxima are not a material-specific safe extrusion temperature.
The control owner must not use a target as a measurement or invent limits from web defaults. UI feedrates
expressed in mm/s convert to mm/min once **before** calling the service.
Native presets remain PLA 200/60, PETG 240/80 and ABS 240/100.

The shared operation flags are `supportsRelativeMovement`,
`supportsAbsoluteMovement`, `supportsDisableMotors`, `supportsExtrusion`,
`supportsZOffset`, `supportsZOffsetFirmwareSave`, `supportsHoming`,
`supportsHomingXY`, `supportsHomingZ`, `supportsHotendTemperature`,
`supportsBedTemperature`, and `supportsFilamentLoad/Unload/Change`.
Native `supportsMovement` and `supportsTemperatureControl` are compatibility
aliases for relative movement and hotend targets. `supportedAxes` normalizes
the server's lowercase axes to native uppercase, without implying they are homed.

Current implementation evidence is deliberately narrower than legacy flags:

| Backend | Proven shared commands |
| --- | --- |
| Moonraker | All/XY/Z home, hotend/bed targets, bounded extrusion, motor release, database-only Z-offset. |
| OctoPrint | All/XY home, hotend/bed targets, database-only Z-offset; Z-only home requires matching derived/configured backend ports. |
| PrusaLink | All/XY home, database-only Z-offset; Z-only home and heater targets require matching derived/configured backend ports. |
| FlashForge | Database-only Z-offset; heater targets require matching derived/configured ports and an actual temperature-control client. |
| SDCP / unknown | Database-only Z-offset; no inferred physical-command support. |

These flags also require the concrete typed backend clients; permission and
runtime readiness remain separate. Moonraker now discovers absolute-movement
geometry and uses separate mode/move commands; safe dispatch still requires
verified clearance and fresh homing/frame telemetry. Other unsupported routes
remain disabled. Firmware Z-offset persistence is not proven by
`SET_GCODE_OFFSET` / `SAVE_CONFIG` or generic `M851` / `M500` transport.
Physical-filament macros are not enabled without installed per-printer macro
evidence. These prerequisites are recorded in #2597 / #2593; the typed native
methods do not invent support or silently issue substitute commands.

### Physical Material and Calibration Safety

Controls now shows separate **Extrude / Retract** and **Load / Unload / Change
filament** actions. These are physical printer requests, not Assign/Change
spool or Clear assignment. Existing NFC and combined Eject remain unchanged.
Physical requests are printer-level: no MMU lane or tool is selected. A
successful response means the request was accepted; follow printer prompts
and verify completion yourself.

Extrusion offers signed 10/25/50/100 mm and 1/5/10 mm/s, converted once to the
API's mm/min. It becomes available when shared `verifiedSafety` discovery proves
a material-safe minimum and fresh `safetyTelemetry` proves the measured hotend
meets it. Load/Unload/Change require the same guard plus verified individual
operation support. A hot target, preset, assigned spool or catalog maximum cannot
override it. **Refresh safety checks** reads evidence without retrying a command.
Current Moonraker discovery proves installed macros but deliberately leaves the
material-safe minimum Unknown; those hardware exclusions remain visible.

**Review calibration** opens Introduction/Home/Position/Adjust/Review-Save/Done.
With verified support, geometry and clearance, the flow waits for fresh homing,
lifts before lateral positioning, and adjusts by 0.01/0.05/0.1 mm (negative is
closer) within verified bounds. Firmware save uses the explicitly reviewed
printer revision; conflicts and uncertain results never auto-retry or become Done.
No guessed center, zero offset, paper-test height or database-only calibration is
substituted. Current Moonraker clearance is Unknown and firmware persistence is
Unsupported, so its calibration remains disabled with explanations even though
the conditional native path is fully wired. Use the printer's supported procedure.

Cancel calibration stops the workflow, not an already-issued printer command.
Emergency Stop remains separate and confirmed. After interruption or an
uncertain response, inspect the original printer before another action.
See the [guarded controls design and availability matrix](docs/design/printer-controls-section.md#guarded-physical-material-and-calibration-2599).

### Advanced Printer Controls

Advanced printer controls are off by default for every server. To use jog,
preheat, home, z-offset, or disable motors, open **Settings** → **Printer
Safety** and enable **Advanced Printer Controls** for the active server. Once
enabled, jog/preheat/home setup is reachable as the Controls page of printer
detail (see above) — there is no separate "Advanced" screen nested inside
another "Advanced" entry. Enabling the controls on one server does not enable
them on another. Turning the setting off removes command access immediately;
an open Controls page retains selection and explains the restriction. Changing a registered
server's URL also resets the setting to off so an opt-in cannot carry over to a
different endpoint. Misuse may damage a printer or ruin a print.

### Post-Login Connection Check

After sign-in or session restoration, the app checks each enabled mobile backend
feature before opening the main interface. If one or more services are
unavailable, the app names them in an alert and lets you continue with cached
data and any services that remain available. Features disabled by the server's
`operatorFeatures` capability flags are omitted from navigation and views rather
than shown as unavailable placeholders. For up to 30 seconds, promptly completed
canonical checks hand their confirmed-live Attention feed, fleet filament
coverage, and printer list to the first tab activation, avoiding a duplicate
startup fetch and stale-cache banner. Attention's original lightweight readiness
request runs concurrently and solely determines availability; canonical warming
is best-effort and capped at one second. Tabs without a handoff perform their
normal fresh load.

### HTTPS Certificate Trust

Public servers require HTTPS with normal system certificate trust. Cleartext
HTTP remains available only for local-network addresses and names such as
`localhost` and `.local`.

For a private HTTPS server using a self-signed certificate, the app pauses the
first connection and asks you to verify its SHA-256 public-key fingerprint
before sending credentials. Compare the displayed value with one obtained
directly from the server:

```bash
openssl x509 -in cert.pem -pubkey -noout \
  | openssl pkey -pubin -outform der \
  | openssl dgst -sha256
```

The certificate must contain a subject alternative name matching the connected
host; certificates used with an IP address need that address as an IP SAN.
Confirmed pins are device-only. If a certificate key changes, the app blocks
the connection. After independently verifying an intentional replacement, open
**Settings** → **Manage Servers**, edit the server, and choose **Forget Trusted
Certificate** before reconnecting.

### Development URL Seeding

`PRINTFARMER_API_URL` is now a development seed/override for the server registry,
not the only server the app can use. Set it in the Xcode scheme when you want a
simulator or device run to start with a specific backend, such as the local .NET
API:

```bash
PRINTFARMER_API_URL=http://localhost:5245
```

Existing installs that saved a single legacy URL under `pf_server_url` migrate
that URL into the server registry on first launch and make it the initial active
server.

## TestFlight Betas

The iOS beta line is authoritative for TestFlight versions. Use
`ios/v1.0-beta.N` tags for beta releases; the `ios/` namespace prevents mobile
tags from triggering server container publication. The repo-root `VERSION` file
is not used to derive iOS beta marketing versions. Historical unscoped
`v1.0-beta.*` tags remain available, with matching `ios/v1.0-beta.*` aliases at
the same commits; future releases use only the namespaced form.

To cut an on-demand internal beta from GitHub Actions:

```bash
gh workflow run testflight-beta.yml -f environment=internal
```

The workflow creates and pushes the next `ios/v1.0-beta.N` tag from the latest
namespaced beta tag series unless `marketing_version` or `beta_number` inputs
are supplied.

The canonical tag-based method is:

```bash
git tag -a ios/v1.0-beta.<N> -m "PrintFarmer iOS beta ios/v1.0-beta.<N>"
git push origin ios/v1.0-beta.<N>
```

## License

The in-repository mobile client is licensed under the
[GNU Affero General Public License v3.0 only](../LICENSE)
(`AGPL-3.0-only`) beginning with PrintFarmer v0.2.3.

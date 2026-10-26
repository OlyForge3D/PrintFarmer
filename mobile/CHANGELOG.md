# Changelog

All notable changes to PrintFarmer iOS will be documented in this file.

## [Unreleased]

### Removed

- **Advanced Printer Controls opt-in and legacy screen** — Removed the
  unreachable Advanced printer controls screen and its navigation destination,
  the per-server **Settings → Printer Safety → Advanced printer controls**
  toggle, and the onboarding prompt that offered it. The Control tab stays
  visible; its controls are blocked with a reason when the printer is offline,
  lacks control support, or the account lacks Queue.Start. Every existing
  capability, connectivity and permission check still applies. Stored opt-in values from older installs
  are ignored.
- **APNs registration from the v1 mobile client** — Removed remote-notification
  registration callbacks, device-token persistence/upload, the in-app push
  toggle, APNs entitlement/build setting, and unreachable actionable
  `JOB_ATTENTION` category/deep-link handling. SignalR remains available for
  live in-app updates; v1 system notifications are limited to the on-device
  `PendingReady` bed-clear reminder.
- Remove tracked manual-motion receipts, operation polling, journals, SignalR
  listeners and recovery/admission UI. Home, jog and absolute positioning use
  ordinary direct commands with capability, authorization and safety checks.
  Success means command accepted, not physical completion; uncertain outcomes
  require checking the printer and are never automatically replayed (#2713).

### Fixed

- **Printer detail no longer blocks on offline printers** — Opening a printer
  from the farm list renders its last-known details immediately instead of a
  multi-second "Loading printer…" spinner. The canonical printer, status and
  job details then fill in progressively in the background, and a failed
  background refresh keeps the shown content. Camera metadata and snapshot
  fetches are skipped while the printer is offline and resume once it reports
  online. The full-screen spinner appears only when no cached printer is
  available. While only cached details are shown, printer controls, filament
  actions and Start Next Job stay disabled until the server confirms the
  printer, except Emergency Stop. NFC "ready" and guided spool-swap deep links
  opened during that window wait and run once the printer is confirmed,
  including after a failed first load succeeds on Retry. A
  connectivity-only live update during a refresh no longer lets the cached
  printer state override the server's current state.
  A failed refresh shows a compact "Showing last known status" note with
  Retry. Switching servers never leaves the previous server's printer on
  screen, and a printer that comes online during a refresh keeps its live
  status and loads its camera (#3507).

- Stop the offline farm snapshot from silently keeping an older fleet after the
  device clock steps backward or two writes land in the same millisecond. Farm
  snapshot writes now use the same launch-scoped logical order as the feature
  caches, and refused dashboard commits are logged (#3074).
- Stop the Attention and filament-coverage offline caches from silently
  keeping an older snapshot when two refreshes land in the same millisecond or
  the device clock steps backward. Cache writes are now ordered by a
  launch-scoped logical sequence instead of the wall clock, and refused writes
  are logged (#3007).
- Keep full Overview/Controls titles readable at accessibility sizes using
  stacked choices when needed; bound scrolling identity metadata so the page
  retains usable space beneath the shared header.
- Use a compact Go action beside the heater targets, stacking for accessibility
  text. Match extrusion length/rate selector heights to the temperature inputs.
- Remove Heat's redundant header caption; current/target hotend and bed
  temperatures remain in the reading strip above it.
- Keep unsupported Controls actions visible and disabled instead of hiding
  them or rearranging Heat, Move & home, and Filament tools.
- Remove the obsolete Details & safety entry points from material and Z-offset
  calibration controls while retaining their operational controls and active
  workflow warnings.
- Replace the Controls page's Home All/XY/Z text row with accessible house
  glyphs in the web UI movement arrangement, including centered XY homing and
  Z homing between Z+ and Z-. Preserve independent capability gating.
- Keep printer identity above both Overview and Controls instead of hiding it
  when Controls is selected. Restore Essential's compact leading-aligned iPad
  selector and native minimum hit height; retain maintenance, failure-detection
  and homing information without the old bordered identity card.

### Added

- **Speed and Flow steppers in Tune** — Control → Tune shows Speed % and
  Flow % steppers (±5%) above babystep while a job is printing or paused
  (#3476). Rows appear only when the backend reports
  `supportsSpeedFactorControl` / `supportsFlowFactorControl`; missing or false
  capabilities hide them. Each step posts `{"percent":N}` (1–200%) to
  `/api/printers/{id}/speed-factor` or `/flow-factor` and stays pending until
  live status reports the new value. Unreported readback shows `--` (VoiceOver
  "not reported") and is never assumed to be 100%. 501 explains that no command
  was sent; 503 warns that the outcome may be unknown. Nothing is retried
  automatically.

- **Essential Controls alignment** — Restore owner-selected concept 1: one
  Heat group with paired inputs and Set targets, compact presets and Cool down,
  grouped directional Move & home, and compact Filament tools. Paired readings
  stay above Heat; iPad separates thermal/material and motion work. Preserve
  atomic validation, omitted/zero targets, lifecycle locks, native accessibility
  and genuine safety evidence rather than illustrative prototype readiness.
  Match the original CSS geometry and typography hierarchy: divided temperature
  strip, bordered controls, ruled motion rows, paired motor/calibration entries,
  and 1.1:1 iPad columns. Compact assignment delegates to the existing host;
  compact assignment retains Clear/NFC and truthful inventory information.

- **Guarded native material controls** — Separate physical filament requests
  from spool assignment and preserve NFC/Eject. Add signed extrusion choices,
  explicit unavailable reasons and an interruptible inline calibration review.
  Integrate shared verified-safety v1 discovery and timestamped telemetry for
  conditional extrusion, physical filament operations and the full calibration
  flow. Missing temperature, geometry, macro or firmware-persistence evidence
  still fails closed. Reviews are one-use, movement respects verified frames and
  clearance, and interrupted/uncertain work never becomes fake completion.

- **Typed Essential control transport** — Added absolute XYZ movement, signed
  extrusion/retraction, motor release and reviewed-revision Z-offset requests,
  plus detailed filament-unload inventory responses and calibration read data.
  New-operation UI is delivered separately. Existing controls now gate Home
  All/XY/Z independently, show a read-only Retry after capability-read failures,
  and hide/block Preheat and Cool Down without confirmed support. Cool Down
  omits an unconfirmed bed rather than sending speculative zero targets.
  Demo controls no longer advertise support based on no-op command methods.

- **Adaptive compact navigation** — iPhone operators can use a focused Simple
  shell (Attention · Farm · Inventory · Oversight) or a Floor/Oversight
  two-mode shell (Floor: Attention · Farm · Tasks · Inventory; Oversight:
  Overview · Fleet · Jobs · Upkeep · Reports), with stable destination
  identities, per-server preference persistence, and a real farm-wide
  Filament Coverage report. The shell is derived from `GET /api/system/farm-shape`
  (accounts, bays, printers) and the resolved `shiftPlanEnabled` capability —
  `shiftPlanEnabled` acts only as a negative signal, and fleet size is not a
  derivation signal. **Settings → Navigation** exposes an **Automatic / Simple
  / Two modes** override, stored per server, with a plain-language explanation
  of why Automatic chose what it did.
- **In-context Oversight upgrade offer** — When a farm crosses a new threshold
  (second account, second bay, or shift planning turned on after having been
  off), the Oversight tab root shows a one-time, dismissible offer card to
  switch to the two-mode shell. The card never appears at onboarding, never
  auto-switches shells, is per-server, and is suppressed permanently when the
  user picks an explicit override.
- **Per-server advanced-controls safety interlock** — Jog, preheat, home,
  z-offset, and disable-motors controls are now hidden by default until enabled
  from **Settings** → **Printer Safety** for the active server. Turning the
  setting off revokes open access immediately, and changing a registered
  server's URL resets the opt-in.
- **Post-login backend readiness check** — The app now checks every enabled
  mobile feature service and SignalR before showing the main interface. If part
  of the backend is unavailable, the app names the affected services and lets
  you continue with cached data and available features.
- **Explicit trust confirmation for self-signed LAN servers** — The first HTTPS
  connection to a private server with a self-signed certificate now requires
  confirming its SHA-256 public-key fingerprint. Confirmed fingerprints are
  pinned in the device Keychain; an unexpected certificate change is blocked
  until the old certificate is explicitly forgotten in **Manage Servers**.
- **Actionable Attention cards** — Server-provided actions now run in place
  with item-scoped progress, retry, and canonical refresh. Failure cards load
  camera snapshots independently, and every card links to its printer and
  related job by stable identity with complete VoiceOver and Dynamic Type
  support on iPhone and iPad.

### Changed

- Control tab follows the #3460 heater contract: **Cool down** is always shown
  and is idle-only (disabled while a job is starting, printing or paused;
  Emergency stop covers that case). It calls `POST /heaters/off` when the
  backend reports `supportsHeaterShutdown`, otherwise it sets hotend/bed
  targets to 0. No confirmation is needed. Heat is idle-only: presets, the
  More menu, Custom… and the Heat target fields are blocked while a job is
  starting, printing or paused, even with
  `supportsTemperatureControlDuringPrint`. Mid-print temperature changes
  happen only on Tune, which keeps that capability gate; Tune omits the `enabled` quantity,
  and `allowZero` targets step to/from 0. Missing thermal values show "--"
  (VoiceOver "not reported").
- Heat presets: PLA · PETG · ABS · **More** · Cool down in one row (wrapping
  to presets, then More + Cool down at xxLarge and above, including every
  accessibility text size, so More stays reachable). Each material preset
  shows its nozzle/bed targets; More and Cool down show none. More's disabled
  VoiceOver hint names the actual reason (printing, offline, a pending command
  or unconfirmed access). More is a native menu (no section title) with ASA, TPU, PC and Nylon, then
  "Custom…", which focuses the Nozzle target field. Preset temperatures
  (°C, nozzle/bed): PLA 200/60, PETG 230/80, ABS 245/100, ASA 245/100,
  TPU 220/50, PC 260/110, Nylon 250/80. PETG and ABS change from 240/80 and
  240/100. PC and Nylon are capped for PTFE-lined hotends (preheat values, not
  print temperatures). All values live in the single `PreheatPreset.definition` table.
- At accessibility text sizes the More menu lists each material on one line
  and puts "Custom…" at the top, because the native menu doesn't scroll and
  Custom… was clipped below its bottom edge.
- Printer detail polish: the pager's page dots use a compact 16 pt indicator,
  the Move empty state shows an SF Symbol like the other empty states, and
  Emergency Stop is a full-width, solid red button with a white icon and label
  (accessibility label and confirmation unchanged).
- Tune shows editable hotend and bed targets while a job is printing or paused,
  disabled with a short reason only when the backend cannot apply them.
- **Removed:** all fan controls from Tune (part-fan stepper and fan or
  temperature-fan sensor rows).
- Move & home X/Y/Z readouts show "--" for a missing position (VoiceOver
  "not reported").
- Instrument panel: while printing, Move and Heat are disabled and Tune is
  the only enabled segment. Tune is selected on entering Control and when a
  print starts; when the print ends Move and Heat re-enable and Tune stays
  selected. Disabled segments use standard VoiceOver "dimmed" semantics (the
  extra "Unavailable while printing" hint is gone). Other instrument panel changes: jog distance defaults to 1 mm, the X/Y/Z readout is
  centred with hairline dividers, the babystep readout sits on the "Z babystep"
  header row in medium-weight monospaced text (shows "--", VoiceOver "Z offset
  not reported", when missing), and temperature steppers show the target as
  centred primary monospaced text with no fill chip.
- Idle Status no longer shows an empty progress bar and duplicate "0%"; the
  progress block appears only while actively printing.
- Native control capability fallback no longer infers physical-command support
  from a backend name. Explicit per-operation server evidence is required;
  older servers without that evidence leave commands unavailable. Heater,
  homing and relative-move responses now surface rejected command results.

- **BREAKING: Scan is no longer a top-level tab.** Scanning is an input method,
  and every scan entry point has been re-homed deliberately:
  - Harvest attention items get a full-width **Scan bin** primary action inside
    the task itself.
  - The Inventory tab exposes a single **Scan** icon in the nav bar (one scan
    affordance per screen — no duplicated buttons).
  - The **Offline Queue** now lives in the account/You area reachable from the
    nav-bar account button.
- **BREAKING: The Attention `⋯` overflow menu is gone.** Every destination it
  used to hide — Dashboard, Dispatch, Filament Coverage, Maintenance,
  Maintenance Analytics, Predictive Insights, Job History, Job Timeline,
  Uptime & Reliability, Locations, Notifications, Advanced Printer Controls,
  Settings, and the settings sub-screens — is now reachable from either the
  Oversight tab (Simple shell) or the Oversight mode tabs (Two modes), with
  no dead ends and no destinations that resolve only from inside a sheet.
- **Nav chrome rule enforced across every tab root.** The server chip sits
  leading; the account button sits trailing and is always the last control;
  screen actions (filter, search, scan) sit between them; the mode control is
  pinned to every root of both modes so no screen is a dead end.

### Fixed

- **Three WCAG contrast defects in `ThemeColors`** — `pfError`, `pfWarning`, and
  `pfButtonPrimary` fills and text-on-fill contrasts have been adjusted to meet
  WCAG AA (`≥ 4.5:1` for body text, `≥ 3:1` for large text and UI components)
  in both light and dark modes.
- **Database-backed print-job timestamps** — Print-job responses whose UTC
  timestamps are serialized without an explicit zone suffix now decode
  correctly instead of failing the entire API response.
- **Backend-disabled operator features** — The app now reads canonical nested
  operator capability flags and removes disabled Attention, Tasks, filament
  coverage, and printed-parts inventory surfaces while keeping spool inventory
  available. Legacy top-level capability payloads remain supported.

## [v1.0-beta.78] — 2026-07-05

### Fixed
- **Login on multi-server builds** — Signing in with valid credentials no longer bounces straight back to the login screen. The access token is now applied to the API client immediately on login (previously it was only wired up when switching to a *different* server, so logging into the already-selected server left the client unauthenticated). The token-expiry check is also now scoped to the specific server you logged into.

## [v1.0-beta.76] — 2026-07-05

### Added
- **Barcode scan-intake for filament spools** — Scan a spool's 1D retail barcode (UPC/EAN/Code128) to import it into Spoolman. A recognized barcode imports the spool instantly in a rapid "scan next" loop; an unrecognized barcode prompts you to pick or create the matching filament, then remembers the barcode so future scans import automatically. The barcode↔filament mapping is stored in Spoolman's article number.
- **Multi-server support** — Register, edit, switch between, and delete multiple PrintFarmer servers from the app. Credentials are stored per server in the Keychain, a quick server switcher is available in the app chrome (and the iPad sidebar), and switching servers rebuilds services at runtime. Existing single-server installs migrate automatically on first launch.

### Notes
- Consolidates the beta.69–beta.75 internal builds.

## [v1.0-beta.68] — 2026-04-03

### Fixed
- **Printer status mismatch between list and detail** — The printer detail screen now applies data from `/api/printers/{id}/status` to the displayed printer state, preventing cases where a printer appears "Ready" in the list but "Offline" after opening details.

## [v1.0-beta.67] — 2026-04-03

### Fixed
- **NFC filament data now shows correct values** — Diameter, print temp, and bed temp in the Write NFC Tag preview were showing "—" or placeholder text. Filament lookup now uses the spool's filament ID (not material/vendor name matching), so the correct filament record is always found.
- **NFC preview no longer blank on first open** — Spool and filament data are now bundled together atomically before the sheet is presented, eliminating a race condition that caused the three filament fields to show "—" on first open and only populate after closing and reopening.
- **OpenPrintTag payload now includes filament data** — The actual NFC tag written in OpenPrintTag format now correctly encodes `filament_diameter`, `print_temp`, and `bed_temp` from Spoolman filament settings.

### Changed
- **NFC field label column widened** — Increased label width from 100 to 130 pt so `filament_diameter` no longer wraps.

## [v1.0-beta.66] — 2026-04-03

### Added
- **OpenPrintTag write support** — New writable NFC format alongside OpenSpool and OpenTag3D. Writes JSON with `filament_type`, `color`, `manufacturer`, `net_weight`, and `spool_id` fields.
- **NFC field preview** — Write NFC Tag view now shows all fields that will be written, with format-specific field names (OpenSpool JSON keys, OpenPrintTag JSON keys, or OpenTag3D binary fields).
- **NFC format descriptions** — Settings picker now shows a dynamic description for each format explaining compatibility and encoding.

### Changed
- **Default NFC format** — New installs default to OpenPrintTag instead of OpenSpool.

## [v1.0-beta.65] — 2026-04-03

### Fixed
- **Notification entitlement error** — Removed `registerForRemoteNotifications()` call that caused "no valid aps-environment entitlement" error when enabling notifications. Local notifications via `UNUserNotificationCenter` require no entitlement.

### Added
- **NFC write format indicator** — The Write NFC Tag view now shows the selected tag format (OpenSpool or OpenTag3D) as a pill badge so users can confirm the format before writing.

## [v1.0-beta.64] — 2026-04-03

### Added
- **OpenTag3D NFC tag format** — New binary NFC tag format (opentag3d.info/spec) as a user-selectable alternative to OpenSpool JSON. Core fields (version, material, manufacturer, color, diameter, density, temperatures, target weight) fit NTAG213. Extended fields (batch/serial number, empty spool weight, measured weight/length) written on NTAG215+ when data is available.
- **NFC format picker** — Settings UI now includes an NFC Tags section with OpenSpool/OpenTag3D format picker, persisted via AppStorage.

## [v1.0-beta.12] — 2026-03-12

### Added
- **Local notifications for PendingReady** — App now sends local "Bed Clear Required" notifications when printers enter PendingReady state. Tracks per-printer to avoid spam; re-notifies if printer re-enters PendingReady.

### Fixed
- **Entitlements build failure** — Removed hardcoded `aps-environment` from entitlements file. The build system injects it automatically from the provisioning profile, fixing the "entitlements modified during build" Xcode Cloud error.

### Changed
- **Switched from APNs to local notifications** — PrintFarmer's self-hosted architecture means each installation can't hold centralized Apple credentials. Notifications now use `UNUserNotificationCenter` local notifications instead of APNs push.

## [v1.0-beta.11] — 2026-03-11

### Added
- **Real-time SignalR updates** — Temperatures, state, progress, and job info now update live via WebSocket. No more manual pull-to-refresh needed.
- **Custom nozzle & radiator icons** — Hotend and bed temperature displays now use 3D printer nozzle and radiator icons matching the web UI (replaces generic flame and stack icons).
- **`bedClearRequired` notification type** — Prepares for push notifications when a printer needs its bed cleared.
- **`fileName` field** — Dashboard, printer cards, and printer detail now show the gcode file name (e.g. `benchy.gcode`) instead of the full job path.

### Fixed
- **Bed clear UI not updating** — Tapping "Confirm Bed Clear" now immediately clears the warning banner and buttons (optimistic update with delayed reload).
- **Push notification entitlements** — Added missing `aps-environment` entitlement so push notifications can be enabled.
- **SignalR log noise** — Silenced `toolheadUpdate`, `extruderUpdate`, and `heaterBedUpdate` events that were flooding the debug log.

## [v1.0-beta.10] — 2026-03-11

### Added
- **PendingReady indicator badge** — Orange badge on the Printers tab (iPhone) and sidebar (iPad) shows count of printers waiting for bed clear, matching the web UI's pulsing alert icon.

## [v1.0-beta.9] — 2026-03-11

### Added
- **Job thumbnails** — Gcode file thumbnails now display in job detail pages and job queue list rows.

### Fixed
- **Dispatch dashboard** — Redesigned to show real metrics: pending printers, busy printers, and dispatched jobs in last 24 hours.
- **Job analytics decoding** — Fixed JSON decoding error ("data couldn't be read because it's missing").
- **Predictive insights** — Risk factor percentages now display correctly (was showing 8000%, 10000%).

## [v0.1.0-beta.7] — 2025-07-21

### Added
- **Per-printer camera rotation** — Rotate button next to camera refresh in PrinterDetailView cycles through 0°→90°→180°→270° with per-printer UserDefaults persistence. Fixes upside-down camera feeds on printers like Phrozen Arco. (`c849000`)

### Changed
- **Compact button layouts & shorter labels** — Simultaneous action buttons now grouped side-by-side (Pause+Abort, Resume+Abort, Retry+Cancel, Set+Scan Tag). Labels shortened for clarity: Change Filament→Change, Write NFC Tag→Write Tag, Acknowledge→Accept, Clear Filters→Reset, Scan NFC Tag→Scan Tag. Fixed NFCWriteView error state layout (VStack→HStack). All touch targets remain ≥44pt HIG compliant. (`9f5fe50`)

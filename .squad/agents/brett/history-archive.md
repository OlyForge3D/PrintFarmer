# Brett History

## Core Context

Brett is the research and strategy specialist for PrintFarmer. Key retained context:
- Competitive analysis repeatedly identified AI failure detection, business analytics, and workflow guidance as the biggest gaps versus commercial competitors.
- Camera management is a farm-platform concern above printer firmware limits; the market expects multi-camera, enable/disable, and health concepts even when firmware APIs are limited.
- OpenAPI, slicer artifact extraction, and project-style organization consistently ranked above free-form tagging in user-value research.
- PrintFarmer's strongest market position is self-hosted + multi-backend + subscription-free, so roadmap recommendations should reinforce that niche.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history
- 2026-03-06 to 2026-03-10: Delivered competitive landscape and five-feature research covering AI, analytics, camera control, OpenAPI, slicer artifacts, and OrcaSlicer workflow opportunities.
- 2026-03-14 to 2026-03-15: Reversed the earlier camera-control “won't fix” stance after proving competitors manage cameras independently from firmware APIs; this fed the approved camera platform decision.

## Moonraker-Obico Plugin Analysis & Validation (2026-03-25 to 2026-03-26)

**Context:** Deep analysis of PrintFarmer's Obico integration vs upstream Moonraker-Obico plugin.

**Key Findings (consolidated):**
- PrintFarmer correctly implements **upstream ML snapshot contract** (`GET /p/?img=...`) for failure detection. This is ✅ **CORRECT and SUFFICIENT** for local use.
- Moonraker-Obico owns 6 responsibilities; PrintFarmer implements only ML/failure-detection slice (intentional architectural divergence).
- PrintFarmer is a **farm controller** (multi-tenant, local); Moonraker-Obico is a **single-printer agent** (cloud-first). Don't replicate WebRTC, tunneling, or full state sync.

**Gap Matrix:** Snapshot upload for remote viewing (5-7 days, medium priority if users request) and state sync (2-3 days, low priority) are potential future enhancements. DO NOT add WebRTC streaming, tunneling, or interactive auth — maintain separation of concerns.

**Validation:** Obico self-hosted UI appearing empty with PrintFarmer is **expected behavior**. OctoPrint plugin provides full state sync; PrintFarmer intentionally differs to avoid second source of truth. Current design is **sound and complete** for stated use case.


## Team Update: Slicer UI Fix (2026-04-05)

**Date:** 2026-04-05  
**Incident:** Slicer UI missing in Docker microservices deployment  
**Status:** ✅ RESOLVED

Jeff Papiez reported slicer UI was missing in live deployment despite slicer-host container running. Root cause: `src/api/Program.cs` conflated slicer module loading with platform capability reporting. In microservices mode, slicer-host runs as separate container, so assembly check returned false.

**Team Response:**
- **Lambert:** Diagnosed root cause and implemented fix in `SystemCapabilitiesController.cs` to detect `DEPLOYMENT_MODE=microservices`
- **Ripley:** Validated frontend capability detection was working correctly
- **Kane:** Added regression test coverage in `SystemCapabilitiesIntegrationTests.cs`
- **Parker:** Deployed fix using `pfdev redeploy api` (per user directive to use canonical script name)

**Outcome:** `slicingEnabled=true` now reported correctly in microservices mode. Slicer UI visible in production deployment.

## 2026-07-16: OrcaSlicer Bundle Format Research (.orca_printer / .orca_filament)

**Role:** Research specialist  
**Status:** ✅ Complete — Format specification delivered

**Context:** Deep-dive into OrcaSlicer C++ source code (`/Users/jpapiez/s/Orca/orcaslicer/src/`) to reverse-engineer the `.orca_printer` and `.orca_filament` bundle formats for PrintFarmer import/export support.

**Key Findings:**

1. **Both formats are standard ZIP archives** — `.orca_printer` and `.orca_filament` are renamed ZIPs containing JSON preset files + a `bundle_structure.json` manifest
2. **`.orca_printer`** bundles a printer preset + all associated filament presets + process presets, organized in `printer/`, `filament/`, `process/` subdirectories
3. **`.orca_filament`** bundles filament variants grouped by printer vendor (e.g., `Creality/`, `Prusa/`), with a vendor-indexed manifest
4. **Preset type detection** uses discriminator fields: `printer_settings_id` (printer), `print_settings_id` (process), `filament_settings_id` (filament)
5. **`bundle_structure.json`** is metadata-only — OrcaSlicer skips it during import and auto-detects types from individual JSONs
6. **Inheritance model:** Presets use `inherits` field referencing parent presets; some presets are incomplete without their parent
7. **Values are strings even for numbers**, and multi-value fields use string arrays (e.g., `"nozzle_diameter": ["0.4"]`)

**Key Source Files:**
- `src/libslic3r/PresetBundle.cpp:958` — `import_presets()` (ZIP extraction + import logic)
- `src/slic3r/GUI/CreatePresetsDialog.cpp:3907` — `archive_preset_bundle_to_file()` (.orca_printer export)
- `src/slic3r/GUI/CreatePresetsDialog.cpp:4027` — `archive_filament_bundle_to_file()` (.orca_filament export)
- `src/libslic3r/Preset.hpp:39-75` — JSON key constants
- `src/libslic3r/PresetBundle.hpp:15` — `BUNDLE_STRUCTURE_JSON_NAME` = `"bundle_structure.json"`

**No `.bbcfg` or `.orca_process` formats exist.** OrcaSlicer offers 5 export types: 2 bundles (.orca_printer, .orca_filament) and 3 plain zips (printer/filament/process presets).

**Output:** Full format specification with schemas in `.squad/decisions/inbox/brett-orca-bundles.md`


## 2026-04-17: OrcaSlicer Bundle Format Research Complete


**Role:** Research specialist  
**Session:** 2026-04-17T19:21:05Z  
**Status:** ✅ Complete — Research documented, decision PFarm1-5duw created

**Research Focus:** Full specification of `.orca_printer` and `.orca_filament` bundle formats to guide PrintFarmer import/export implementation.

**Key Findings Summary:**
- Both formats are ZIP archives with JSON presets + manifest
- `.orca_printer` bundles printer + filaments + processes in subdirectories
- `.orca_filament` bundles vendor-grouped filament variants
- Import skips manifest, auto-detects preset types from discriminator fields
- Values mostly strings; multi-value fields use string arrays
- Inheritance chain support via `inherits` field

**Deliverable:** Complete specification with schemas, export workflows, and implementation recommendations in `decisions.md`

**Decision Created:** PFarm1-5duw — Support `.orca_printer` and `.orca_filament` bundle import

**Handoff:** Ripley's gap analysis identifies missing frontend ZIP extraction and backend endpoint wiring; implementation planning ready.

## 2025-07-24: Infill Pattern Icon Audit — OrcaSlicer Comparison

**Role:** Research specialist
**Status:** Complete — Detailed audit in `.squad/decisions/inbox/brett-infill-icon-audit.md`

**Context:** Jeff reported our infill pattern icons don't match OrcaSlicer's actual icons. Performed systematic comparison by downloading all 30 infill `param_*.svg` files from OrcaSlicer's GitHub (`SoftFever/OrcaSlicer/resources/images/`) and comparing against our 28 icons in `InfillPatternIcons.tsx`.

**Key Findings:**
- 0 out of 28 icons are accurate matches to OrcaSlicer's SVGs
- 4 are partially correct in concept (gyroid, hilbert curve, archimedean chords, honeycomb)
- 24 are completely wrong — drawn from imagination rather than actual toolpath geometry
- Root cause: Our icons depict naive geometric interpretations of pattern names (e.g., "triangles" as a triangle). OrcaSlicer icons show the actual toolpath cross-section (e.g., "rectilinear" as diagonal cross-hatch because toolpath alternates plus/minus 45 degrees per layer)
- Missing 2 patterns: rectilinear-grid, rectilinear_interlaced exist in OrcaSlicer but not in our codebase
- 1 phantom pattern: stars exists in our code but has no corresponding icon in OrcaSlicer
- OrcaSlicer icons are 24x24 with two-layer design (gray for alternate layer, teal for current layer)

**Recommendation:** All 28 icons need replacement using OrcaSlicer's actual SVG path data as source. AGPL licensing implications noted.

## Learnings

## 2026-05-31: bambuddy Broad Feature Sweep (brett-3)

**Scope:** Feature inventory of maziggy/bambuddy excluding slicing UI and 3D/gcode viewers.

**Architecture notes:**
- bambuddy is a single Python FastAPI monolith (`backend/app/main.py`, 274 KB) with React frontend; Bambu-specific MQTT-only transport (no OctoPrint/Klipper/Moonraker backends).
- Default DB is SQLite; optional PostgreSQL via `DATABASE_URL` env. Single Docker container with host networking for discovery.
- "Virtual printer" feature makes bambuddy impersonate a Bambu Lab printer (MQTT broker + FTP server + RTSP proxy) so OrcaSlicer/BambuStudio treat bambuddy as the target printer — queue-based dispatch model.
- i18n supported natively in 10+ languages (`frontend/src/i18n/locales/`).

**Standout features worth remembering:**
1. **Print queue scheduler with SJF** (`backend/app/services/print_scheduler.py`): Filament-validated dispatch, shortest-job-first option, dispatch watchdog for H2D timing bugs, FTP retry, starvation guard, filament deficit pre-check.
2. **Energy + cost per print** (`backend/app/models/print_log.py`): Every print log entry tracks `filament_used_grams`, `cost`, `energy_kwh`, `energy_cost` — populated via smart plug energy snapshots.
3. **8-provider notification system** (`backend/app/schemas/notification.py`, `frontend/src/components/AddNotificationModal.tsx`): email, Telegram, Discord, webhook, ntfy, Pushover, CallMeBot/WhatsApp, Home Assistant. User-level opt-in prefs.
4. **Layer timelapse → MP4** (`backend/app/services/layer_timelapse.py`): Per-print ffmpeg-based timelapse, attached to archive on print complete.
5. **MakerWorld direct import** (`backend/app/services/makerworld.py`): Resolves MakerWorld URL, fetches 3MF via Bambu Cloud token — no separate OAuth needed. 200 MB cap.
6. **SpoolBuddy NFC sub-system** (`frontend/src/pages/spoolbuddy/`, `backend/app/api/routes/spoolbuddy.py`): Companion ESP32 device writes NDEF tags; bambuddy serves tag payloads to the device and auto-assigns spools when tags are scanned.
7. **Filament shopping list** (`backend/app/models/shopping_list.py`): Tracks filament SKUs with pending/purchased/received status.
8. **HMS error modal** (`frontend/src/components/HMSErrorModal.tsx`): Decodes Bambu firmware error codes into human-readable messages inline in the printer card.
9. **Print calendar / forecast** (`frontend/src/components/CalendarView.tsx`, `ForecastPanel.tsx`): Prints shown on a calendar; forecast panel for capacity planning.
10. **Enterprise auth** (`backend/app/api/routes/auth.py`, LDAP + OIDC + TOTP/MFA routes): Full enterprise SSO support.

**Features NOT to chase:**
- Virtual printer emulation: deep Bambu-specific protocol work (MQTT broker + FTP + RTSP proxy), maintenance liability, architecturally foreign to PrintFarmer's multi-backend model.
- SpoolBuddy NFC hardware companion: requires ESP32 peripheral, embedded firmware — out of PrintFarmer's scope.

**Key files for future reference:**
- `backend/app/services/print_scheduler.py` — queue dispatch with filament validation
- `backend/app/models/print_log.py` — per-print cost/energy schema
- `backend/app/services/makerworld.py` — MakerWorld 3MF fetch
- `backend/app/services/layer_timelapse.py` — timelapse assembly
- `frontend/src/components/AddNotificationModal.tsx` — notification provider UX

## 2026-05-31: bambuddy Deep-Dive Follow-up

- `gcode-preview` npm latest is `2.18.0` (MIT, ~39 KB package / 128.5 KB unpacked) while the upstream develop branch is already `3.0.0-alpha.4`; adoption should pin v2 initially or deliberately evaluate the v3 API split.
- `gcode-preview` v2 is a Three.js/WebGL renderer with `WebGLPreview`, full-string parsing, an experimental stream reader, G0/G1/G2/G3 and T-code handling, and color arrays indexed by tool number; no built-in worker integration was found.
- bambuddy's `GcodeViewer.tsx` fetches raw G-code with auth headers, remaps Bambu/AMS tool IDs into T0-T7, filters high-number special T commands, and uses layer slider/buttons by setting `preview.endLayer`.
- bambuddy's `ModelViewer.tsx` parses 3MF client-side with JSZip + DOMParser, reading standard `3D/3dmodel.model` geometry plus Bambu/Orca-specific `Metadata/model_settings.config` and `Metadata/plate_*.json` for extruders, plates, offsets, and bounds.
- The 3MF viewer contains useful responsiveness workarounds: `setTimeout(0)` yielding in vertex/triangle/component loops and geometry merging by extruder with `mergeGeometries`, but it remains main-thread parsing and should be workerized before PrintFarmer-scale adoption.

## 2026-05-31: bambuddy Architecture Review

**Role:** Research specialist  
**Status:** ✅ Complete — focused external-repo review

**Scope:** Reviewed `maziggy/bambuddy` only through OrcaSlicer integration / slicing pipeline and 3D model visualization lenses.

**Key Findings:**
- bambuddy uses an optional Docker Compose sidecar stack (`slicer-api/`) that wraps OrcaSlicer and Bambu Studio CLIs behind HTTP, rather than embedding slicer logic in the main FastAPI app.
- Profiles are hybrid: local/imported presets live in the app database, Orca base profiles are cached in the database with a 7-day TTL, bundled/standard profiles can be materialized by the sidecar from slicer resources, and `.bbscfg` bundles are stored/extracted on the sidecar.
- Slice jobs are in-memory backend jobs: library/archive endpoints enqueue work, call the sidecar `/slice` endpoint, poll sidecar progress by request ID, and persist `.gcode.3mf` artifacts back to the library/archive tables.
- Visualization uses Three.js for source STL/3MF mesh preview and `gcode-preview` for sliced G-code toolpath preview. The mesh viewer parses 3MFs client-side with JSZip and includes event-loop yielding plus geometry merging for responsiveness.

**Adoption candidates recorded:**
- `.squad/decisions/inbox/brett-bambuddy-gcode-toolpath-preview.md`
- `.squad/decisions/inbox/brett-bambuddy-slice-progress-contract.md`

**Reusable pattern:** External repo triage workflow captured in `.squad/skills/external-repo-triage/SKILL.md`.


- OrcaSlicer infill icons are NOT abstract geometric shapes — they show the actual toolpath cross-section of each infill pattern as it would appear in a single printed layer
- Rectilinear family patterns use plus/minus 45 degree diagonal lines (not horizontal/vertical) because that is the actual print direction
- OrcaSlicer uses a consistent two-layer visual language: gray (#949494) at 75% opacity for the alternate layer, teal (#009688) for the active layer
- All infill icons in OrcaSlicer are 24x24 SVGs in resources/images/param_*.svg
- Lateral variants (lateral-honeycomb, lateral-lattice) have a unique 3D perspective corner-fold border treatment
- There is no param_stars.svg in OrcaSlicer — the stars infill pattern may not be valid or goes by another name

---

## Round 17 (2025-11-23): PrinterControlsSection Snapshot Testing Spike — PR #14

**Task:** Research and recommend snapshot testing strategy for PrinterControlsSection regression suite  
**Status:** ✅ COMPLETE — Snapshot spike PR opened

### Research Summary

**Challenge:** PrinterControlsSection (Preheat, Home, Jog subgroups) ships as iOS native SwiftUI. Need regression protection against visual/layout drift across multiple printers, network states, and dark mode.

**Recommendation:** `swift-snapshot-testing` (pointfreeco) via SPM
- **Why:** Industry standard for SwiftUI regression testing; Xcode/iOS 15+ compatible; SPM distribution avoids CocoaPods; no third-party build tool dependencies
- **Testing matrix:** 8 snapshots
  - Backends: Moonraker, FlashForge, SDCP
  - States: blocked (printing), in-flight (jog/preheat active), error, dark-mode, iPhone SE form factor
- **Biggest risk:** Simulator OS version drift between CI runs. Baseline snapshots captured on e.g. iOS 18.1; if CI upgrades to iOS 18.2, all snapshots fail as false positives. **Mitigation:** Pin simulator OS version in CI YAML (e.g., `xcode-select-version: 15.4`, `simulator-os-version: 18.1`).

### Architecture Decisions

- **Per-backend snapshots:** Separate directories for Moonraker, FlashForge, SDCP to detect backend-specific layout regressions (e.g., SDCP capabilities subset affects button visibility)
- **State variants:** Capture each state combo as separate snapshot file; makes diffs clear when state handling breaks
- **Dark mode:** Automatic via `traitCollection` in test setup; one matrix entry captures all dark-mode variants

### Key Learnings

- Snapshot testing is fragile if simulator environment isn't pinned — requires CI discipline
- Small form factors (iPhone SE) are critical for regression detection; larger phones can hide layout bugs
- Backend-specific capability subsets must be tested separately; a Moonraker snapshot won't catch SDCP-specific failures

### Deliverable

- PR https://github.com/OlyForge3D/PrintFarmerMobile/pull/14
- Research-grade spike; implementation deferred to coding phase
- CI environment pinning strategy documented

---


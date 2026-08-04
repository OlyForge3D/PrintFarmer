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

## 2026-05-31: external-reference-app Broad Feature Sweep (brett-3)

**Scope:** Feature inventory of [external-reference-app] excluding slicing UI and 3D/gcode viewers.

**Architecture notes:**
- external-reference-app is a single Python FastAPI monolith (`backend/app/main.py`, 274 KB) with React frontend; Bambu-specific MQTT-only transport (no OctoPrint/Klipper/Moonraker backends).
- Default DB is SQLite; optional PostgreSQL via `DATABASE_URL` env. Single Docker container with host networking for discovery.
- "Virtual printer" feature makes external-reference-app impersonate a Bambu Lab printer (MQTT broker + FTP server + RTSP proxy) so OrcaSlicer/BambuStudio treat external-reference-app as the target printer — queue-based dispatch model.
- i18n supported natively in 10+ languages (`frontend/src/i18n/locales/`).

**Standout features worth remembering:**
1. **Print queue scheduler with SJF** (`backend/app/services/print_scheduler.py`): Filament-validated dispatch, shortest-job-first option, dispatch watchdog for H2D timing bugs, FTP retry, starvation guard, filament deficit pre-check.
2. **Energy + cost per print** (`backend/app/models/print_log.py`): Every print log entry tracks `filament_used_grams`, `cost`, `energy_kwh`, `energy_cost` — populated via smart plug energy snapshots.
3. **8-provider notification system** (`backend/app/schemas/notification.py`, `frontend/src/components/AddNotificationModal.tsx`): email, Telegram, Discord, webhook, ntfy, Pushover, CallMeBot/WhatsApp, Home Assistant. User-level opt-in prefs.
4. **Layer timelapse → MP4** (`backend/app/services/layer_timelapse.py`): Per-print ffmpeg-based timelapse, attached to archive on print complete.
5. **MakerWorld direct import** (`backend/app/services/makerworld.py`): Resolves MakerWorld URL, fetches 3MF via Bambu Cloud token — no separate OAuth needed. 200 MB cap.
6. **SpoolBuddy NFC sub-system** (`frontend/src/pages/spoolbuddy/`, `backend/app/api/routes/spoolbuddy.py`): Companion ESP32 device writes NDEF tags; external-reference-app serves tag payloads to the device and auto-assigns spools when tags are scanned.
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

## 2026-05-31: external-reference-app Deep-Dive Follow-up

- `gcode-preview` npm latest is `2.18.0` (MIT, ~39 KB package / 128.5 KB unpacked) while the upstream develop branch is already `3.0.0-alpha.4`; adoption should pin v2 initially or deliberately evaluate the v3 API split.
- `gcode-preview` v2 is a Three.js/WebGL renderer with `WebGLPreview`, full-string parsing, an experimental stream reader, G0/G1/G2/G3 and T-code handling, and color arrays indexed by tool number; no built-in worker integration was found.
- external-reference-app's `GcodeViewer.tsx` fetches raw G-code with auth headers, remaps Bambu/AMS tool IDs into T0-T7, filters high-number special T commands, and uses layer slider/buttons by setting `preview.endLayer`.
- external-reference-app's `ModelViewer.tsx` parses 3MF client-side with JSZip + DOMParser, reading standard `3D/3dmodel.model` geometry plus Bambu/Orca-specific `Metadata/model_settings.config` and `Metadata/plate_*.json` for extruders, plates, offsets, and bounds.
- The 3MF viewer contains useful responsiveness workarounds: `setTimeout(0)` yielding in vertex/triangle/component loops and geometry merging by extruder with `mergeGeometries`, but it remains main-thread parsing and should be workerized before PrintFarmer-scale adoption.

## 2026-05-31: external-reference-app Architecture Review

**Role:** Research specialist  
**Status:** ✅ Complete — focused external-repo review

**Scope:** Reviewed `[external-reference-app]` only through OrcaSlicer integration / slicing pipeline and 3D model visualization lenses.

**Key Findings:**
- external-reference-app uses an optional Docker Compose sidecar stack (`slicer-api/`) that wraps OrcaSlicer and Bambu Studio CLIs behind HTTP, rather than embedding slicer logic in the main FastAPI app.
- Profiles are hybrid: local/imported presets live in the app database, Orca base profiles are cached in the database with a 7-day TTL, bundled/standard profiles can be materialized by the sidecar from slicer resources, and `.bbscfg` bundles are stored/extracted on the sidecar.
- Slice jobs are in-memory backend jobs: library/archive endpoints enqueue work, call the sidecar `/slice` endpoint, poll sidecar progress by request ID, and persist `.gcode.3mf` artifacts back to the library/archive tables.
- Visualization uses Three.js for source STL/3MF mesh preview and `gcode-preview` for sliced G-code toolpath preview. The mesh viewer parses 3MFs client-side with JSZip and includes event-loop yielding plus geometry merging for responsiveness.

**Adoption candidates recorded:**
- `.squad/decisions/inbox/brett-external-reference-app-gcode-toolpath-preview.md`
- `.squad/decisions/inbox/brett-external-reference-app-slice-progress-contract.md`

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


## 2026-05-31: Trio Review Cycle #355, #371, #405
Participated in follow-up validation on merged stack (round 8). Multi-reviewer consensus with fresh-hand rotation proved effective. Key learnings:
1. **Surgical-fix pattern:** Kane's narrow corrections across three branches demonstrated cost efficiency
2. **Multi-reviewer consensus:** Three independent reviewers with fresh hands prevents fatigue. (The author-lockout rule that once accompanied this has been RESCINDED by the repo owner — authors fix their own rejected work; nobody is ever locked out of an artifact.)
3. **Session-end report validation:** Must verify trio drops match current commit SHA
4. **PR auto-close gap:** Manual close required for development branch merges

## 2026-05-31: external-reference-app Slicing UX Comparison (3rd Pass — Slice Modal Focus)

**Role:** Research specialist
**Status:** ✅ Complete — Findings documented, decision inbox updated
**Prior work:** Session brett-1 covered external-reference-app architecture and Docker sidecar; separate session covered gcode toolpath preview. This is the dedicated slice UX pass.

### Research Questions Answered

**Q1: How does external-reference-app's slice-job creation flow work?**

Five-step flow:
1. Right-click / toolbar "Slice" on a `.stl`, `.3mf`, or `.step` file in `FileManagerPage`
2. If multi-plate 3MF: `PlatePickerModal` appears first — shows plate thumbnails and per-plate filament requirements
3. `SliceModal` loads presets from `GET /slicer/presets` (3-tier unified response) and plate metadata
4. User selects Printer preset → Process preset → Bed type override (optional) → Filament preset(s) per AMS slot
5. Click "Slice" → `POST /library/files/{id}/slice` → returns `{job_id}` → modal polls `GET /slice-jobs/{job_id}` with live progress inline

Source: `frontend/src/components/SliceModal.tsx`, `backend/app/api/routes/library.py:3695`, `backend/app/api/routes/slice_jobs.py`

**Q2: What slicing options does external-reference-app expose?**

ZERO individual parameter controls. Exactly:
- Printer preset (dropdown, 3-tier)
- Process preset (dropdown, 3-tier, filtered by `compatible_printers`)
- Filament preset(s) (dropdown per AMS slot, 3-tier, auto-picked by type+color scoring)
- Bed type override (optional: Cool Plate, Engineering Plate, High Temp Plate, Textured PEI Plate, Smooth PEI Plate, Cool Plate SuperTack, or inherit)
- Plate picker (pre-step, only for multi-plate 3MF: plate N or all plates)
- Bundle mode: alternative path using `.bbscfg` import — hides tier dropdowns, shows bundle + process name + filament names from the bundle's extracted manifest

Source: `backend/app/schemas/slicer.py:SliceRequest`

**Q3: UI paradigm — is it Orca-in-browser or a simplified abstraction?**

Simplified abstraction. The entire modal is `max-w-xl`, single column, ~5 dropdowns maximum. No OrcaSlicer settings categories, no nested sections, no parameter sliders. Design language: dark with `bg-bambu-dark-secondary`, `bambu-green` accents. The preset system means users are trusting pre-validated OrcaSlicer/BambuStudio profiles rather than composing settings ad-hoc.

PrintFarmer contrast: `SlicerConfigModal.tsx` exposes layer height slider, infill slider, print speed slider, nozzle temp, bed temp, supports toggle — individual parameters as the primary surface. `NewSliceJobPage` is more advanced (OrcaSlicer profiles via `SlicerSettingsPanel`) but the default entry point is still parameter-first.

**Q4: Upload format support — especially .gcode?**

| Format | external-reference-app | PrintFarmer |
|--------|----------|-------------|
| `.stl` | ✅ upload + slice source | ✅ upload + slice source |
| `.3mf` | ✅ upload + slice source | ✅ upload + slice source |
| `.obj` | ✅ upload only | ✅ upload only |
| `.step` / `.stp` | ✅ upload + slice source | ✅ upload + slice source |
| `.ply` | ❌ | ✅ upload only |
| `.gcode` (raw) | ❌ REJECTED: HTTP 400, "needs .gcode.3mf container" | ✅ Direct gcode upload via `/gcode-files/upload` |
| `.gcode.3mf` | ✅ Accepted + print-queue eligible | N/A (not Bambu-specific) |
| `.zip` | ✅ Auto-extracted with folder options | N/A |
| `.bbscfg` | ✅ Bundle import via SlicerBundlesPanel | ❌ |

external-reference-app gcode rejection rationale: Bambu printers require `.gcode.3mf` containers in network mode. Raw `.gcode` is accepted to the file system but rejected at print-time validation with an explicit educational error message. PrintFarmer does NOT have this constraint because its backends (Moonraker, PrusaLink, FlashForge, SDCP) all accept raw gcode natively — a genuine competitive differentiator for non-Bambu fleets.

### Key Learnings for PrintFarmer

1. **Preset-first is farm-correct.** external-reference-app's choice to hide all parameter sliders behind a preset triplet prevents per-job drift — exactly what farm operators need. PrintFarmer's parameter-first UX encourages accidental variance across jobs. A "Quick Slice" entry point (printer preset → process preset → filament preset → submit) would be more farm-appropriate as the default.

2. **The 3-tier preset system is materially different from PrintFarmer's profile system.** external-reference-app's `cloud > local > standard` cascade deduplicates presets by name, prefers user-customized local imports, and pulls from Bambu Cloud for stock profiles. PrintFarmer uses OrcaSlicer worker-cached profiles (no cloud tier, no user import tier). For non-Bambu farms this is fine, but it means profile management is operator-owned end-to-end.

3. **Bed type as a standalone, always-visible override is smart UX.** It's the one parameter that meaningfully differs between identical jobs (e.g., same model, same settings, but different surface on the bed). external-reference-app surfaces it as a top-level dropdown, not buried in a process profile. PrintFarmer buries it.

4. **Smart filament auto-pick by type+color scoring reduces AMS job errors.** The type-match + color-proximity + tier scoring at `SliceModal.tsx:123-164` auto-selects the right filament for each slot based on 3MF plate metadata. PrintFarmer's filament picker is fully manual.

5. **PrintFarmer's raw gcode upload is a differentiator — protect it.** external-reference-app can't accept raw gcode at all. PrintFarmer's `/gcode-files/upload` endpoint, configurable `allowedExtensions`, and multi-backend gcode dispatch are meaningful advantages for heterogeneous farms. Do not add Bambu-style rejection logic globally; gate it per-backend at send time if Bambu support is added.

6. **Bundle mode (.bbscfg) is an interesting "canonical config" pattern.** It lets farm operators lock an entire BambuStudio settings package (printer + process + filament names) and slice from it. Reduces configuration surface area. PrintFarmer analog would be an `.orca_printer` bundle import as slice source — which is adjacent to the already-planned bundle import feature (see 2026-04-17 session).

### Files Investigated

- `[external-reference-app]: frontend/src/components/SliceModal.tsx` (50.5 KB — full read + grep analysis)
- `[external-reference-app]: backend/app/schemas/slicer.py` (full read)
- `[external-reference-app]: backend/app/api/routes/library.py` (grepped: ALLOWED_EXTENSIONS, validate_print_file_upload, slice endpoint)
- `[external-reference-app]: backend/app/api/routes/slicer_presets.py` (grepped: 3-tier system, caching, compatible_printers)
- `[external-reference-app]: frontend/src/components/FileUploadModal.tsx` (full read)
- `PFarm1: src/Web/ReactApp/src/features/slicer/components/SlicerConfigModal.tsx` (full read)
- `PFarm1: src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx` (partial read — imports + structure)
- `PFarm1: src/Web/ReactApp/src/services/api.ts` (grepped: uploadGcodeFile, allowedExtensions)

### Decision Inbox

Created `.squad/decisions/inbox/brett-external-reference-app-ux-comparison.md` with four decision recommendations:
1. Add Quick Slice modal (preset-first flow)
2. Evaluate `.bbscfg` / OrcaSlicer bundle import as canonical farm config
3. Preserve and document raw gcode upload as differentiator
4. Implement smart filament auto-selection by type+color proximity

## 2026-05-31: external-reference-app Settings System & NFC UX Deep Dive

**Role:** Research specialist  
**Status:** ✅ Complete — Decision drops written to inbox

### PART A: Settings UX Findings

**Key Discovery:** external-reference-app consolidates all admin/config into a **single `/settings` route with 12 tabs**, not scattered navigation.

- **Tab Structure:** General, Plugs, Notifications, Queue, Filament, Network, APIKeys, Virtual-Printer, SpoolBuddy, Failure-Detection, Users (with sub-tabs), Backup
- **Organization:** Semantic cards within each tab; collapsible for progressive disclosure
- **Search:** Cross-tab search with keyword indexing; results tagged by tab (e.g., "notifications → Message Templates")
- **Secrets:** Tokens masked + revoke-only (never edit in-place); one-time copy for display

**PrintFarmer Contrast:** 25+ scattered nav items (SettingsPage, FilamentManagementPage, LocationManagementAdminPage, UserManagementPage, CamerasPage, NfcDevicesPage, WebhooksAdminPage, TagAdminPage, BedTypeAdminPage, CustomFieldsAdminPage, DataManagementPage, etc.)

**Recommendation:** Collapse 15+ settings-like pages into a single Settings area with 8-9 tabs (General, Filament, Slicing, Hardware, Notifications, Integrations, Data, Users). Keep Printers, Queue, Projects, Analytics, Automation as top-level workflow destinations.

**Decision Drop:** `.squad/decisions/inbox/brett-external-reference-app-settings-ux.md` — Full nav consolidation strategy + tab structure.

### PART B: NFC UX Findings

**Architecture:** external-reference-app pairs physical RFID tags (via SpoolBuddy ESP32) with spools using a **two-step modal flow**.

**Binding Flow:**
1. SpoolBuddy scans tag on tray → backend event
2. Frontend shows `LinkSpoolModal` with tag UUID + printer/AMS/tray context
3. User searches inventory by name/vendor/material/ID (search-first, not scroll)
4. User clicks spool → `POST /link-spool { spoolId, tagUid, printerId, amsId, trayId }`
5. Success: binding persisted, modal closes

**Tag Reading (Happy Path):**
- Re-scanning a known tag is **silent** (no modal)
- System updates spool "last_read" timestamp
- Other sessions notified via WebSocket (real-time sync)

**Error Handling:**
- **Unrecognized tag:** LinkSpoolModal appears (normal binding flow); user can search or create new spool
- **Tag bound to different spool/printer:** Toast warning; user can relink or cancel
- **Tag mismatch (physical vs binding):** Warning modal with details; user can override
- **Offline SpoolBuddy:** Local queue; syncs on reconnect

**Design Patterns Worth Stealing:**
1. Search-first modal (not scroll-heavy list)
2. Passive reads (no modal spam for known tags)
3. WebSocket real-time sync across sessions
4. Toast feedback (not error dialogs)
5. One-way tag write (never edit-in-place; prevents corruption)

**Decision Drop:** `.squad/decisions/inbox/brett-external-reference-app-nfc-ux.md` — Full NFC binding flows, error cases, sync strategy, + PrintFarmer implementation roadmap.

### Files Investigated

- `[external-reference-app]: frontend/src/pages/SettingsPage.tsx` (Truncated; ~700 lines)
- `[external-reference-app]: frontend/src/components/SpoolBuddySettings.tsx` (Device management for ESP32)
- `[external-reference-app]: frontend/src/components/LinkSpoolModal.tsx` (Tag-to-spool binding UX)
- `[external-reference-app]: frontend/src/components/AssignSpoolModal.tsx` (Spool-to-slot assignment)
- `[external-reference-app]: frontend/src/pages/InventoryPage.tsx` (Spool inventory with NFC integration)
- `[external-reference-app]: frontend/src/components/Layout.tsx` (defaultNavItems + sidebar)

### Key Takeaway

**Settings = Consolidation Opportunity:** PrintFarmer's scattered admin pages can collapse into a single Settings hub modeled on external-reference-app. NFC UX is proven; adopt modal-based binding with real-time sync for immediate farm-level coherence.

## 2026-05-31: gcode-preview v1 → v2 Worker Throwaway Risk Analysis

**Role:** Research specialist (technical feasibility analysis)  
**Status:** ✅ Complete — Decision inbox updated, learnings captured  
**Request:** Brady asked: "Will there be unnecessary throwaway work if we implement v1 (no-worker) and v2 (with workers)?"  

### Findings

1. **gcode-preview v2.18.0 has NO native worker support.**
   - `GCodePreview.processGCode()` parses G-code synchronously on main thread.
   - No streaming API; parser and rendering are tightly coupled to Three.js.
   - xyz-tools fork (active maintainer) lists "streaming" on roadmap but not yet shipped.

2. **Throwaway risk is LOW (~200–400 LOC rework, not deletion).**
   - UI components (layer slider, color picker, T-command filter) reuse ~95%.
   - Parser invocation site changes (sync → async), but only 1–2 files affected.
   - No throwaway logic; v1 code refactors cleanly to v2 service abstraction.

3. **Cheapest architecture: implement `GcodeParserService` abstraction NOW.**
   - Single point of change for parser invocation (50–60 LOC in v2).
   - All UI remains unchanged.
   - Estimated v1→v2 delta: +200 LOC net (worker harness + messaging).

4. **v1→v2 upgrade is a 2-week sprint task**, not a multi-month refactor.
   - Day 1–2: Extract parser into pure function.
   - Day 3–4: Write Web Worker wrapper.
   - Day 5–10: Service update + component test + streaming layer UI.

### Recommendation

**Ship v1 now.** The cost of delaying gcode preview UX is 4+ weeks; the cost of v1 main-thread is <2 weeks of lost productivity in v2. Service abstraction eliminates throwaway work.

### Decision Inbox

Created `.squad/decisions/inbox/brett-gcode-preview-worker-throwaway.md` with:
- API surface analysis (xyz-tools/gcode-preview v2.18.0)
- Worker integration feasibility (pure parser decoupling required)
- Service abstraction code sketch
- v1→v2 LOC delta estimate (200–400 LOC rework)
- Go/no-go recommendation (GO: ship v1 now)

## Learnings

- **2026-05-31T16:42:** Before committing, scrub message for forbidden external refs: "external-reference-app", "external-author", "external reference app", [external reference repo]. Acceptable alternatives: "adoption plan", "Phase N work breakdown", or standalone feature description. See .squad/decisions.md 2026-05-31T09:42 entry.
For full historical context, see `.squad/decisions.md` and `.squad/orchestration-log.md`.

## Learnings

### 2026-05-31 — Issue #346 PowerMonitor entities and migrations
- PowerMonitor/PowerReading live in `src/infra/Domain` with configuration classes in `src/infra/Data/Configurations`; `AppDbContext` is in `src/infra/Data`, not the older issue-body `src/api/Models`/`src/api/Data` paths.
- `Printer.Id` is `Guid`, so `PowerMonitor.PrinterId` must remain `Guid` even though the issue sketch said `int`; the generated migrations use `uuid` for PostgreSQL and `uniqueidentifier` for SQL Server.
- PowerReading retention is implemented by `Farm.Infrastructure.Services.Electricity.PowerReadingPruneService`, registered through `src/api/Startup/BackgroundServicesStartup.cs`, and deletes readings older than 90 days once daily.
- Farm-wide electricity fallback is the existing `CostTrackingSettings.ElectricityRatePerKwh`; per-monitor `ElectricityRateUsdPerKwh > 0` overrides it, otherwise cost calculation falls back to the farm-wide rate.
- Rebase lesson: after #413, rerun AppDbContext provider drift checks and avoid carrying stale snapshot changes that regress `LoginAuditEntry.Timestamp` provider metadata.

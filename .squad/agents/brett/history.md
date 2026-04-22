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

## Moonraker-Obico Plugin Analysis (2026-03-25)

**Context:** Reviewed upstream Moonraker-Obico plugin vs PrintFarmer's Obico integration to identify concrete gaps and mismatches.

**Key findings:**
- PrintFarmer correctly implements the **upstream ML snapshot contract** (`GET /p/?img=...`) with fallback to legacy multipart upload
- Moonraker-Obico owns **6 responsibilities**, of which PrintFarmer implements **1 partially** and **0 fully** for the other 5:
  1. Snapshot delivery to ML API — ✅ Correct (PrintFarmer)
  2. Periodic snapshot upload for remote viewing — ❌ Not implemented (medium effort)
  3. WebRTC/Janus live streaming — ❌ Out of scope (Obico's responsibility)
  4. Tunnel/remote HTTP access — ❌ Out of scope (security risk, use Obico's client)
  5. Printer state reporting to Obico server — ⚠️ Mismatch (PrintFarmer broadcasts to local clients, not Obico)
  6. Auth & linked printer mgmt — ❌ Not implemented (self-hosted, users manually configure tokens)

**Actionable gaps for PrintFarmer:**
- **Gap 1 (Medium priority):** Periodic snapshot upload to Obico `POST /api/v1/octo/pic/` for remote viewing dashboard (5-7 days)
- **Gap 2 (Medium):** Printer state visibility on Obico server for server-side decision logic (2-3 days)
- **Gap 3 (Low):** Failure detection result webhook integration (3-5 days)
- **Gap 4 (Low):** Multi-camera tagging (is_primary, is_nozzle) metadata propagation (1-2 days)

**Recommendation:** Status quo acceptable for local failure detection use case. Only implement snapshot upload if users request remote viewing through Obico's dashboard.

**Architecture principle:** PrintFarmer is a multi-tenant farm controller; Obico is single-printer agent. Don't adopt Obico's patterns (WebRTC, tunneling) — maintain separation of concerns.

## 2026-03-26: Moonraker-Obico Plugin Gap Analysis — Final Recommendation

**Role:** Research specialist + gap analyst  
**Status:** ✅ Complete — Recommendation finalized in decisions.md

**Context:** Deep analysis of how PrintFarmer's Obico integration aligns with upstream Moonraker-Obico plugin. Goal: identify missing features, determine whether gaps matter, and establish priority for future work.

**Key Findings:**

**Current Status:**
- PrintFarmer correctly implements upstream ML snapshot contract (`GET /p/?img=...` with legacy fallback)
- Snapshot delivery to ML API is ✅ **CORRECT and SUFFICIENT** for local failure detection
- PrintFarmer is a **farm controller** (multi-tenant); Moonraker-Obico is a **single-printer agent** (cloud-first)
- Architectural difference explains why PrintFarmer should NOT replicate all plugin responsibilities

**Gap Matrix (6 responsibilities from Moonraker-Obico):**

| # | Responsibility | Plugin Does | PrintFarmer Does | Effort | Priority |
|---|---|---|---|---|---|
| 1 | Snapshot delivery to ML API | Direct HTTP | Direct HTTP | ✅ Done | N/A |
| 2 | Snapshot upload for remote viewing | `POST /api/v1/octo/pic/` | ❌ No | 5-7 days | Medium (user-requested) |
| 3 | WebRTC/Janus live streaming | Full stack | ❌ No | Out of scope | ❌ Never add |
| 4 | Tunnel/remote HTTP access | LocalTunnel proxy | ❌ No | Out of scope | ❌ Never add |
| 5 | Printer state reporting | WebSocket push | Only local clients | 2-3 days | Low (future optimization) |
| 6 | Auth & linked printer mgmt | Interactive discovery | Manual token config | Out of scope | ❌ Never add |

**Recommendation:**
- **Current:** ACCEPTABLE. Local failure detection works correctly.
- **If users request remote viewing:** Implement Gap 1 (snapshot upload) as a 1-sprint feature.
- **DO NOT add:** WebRTC streaming, tunneling, or interactive auth (maintain separation of concerns; Obico already provides these).
- **Future enhancements:** State visibility and webhook integration are useful for later optimization but not blocking.

**Architectural Principle:** PrintFarmer should focus on farm-controller responsibilities (multi-printer state management, local workflow); Obico handles cloud responsibilities (WebRTC, remote access, account management). Don't force PrintFarmer to replicate a single-printer agent's full feature set.

**Files:** Documented in decisions.md; informs product roadmap and feature prioritization.

## 2026-03-26: Obico Self-Hosted UI Gap Analysis — Final Validation

**Role:** Research validation specialist  
**Status:** ✅ Complete — Findings merged and validated

**Team Collaboration:**
- Validated OctoPrint Obico plugin behavior (sends full printer/job/session state)
- Worked with Lambert to establish that PrintFarmer intentionally uses only ML/failure-detection slice
- Confirmed with Parker that empty Obico UI is expected, not a defect

**Key Conclusions:**
1. Obico self-hosted UI appearing empty with PrintFarmer is **expected behavior**
2. OctoPrint plugin provides full state sync; PrintFarmer **intentionally differs**
3. Current architecture avoids second source of truth (PrintFarmer is authoritative)
4. Mirroring printer/job state to Obico would be separate integration work, out-of-scope
5. User context: Jeff has obico-server fork if future server-side extensions needed

**Architecture Validation:**
- PrintFarmer → Obico (ML/failure-detection only) ✅ Correct
- PrintFarmer → Obico (full sync) ❌ Not implemented, intentional
- Current design is **sound and complete** for stated use case

**Files:** Documented in decisions.md; orchestration logs (`2026-03-26T01-45-41Z-brett.md`).


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

- OrcaSlicer infill icons are NOT abstract geometric shapes — they show the actual toolpath cross-section of each infill pattern as it would appear in a single printed layer
- Rectilinear family patterns use plus/minus 45 degree diagonal lines (not horizontal/vertical) because that is the actual print direction
- OrcaSlicer uses a consistent two-layer visual language: gray (#949494) at 75% opacity for the alternate layer, teal (#009688) for the active layer
- All infill icons in OrcaSlicer are 24x24 SVGs in resources/images/param_*.svg
- Lateral variants (lateral-honeycomb, lateral-lattice) have a unique 3D perspective corner-fold border treatment
- There is no param_stars.svg in OrcaSlicer — the stars infill pattern may not be valid or goes by another name

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


_Last 5 most-recent learnings preserved from full history. Older entries are in `history-archive.md` (archived 2026-05-31 by Scribe)._

## 2026-05-31: bambuddy Slicing UX Comparison (3rd Pass — Slice Modal Focus)

**Role:** Research specialist
**Status:** ✅ Complete — Findings documented, decision inbox updated
**Prior work:** Session brett-1 covered bambuddy architecture and Docker sidecar; separate session covered gcode toolpath preview. This is the dedicated slice UX pass.

### Research Questions Answered

**Q1: How does bambuddy's slice-job creation flow work?**

Five-step flow:
1. Right-click / toolbar "Slice" on a `.stl`, `.3mf`, or `.step` file in `FileManagerPage`
2. If multi-plate 3MF: `PlatePickerModal` appears first — shows plate thumbnails and per-plate filament requirements
3. `SliceModal` loads presets from `GET /slicer/presets` (3-tier unified response) and plate metadata
4. User selects Printer preset → Process preset → Bed type override (optional) → Filament preset(s) per AMS slot
5. Click "Slice" → `POST /library/files/{id}/slice` → returns `{job_id}` → modal polls `GET /slice-jobs/{job_id}` with live progress inline

Source: `frontend/src/components/SliceModal.tsx`, `backend/app/api/routes/library.py:3695`, `backend/app/api/routes/slice_jobs.py`

**Q2: What slicing options does bambuddy expose?**

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

| Format | bambuddy | PrintFarmer |
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

bambuddy gcode rejection rationale: Bambu printers require `.gcode.3mf` containers in network mode. Raw `.gcode` is accepted to the file system but rejected at print-time validation with an explicit educational error message. PrintFarmer does NOT have this constraint because its backends (Moonraker, PrusaLink, FlashForge, SDCP) all accept raw gcode natively — a genuine competitive differentiator for non-Bambu fleets.

### Key Learnings for PrintFarmer

1. **Preset-first is farm-correct.** bambuddy's choice to hide all parameter sliders behind a preset triplet prevents per-job drift — exactly what farm operators need. PrintFarmer's parameter-first UX encourages accidental variance across jobs. A "Quick Slice" entry point (printer preset → process preset → filament preset → submit) would be more farm-appropriate as the default.

2. **The 3-tier preset system is materially different from PrintFarmer's profile system.** bambuddy's `cloud > local > standard` cascade deduplicates presets by name, prefers user-customized local imports, and pulls from Bambu Cloud for stock profiles. PrintFarmer uses OrcaSlicer worker-cached profiles (no cloud tier, no user import tier). For non-Bambu farms this is fine, but it means profile management is operator-owned end-to-end.

3. **Bed type as a standalone, always-visible override is smart UX.** It's the one parameter that meaningfully differs between identical jobs (e.g., same model, same settings, but different surface on the bed). bambuddy surfaces it as a top-level dropdown, not buried in a process profile. PrintFarmer buries it.

4. **Smart filament auto-pick by type+color scoring reduces AMS job errors.** The type-match + color-proximity + tier scoring at `SliceModal.tsx:123-164` auto-selects the right filament for each slot based on 3MF plate metadata. PrintFarmer's filament picker is fully manual.

5. **PrintFarmer's raw gcode upload is a differentiator — protect it.** bambuddy can't accept raw gcode at all. PrintFarmer's `/gcode-files/upload` endpoint, configurable `allowedExtensions`, and multi-backend gcode dispatch are meaningful advantages for heterogeneous farms. Do not add Bambu-style rejection logic globally; gate it per-backend at send time if Bambu support is added.

6. **Bundle mode (.bbscfg) is an interesting "canonical config" pattern.** It lets farm operators lock an entire BambuStudio settings package (printer + process + filament names) and slice from it. Reduces configuration surface area. PrintFarmer analog would be an `.orca_printer` bundle import as slice source — which is adjacent to the already-planned bundle import feature (see 2026-04-17 session).

### Files Investigated

- `maziggy/bambuddy: frontend/src/components/SliceModal.tsx` (50.5 KB — full read + grep analysis)
- `maziggy/bambuddy: backend/app/schemas/slicer.py` (full read)
- `maziggy/bambuddy: backend/app/api/routes/library.py` (grepped: ALLOWED_EXTENSIONS, validate_print_file_upload, slice endpoint)
- `maziggy/bambuddy: backend/app/api/routes/slicer_presets.py` (grepped: 3-tier system, caching, compatible_printers)
- `maziggy/bambuddy: frontend/src/components/FileUploadModal.tsx` (full read)
- `PFarm1: src/Web/ReactApp/src/features/slicer/components/SlicerConfigModal.tsx` (full read)
- `PFarm1: src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx` (partial read — imports + structure)
- `PFarm1: src/Web/ReactApp/src/services/api.ts` (grepped: uploadGcodeFile, allowedExtensions)

### Decision Inbox

Created `.squad/decisions/inbox/brett-bambuddy-ux-comparison.md` with four decision recommendations:
1. Add Quick Slice modal (preset-first flow)
2. Evaluate `.bbscfg` / OrcaSlicer bundle import as canonical farm config
3. Preserve and document raw gcode upload as differentiator
4. Implement smart filament auto-selection by type+color proximity

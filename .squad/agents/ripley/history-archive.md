# Ripley History

## Core Context

Ripley is the frontend architect and API integration specialist. Key retained context:
- Owns printer-card UX, BedClearBanner behavior, and frontend cache/signal updates for auto-dispatch state.
- Prefers centralizing transport compatibility in `src/Web/ReactApp/src/services/` wrappers so product language can stay clean in hooks/components.
- Uses focused React integration tests to protect compact-card, banner, and SignalR merge seams where stale partial payloads can hide operator actions.
- Consolidates repeated status affordances into a single predictable surface when duplicate UI adds cognitive load.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history

**2026-03-25:** Finalized icon-only failure-detection badge behavior, removed redundant camera overlays, and documented the header-badge-as-single-source pattern.

**2026-03-25 to 2026-03-27:** Landed PendingReady compact-card fallback + live merge protections. Fixed BedClearBanner handling so failed bed-clear gates stay visible across stale bulk snapshots. Protected the live-update seam by preserving prior optional ready-gate detail when partial SignalR payloads omit it. Completed frontend transport alignment toward canonical auto-dispatch naming while preserving a safe adapter strategy. Failure detection is live monitoring, not historical audit—modal is the right interaction depth.

**2026-04-04:** Implemented P3 Send to Printer Modal and P5 Onboarding Profile Detection. Send to Printer: modal-based UX for printer selection and gcode delivery, integrated on completed jobs in SliceJobsPage. Onboarding: full-page banner pattern detecting empty profile state via `listExtended()`, routed to `/slicer/import-official` for profile import.

**2026-04-04:** Completed major frontend features: P3 Send to Printer Modal (8 tests passing), P5 Onboarding Detection (4 tests passing). Both features include proper TypeScript strict mode, accessibility WCAG 2.2 Level AA, and lint/build cleanup.

**Failure Detection Pattern:** Real-time monitoring state machine. Badge + modal for operators (badge shows compact state at-rest, modal shows richer session context during printing). No timeline view. Modal displays coverage source, snapshot URL, last scan, last outcome, auto-pause action, next step.

**Failed-Validation Model Download:** Added `GetByIdUnfilteredAsync()` pattern to `IModel3DFileRepository`. File/thumbnail downloads work for all models regardless of IsValid status. UI listings use filtered query.

**Cost & Analytics:** `TimePeriodFilterValue` is discriminated union for preset (7d/30d/90d/1yr/All) or custom date range. Cost hooks accept `(days?, startDate?, endDate?)`. Settings page is metadata-driven via backend `[AppSetting]` attributes.

**Printer Metadata:** AddPrinterModal, EditPrinterModal, and PrinterModelsCatalog integrated with Wattage (W) and Machine Hourly Rate ($) for cost tracking. Backend `PrinterDetailsDto` returns wattage/machineHourlyRate.


# Ripley History

## Core Context

Ripley is the frontend architect and API integration specialist. Key retained context:
- Owns printer-card UX, BedClearBanner behavior, and frontend cache/signal updates for auto-dispatch state.
- Prefers centralizing transport compatibility in `src/Web/ReactApp/src/services/` wrappers so product language can stay clean in hooks/components.
- Uses focused React integration tests to protect compact-card, banner, and SignalR merge seams where stale partial payloads can hide operator actions.
- Consolidates repeated status affordances into a single predictable surface when duplicate UI adds cognitive load.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history

**2026-03-25:** Finalized icon-only failure-detection badge behavior, removed redundant camera overlays, and documented the header-badge-as-single-source pattern.

**2026-03-25 to 2026-03-27:** Landed PendingReady compact-card fallback + live merge protections. Fixed BedClearBanner handling so failed bed-clear gates stay visible across stale bulk snapshots. Protected the live-update seam by preserving prior optional ready-gate detail when partial SignalR payloads omit it. Completed frontend transport alignment toward canonical auto-dispatch naming while preserving a safe adapter strategy. Failure detection is live monitoring, not historical audit—modal is the right interaction depth.

**2026-04-04:** Implemented P3 Send to Printer Modal and P5 Onboarding Profile Detection. Send to Printer: modal-based UX for printer selection and gcode delivery, integrated on completed jobs in SliceJobsPage. Onboarding: full-page banner pattern detecting empty profile state via `listExtended()`, routed to `/slicer/import-official` for profile import.

**2026-04-04:** Completed major frontend features: P3 Send to Printer Modal (8 tests passing), P5 Onboarding Detection (4 tests passing). Both features include proper TypeScript strict mode, accessibility WCAG 2.2 Level AA, and lint/build cleanup.

**Failure Detection Pattern:** Real-time monitoring state machine. Badge + modal for operators (badge shows compact state at-rest, modal shows richer session context during printing). No timeline view. Modal displays coverage source, snapshot URL, last scan, last outcome, auto-pause action, next step.

**Failed-Validation Model Download:** Added `GetByIdUnfilteredAsync()` pattern to `IModel3DFileRepository`. File/thumbnail downloads work for all models regardless of IsValid status. UI listings use filtered query.

**Cost & Analytics:** `TimePeriodFilterValue` is discriminated union for preset (7d/30d/90d/1yr/All) or custom date range. Cost hooks accept `(days?, startDate?, endDate?)`. Settings page is metadata-driven via backend `[AppSetting]` attributes.

**Printer Metadata:** AddPrinterModal, EditPrinterModal, and PrinterModelsCatalog integrated with Wattage (W) and Machine Hourly Rate ($) for cost tracking. Backend `PrinterDetailsDto` returns wattage/machineHourlyRate.


- ✅ Build: 0 errors (10.98s)
- ✅ Lint: 0 errors, 4 warnings (all pre-existing)
- ✅ Tests: 1710/1710 passing (11.26s)

**Files Changed:**
- `src/Web/ReactApp/package.json` — added fflate dependency
- `src/Web/ReactApp/src/features/slicer/orca/utils/orcaBundleExtractor.ts` — new utility
- `src/Web/ReactApp/src/features/slicer/orca/components/OrcaImportWizard.tsx` — updated file handling

**Learnings:**
- OrcaSlicer bundle files are standard ZIP archives — no custom format
- Preset type detection is reliable via discriminator fields in JSON
- Client-side extraction keeps backend simple and stateless
- `extractOrcaBundle()` returns same JSON format as direct upload — perfect API compatibility
- `isZipFile()` byte check prevents false positives from renamed files
- Error handling: ZIP extraction failures show user-friendly toast, malformed presets within ZIP are skipped with console.warn but don't break entire import
- `fflate.unzipSync()` is synchronous but fast enough for typical bundle sizes (11 files = instant)
- Backend never sees ZIP — frontend normalizes to the same `bundleJson` string format

### Session: Fix profile import for ZIP bundles on NewSliceJobPage
- `NewSliceJobPage.tsx` line ~1273 file input `accept` must include `.orca_printer,.orca_filament` alongside `.json`
- `handleProfileFileImport` must branch on file extension: ZIP bundles go through `extractOrcaBundle()`, plain JSON keeps existing `text()→JSON.parse()` path
- Extracted bundles contain `{ process: [...] }` — each entry is uploaded individually via `slicerProfilesService.uploadProfile()`
- Always reset `e.target.value = ''` after reading file so re-importing the same file triggers `onChange`
- Reuse `isZipFile()` + `extractOrcaBundle()` from `@/features/slicer/orca/utils/orcaBundleExtractor` — never re-implement ZIP handling


---

## Learnings

### Cut Model, Paint Supports, Paint Seam Toolbar Features (2026-04-22)

**Files Created:**
- `src/Web/ReactApp/src/features/slicer/components/CutPlaneOverlay.tsx` — 3D plane visualization overlay for cut model tool
  - Props: `isActive`, `position`, `orientation`, `onChange`, `onDeactivate`
  - Renders plane geometry + position slider + orientation selector
  - Integrated with SlicerBedVisualization via OverlayStack pattern

- `src/Web/ReactApp/src/features/slicer/components/FacePaintOverlay.tsx` — Face selection UI for paint supports/seam
  - Props: `isActive`, `toolMode` (supports|seam), `selectedFaces`, `onFaceSelect`, `onDeactivate`
  - Renders 3D mesh with face highlighting (cyan for supports, magenta for seam)
  - Dropdown for support type/seam alignment + density/strength sliders
  - Handles multi-face selection via Shift+Click

**Files Modified:**
- `src/Web/ReactApp/src/features/slicer/components/SlicerBedVisualization.tsx` — Added overlay rendering logic
  - OverlayStack pattern manages multiple overlays (cut plane + face paint) simultaneously
  - Each overlay is conditionally rendered based on toolbar state

- `src/Web/ReactApp/src/features/slicer/components/SlicerToolbar.tsx` — Added toolbar buttons for 3 new tools
  - Cut Model button toggles CutPlaneOverlay active state
  - Paint Supports button toggles FacePaintOverlay with mode=supports
  - Paint Seam button toggles FacePaintOverlay with mode=seam
  - Icons: cube-cut (cut), palette-advanced (supports), pen (seam)

- `src/Web/ReactApp/src/features/slicer/components/SlicerWorkspace.tsx` — Wired toolbar state to overlay components
  - Context provider manages toolbar tool state (which tool is active)
  - Passes state to SlicerBedVisualization which renders appropriate overlay

**Key Implementation Patterns:**

1. **Overlay State Management:**
   - Single `activeToolMode` in context tracks which tool is selected (null | cut | supports | seam)
   - Overlays check `activeToolMode` to determine if they should render
   - Toggling same tool twice deactivates it (toggle semantics)

2. **3D Interaction Model:**
   - Cut Model: Numeric slider for position, dropdown for plane orientation (XY/XZ/YZ)
   - Paint Supports/Seam: Click faces on 3D model to select, visual highlight feedback
   - All interactions debounced to avoid excessive re-renders

3. **Accessibility Considerations:**
   - All overlays keyboard-navigable (Tab moves between controls)
   - Slider controls have keyboard increment (arrow keys: ±0.1mm)
   - Face selection provides ARIA labels for screen readers
   - Visual focus indicators on all interactive elements

4. **Integration Points:**
   - SlicerToolbar dispatches tool state changes to SlicerWorkspace context
   - SlicerWorkspace passes active tool mode to SlicerBedVisualization as prop
   - SlicerBedVisualization renders CutPlaneOverlay or FacePaintOverlay based on mode
   - No API calls in overlays (UI-only preview features; actual operations handled separately)

**Testing Strategy:**
- Unit tests for overlay component state transitions (active/inactive)
- Integration tests for toolbar button clicks to overlay appearance
- Visual regression tests for overlay rendering on different bed geometries
- Accessibility tests for keyboard navigation and screen reader announcements

**Quality Metrics:**
- 888 lines added across 5 files
- 1734/1734 tests passing (no regressions)
- ESLint 0 errors, 0 new warnings
- TypeScript strict mode: 0 errors
- Accessibility: WCAG 2.2 Level AA verified (skip links, focus indicators, contrast)

**Known Limitations & Future Work:**
- Overlays render as separate 2D canvases (may need 3D mesh integration for production)
- Paint Supports/Seam doesn't validate against actual printer capabilities yet
- No persistence of cut planes or face selections between sessions
- Linked beads for follow-up:
  - PFarm1-eh3a: Profile reset bug (discovered during feature testing)
  - PFarm1-yigr: Profile filtering enhancement (UX refinement)
  - PFarm1-issr: Multi-select import feature request

**Reusable Patterns:**
- OverlayStack for managing multiple conditional overlays in single visualization
- useSlicerToolState hook pattern for modal-like tool activation (singleton per workspace)
- Slider + dropdown combo pattern for numeric + categorical parameters
- 3D face selection pattern (click detection + visual feedback)

## 2026-07-31: 4 Frontend Beads — Blob Leak, Profile Reset, Filtering, Multi-Select

**Role:** Frontend / Slicer UX  
**Status:** ✅ COMPLETE — All 4 beads fixed, build+lint+tests clean

### Bead PFarm1-eidj: Blob URL Memory Leak Fix
- `geometryToBlobUrl()` now tracks created URLs via `useRef<Set<string>>`
- Old blob URLs revoked when models replaced by cut operations
- All tracked URLs cleaned up on component unmount via `useEffect` return
- **File:** `SlicerWorkspace.tsx`

### Bead PFarm1-eh3a: Machine Profile Reset Fix
- Auto-select effect now checks both system (`machineProfilesData`) AND custom (`customMachineProfiles`) for current selection validity
- Previously: selecting a custom/imported profile triggered the effect to reset to first system profile because custom profiles weren't in `machineProfilesData`
- **File:** `NewSliceJobPage.tsx` — useEffect at machine profile auto-select

### Bead PFarm1-yigr: Filter Imported Profiles by Printer
- Custom machine profiles filtered by selected printer using rawJson `printer_model` field + fuzzy name matching
- Custom filament/process profiles filtered by `compatible_printers` in rawJson when available
- Profiles without matching metadata shown as fallback (safe default)
- **File:** `NewSliceJobPage.tsx` — `customMachineProfiles`, `customFilamentProfiles`, `customProcessProfiles` useMemo blocks

### Bead PFarm1-issr: Multi-Select File Import
- All 3 hidden file inputs (`machine`, `filament`, `process`) now have `multiple` attribute
- Import handlers iterate over `FileList` instead of accessing `files[0]`
- Per-file error handling with aggregate success/fail toast
- **File:** `NewSliceJobPage.tsx` — `handleMachineFileImport`, `handleFilamentFileImport`, `handleProfileFileImport`

**Validation:** ✅ Build 0 errors (7.99s), ESLint 0 errors, 1734/1734 tests pass

## Learnings

- **Blob URL lifecycle:** `URL.createObjectURL()` allocates memory that persists until explicitly revoked or page unloads. In SPAs, components can unmount without page unload, so always track and revoke.
- **Custom vs system profile split:** Custom profiles from `listCustomProfiles()` are NOT in the system profile queries (`machineProfilesForModel`). Any selection validation must check both data sources.
- **OrcaSlicer rawJson metadata:** Custom profiles store the original OrcaSlicer JSON in `rawJson`. Key fields: `printer_model` for machine profiles, `compatible_printers` array for filament/process profiles.
- **Multi-file import pattern:** Use `Array.from(files)` to iterate FileList, accumulate counts, and show aggregate results. Per-file `try/catch` prevents one bad file from blocking the rest.
- **Filament settings types (PFarm1-pysq.2):** Created filamentSettingsTypes.ts with 108 OrcaSlicer filament settings using native snake_case keys from orcaSettingsMetadata.json. Compound (per-extruder) fields typed as string; non-compound use number/boolean/string. Mode map marks 12 core keys as simple, rest as advanced. Category map matches OrcaSlicer Tab.cpp tab structure. Defaults use typical PLA values.
- **coFloats rendering (PFarm1-suv8):** `coType: "coFloats"` settings store comma-separated per-extruder values (e.g. "500,200"). Added `'coFloats'` control type to `resolveControlType` and compound rendering in `MetadataSettingRow` that splits on commas, renders per-extruder inputs, and joins back on change. Single values still render as normal number inputs. The `metadata-editors.test.ts` VALID_CONTROLS set must include any new control types.

### 2025-01-12: Multi-axis Cut + Drag Interference Fix

**Issue 1: Drag-to-move interfering with Cut/Paint tools**
- Root cause: `draggable` prop on `STLModel` and `PrebuiltSTLModel` only checked `layFlatMode`, `assemblyViewActive`, and `transformMode`, but not tool modes
- Fix: Extended draggable condition to also be false when any modifying tool is active:
  - `cutMode`, `supportPaintMode`, `seamPaintMode`, `colorPaintMode`, `fuzzySkinPaintMode`, `measureMode`, `textPlacementMode`
- Both `STLModel` and `PrebuiltSTLModel` components updated in `SlicerBedVisualization.tsx`

**Issue 2: Multi-axis cut control (OrcaSlicer-style)**
- Completely rewrote `CutPlaneOverlay.tsx` to support X/Y/Z axis cuts
- Key changes:
  - Added `CutAxis` type: `'x' | 'y' | 'z'`
  - Generalized `splitGeometryAtPlane` to accept axis parameter
  - Updated `classifyPoint` to work with any axis value
  - Plane orientation:
    - Z: rotation (0,0,0) - horizontal (unchanged)
    - X: rotation (0, π/2, 0) - vertical perpendicular to X
    - Y: rotation (π/2, 0, 0) - vertical perpendicular to Y
  - Added red sphere drag handle (3mm radius) at plane center
  - Drag cursor changes based on axis: `ns-resize` for Z, `ew-resize` for X/Y
  
**New UI Panel (matching OrcaSlicer):**
- Mode selector: "Planar" (static, disabled)
- Build Volume display: shows bed dimensions
- Cut position: axis dropdown (X/Y/Z) + numeric input (mm) + reset button
- Action buttons: "Add connectors" (disabled/coming soon), "Reset cut"
- After cut section:
  - Upper part: Keep ✓, Place on cut ✓, Flip □ (with teal color indicator)
  - Lower part: Keep ✓, Place on cut □, Flip □ (with purple color indicator)
  - Cut to parts □
- "Perform cut" button (primary)

**Interface Changes:**
- Updated `CutPlaneOverlayProps` to accept `bedConfig: BedConfig`
- Added `CutOptions` interface with all cut configuration options
- Updated `onCutComplete` callback signature to accept optional `CutOptions`
- Updated `SlicerBedVisualizationProps.onCutComplete` type to match
- Modified `handleCutComplete` in `SlicerWorkspace.tsx` to:
  - Accept optional `CutOptions` parameter
  - Respect `keepUpper`/`keepLower` options (only add models that should be kept)
  - Added TODO stubs for `placeOnCut`, `flip`, and `cutToParts` options

**Implementation Details:**
- Reset cutHeight to 0.5 (center) when axis changes
- Compute axis-specific bounds from model bounding box
- Handle sphere moved to child of plane mesh for correct transformation
- Panel positioned top-right at fixed world coordinates (not attached to plane)
- Used Button components (not raw buttons) to satisfy ESLint rules
- Avoided raw `<select>` elements (used styled button for axis toggle instead)

**Patterns:**
- R3F `useFrame` for syncing plane/handle position with model transform
- `useMemo` for expensive bounding box calculations
- `useCallback` for event handlers to prevent re-renders
- Ref-based dragging state (`isDraggingRef`) to avoid render loops
- Toast notifications for user feedback

**Validation:**
- TypeScript: ✅ No errors
- ESLint: ✅ No errors or warnings
- Tests: Pre-existing failures unrelated to our changes (import resolution issues in test setup)

**Files Changed:**
- `src/Web/ReactApp/src/features/slicer/components/viewer/CutPlaneOverlay.tsx` (complete rewrite, ~580 lines)
- `src/Web/ReactApp/src/features/slicer/components/viewer/SlicerBedVisualization.tsx` (draggable guards, bedConfig prop)
- `src/Web/ReactApp/src/features/slicer/components/viewer/SlicerWorkspace.tsx` (handleCutComplete signature)

---

## PFarm1-4ex2 — Hollow cut detection (2026-04-25)

**Learnings:**
- The cut tool already handles disjoint cap loops correctly: `orderCapEdges` returns `THREE.Vector3[][]` and the consumer iterates each loop through `earClipTriangulate`. Only nested loops (true holes — e.g. the inner ring of a hollow tube cross-section) remain a v1 limitation.
- AABB containment in the cap's 2D projection plane is a cheap, no-import heuristic for detecting nested loops without point-in-polygon tests.
- Full hole bridging would require either constrained Delaunay triangulation or ear-clipping with bridge edges connecting outer/inner loops — a much larger change deferred from this v1 fix.

---

## Slicer page UI cohesion polish (2026-07-21)

**Learnings:**
- SlicerSelector used `p-4 mb-3` while all other section cards use `p-3 mb-2`. Standardized.
- Emoji fallbacks (🔪, 🖨️) replaced with SVG MDI icons (`GearIcon`, `PrinterIcon`) for consistency and rendering reliability.
- Empty/loading states in NewSliceJobPage had `italic` on instructional text ("Select a machine profile…") which should only apply to loading/no-data messages.
- Online status dot in PrinterSlicerSelector is very small (`w-2 h-2`); adding `ring-1 ring-{color}/30` provides a subtle glow that increases visibility without changing size.
- Process section card was missing `space-y-2` that all other sections had — easy to miss in a 2400-line file.
- Select placeholder patterns were inconsistent (`-- Select X --` vs short form). Standardized to `Select x...` / `Loading...`.

---

## Spoolman Filter Options Endpoint (2026-05-03)

**Status:** ✅ DELIVERED

**Summary:**
Added GET `/api/spoolman/filter-options` endpoint to expose filter definitions from Spoolman, enabling dynamic filter option population in SpoolsTab instead of relying on hardcoded values.

**Backend Changes:**
- Endpoint: `GET /api/spoolman/filter-options` → `IEnumerable<FilterOptionDto>`
- Response DTO: `FilterOptionDto { Id: string, Name: string }`
- Serialization: `JsonPropertyName` for camelCase naming
- Integrated with Spoolman service layer

**Frontend Changes:**
- Hook: `useSpoolFilterOptions()` with TanStack Query caching
- Integration: SpoolsTab loads options on mount via `useEffect`
- Error handling: Toast notifications for API failures
- Type safety: Full TypeScript coverage

**Validation:**
- Backend: All tests passing
- Frontend: 1734/1734 tests passed, 12 skipped
- Linting: No new warnings
- Serialization: camelCase validated

### Session: Buddy Camera IP frontend field (PFarm1-873d)

**Task:** Add `buddyCameraIp` field to the printer edit UI for PrusaLink printers.

**Changes:**
- `types/api.ts`: Added `buddyCameraIp?: string` to `UpdatePrinterDto` and `PrinterDetails`
- `EditPrinterModal.tsx`: Added field to form initialization, dirty tracking, and Camera Configuration section
- Field is conditional on `formData.backend === PrinterBackend.PrusaLink`
- Shows derived RTSP URL preview (`rtsp://{ip}:554/live/`) when an IP is entered

**Decisions:**
- Not added to `CreatePrinterDto` or `AddPrinterModal` — backend `CreatePrinterFromDiscoveryDto` doesn't have the field; set via edit after creation
- Used `handleInputChange` pattern consistent with existing camera URL fields
- Placed inside the Camera Configuration section, after the snapshot URL field


## 2026-05-12 Buddy Camera IP Field — Session Complete

**Task:** PFarm1-873d — Implement BuddyCameraIp field in EditPrinterModal  
**Status:** ✅ CLOSED  
**Timestamp:** 2026-05-12T19:20:00Z

**Changes:**
- `types/api.ts`: Added `buddyCameraIp?: string` to UpdatePrinterDto and PrinterDetails
- `EditPrinterModal.tsx`: Added Camera Configuration section with buddyCameraIp field, conditional on PrusaLink backend
- RTSP URL preview: Shows `rtsp://{ip}:554/live/` when IP is entered
- Form state: Integrated with dirty tracking and form initialization

**Validation:**
- ✅ Build passing
- ✅ Lint passing  
- ✅ No new warnings

**Outcome:** BuddyCameraIp field ready for integration with backend camera auto-discovery service.

**[Older entries archived on 2026-05-12 — see history.md for recent updates]**


---

## [Archived 2026-05-21 by Scribe — full ## Learnings section before Phase 1 closeout summarization]

## Learnings

- **Blob URL lifecycle:** `URL.createObjectURL()` allocates memory that persists until explicitly revoked or page unloads. In SPAs, components can unmount without page unload, so always track and revoke.
- **Custom vs system profile split:** Custom profiles from `listCustomProfiles()` are NOT in the system profile queries (`machineProfilesForModel`). Any selection validation must check both data sources.
- **OrcaSlicer rawJson metadata:** Custom profiles store the original OrcaSlicer JSON in `rawJson`. Key fields: `printer_model` for machine profiles, `compatible_printers` array for filament/process profiles.
- **Multi-file import pattern:** Use `Array.from(files)` to iterate FileList, accumulate counts, and show aggregate results. Per-file `try/catch` prevents one bad file from blocking the rest.
- **Filament settings types (PFarm1-pysq.2):** Created filamentSettingsTypes.ts with 108 OrcaSlicer filament settings using native snake_case keys from orcaSettingsMetadata.json. Compound (per-extruder) fields typed as string; non-compound use number/boolean/string. Mode map marks 12 core keys as simple, rest as advanced. Category map matches OrcaSlicer Tab.cpp tab structure. Defaults use typical PLA values.
- **coFloats rendering (PFarm1-suv8):** `coType: "coFloats"` settings store comma-separated per-extruder values (e.g. "500,200"). Added `'coFloats'` control type to `resolveControlType` and compound rendering in `MetadataSettingRow` that splits on commas, renders per-extruder inputs, and joins back on change. Single values still render as normal number inputs. The `metadata-editors.test.ts` VALID_CONTROLS set must include any new control types.

### 2025-01-12: Multi-axis Cut + Drag Interference Fix

**Issue 1: Drag-to-move interfering with Cut/Paint tools**
- Root cause: `draggable` prop on `STLModel` and `PrebuiltSTLModel` only checked `layFlatMode`, `assemblyViewActive`, and `transformMode`, but not tool modes
- Fix: Extended draggable condition to also be false when any modifying tool is active:
  - `cutMode`, `supportPaintMode`, `seamPaintMode`, `colorPaintMode`, `fuzzySkinPaintMode`, `measureMode`, `textPlacementMode`
- Both `STLModel` and `PrebuiltSTLModel` components updated in `SlicerBedVisualization.tsx`

**Issue 2: Multi-axis cut control (OrcaSlicer-style)**
- Completely rewrote `CutPlaneOverlay.tsx` to support X/Y/Z axis cuts
- Key changes:
  - Added `CutAxis` type: `'x' | 'y' | 'z'`
  - Generalized `splitGeometryAtPlane` to accept axis parameter
  - Updated `classifyPoint` to work with any axis value
  - Plane orientation:
    - Z: rotation (0,0,0) - horizontal (unchanged)
    - X: rotation (0, π/2, 0) - vertical perpendicular to X
    - Y: rotation (π/2, 0, 0) - vertical perpendicular to Y
  - Added red sphere drag handle (3mm radius) at plane center
  - Drag cursor changes based on axis: `ns-resize` for Z, `ew-resize` for X/Y
  
**New UI Panel (matching OrcaSlicer):**
- Mode selector: "Planar" (static, disabled)
- Build Volume display: shows bed dimensions
- Cut position: axis dropdown (X/Y/Z) + numeric input (mm) + reset button
- Action buttons: "Add connectors" (disabled/coming soon), "Reset cut"
- After cut section:
  - Upper part: Keep ✓, Place on cut ✓, Flip □ (with teal color indicator)
  - Lower part: Keep ✓, Place on cut □, Flip □ (with purple color indicator)
  - Cut to parts □
- "Perform cut" button (primary)

**Interface Changes:**
- Updated `CutPlaneOverlayProps` to accept `bedConfig: BedConfig`
- Added `CutOptions` interface with all cut configuration options
- Updated `onCutComplete` callback signature to accept optional `CutOptions`
- Updated `SlicerBedVisualizationProps.onCutComplete` type to match
- Modified `handleCutComplete` in `SlicerWorkspace.tsx` to:
  - Accept optional `CutOptions` parameter
  - Respect `keepUpper`/`keepLower` options (only add models that should be kept)
  - Added TODO stubs for `placeOnCut`, `flip`, and `cutToParts` options

**Implementation Details:**
- Reset cutHeight to 0.5 (center) when axis changes
- Compute axis-specific bounds from model bounding box
- Handle sphere moved to child of plane mesh for correct transformation
- Panel positioned top-right at fixed world coordinates (not attached to plane)
- Used Button components (not raw buttons) to satisfy ESLint rules
- Avoided raw `<select>` elements (used styled button for axis toggle instead)

**Patterns:**
- R3F `useFrame` for syncing plane/handle position with model transform
- `useMemo` for expensive bounding box calculations
- `useCallback` for event handlers to prevent re-renders
- Ref-based dragging state (`isDraggingRef`) to avoid render loops
- Toast notifications for user feedback

**Validation:**
- TypeScript: ✅ No errors
- ESLint: ✅ No errors or warnings
- Tests: Pre-existing failures unrelated to our changes (import resolution issues in test setup)

**Files Changed:**
- `src/Web/ReactApp/src/features/slicer/components/viewer/CutPlaneOverlay.tsx` (complete rewrite, ~580 lines)
- `src/Web/ReactApp/src/features/slicer/components/viewer/SlicerBedVisualization.tsx` (draggable guards, bedConfig prop)
- `src/Web/ReactApp/src/features/slicer/components/viewer/SlicerWorkspace.tsx` (handleCutComplete signature)

---

## PFarm1-4ex2 — Hollow cut detection (2026-04-25)

**Learnings:**
- The cut tool already handles disjoint cap loops correctly: `orderCapEdges` returns `THREE.Vector3[][]` and the consumer iterates each loop through `earClipTriangulate`. Only nested loops (true holes — e.g. the inner ring of a hollow tube cross-section) remain a v1 limitation.
- AABB containment in the cap's 2D projection plane is a cheap, no-import heuristic for detecting nested loops without point-in-polygon tests.
- Full hole bridging would require either constrained Delaunay triangulation or ear-clipping with bridge edges connecting outer/inner loops — a much larger change deferred from this v1 fix.

---

## Slicer page UI cohesion polish (2026-07-21)

**Learnings:**
- SlicerSelector used `p-4 mb-3` while all other section cards use `p-3 mb-2`. Standardized.
- Emoji fallbacks (🔪, 🖨️) replaced with SVG MDI icons (`GearIcon`, `PrinterIcon`) for consistency and rendering reliability.
- Empty/loading states in NewSliceJobPage had `italic` on instructional text ("Select a machine profile…") which should only apply to loading/no-data messages.
- Online status dot in PrinterSlicerSelector is very small (`w-2 h-2`); adding `ring-1 ring-{color}/30` provides a subtle glow that increases visibility without changing size.
- Process section card was missing `space-y-2` that all other sections had — easy to miss in a 2400-line file.
- Select placeholder patterns were inconsistent (`-- Select X --` vs short form). Standardized to `Select x...` / `Loading...`.

---

## Spoolman Filter Options Endpoint (2026-05-03)

**Status:** ✅ DELIVERED

**Summary:**
Added GET `/api/spoolman/filter-options` endpoint to expose filter definitions from Spoolman, enabling dynamic filter option population in SpoolsTab instead of relying on hardcoded values.

**Backend Changes:**
- Endpoint: `GET /api/spoolman/filter-options` → `IEnumerable<FilterOptionDto>`
- Response DTO: `FilterOptionDto { Id: string, Name: string }`
- Serialization: `JsonPropertyName` for camelCase naming
- Integrated with Spoolman service layer

**Frontend Changes:**
- Hook: `useSpoolFilterOptions()` with TanStack Query caching
- Integration: SpoolsTab loads options on mount via `useEffect`
- Error handling: Toast notifications for API failures
- Type safety: Full TypeScript coverage

**Validation:**
- Backend: All tests passing
- Frontend: 1734/1734 tests passed, 12 skipped
- Linting: No new warnings
- Serialization: camelCase validated

### Session: Buddy Camera IP frontend field (PFarm1-873d)

**Task:** Add `buddyCameraIp` field to the printer edit UI for PrusaLink printers.

**Changes:**
- `types/api.ts`: Added `buddyCameraIp?: string` to `UpdatePrinterDto` and `PrinterDetails`
- `EditPrinterModal.tsx`: Added field to form initialization, dirty tracking, and Camera Configuration section
- Field is conditional on `formData.backend === PrinterBackend.PrusaLink`
- Shows derived RTSP URL preview (`rtsp://{ip}:554/live/`) when an IP is entered

**Decisions:**
- Not added to `CreatePrinterDto` or `AddPrinterModal` — backend `CreatePrinterFromDiscoveryDto` doesn't have the field; set via edit after creation
- Used `handleInputChange` pattern consistent with existing camera URL fields
- Placed inside the Camera Configuration section, after the snapshot URL field


## 2026-05-12 Buddy Camera IP Field — Session Complete

**Task:** PFarm1-873d — Implement BuddyCameraIp field in EditPrinterModal  
**Status:** ✅ CLOSED  
**Timestamp:** 2026-05-12T19:20:00Z

**Changes:**
- `types/api.ts`: Added `buddyCameraIp?: string` to UpdatePrinterDto and PrinterDetails
- `EditPrinterModal.tsx`: Added Camera Configuration section with buddyCameraIp field, conditional on PrusaLink backend
- RTSP URL preview: Shows `rtsp://{ip}:554/live/` when IP is entered
- Form state: Integrated with dirty tracking and form initialization

**Validation:**
- ✅ Build passing
- ✅ Lint passing  
- ✅ No new warnings

**Outcome:** BuddyCameraIp field ready for integration with backend camera auto-discovery service.

- 2026-05-20: Assigned mobile controls v1 spike #279 — validate backend print-state enforcement (block jog/preheat/home while printing or paused). Trust `PrinterBackendCapabilities.supportsTemperatureControl` flag per locked decision. See decisions.md "Mobile API Drift + Basic Printer Controls v1".

## 2026-05-21: Spike #279 — Server-side guards for /temps and /move

**Role:** Tester/QA (read-only investigation)
**Verdict:** **(c) NOT trust backend** — iOS client must fully gate /temps and /move client-side.

**Key findings:**
- Controller + service + plugins all forward `/temps` and `/move` blindly. No `Printer.Status` check anywhere.
- All failures collapse to `bool false` → HTTP 404, masking real causes (offline / capability missing / firmware 409 / exception).
- Per-backend: Moonraker accepts mid-print silently; PrusaLink/OctoPrint firmware 409s but result is lost; FlashForge has no movement capability; SDCP implements neither.
- Zero test coverage on either route (`FNDA:0` in coverage report).

**Outputs:**
- Comment: https://github.com/OlyForge3D/PrintFarmer/issues/279#issuecomment-4509132269
- Follow-up: #290 (P0, labels `squad,squad:ripley,type:bug,area:api,priority:P0`)
- Decision: `.squad/decisions/inbox/ripley-279-server-guard-verdict.md`

**For Hudson (#284-#286):** disable temp/move controls when status ∈ `{Printing, Pausing, Paused, Resuming, Cancelling, Heating}`. Re-evaluate on every SignalR `printerupdated`. Moonraker has no firmware safety net — consider a small operator warning even when status looks idle.
- 2026-05-21: Ralph Round 1 (Phase 0) completed — see `.squad/log/2026-05-21T09-00-00Z-ralph-round-1-phase-0.md`.


- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291, #292, #293, #294, #295, #296, #297, #298). See `.squad/log/2026-05-21T08-15-00Z-ralph-rounds-2-5-phase-1-complete.md`. Phase 2 launching (#284 preheat, #285 home, #286 jog).

---

## 2026-05-21 — Mobile Controls Phase 1 (archived from main history)

_Detailed entries moved from ripley/history.md for space on 2026-05-26._

### 2026-05-21T09:38-07:00 — Issue #302 AMS slot count investigation (no code change)

**Symptom:** Bambu printer with AMS shows AMS panel "3/3 loaded" + duplicate "Spools — Assign spools to each toolhead" list (T0–T3) below.

**Root cause (backend):** `src/infra/Services/Printers/PrintersService.cs:2959` — `for (int i = 1; i < mmuGateCount; i++)`. Default `mmuGateCount=4` ⇒ creates only 3 MmuGate toolheads at indices 1,2,3. T0 stays Physical. The test `MmuGateAutoCreationTests.CreatePrinter_MultiMaterialTrue_CreatesThreeMmuGateToolheads` codifies the wrong count, so it's the seeding semantics that need re-deciding (total gates vs. total toolheads), not just a mechanical loop fix.

**Frontend is data-driven on the count.** `AmsSlotVisualization.tsx` renders `unit.slots.length` from props; `BAMBU_AMS_UNIT_SIZE = 4` is the chunk size for splitting >5 gates into multiple AMS units, not a slot cap. There is no hardcoded "3" anywhere in the renderer.

**Duplicate "Spools" list (frontend follow-up):** `PrinterDetailsSidebar.tsx:1175-1247` only hides the lower section when `displayPrinter.mmuStatus.gates` has live gates (Klipper Happy-Hare path). Bambu data flows through `printerDetails.toolheads`, not `mmuStatus`, so the dedup guard never fires for Bambu. ~10-line fix to also hide when `printerDetails.toolheads` contains MmuGate entries — but it must land **after** the backend slot-count fix or the user loses any UI for the missing 4th slot.

### 2026-05-21 — Issue #302 backend root cause
- AMS slot rendering bug: `CreateMmuVirtualToolheads` loop is `i < mmuGateCount` → produces N-1 gates. Fix is loop bound, not frontend.
- Triage discipline: posted analysis comment on #302, tagged `area:backend`, handed off to Lambert without implementing.
- Frontend dedup of lower 'Spools' section is still queued (after backend lands).

### 2026-05-21 — Issue #302 frontend dedup shipped (PR #305)
- Removed duplicate lower "Spools" section in `AmsSlotVisualization` once Lambert's backend gate-count fix (PR #303) was on `development`.
- PR #305 merged; issue #302 CLOSED end-to-end (backend + frontend both landed).

### 2026-05-21 — Picked up bug #309 (team update)
- Mobile-controls v1 board cleared this round (Hudson #289 PR #306, Lambert #290 PR #308, #276 verified shipped).
- User filed bug #309: spaghetti detection shield in web app says "printer not printing" on printers that ARE actively printing. State-detection mismatch on the shield component. Investigation underway — own this through fix.

### 2026-05-21T23:14Z — Issue #309 spaghetti shield triage (backend handoff, no code change)
- **Symptom:** Shield/modal says "Printer is not actively printing." on actively-printing printers (two reproducing).
- **Root cause:** Backend, not frontend. `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs:107-117` (`EvaluateMonitoringWindow`) requires `status.State == "Printing"` (case-insensitive, exact); any other normalized state (`Paused`, `Heating`, `Resuming`, `Pausing`, ...) returns the literal `NotPrintingReason = "Printer is not actively printing."` defined at line 36. Inconsistent with the busy-state set established in PR #308 / issue #290 (`PrinterControlGate.IsBusyForControl({Printing, Pausing, Paused, Resuming, Cancelling, Heating})`).
- **Frontend pass-through confirmed:** `usePrinterFailureDetectionStatus` is a pure DTO consumer; `FailureDetectionMonitoringBadge` and `failureDetectionStatus.ts` render backend `state`/`reason` verbatim. No client-side predicate to fix.
- **Action:** Posted analysis to #309 (comment 4513509761), added `area:backend` label, handed to Lambert. No PR, no worktree.
- **Lesson:** When a UI string is the exact literal of a backend `const string`, the frontend is almost certainly a passive renderer — grep the literal against `*.cs` before diving into React code. Same triage discipline as #302.


## Summarized History
Detailed work entries from 2026-03-25 through 2026-05-25 archived in history-archive.md. Key themes: compact-card UX, live update seams, profile management, cost tracking, 3D visualization tools.

**2026-03-25:** Finalized icon-only failure-detection badge behavior, removed redundant camera overlays, and documented the header-badge-as-single-source pattern.

**2026-03-25 to 2026-03-27:** Landed PendingReady compact-card fallback + live merge protections. Fixed BedClearBanner handling so failed bed-clear gates stay visible across stale bulk snapshots. Protected the live-update seam by preserving prior optional ready-gate detail when partial SignalR payloads omit it. Completed frontend transport alignment toward canonical auto-dispatch naming while preserving a safe adapter strategy. Failure detection is live monitoring, not historical audit—modal is the right interaction depth.

**2026-04-04:** Implemented P3 Send to Printer Modal and P5 Onboarding Profile Detection. Send to Printer: modal-based UX for printer selection and gcode delivery, integrated on completed jobs in SliceJobsPage. Onboarding: full-page banner pattern detecting empty profile state via `listExtended()`, routed to `/slicer/import-official` for profile import.

**2026-04-04:** Completed major frontend features: P3 Send to Printer Modal (8 tests passing), P5 Onboarding Detection (4 tests passing). Both features include proper TypeScript strict mode, accessibility WCAG 2.2 Level AA, and lint/build cleanup.

**Failure Detection Pattern:** Real-time monitoring state machine. Badge + modal for operators (badge shows compact state at-rest, modal shows richer session context during printing). No timeline view. Modal displays coverage source, snapshot URL, last scan, last outcome, auto-pause action, next step.

**Failed-Validation Model Download:** Added `GetByIdUnfilteredAsync()` pattern to `IModel3DFileRepository`. File/thumbnail downloads work for all models regardless of IsValid status. UI listings use filtered query.

**Cost & Analytics:** `TimePeriodFilterValue` is discriminated union for preset (7d/30d/90d/1yr/All) or custom date range. Cost hooks accept `(days?, startDate?, endDate?)`. Settings page is metadata-driven via backend `[AppSetting]` attributes.

**Printer Metadata:** AddPrinterModal, EditPrinterModal, and PrinterModelsCatalog integrated with Wattage (W) and Machine Hourly Rate ($) for cost tracking. Backend `PrinterDetailsDto` returns wattage/machineHourlyRate.


- No persistence of cut planes or face selections between sessions
- Linked beads for follow-up:
  - PFarm1-eh3a: Profile reset bug (discovered during feature testing)
  - PFarm1-yigr: Profile filtering enhancement (UX refinement)
  - PFarm1-issr: Multi-select import feature request

**Reusable Patterns:**
- OverlayStack for managing multiple conditional overlays in single visualization
- useSlicerToolState hook pattern for modal-like tool activation (singleton per workspace)
- Slider + dropdown combo pattern for numeric + categorical parameters
- 3D face selection pattern (click detection + visual feedback)

## Archived Detailed Work

**2026 Slicer UX & Profile Management:** 4 beads (blob-leak memory fix, profile-reset validation, imported-profile filtering by printer, multi-select file import). All COMPLETE, build+lint+tests clean. Profiles now filter by `compatible_printers` in rawJson and fuzzy printer-model match. See `history-archive.md` for full details. Camera management UI + login audit frontend (23 tests) completed 2026-05-26.


## Recent

_Last 5 most-recent learnings preserved from full history. Older entries are in `history-archive.md` (archived 2026-05-21 by Scribe, updated 2026-05-26)._

- **2026-05-21 — Mobile spike #279 verdict (c).** iOS client must fully gate `/temps` and `/move` client-side based on cached `Printer.Status`. Backend forwards both routes blindly across all plugins (Moonraker silently accepts mid-print; PrusaLink/OctoPrint firmware 409s collapse to bool→404; FlashForge has no movement; SDCP implements neither). Zero test coverage on either route. For Hudson (#284–#286): disable temp/move when status ∈ `{Printing, Pausing, Paused, Resuming, Cancelling, Heating}`; re-evaluate on every SignalR `printerupdated`. Follow-up #290 reassigned to Dallas.
- **2026-05-12 — Buddy Camera IP frontend field (PFarm1-873d).** `buddyCameraIp?: string` added to `UpdatePrinterDto` and `PrinterDetails` types. New "Camera Configuration" section in `EditPrinterModal.tsx` rendered conditionally on `formData.backend === PrinterBackend.PrusaLink`. Shows derived `rtsp://{ip}:554/live/` preview when an IP is entered. Field is edit-only (not on `AddPrinterModal`/`CreatePrinterDto`); set after creation via the edit modal. Used `handleInputChange` for dirty tracking, consistent with existing camera URL fields.
- **2026-05-03 — Spoolman filter-options endpoint.** `GET /api/spoolman/filter-options` returns `IEnumerable<FilterOptionDto>` (`{ id, name }`, camelCase). Frontend `useSpoolFilterOptions()` hook uses TanStack Query caching; SpoolsTab loads options on mount via `useEffect` with toast-based error handling. Replaces hardcoded filter values.
- **2026-04-25 — Hollow cut detection (PFarm1-4ex2).** Cut tool already handles disjoint cap loops correctly: `orderCapEdges` returns `THREE.Vector3[][]` and the consumer iterates each loop through `earClipTriangulate`. Only nested loops (true holes — inner ring of a hollow tube cross-section) remain v1 limitation. AABB containment in the cap's 2D projection plane is a cheap heuristic for detecting nested loops without point-in-polygon tests. Full hole bridging (constrained Delaunay or ear-clipping with bridge edges) deferred from v1 fix.
- **2026-01-12 — Multi-axis Cut + Drag interference fix.** `draggable` prop on `STLModel` / `PrebuiltSTLModel` extended to be false when ANY tool mode is active (`cutMode`, `supportPaintMode`, `seamPaintMode`, `colorPaintMode`, `fuzzySkinPaintMode`, `measureMode`, `textPlacementMode`). Rewrote `CutPlaneOverlay.tsx` to support X/Y/Z axis cuts with axis-specific plane orientation (Z: rot(0,0,0), X: rot(0,π/2,0), Y: rot(π/2,0,0)). Red sphere drag handle (3mm radius) at plane center; cursor `ns-resize` for Z, `ew-resize` for X/Y. New OrcaSlicer-style UI panel: Mode selector, build volume display, cut position (axis dropdown + numeric mm input + reset), Add connectors (disabled), After-cut options (Keep upper/lower, Place on cut, Flip, Cut to parts), Perform cut button. R3F `useFrame` syncs plane/handle position with model transform.

---

## 2026-05-26 — Camera Management UI + Login Audit Frontend

### Camera endpoint detection integration
- Camera card management now supports Edit/Delete actions (admin-only).
- Edit modal allows selecting an associated printer and detecting endpoints.
- Detected `streamUrl` and `snapshotUrl` auto-populate from backend probe.
- Validation: build, lint, and targeted camera tests passed.

### Login audit log UI (23 tests)
- Built `/admin/security/login-audit` page using project Tailwind components.
- Features: date-range filter, username substring search, success/failure toggle, pagination.
- Navigation: added "Security" section header as peer to "Settings" in admin nav.
- API integration: direct `apiClient.get<T>()` in `securityAuditService.ts`.
- Pushed to `development`; awaiting E2E validation.

## Learnings

- 2026-06-01 — Ambient System Pulse pill lives in `src/Web/ReactApp/src/features/system/components/SystemPulsePill.tsx` and is mounted from `src/Web/ReactApp/src/common/components/Layout.tsx`. It stays hidden unless `hasRole('farm_admin')` is true and `apiClient.getSystemInfo()` returns data; the top-bar panel traps focus while open, closes on `Escape`, and restores focus to the trigger.
- 2026-05-26 — Camera management UI polish: camera management lives in `src/Web/ReactApp/src/features/cameras/pages/CamerasPage.tsx`, `src/Web/ReactApp/src/features/cameras/components/CameraManagementPanel.tsx`, and `src/Web/ReactApp/src/features/cameras/components/EditCameraModal.tsx`; printer-card camera rendering is in `src/Web/ReactApp/src/features/printers/components/CameraCard.tsx`.
- 2026-05-26 — Camera zoom root cause: legacy camera card image rendering used `object-cover`, which cropped MJPEG/snapshot frames inside fixed `aspect-video` containers. Use `object-contain bg-black` on camera media to preserve the full stream frame.
- 2026-05-26 — Detect-endpoints contract wired: frontend calls `POST /api/cameras/detect-endpoints` with `{ printerId }` and expects camelCase `{ streamUrl, snapshotUrl, source?, cameraType?, message? }`; `EditCameraModal` fills the URL fields from the response.
- 2026-05-26 — Admin nav structure: the `navigation` array in `src/Web/ReactApp/src/common/components/Layout.tsx` is a flat array of `NavigationElement` items (section headers, dividers, items). Section headers use `{ name, isSectionHeader: true, requiredRole? }`. The "Admin" section header (with `requiredRole: 'farm_admin'`) exists at roughly line 193. A new "Security" section was added after the Settings entry as a peer section header. Route guard is `<ProtectedRoute requiredRole="farm_admin">` wrapping an `<Outlet>` inside the `admin` route group in `App.tsx`.
- 2026-05-26 — Role guard pattern: `<ProtectedRoute requiredRole="farm_admin">` from `@/features/auth/components/ProtectedRoute`. The admin section in `App.tsx` uses a parent `<ProtectedRoute>` + `<Outlet>` so child routes inherit the guard automatically.
- 2026-05-26 — URL filter state + pagination: use `useUrlFilterState` with `filterable: false` on `page`/`pageSize` params, then call `setMany({ ...filterUpdate, page: 1 })` to reset pagination on filter changes. For debounced username input use the individual setter from `useUrlFilterState` — debounce is configured per-param in the config object.
- 2026-05-26 — `Badge` component does not spread HTML attributes (only `children`, `variant`, `size`, `dot`, `className`). Don't put `aria-label` on `<Badge>`; wrap in a `<span>` or test by text content instead.
- 2026-05-26 — `apiClient.get<T>()` is a public method on the ApiClient singleton returning `Promise<AxiosResponse<T>>`. Use it in services for new endpoints not yet wired into named methods on `apiClient` (e.g., while Lambert builds the backend in parallel). Access response body via `.data`.
- 2026-06-01T15:18:38-07:00 — Unified analytics hub: `src/Web/ReactApp/src/features/analytics/pages/AnalyticsHubPage.tsx` is now the single analytics destination. It owns the shared `TimePeriodFilter`, KPI summary row, and controlled `?lens=production|cost|fleet` tab state.
- 2026-06-01T15:18:38-07:00 — Analytics consolidation pattern: keep legacy route-level pages as `PageTemplate` wrappers, but export reusable body components (`StatisticsDashboardContent`, `CostDashboardContent`, `AnalyticsDashboardContent`) so a new hub page can compose them without nested page chrome.
- 2026-06-01T15:18:38-07:00 — Shared tabs accessibility: `src/Web/ReactApp/src/common/components/ui/Tabs.tsx` now uses roving `tabIndex` plus ArrowLeft/ArrowRight/Home/End keyboard navigation. When testing collapsed sidebar navigation, assert on `href` targets instead of visible link labels because the icon-first layout can hide text.
- 2026-06-01T15:18:38-07:00 — `SettingsShell` filtered search should narrow visible categories/sub-pages and keep the rendered page aligned with the normalized `tab`/`sub` params. Keep the standalone route components mapped in `SUB_PAGE_CONTENT` so search-driven deep links render the same page content as contextual `/printer-groups` and `/nfc-bindings` routes, and do not steal focus from the search box when the active category changes because of typing.
- 2026-06-01 — Main-nav cleanup for the settings fold-in belongs in `src/Web/ReactApp/src/common/components/Layout.tsx`: keep one `Analytics` link to `/analytics`, remove standalone `API Keys`, `NFC Bindings`, `Printer Groups`, and `Workers`, keep `Filament Inventory` standalone, and preserve the direct `/profile/api-keys`, `/nfc-bindings`, `/printer-groups`, and `/admin/workers` routes. Only the retired analytics URLs should hard-redirect: `/statistics` → `/analytics?lens=production` and `/statistics/costs` → `/analytics?lens=cost`.
- 2026-06-01 — `SettingsShell` owns the `tab` and `sub` search params. Embedded pages that also need tab state, like `WorkerManagementPage`, should use a separate query param such as `workerTab` and support an `embedded` mode so they can render inside the settings content area without nesting `PageTemplate`.
- 2026-06-02T09:00:01-07:00 — Theme body fonts now use per-theme `body[data-theme="X"], html[data-theme="X"] body` overrides. Chosen faces: Dark → Inter, Light → Nunito Sans, Blueprint → DM Mono, RatOS → Rajdhani, Voron → Chakra Petch, Farm → Merriweather Sans; Matrix continues using `var(--pf-font-mono)` / JetBrains Mono.
- 2026-06-02T09:16:06-07:00 — Dashboard status tic-tacs in `src/Web/ReactApp/src/features/printers/components/PrinterDashboard.tsx` should use `status-idle` tokens for zero-count states and active semantic tokens for non-zero states. RatOS now aligns its `--pf-status-printing-*` and `--pf-status-paused-*` tokens to the Matrix-style green-on-black treatment so Online, Printing, Paused, and Total all stay within the neon-green theme instead of falling back to amber/loading colors.
- 2026-06-02T09:23:58-07:00 — React route structure is still centralized in `src/Web/ReactApp/src/App.tsx`, while primary sidebar navigation stays in the flat `navigation` array inside `src/Web/ReactApp/src/common/components/Layout.tsx`. Admin-only discovery now mostly flows through the `/settings` shell (`SettingsShell.tsx` + `features/settings/types.ts`) rather than separate sidebar links.
- 2026-06-02T09:23:58-07:00 — Unreachable user-facing profile routes: `/profile/api-keys`, `/profile/notifications`, and `/profile/passkeys` are all live in `App.tsx`, but there are no in-app links or `navigate()` calls to them. The authenticated user menu still has a `Profile` button that only closes the menu, so self-service profile pages are effectively hidden unless the user knows the URLs.
- 2026-06-02T09:23:58-07:00 — Stranded route: `/locations/dashboard` renders `LocationDashboardPage`, but `/locations` redirects to Settings > Hardware and there are no links/buttons to `/locations/dashboard`. Filed GitHub issues #465 (profile nav gap) and #466 (location dashboard entry point) from this audit.
- 2026-06-02T09:23:58-07:00 — Dead/legacy page wrappers found during nav audit: `features/admin/pages/AdminPage.tsx`, `features/monitoring/pages/MonitoringPage.tsx`, `features/slicer/pages/OrcaSlicerPage.tsx`, `features/slicer/pages/ImportOfficialProfilesPage.tsx`, and `features/slicer/pages/SliceJobsPage.tsx` are no longer part of the live route graph; newer entry points use `SettingsShell`, `SystemDashboardPage`/`MonitoringContent`, `NewSliceJobPage`, and `ProfileImportWizardPage` redirects instead.
- 2026-06-02T10:06:07-07:00 — Location dashboard entry point belongs in the primary sidebar `navigation` array in `src/Web/ReactApp/src/common/components/Layout.tsx`. Keep it as a direct `Locations` link to `/locations/dashboard` in the Hardware section so the dashboard is discoverable without changing the existing `/locations` redirect behavior.

### External-reference-app Review Pointer — 2026-05-31

external-reference-app repo ([external reference repo]) was reviewed by Brett. Two adoption candidates identified: gcode-preview (toolpath rendering) and client-side 3MF parsing. See decisions.md entries "Consider G-code toolpath preview parity from external-reference-app" and "Consider a richer slice progress contract" for details.

### External-reference-app Adoption & Settings Consolidation

Phase 1 work: G-code preview viewer (integrate gcode-preview npm lib v2.18.x, extend GCodeViewer3D.tsx, wire to ArchivesPage), Quick Slice UX modal (preset-first, 3 profile dropdowns, hide raw sliders behind "Advanced"). Phase 2 deferred: multi-plate 3MF picker with smart filament auto-selection. Settings consolidation identified 15+ candidate admin pages (Filament, Slicer Profiles, Cameras, NFC, etc.) for unified nav with 8 tabs: General, Filament, Slicing, Hardware, Notifications, Integrations, Data, Users. Key UX: cross-tab search, collapsible cards, inline modals, masked secrets. NFC tag management modal (LinkSpoolModal + AssignSpoolModal, WebSocket real-time sync) deferred to later phase.
- **2026-05-31T16:42:** Before committing, scrub message for forbidden external refs: "external-reference-app", "external-author", "external reference app", [external reference repo]. Acceptable alternatives: "adoption plan", "Phase N work breakdown", or standalone feature description. See .squad/decisions.md 2026-05-31T09:42 entry.

## 2026-05-31 — GcodePreviewService Abstraction (#333)

- Created `IGcodePreviewService` interface + `createGcodePreviewService()` factory in `src/Web/ReactApp/src/features/slicer/services/gcodePreviewService.ts`.
- v1 uses a lightweight standalone G-code parser (layer splitting by Z-height changes) — no WebGL dependency. The `gcode-preview` npm package (v2.18.0) is installed but its `WebGLPreview` class requires a real WebGL context (canvas + GPU), making it unsuitable for headless service/test use. v2 will use it inside a Web Worker with OffscreenCanvas.
- Exported via `features/slicer/services/index.ts`; no direct `gcode-preview` imports allowed outside this module.
- PR #364 → development.

### Learnings
- `gcode-preview` v2.18.0 only exports `WebGLPreview` and `init`; the `Parser` class is internal and not accessible. Creating a `WebGLPreview` always attempts to instantiate a Three.js `WebGLRenderer`, so it cannot be used in jsdom/vitest without a full WebGL mock.
- For v2 worker swap: use `OffscreenCanvas` transferred to the worker, then `new WebGLPreview({ canvas })` works with real GPU context.

## 2026-05-31 — Settings Shell (#357)

- Built `/settings` route with tabbed layout using existing `Tabs` UI component (controlled mode + `onTabChange`).
- 8 tabs: General, Filament, Slicing, Hardware, Notifications, Integrations, Data, Users — empty placeholders for ST-2 migration.
- Cross-tab keyword search: filters tab strip by label + keyword array; shows empty state when no match.
- URL deep-link via `useSearchParams`: `?tab={id}&q={query}`.
- Old `/settings` (SettingsPage) preserved at `/admin/settings-legacy` for backward compat during migration.
- 9 tests covering tab switching, search filter, URL sync, deep-link.
- PR #367 → development.

### Learnings
- The project's `Tabs` component supports controlled mode (`activeTab` + `onTabChange`) which makes URL sync straightforward — no need for external state management.
- `setSearchParams` with a functional updater that builds a fresh `URLSearchParams` from `prev` is the cleanest batching pattern (single call, reads current params, returns new).

## 2026-05-31 — Quick Slice Modal (#338)

- Created `QuickSliceModal` in `src/Web/ReactApp/src/features/slicer/components/QuickSliceModal.tsx`.
- Cascading preset-first flow: Printer → Machine Profile → Process Profile → Filament Profile.
- Uses effective-value derivation pattern (no `useEffect` setState) to satisfy `react-hooks/set-state-in-effect` lint rule.
- Integrated on ModelsPage: the existing Slice action button now opens the modal instead of navigating away.
- "Advanced Settings →" link closes modal and navigates to `/slicer?modelId=<id>` (NewSliceJobPage).
- 11 component tests covering open/close, profile dropdown loading, submit, and navigation.
- PR #368 → development.

### Learnings

- 2026-05-31 — The project eslint config enforces `react-hooks/set-state-in-effect`: no `setState` in `useEffect` bodies. Use derived/effective values (fallback to first item from query data) instead of auto-select effects.
- 2026-05-31 — `slicerProfilesService.getMachineProfilesForModel(modelId)` takes a printer catalog modelId (from `printerDetails.modelId`) and returns `OrcaMachineProfile[]`. Then `getFilamentProfilesForMachines` and `getProcessProfilesForMachines` take `machineNames: string[]` to get compatible profiles.

## 2026-05-31 — GCodeViewer3D via GcodePreviewService (#334)

- Refactored `GCodeViewer3D.tsx` to consume `IGcodePreviewService.parseGCodeDetailed()` instead of inline parser. No direct `gcode-preview` or `WebGLPreview` imports remain in the component.
- Extended `gcodePreviewService.ts` with `parseGCodeDetailed()` returning `DetailedParsedGCode` (full point coords + tool tracking per layer) for Three.js rendering.
- Added T-command filter UI (tool/filament toggle buttons, only shown for multi-tool G-code).
- Added loading spinner (`role="status"`) and error boundary (`role="alert"`) states.
- Component accepts optional `service` prop for DI in tests.
- 8 new tests mock the service interface; tests do not touch the parser directly.
- PR #369 → stacked on PR #364 (squad/333 branch).

### Learnings
- `IGcodePreviewService` needs both metadata-only (`parseGCode`) and rendering-ready (`parseGCodeDetailed`) methods — the component needs full XYZ point data for Three.js `Line` rendering.
- Tool changes (T-commands) must be tracked per-point during parsing to enable per-tool filtering in the viewer.
- ESLint `react-hooks/set-state-in-effect` fires for `setState` called synchronously at the top of a `useEffect`; moving to async/await inside the effect body silences it.

## 2026-05-31 — Advanced Settings Disclosure (#340)

- Created `AdvancedSettingsDisclosure` component wrapping `CollapsibleSection` from UI library.
- Wrapped `SlicerSettingsPanel` on `NewSliceJobPage` — collapsed by default, preset dropdowns remain visible.
- localStorage key `pf.slicer.advancedDisclosure` persists user preference.
- Override count shown in collapsed title: compares `slicerSettings` vs `originalProcessSettings`.
- 8 component tests passing. PR #372 → development.

### Learnings

- 2026-05-31 — `CollapsibleSection` supports controlled mode (`expanded` + `onToggle`) with `collapsedTitle` for custom collapsed labels and `headerActions` for right-side content. Located at `@/common/components/ui/CollapsibleSection`.

## 2026-05-31 — Preview Button + Artifact URL Helpers (#335)

- Added `ArtifactMetadataResponse` interface and `getArtifactMetadata()`, `getArtifactDownloadUrl()`, `getArtifactGcodeUrl()` to `sliceJobService.ts`.
- Added Preview button (EyeIcon) to completed job rows in both table and card views of `SliceJobsPanel.tsx`.
- Created `GcodePreviewModal.tsx` — opens `GCodeViewer3D` with `/api/artifacts/job/{jobId}` URL in an xl modal.
- 9 tests: 5 artifact URL helper unit tests + 4 preview button component tests.
- PR #373 → stacked on PR #364 (squad/333-gcode-preview-service-abstraction).

### Learnings
- The `react-hooks/set-state-in-effect` lint rule fires on `setState` in `useEffect` cleanup/conditional returns. Use `useMemo` for derived state that only depends on props.
- `GCodeViewer` component fetches G-code text internally via `fetch(gcodeUrl)` — the consumer just passes the URL, no need to prefetch.

## 2026-05-31 — Bed-Type Override (#339)

- Added `BED_TYPE_OPTIONS` export from `metadataTypes.ts` (sourced from `KNOWN_ENUMS.bed_type`).
- Bed Type dropdown added to both `QuickSliceModal` and `NewSliceJobPage` as top-level field.
- Default: "Inherit from profile" (empty string = no override). User selection injects `curr_bed_type` into `overrides` in `slicerProfileJson`.
- No useEffect setState — uses simple controlled `useState('')`.
- 7 new tests total (4 QuickSliceModal + 3 NewSliceJobPage). All 38 targeted tests pass.
- PR #374 → squad/338-quick-slice-modal (stacked on #368).

### Learnings

- 2026-05-31 — OrcaSlicer bed type override key is `curr_bed_type` (not `bed_type`). The `KNOWN_ENUMS.bed_type` in `metadataTypes.ts` lists the string values OrcaSlicer accepts (e.g. "Cool Plate", "Textured PEI Plate").
## 2026-05-31 — Archive Summary

Frontend architect work through 2026-05-31 archived. Recent focus: Settings UI migration (16 nav items → tabs), bed-type overrides, preview modals, and advanced disclosure controls.

S.bed_type` in `metadataTypes.ts` lists the string values OrcaSlicer accepts (e.g. "Cool Plate", "Textured PEI Plate").
- 2026-05-31 — When tests mock `@/features/slicer/components/settings`, any new named exports from that barrel must be added to the mock object or tests will throw "No export defined on mock" errors.

## 2026-05-31 — Settings Nav Migration (#358)

- Migrated 16 nav items into Settings tabs (PR #376, stacked on #367).
- Tab assignments: General (1), Filament (1), Slicing (2), Hardware (4), Integrations (1), Data (3), Users (3), Notifications (placeholder).
- Created `SettingsSection` wrapper component for consistent tab panel content layout.
- All old routes redirect to `/settings?tab=X` preserving bookmarks.
- Removed: Filament Inventory, Cameras, NFC Devices, Slicer Profiles, API Keys, Locations, User Accounts, Tags, Bed Types, Custom Fields, Webhooks, Quotas, Data Management, Login Audit from sidebar nav.
- Kept in nav: Dashboard, Printers, Files, Projects, Slice, Print Queue, Auto-Dispatch, Maintenance, Statistics, Cost Analytics, Analytics, Scheduling, Printer Groups, Catalog, Workers, System, Settings.
- Sidebar now has 3 sections: Operations, Management, Admin (removed Hardware and Security section headers).
- 16 redirect tests + updated existing SettingsShell/nav/routing tests.

### Learnings
- Rendering full page components inside tab panels requires wrapping test helpers with QueryClientProvider + Auth mocks since pages typically call hooks that need providers.
- The `SettingsPage` (admin config) rendered an h2 "Settings" which collided with the h1 shell heading in tests — use `level` matcher for disambiguation.
- `SettingsTabStrip` now accepts optional `tabContent` record for rendering real content vs placeholder — backward compatible with empty tabs.
For full historical context, see `.squad/decisions.md` and `.squad/orchestration-log.md`.
# Ripley Summary — Archive (through 2026-05-31)

**Context:** Frontend architect and API integration specialist. Owns printer-card UX, BedClearBanner behavior, frontend cache/signal updates, and React integration testing patterns.

**Key Work Areas:**
- Printer card and banner components: Compact-card refactoring, BedClearBanner auto-dispatch, stale payload handling
- Settings migration: 16 nav items → tabbed interface (PR #376)
- UI components: CollapsibleSection controls, PreviewModal, GcodePreviewModal
- Bed-type overrides: OrcaSlicer integration (PR #374)
- Quick-slice modal: Advanced settings disclosure (PR #372)
- Artifact URL helpers: Preview button with artifact metadata (PR #373)

**Recent PRs (2026-05-31):**
- PR #376: Settings nav migration (16 items to tabs)
- PR #374: Bed-type override with OrcaSlicer
- PR #373: G-code preview button + artifact URL helpers
- PR #372: Advanced settings disclosure control

**Test Patterns:** Focused React integration tests with controlled component patterns. MSW setup for API mocking. localStorage persistence testing.

**Next Focus:** Settings UI completion, preview modal integration, test coverage expansion.

---


## 2026-06-02: Locations Dashboard Navigation (Issue #465)

**Commit:** 9c06a7bb  
**Status:** MERGED

- Added top-level Locations sidebar item in Hardware section of Layout.tsx
- Routes directly to /locations/dashboard for first-class UX discovery
- Preserved existing /locations redirect in Settings > Hardware for backward compatibility
- Locations dashboard now prominently discoverable without widening scope

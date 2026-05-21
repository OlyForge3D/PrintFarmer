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

- 2026-05-20: Assigned mobile controls v1 spike #279 — validate backend print-state enforcement (block jog/preheat/home while printing or paused). Trust `PrinterBackendCapabilities.supportsTemperatureControl` flag per locked decision. See decisions.md "Mobile API Drift + Basic Printer Controls v1".

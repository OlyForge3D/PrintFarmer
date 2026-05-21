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


## Recent

_Last 5 most-recent learnings preserved from full history. Older entries are in `history-archive.md` (archived 2026-05-21 by Scribe)._

- **2026-05-21 — Mobile spike #279 verdict (c).** iOS client must fully gate `/temps` and `/move` client-side based on cached `Printer.Status`. Backend forwards both routes blindly across all plugins (Moonraker silently accepts mid-print; PrusaLink/OctoPrint firmware 409s collapse to bool→404; FlashForge has no movement; SDCP implements neither). Zero test coverage on either route. For Hudson (#284–#286): disable temp/move when status ∈ `{Printing, Pausing, Paused, Resuming, Cancelling, Heating}`; re-evaluate on every SignalR `printerupdated`. Follow-up #290 reassigned to Dallas.
- **2026-05-12 — Buddy Camera IP frontend field (PFarm1-873d).** `buddyCameraIp?: string` added to `UpdatePrinterDto` and `PrinterDetails` types. New "Camera Configuration" section in `EditPrinterModal.tsx` rendered conditionally on `formData.backend === PrinterBackend.PrusaLink`. Shows derived `rtsp://{ip}:554/live/` preview when an IP is entered. Field is edit-only (not on `AddPrinterModal`/`CreatePrinterDto`); set after creation via the edit modal. Used `handleInputChange` for dirty tracking, consistent with existing camera URL fields.
- **2026-05-03 — Spoolman filter-options endpoint.** `GET /api/spoolman/filter-options` returns `IEnumerable<FilterOptionDto>` (`{ id, name }`, camelCase). Frontend `useSpoolFilterOptions()` hook uses TanStack Query caching; SpoolsTab loads options on mount via `useEffect` with toast-based error handling. Replaces hardcoded filter values.
- **2026-04-25 — Hollow cut detection (PFarm1-4ex2).** Cut tool already handles disjoint cap loops correctly: `orderCapEdges` returns `THREE.Vector3[][]` and the consumer iterates each loop through `earClipTriangulate`. Only nested loops (true holes — inner ring of a hollow tube cross-section) remain v1 limitation. AABB containment in the cap's 2D projection plane is a cheap heuristic for detecting nested loops without point-in-polygon tests. Full hole bridging (constrained Delaunay or ear-clipping with bridge edges) deferred from v1 fix.
- **2026-01-12 — Multi-axis Cut + Drag interference fix.** `draggable` prop on `STLModel` / `PrebuiltSTLModel` extended to be false when ANY tool mode is active (`cutMode`, `supportPaintMode`, `seamPaintMode`, `colorPaintMode`, `fuzzySkinPaintMode`, `measureMode`, `textPlacementMode`). Rewrote `CutPlaneOverlay.tsx` to support X/Y/Z axis cuts with axis-specific plane orientation (Z: rot(0,0,0), X: rot(0,π/2,0), Y: rot(π/2,0,0)). Red sphere drag handle (3mm radius) at plane center; cursor `ns-resize` for Z, `ew-resize` for X/Y. New OrcaSlicer-style UI panel: Mode selector, build volume display, cut position (axis dropdown + numeric mm input + reset), Add connectors (disabled), After-cut options (Keep upper/lower, Place on cut, Flip, Cut to parts), Perform cut button. R3F `useFrame` syncs plane/handle position with model transform.

- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291, #292, #293, #294, #295, #296, #297, #298). See `.squad/log/2026-05-21T08-15-00Z-ralph-rounds-2-5-phase-1-complete.md`.

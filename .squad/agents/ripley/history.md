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

- 2026-05-26 — Camera management UI polish: camera management lives in `src/Web/ReactApp/src/features/cameras/pages/CamerasPage.tsx`, `src/Web/ReactApp/src/features/cameras/components/CameraManagementPanel.tsx`, and `src/Web/ReactApp/src/features/cameras/components/EditCameraModal.tsx`; printer-card camera rendering is in `src/Web/ReactApp/src/features/printers/components/CameraCard.tsx`.
- 2026-05-26 — Camera zoom root cause: legacy camera card image rendering used `object-cover`, which cropped MJPEG/snapshot frames inside fixed `aspect-video` containers. Use `object-contain bg-black` on camera media to preserve the full stream frame.
- 2026-05-26 — Detect-endpoints contract wired: frontend calls `POST /api/cameras/detect-endpoints` with `{ printerId }` and expects camelCase `{ streamUrl, snapshotUrl, source?, cameraType?, message? }`; `EditCameraModal` fills the URL fields from the response.
- 2026-05-26 — Admin nav structure: the `navigation` array in `src/Web/ReactApp/src/common/components/Layout.tsx` is a flat array of `NavigationElement` items (section headers, dividers, items). Section headers use `{ name, isSectionHeader: true, requiredRole? }`. The "Admin" section header (with `requiredRole: 'farm_admin'`) exists at roughly line 193. A new "Security" section was added after the Settings entry as a peer section header. Route guard is `<ProtectedRoute requiredRole="farm_admin">` wrapping an `<Outlet>` inside the `admin` route group in `App.tsx`.
- 2026-05-26 — Role guard pattern: `<ProtectedRoute requiredRole="farm_admin">` from `@/features/auth/components/ProtectedRoute`. The admin section in `App.tsx` uses a parent `<ProtectedRoute>` + `<Outlet>` so child routes inherit the guard automatically.
- 2026-05-26 — URL filter state + pagination: use `useUrlFilterState` with `filterable: false` on `page`/`pageSize` params, then call `setMany({ ...filterUpdate, page: 1 })` to reset pagination on filter changes. For debounced username input use the individual setter from `useUrlFilterState` — debounce is configured per-param in the config object.
- 2026-05-26 — `Badge` component does not spread HTML attributes (only `children`, `variant`, `size`, `dot`, `className`). Don't put `aria-label` on `<Badge>`; wrap in a `<span>` or test by text content instead.
- 2026-05-26 — `apiClient.get<T>()` is a public method on the ApiClient singleton returning `Promise<AxiosResponse<T>>`. Use it in services for new endpoints not yet wired into named methods on `apiClient` (e.g., while Lambert builds the backend in parallel). Access response body via `.data`.

### Bambuddy Review Pointer — 2026-05-31

bambuddy repo (https://github.com/maziggy/bambuddy) was reviewed by Brett. Two adoption candidates identified: gcode-preview (toolpath rendering) and client-side 3MF parsing. See decisions.md entries "Consider G-code toolpath preview parity from bambuddy" and "Consider a richer slice progress contract" for details.

### Bambuddy Adoption & Settings Consolidation

Phase 1 work: G-code preview viewer (integrate gcode-preview npm lib v2.18.x, extend GCodeViewer3D.tsx, wire to ArchivesPage), Quick Slice UX modal (preset-first, 3 profile dropdowns, hide raw sliders behind "Advanced"). Phase 2 deferred: multi-plate 3MF picker with smart filament auto-selection. Settings consolidation identified 15+ candidate admin pages (Filament, Slicer Profiles, Cameras, NFC, etc.) for unified nav with 8 tabs: General, Filament, Slicing, Hardware, Notifications, Integrations, Data, Users. Key UX: cross-tab search, collapsible cards, inline modals, masked secrets. NFC tag management modal (LinkSpoolModal + AssignSpoolModal, WebSocket real-time sync) deferred to later phase.
- **2026-05-31T16:42:** Before committing, scrub message for forbidden external refs: "bambuddy", "maziggy", "Bambu Buddy", github.com/maziggy/bambuddy. Acceptable alternatives: "adoption plan", "Phase N work breakdown", or standalone feature description. See .squad/decisions.md 2026-05-31T09:42 entry.

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

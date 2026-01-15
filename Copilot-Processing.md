Current request (2026-01-13):
- Recreate missing GcodeLibraryPage with PageTemplate header hidden (showHeader=false), preserving padding/layout and page heading/breadcrumbs.
- Confirm file deletion state and ensure no duplicate titles/headers.

Action plan:
- [ ] Confirm GcodeLibraryPage.tsx absence and gather related components/props.
- [ ] Recreate GcodeLibraryPage with PageTemplate showHeader=false, header block, breadcrumbs, file browser wiring, FAB upload action, keyboard shortcuts.
- [ ] Verify file saved cleanly with no duplicate content or parse errors.

Current work (backend fix for gcode list/delete):
- [x] Inspect GcodeFilesService/Controller for list/delete payloads.
- [x] Add GcodeFileId and DirectoryId to list responses; emit virtual path (directory only) for files.
- [ ] Verify API serialization and shape match frontend expectations (id is GUID, path is directory).

User request:
- GCode files are already sliced and should NOT render a slice button; Model files must show a slice button because they require slicing.
- Rebuild model and gcode file browsers with a unified, headless `useFileBrowser` hook plus shared Grid/Explorer views.
- Rebuild model and gcode file browsers with a unified, headless `useFileBrowser` hook plus shared Grid/Explorer views.
- Keep Windows Explorer-style tree/list and card/grid views with search, pagination, sorting, controlled selection, and bulk/single actions (tag, delete, download).
- Domain actions: models need tag/upload/viewer buttons; gcode needs per-file slice; both support bulk tag/delete and permissions-aware controls.
- Accessibility: keyboardable list/grid, visible focus, screen reader labels, announcements for selection/batch actions.
- Data flow: React Query + suspense-friendly; thin domain mappers (`mapDomainToFileItem`, `mapQueryParams`); no extra caches.

Notes:
- Core toolbar supplies search/sort/view toggle/delete; domains inject extras (Tag/Upload for models, Slice/Download for gcode).
- Selection must be overridable (controlled props) for Tag/Delete counts; both grid/list share selection affordances.
- Explorer/list columns sortable with pagination; grid/list both support per-item actions and bulk operations.

Action plan:
- Audit slice button rendering: ensure Models file browser surfaces slice action, and GCode file browser omits slice action.
- Define a unified `FileItem` contract and domain mappers for models/gcode, including query param translation.
- Implement `useFileBrowser` hook with React Query handling fetch, pagination, sorting, search, selection (controlled/uncontrolled), and mutations (delete/download); expose stable state + callbacks.
- Build shared `GridView` and `ExplorerView` components consuming a unified prop contract (files, selection handlers, navigation, sort, pagination, actions, busy flags) with accessibility baked in.
- Create a composable `FileBrowser` shell wiring hook, toolbar (core + slots), and view switcher; respect permissions/capabilities config.
- Implement domain browsers (`ModelBrowser`, `GcodeBrowser`) that supply fetchers, mappers, toolbar slots, and domain actions (viewer/tag/upload for models; slice/download for gcode) without extra adapters or caches.
- Integrate 3D viewer entry point for models and slice action for single gcode files in both views.
- Add accessibility affordances (ARIA labels/roles, focus outlines, keyboard nav, live region for selection changes).
- Outline testing strategy: unit tests for hook (query, pagination, selection, mutations) with mocked fetchers; component tests for shared views covering selection, sort, toolbar slots, permissions; light domain tests for mappers/actions wiring.

Tasks:
- [x] Confirm ModelsFileBrowser renders slice action for model items and Gcode browser hides slice action (no slice button for gcode).
- [x] Define unified `FileItem` type and domain mappers for models/gcode (including query param mapping).
- [x] Draft and implement `useFileBrowser` API (React Query) with pagination, search, sorting, selection control, delete/download mutations.
- [x] Implement selection helpers (toggle, selectAll, clear) supporting controlled overrides and stable pagination/selection behavior.
- [x] Build shared `GridView` with selection affordances, per-item actions, and accessible keyboard/focus patterns.
- [x] Build shared `ExplorerView` (tree/list) with sortable columns, pagination, selection, per-row actions, and keyboard navigation.
- [x] Create `FileBrowser` compositor (toolbar, view toggle, search/sort, delete) with slots for domain actions and permissions gating.
- [x] Wire `ModelBrowser` to shared pieces (fetchers, mappers, tag/upload/viewer actions; bulk tag/delete) and verify 3D viewer entry points.
- [x] Wire `GcodeBrowser` to shared pieces (fetchers, mappers, slice per file, download/delete) and ensure no bulk slice.
- [x] Add accessibility instrumentation (ARIA, focus outlines, live regions) across toolbar/list/grid.
- [x] Author unit tests for hook and component tests for shared views; cover permissions gating and controlled selection paths.User request:
- Rebuild model and gcode file browsers with a unified, headless `useFileBrowser` hook plus shared Grid/Explorer views.
- Maintain two views (Windows Explorer-style tree/list and card/grid) with search, pagination, sorting, controlled selection, and bulk/single actions (tag, delete, download).
- Domain-specific actions: models need tag upload/viewer buttons; gcode needs slice button (single-file only). Both must support bulk tag/delete, controlled selection, toolbar slots, and permissions-aware controls.
- Accessibility requirements: keyboardable list/grid, visible focus, SR-friendly labels, and announcements for batch actions.
- React Query + suspense-friendly flow; thin domain mappers (`mapDomainToFileItem`, `mapQueryParams`); no extra caches.

Notes:
- Toolbar should provide search/sort/view toggle/delete; domains inject extra actions (Tag/Upload for models, Slice/Download for gcode).
- Selection must be overridable via controlled props for Tag/Delete counts.
- Explorer/list columns sortable with pagination; grid/list both offer selection affordances and bulk actions.User request: Concern that extracting GenericFileBrowser increased complexity; questioning whether keeping separate file browsers would be better.
Action plan:
- Assess current GenericFileBrowser usage for models vs gcode and map complexity points.
- Identify selection/tagging wiring gaps caused by generic abstraction.
- Propose minimal adjustments (or rollback strategy) to reduce complexity while preserving functionality.
- Validate impact on builds/tests/lint if changes are proposed.

Tasks:
- [x] Review ModelsFileBrowser and GenericFileBrowser integration points (selection, tag actions).
- [x] Identify specific complexity pain points introduced by GenericFileBrowser.
- [x] Draft recommendation: keep generic with fixes vs split browsers; outline pros/cons.
- [x] If needed, suggest minimal refactor steps and validation plan.

Recommendation:
- Keep GenericFileBrowser but narrow surface: expose optional controlled selection (`selectedFiles`, `onSelectionChange`) so model/gcode browsers can drive Tag/Delete counts without duplicating state.
- Extend view component contracts with optional selection props (selectedFiles, onToggleSelection, onSelectAll) for both grid/explorer; this removes one-off adapters and cache hacks.
- Allow a lightweight `mapDomainToFileItem` helper in config to avoid cache maps; models/gcode can pass transformation without manual caches.

Minimal refactor steps:
- Add controlled selection props to GenericFileBrowser and wire toolbar buttons to use them.
- Update grid/explorer component prop types to accept optional selection handlers; adapt ModelGridView/ExplorerModelListView to consume them directly (no wrapper components).
- Provide mapping hook/prop in GenericFileBrowser to convert domain items to FileItem, so ModelsFileBrowser can drop modelCache and adapter components.
- Run lint/build/tests after API change to ensure gcode/browser usage still compiles.

Summary:
- The complexity comes from GenericFileBrowser hiding selection while models need it for Tag/selection parity; adapters/cache are symptoms.
- Best path is to keep the generic but add controlled selection hooks and view props so models/gcode share one implementation without local hacks.
- Streamlining mapping (mapDomainToFileItem) lets models drop cache adapters, reducing indirection without forking browsers.

Findings:
- GenericFileBrowser keeps selection state internal; ModelsFileBrowser needs selected IDs for Tag button and adapters, forcing duplicated local state and adapters with no way to sync selection back up.
- Grid adapter lacks selection hooks entirely (only navigate/delete), so models cannot align grid vs explorer selection without extending GenericFileBrowser surface.
- Model-specific formatting/fields require adapters and caches, adding overhead for the generic shape.
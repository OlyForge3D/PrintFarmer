User request: Concern that extracting GenericFileBrowser increased complexity; questioning whether keeping separate file browsers would be better.
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
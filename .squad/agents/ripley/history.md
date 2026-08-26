# Ripley Summary — Recent Sessions

Ripley is the frontend architect and API integration specialist.

## 2026-06-02: Theme-specific Body Fonts & Multi-file Import Decisions

**Scope:** React frontend theming, Printables multi-file import modal  
**Status:** Decisions merged to squad/decisions.md

- Assigned distinct body font to each supported theme (7 themes total: Dark/Inter, Light/Nunito, Blueprint/DM Mono, RatOS/Rajdhani, Voron/Chakra Petch, Farm/Merriweather, Matrix/JetBrains Mono)
- Updated frontend to send `fileIds: string[]` in Printables import payload for multi-file contract support
- Used `CubeIcon` as thumbnail fallback for Printables CDN failures

## Recent Work Patterns (2026-05-26 to 2026-05-31)
- Camera management UI: printer association, endpoint detection, backend probe abstraction
- Login audit frontend: security page with tri-state filter and audit log display
- Settings system consolidation: tabbed layout, 8-tab navigation, cross-tab search
- Frontend transport integration: SignalR updates, status affordances, auto-dispatch naming
- React component patterns: modal-based UX, BedClearBanner state preservation, failure-detection badge

## Archived History

Older entries (pre-2026-05-26) archived to history-archive.md for size management.

## Team Coordination (2026-06-02)

**Scribe Session 17:44:47Z**
- Merged Profile Settings Discoverability decision (Ripley)
- Processed 2 inbox decisions; cleaned up inbox workflow
- Created orchestration logs for ripley-14 and newt-8 sessions
- decisions.md: 268,270 bytes → 2 entries merged

## Learnings

- 2026-06-02: Self-service profile routes need explicit navigation affordances; routing alone is not enough. A default Profile action plus Preferences quick links makes API Keys, Notifications, and Passkeys discoverable.
- 2026-06-06T08:58:45.350-07:00: Phase 1 of the settings restructure works best with a normalized `scope` + `tab` + `sub` query model, so User, System, and Admin navigation can share one shell while legacy `/settings?tab=*` deep links still resolve cleanly.
- 2026-06-02: Brand assets that must adapt across themes should use inline SVG with `currentColor` so shared auth and layout surfaces inherit the active theme automatically.
- 2026-06-02: Accent-filled controls cannot assume white foregrounds across the 7-theme system; shared `--pf-on-accent` and `--pf-on-danger` tokens need to drive badges, destructive actions, active settings nav, and selected theme chips.
- 2026-06-02T15:20:56.358-07:00: Native slicer .3mf support now lives in `src/Web/ReactApp/src/features/slicer/utils/threemf-parser.ts` and `src/Web/ReactApp/src/features/slicer/components/ThreeMFViewer.tsx`, with `SlicerBedVisualization.tsx` routing `.stl` and `.3mf` through the same selection and transform flow.
- 2026-06-02T15:20:56.358-07:00: PrintFarmer’s slicer scene is already Z-up, so BamBuddy’s Y/Z swap does not apply here; raw `/api/3d-models/file/{id}` URLs stay native for `.3mf`, and `?forceStl=true` is only a viewer-side fallback after parse failure.
- 2026-06-02T21:58:53.720-07:00: Both `.3mf` preview surfaces should drop each parsed mesh to its own bed plane before shared XY centering; the shared helper for that lives in `src/Web/ReactApp/src/features/slicer/utils/threemf-display.ts` and feeds both `ThreeMFViewer.tsx` and `ModelViewer3D.tsx`.
- 2026-06-02T21:58:53.720-07:00: The models-library viewer should keep its mock bed palette aligned with the slicer workspace (`#2a2a3a` bed, `#4a4a6a` outline, `#555577`/`#7777aa` grid) so `.3mf` and `.stl` previews feel consistent across pages.
- 2026-06-03T10:36:27.015-07:00: The unified Files route now lives in `src/Web/ReactApp/src/features/files/pages/FilesPage.tsx`; it merges model and G-code queries into one client-sorted browser, drives the lens from `?type=`, and rewrites legacy `/files/gcode` and `/files/3d-models` links back to `/files`.
- 2026-06-03T10:36:27.015-07:00: Entry points that want a prefiltered unified library should link with query params, not child routes; `src/Web/ReactApp/src/features/queue/components/QueueJobsTable.tsx` now targets `/files?type=gcode`.
- 2026-06-03T11:34:00-07:00: Settings search is easier to scan when matches are highlighted inline and the search box supports the `/` focus shortcut; `SettingsSidebar.tsx`, `SettingsSubTabs.tsx`, and `SettingsSearch.tsx` now treat search as navigational affordance instead of a badge-only filter.
- 2026-06-03T11:36:03-07:00: Settings navigation metadata now lives in `src/Web/ReactApp/src/features/settings/settings-navigation.ts`, so the sidebar and Command-K palette share one source for category icons, descriptions, and route targets while fuzzy jumps stay scoped to `SettingsShell.tsx`.
- 2026-06-03T12:26:51-07:00: The settings workspace works best as a fixed-height split shell keyed off `data-settings-shell`; shell-scoped card-heading typography and number-input styling let embedded admin pages inherit the industrial settings treatment while only the content pane scrolls.


## 2026-07-25 — #942 tag edit "silent discard" — fix was already in dev, test gap was real

The issue said `TagAdminPage.handleSaveEdit` was still a `// In a full implementation` stub that silently discarded edits, and asked me to wire it up + add a backend endpoint. Neither claim held against `development`:

- Commit `24dba89c5` (PR #844) had already removed the stub, wired the mutation, added the revision-conflict dialog, and shipped `PUT /api/tags/{tagId}` on `TagsController`.
- `TagService.UpdateTagAsync` already catches `DbUpdateException` for UNIQUE-key violations and rethrows as `DuplicateEntityException`; `TagsController` maps that to a 409 with `{ error }` (no revision fields).
- `TagAdminPage.test.tsx` already had 8 tests covering success, generic-Error, revision-conflict, deleted-tag placeholder, reload, escape, missing-revision.

**One gap was real:** no test proved the duplicate-name collision path (409 with `{ error }` and no revision fields → must fall through `getRevisionConflict` to `setSaveError`, not misclassify as a concurrency conflict). Added exactly that one test in `TagAdminPage.test.tsx`. Verified break-first by temporarily stubbing `handleSaveEdit` back to its pre-#844 form: 7 of 9 tests failed (including mine); after restore, all 9 pass.

Lessons carried forward:

- **Grep the exact quoted comment/line before believing an issue's "stub" claim.** A 30-second `grep "In a full implementation"` returned zero matches and would have caught the issue-vs-reality drift immediately. The issue was filed 2026-07-25 against code that had already been fixed months earlier.
- **When an issue's "Done when" list is more specific than the "The bug" narrative, treat the Done-when list as the real spec.** The narrative here was stale, but the "test covers both the success AND collision path" bullet exposed a genuine, small, still-needed test gap.
- **The Axios interceptor unpacks `data.error` → `ApiError.message` for you.** For any 409 with `{ error: "..." }` in the body, `getErrorMessage(error, fallback)` returns the backend's own message. Don't mock `ApiError.message` and `data.error` differently in tests — mirror the real interceptor shape so tests catch classification bugs.
- Validation: `npm run lint` = 0/0; `npm run build` = ✓ 11.69s; `npm run test:run` = 276 files / 2903 tests / 0 failed (baseline was 2902, net +1).



## 2025 — #938 command palette (extended, not rebuilt)

Lessons learned:

- Always check the issue body first. The task summary said "build a Ctrl+K
  palette from scratch"; the rewritten issue body said "close the gaps in
  the existing 600-line palette." Reading the actual issue saved a full
  rebuild.

- fast-refresh forces hooks + components into separate files. Exporting
  useCommandPalette from the same file as GlobalCommandPaletteProvider
  fails react-refresh/only-export-components and the rule is not
  suppressible without disabling fast refresh globally. Fix pattern: split
  context + hook into xxxContext.ts and let the component file only export
  the component.

- Tests that use useSearchParams need a MemoryRouter wrap. When I added
  ?field= handling to SettingsPage, six existing tests started failing at
  render time. The fix is uniform: wrap the render helper in MemoryRouter.
  Apply this pattern to every test that touches a page which now consumes
  URL params.

- Providers that throw outside their scope break every test that renders
  the shell. GlobalCommandPaletteProvider throws on missing context by
  design (safer than silently returning null). Any component test that
  reaches a useCommandPalette call site needs the provider in its wrapper.

- Prefer suffix data-* selectors when the caller only knows part of the
  key. The palette knows the property name (e.g. FarmName) but not the
  section key. Emitting data-setting-property="FarmSettings.FarmName" and
  querying with [data-setting-property$=".FarmName"] is stable across
  section renames and needs no state coordination.

- Gate expensive queries on visible UI. useSettingsMetadata runs only when
  isOpen && user - no fetch until the palette actually opens. Cost matters
  on cold-load; TanStack Query enabled flag is the right lever.

Files I now know cold:
- settings-navigation.ts - palette item schema, kind discriminator,
  scope-to-path routing (buildSettingsPath).
- SettingsPagelet.tsx - property render loop, where data-setting-property
  lives.
- SettingsPage.tsx - ?field= handling + RAF scroll pattern for deep-linking.
- ADMIN_DESTINATIONS registry from #934 - consume via
  filterDestinationsByAccess({ hasRole, hasPermission }).


---


## 2026-08-16 — Printer revision "dead-end" spool guards (frontend resilience)

**Scope:** `src/Web/ReactApp/` only (Lambert owned the parallel backend DTO fix). Branch `dev/jpapiez/legendary-tribble`, commit `44cabf30c`.

**Bug:** `GET /api/printers` returns a compact DTO omitting `rowVersion`. `PrintersPage` passes those list-sourced objects as the `printer` prop into the sidebar and cards, so every spool mutation guard read `rowVersion` off the prop, found `undefined`, and returned early — single-toolhead/no-AMS eject + change-spool silently issued no network request; controls looked enabled.

**What I changed:**
- `PrinterDetailsSidebar.tsx`: the old fallback `displayPrinter?.rowVersion ?? printer.rowVersion` traced to the *same* list-sourced prop (no-op). Now resolve from `printerDetails?.rowVersion` (authoritative, from `/printers/{id}/details`) and widen the existing `usePrinterDetails` `enabled` to also fetch when the prop lacks a revision.
- `DetailedPrinterCard.tsx`: `usePrinterDetails` was `enabled: spoolmanReady` (flaky). Now `spoolmanReady || !printer.rowVersion` — strict superset, so topology resolves whenever it did before; extra fetch only fires while the DTO omits the token and self-corrects after Lambert's fix.
- Both surfaces: disable the affected control with an explanatory `title`/`aria-label` when no revision resolves, reusing the `MaterialLoadout` disable-with-tooltip pattern. Guard stays strong — never fabricate/default a revision.
- Fed resolved revisions to `MaterialLoadout`, `CalibrationSetupModal`, `ZOffsetCalibrationWizard`.

**Rationale for reusing `usePrinterDetails` over forcing `usePrinter`:** the sidebar's `usePrinter` is gated `!printerProp && !!printerId` and the grid always passes a prop, so it never fires there. The details query already runs for toolhead topology, shares its cache key (dedupes), has 60s `staleTime`, and there's a single sidebar instance — no redundant per-card request.

**Tests added:** `PrinterDetailsSidebar.spoolRevision.test.tsx` (recover token from detail record; disabled+blocked when no revision; direct prop use post-fix) and `DetailedPrinterCard.spoolRevision.test.tsx` (no silent no-op; recovery from detail record). Updated `DetailedPrinterCard.detailsGating.test.tsx` to split the old flaky assertion into revision-present→no-fetch / revision-absent→fetch.

**Learnings:**
- A `??` fallback is only real if the operands come from *different sources*. `a?.rowVersion ?? b.rowVersion` where both `a` and `b` derive from one list-sourced prop is a no-op that reads like a safety net — the exact trap here.
- The compact list DTO (`getPrinters()` → `PrinterFast[]` cast to `Printer[]`) is a recurring hazard: any guard reading a concurrency/state field off a grid-passed `printer` prop must fall back to a detail fetch, not to another view of the same prop.
- Prefer disabling a control with an accessible reason over letting a click dead-end into a toast — matches `MaterialLoadout`'s existing `canMutate`/`blockedReason` pattern and the a11y rule that disabled controls carry an explanation.
- Validation: `npm run build` clean; `npm run test:run` 428 files / 4753 tests / 0 failed; `npm run lint` clean.

## 2026-08-25 — Machine profile family frontend trace

- New Slice Job is hybrid: live Orca worker system profiles (`machineProfilesForModel`) plus DB-owned custom profiles (`customProfiles`) merged client-side.
- The no-profile clone loop has a deterministic frontend cache defect: `/clone-from-template` success invalidates worker/hierarchy/extended keys but not `['customProfiles']`; the worker correctly remains empty after a DB clone, while the 30-second custom cache remains empty.
- `CloneProfilesModal` performs no navigation. It closes back to `/slicer`; any observed URL redirect comes from outside that component.
- No React code consumes literal Orca `machine_model_list`. Worker hierarchy types expose manufacturer → model → variants, but their service methods have no frontend callers.
- Slicer Profiles management labels an individual variant list as “Machine Model”; family UX needs a first-class base-model/family picker rather than reusing that selector unchanged.
- `MetadataProfileEditor` is reusable for shared family fields, but `ProfileEditorModal` is not reusable whole because it saves one profile and exposes nozzle-specific settings.


## 2026-08-25 — Profile family Phases 0–1 truth-state UI

- Removed the auto-opened `CloneProfilesModal` from `/slicer`; it cloned process profiles and could not resolve missing machine coverage.
- Added a reason-specific machine-profile empty-state card: `no_profiles_for_model` offers an explained-disabled **Create profile family** action; `alias_matched_no_profiles` points to coverage/engine-version drift; absent/unknown codes degrade generically.
- Assumed HTTP body `{ code, detail? }`, surfaced by `apiClient` at `error.data`; reason values are `no_profiles_for_model` and `alias_matched_no_profiles`.
- Because Phase 1 removes the clone success callback, the Phase 0 `customProfiles` invalidation line has no surviving site in the combined end state; Phase 3 must invalidate all four approved family-related keys.
- Validation: build passed; React suite passed once (463 files / 5,135 tests); lint passed with one pre-existing warning in untouched `SlicerWorkspace.tsx`.

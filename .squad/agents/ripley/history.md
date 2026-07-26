# Ripley Summary — Recent Sessions

Ripley is the frontend architect and API integration specialist.

## 2026-06-02: Theme-specific Body Fonts & Multi-file Import Decisions

**Scope:** React frontend theming, Printables multi-file import modal  
**Status:** Decisions merged to squad/decisions.md

- Assigned distinct body font to each supported theme (7 themes total: Dark/Inter, Light/Nunito, Blueprint/DM Mono, RatOS/Rajdhani, Voron/Chakra Petch, Farm/Merriweather, Matrix/JetBrains Mono)
- Updated frontend to send `fileIds: string[]` in Printables import payload for multi-file contract support
- Used `CubeIcon` as thumbnail fallback for Printables CDN failures

## 2026-05-31: Trio Review Cycle #355, #371, #405
Participated in multi-round trio review cycle with strict three-reviewer consensus and fresh-hand rotation (Brett, Kane). Key learnings:
1. **Multi-reviewer consensus:** Three independent reviewers with fresh hands prevents fatigue. (The author-lockout rule that once accompanied this has been RESCINDED by the repo owner — authors fix their own rejected work; nobody is ever locked out of an artifact.)
2. **Kane surgical-fix MVP:** Small, scoped corrections proved cost-effective
3. **Session-end report validation:** Always verify trio drops match current commit SHA
4. **PR auto-close gap:** Manual close required for development branch merges

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



## 2025-12 — #937 Essential/Everything toggle + inline search

- Kept `SettingsPage` public props (`allowedGroups`, `introText`, `afterContent`) stable so `SettingsShell.test.tsx` and `SettingsShellEdgeCases.test.tsx` mocks kept working untouched. That constraint is the single most useful thing carried forward from #935.
- Chose a **client-side manifest** over extending `SettingDisplayAttribute`. Full rationale in `decisions/inbox/ripley-937-client-manifest.md`.
- **React Compiler pattern for persisted state:** read `localStorage` in a lazy `useState(readPersistedMode)` initialiser, write via a `useCallback` that calls `setState` then `localStorage.setItem`. NO `useEffect` doing the sync. This dodges `react-hooks/purity` (no localStorage in render body) AND `react-hooks/set-state-in-effect` (no setState inside effect) in one go.
- **Filter for rendering, save on the full list.** The `GroupSaveBlock` accepts an optional `propertyFilter?: Record<sectionKey, ReadonlySet<propName>>` for what to render, but its save loop still walks the full `metadataItems`. If any changed key belongs to a section that filter would have removed, save still succeeds — otherwise search results would silently drop edits.
- **Sections filtered to zero properties return `null` from the map.** No empty cards, no confusion.
- **Made `Button variant="unstyled"`** satisfy `local/pf-no-raw-html-controls` for the segmented radiogroup — Button spreads `...rest` so `role="radio"`, `aria-checked`, and `tabIndex` flow straight through to the underlying `<button>`. No raw `<button>` needed, no lint suppression needed.
- **Existing SettingsPage tests fixed with one `beforeEach` line** setting `localStorage.setItem('pf.settings.mode', 'everything')`. Their fixture uses synthetic section keys (`SystemLogSettings`, `NotificationSettings`) not in the essential manifest, so Essential mode would have hidden every field. The one-line change is legitimate test-environment setup, not a behavioural test rewrite.
- Validation: `npm run lint` = 0 errors, 0 warnings; `npm run build` = 0 errors; `npm run test:run` = 2852/2852 passing (11 net new).

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

## 2025-12-21 - issue #941 regate follow-up (backend + frontend)

Second review gate on epic #931 (feature/admin-console-redesign). Two
independent reviewers both flagged the same primary defect: memberless
ValidationExceptions produced generic user-facing messages that hid the
real reason (e.g. bad CIDR, missing Telegram token).

Root cause I keep bumping into on this epic: prop.name is the camelCase
wire name and is NOT unique across sections. enabled is declared on 13
settings classes, intervalSeconds on 4, baseUrl on 3, and multiple
sections render on the same page simultaneously. Any lookup keyed on a
bare property name is suspect. This exact confusion produced Finding 1 -
the frontend treated a bare ` errors[sectionKey] ` entry as a field name
in the posted section and rendered it under a non-existent property.

Backend fix (UnifiedSettingsController.BuildValidationErrorResponse):
- Top-level `message` now carries vex.Message, not the generic
  "Validation failed for section 'X'". That generic string was the only
  place the frontend could actually surface a memberless reason, and it
  was overwriting it with a placeholder.
- Kept the memberless `errors[sectionKey] = vex.Message` entry so the
  frontend can attach it to the section card too.
- Left the member-names branch untouched - it works and the frontend
  parser depends on the split-on-dot shape.
- Left the reflection-unwrapping ValidationException path in the bulk
  POST alone. Same shape bug, but jpapiez scoped this task to
  BuildValidationErrorResponse; surgical.

Frontend fix (SettingsPage.tsx extractFieldErrors + GroupSaveBlock):
- Extended extractFieldErrors return type to
  `{ fieldErrors, sectionErrors, message }`. A bare `errors` key that
  equals the section we just POSTed lands in sectionErrors, not
  fieldErrors. GroupSaveBlock holds sectionErrors in state alongside
  fieldErrors, wires save-failure into both, resets both on discard/success.
- Threaded `error={sectionErrors[meta.key]}` into SettingsPagelet.
  Notable: SettingsPagelet.error already existed - accepts string | null
  and renders role="alert" inside the card. Did NOT invent a new prop.
- Extension helper (fieldErrors) still routes member-names errors to
  their claimed property, so the ExternalServicesHealthSettings path is
  unaffected.

Finding 2 (settings-navigation.test.ts):
- Added `expect(segments).toHaveLength(2)` with a hint pointing at
  "update this guard before introducing nested keys". A 3-segment key
  like general.security.advanced was previously truncated silently by
  destructuring, so any future developer adding it would be forced to
  map their location to a wrong sub-page id just to make the test pass.
- Did NOT try to make this test enumerate real backend metadata. That's
  a genuine gap (the neighbouring "maps every settings group ..." test
  hardcodes a Job Queue check and is misleadingly named) but that is
  issue #951's scope, not mine.

Finding 3 (SettingsPage.tsx):
- Swapped EmptyState (from @/common/components/ui) for AdminEmpty (from
  @/common/components/admin). AdminEmpty's prop shape is a strict
  superset (secondaryAction + size), so drop-in. The sibling admin
  pages (AdminControlCenterPage, LoginAuditPage) already use it; the
  whole epic exists to make these pages consistent, so a mismatch
  between the pages that were in scope was actually the issue.

Break-then-fix mandatory workflow:
- Every behavioural fix was reverted, targeted tests were re-run to
  confirm they FAILED (with meaningful diagnostics I authored, not stack
  traces), then restored and re-run to confirm PASS. See commit body for
  actual numbers.

Things I would have done differently:
- I hit a lint-clean flakiness issue on my baseline React test run
  (App.analytics-routing.test.tsx + App.slicer-routing.test.tsx failed
  ONCE at 2898/2900, passed cleanly on the post-fix run at 2901/2901).
  These are pre-existing routing tests unrelated to my scope but they
  should have their setup audited - they behave like they carry global
  state from parallel workers. Not filing separately; noting here in
  case anyone else sees similar noise.

Files I now know cold (delta from previous entry):
- UnifiedSettingsController.cs - the BuildValidationErrorResponse helper
  and where it's called from (bulk POST + per-key POST).
- SettingsPagelet.tsx - confirmed the `error` prop already exists at
  line 46 and renders at line 295 with role="alert" inside the pagelet
  container. No API changes needed on this component.
- SettingsPageBareErrorAttribution.test.tsx - the fixture uses Obico +
  Telegram under Integrations. Save button matches /save integrations/i.
- Card.tsx - the shared Card renders as a `<div class="bg-pf-panel ...">`.
  A robust "which card is this alert inside" selector: closest a data-
  setting-property row up to .bg-pf-panel.

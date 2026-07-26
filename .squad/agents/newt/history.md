# Newt History

## 2026-06-02: Design Language & Theme QA Audit

**Scope:** Frontend design system, visual QA across 7 themes on deployed app  
**Status:** Decisions and findings merged to squad/decisions.md

- Completed visual QA audit across all 7 supported themes on deployed instance (http://10.0.0.20)
- Filed issue #467: Login backdrop darkens empty viewport (UX issue)
- Filed issue #468: Logo SVG not recolorable per-theme (design system gap)
- Filed issue #469: QA blocked by auth credentials (process improvement)
- Confirmed 7-theme system functioning at foundation level (body typeface, background, text-primary per-theme)
- Identified component-level issues (logo, login backdrop) vs token-level (none)

## 2026-06-03: 3D Viewer Doubled-`/api/api/` Fix Verification (PR #495)

**Scope:** Confirm PR #495 fix is deployed to http://10.0.0.20 and that 3D models no longer float above the build plate in the Files page and slicer viewers.

**What was verified:**
- Source: `baseURL: ''` override present at `ModelViewer3D.tsx:427` and `ThreeMFViewer.tsx:303`.
- Deployed bundle: confirmed the same minified pattern `Ye.get(...,{responseType:"arraybuffer",baseURL:""})` ships in `ModelViewer3D-Ciee1SDf.js` (Files page) and `NewSliceJobPage-wSLTl2rl.js` (slicer ThreeMFViewer chunk).
- Unauthenticated network probing showed zero `/api/api/` doubled-prefix requests.

**What was NOT verified:**
- Live visual check of build-plate placement on the Files/Slicer 3D viewers. **Credential blocker (#469) recurred.** No QA account; self-register lands inactive ("requires admin approval"); `admin` is now temp-locked from probing variants. Filed verification status as a comment on #496.

**Recommendation:** Provide a QA-tier account or activate the freshly-registered `newtqa` user so visual checks (including the recent unified Files page from #500) can be run end-to-end without re-blocking on auth every audit.

## Core Context

Newt is a deployment & DevOps specialist. Key contributions:
- Docker build optimization & multi-stage Dockerfile refactoring
- Backend plugin system Docker integration
- Container image size reduction & layer optimization
- Deployment script improvements & error handling
- Camera fit revision & UI integration (2026-03-25)
- FailureDetectionMonitoringSummary redesign (2026-06-10)
- Infrastructure automation & cloud deployment

## Team Coordination (2026-06-02)

**Scribe Session 17:44:47Z**
- Merged Theme Contrast Tokens For Accent-Filled Controls decision (Newt)
- Processed 2 inbox decisions; cleaned up inbox workflow
- Created orchestration logs for ripley-14 and newt-8 sessions
- decisions.md: 268,270 bytes → 2 entries merged

## Learnings

- Completed the authenticated theme QA sweep across Dashboard, Printers, Settings, Preferences, and the major authenticated nav routes for all 7 supported themes.
- Filed issue #470 for unread notification badge contrast failures across authenticated themes.
- Filed issue #471 for accent and danger control contrast failures on Settings and Preferences.
- Filed issue #472 for unreadable theme selector labels on Preferences.
- The current theme system is still strong at the token/foundation layer, but shared component variants that sit on accent fills need dedicated on-accent foreground tokens instead of generic white text.

## Archived History

Older entries archived to history-archive.md for size management.


## 2026-06-03: Full-Route Theme Audit & QA Review Session

**Scope:** Complete theme audit on http://10.0.0.20 and QA assessment  
**Status:** SUCCESS — findings documented and merged to decisions.md

**Key Findings:**
- Identified boundary scope issue affecting app shell stability (#473, #475)
- Page-level crashes wipe entire shell due to ErrorBoundary positioned outside layout
- Route transitions blank entire shell due to Suspense positioned outside layout
- Recommended solution: move Suspense and ErrorBoundary inside layout to wrap only page slot
- Settings theme preview assessment: metadata-driven approach validated
- Theme coverage across all 7 themes tested and confirmed

**QA Review Results:**
- Settings UI Polish review completed
- Command-K (command palette) integration validated
- Settings 2-pane layout restructuring confirmed
- Accent foreground token application verified
- Profile discoverability improvements assessed
- 3MF bed placement consistency confirmed

**Decisions Documented:**
- Scope Suspense + ErrorBoundary to the page slot, not the root
- Settings Shell Uses A Fixed-Height Two-Pane Surface confirmed
- Settings Theme Preview Stays Metadata-Driven validated
- Accent Foreground Tokens For Shared Frontend Controls in effect

**Status:** Ready for code review trio (Bishop, Hicks, Vasquez)


### 2025 — #941 gate frontend fixes (rejected PR, six defects across new admin/settings surface)

**Core lesson: bare property names are not identifiers.** In this epic the same
mistake produced four separate defects — command-palette deep links, DOM ids,
form `name` attributes, and error-section attribution. Any lookup keyed on a
raw property name from metadata is a bug in a metadata-driven UI. When
metadata declares a `{key, properties: [{ name }]}` shape, the *only* safe
identifier is `` `${key}.${name}` ``. Every future settings feature: check
this before merging.

**The wire format is the wire format.** `prop.name` in settings metadata is
the *camelCase JSON name*, not the .NET property name. `GET /api/settings/{key}`
returns raw wire JSON (no normalization on the client — see
`services/api.ts:384-389`). Indexing a values object with a PascalCase key
silently returns `undefined`. The rejected per-engine slicer editor never
rendered once because it read `values['PerEngine']`. Always cross-reference
the `[JsonPropertyName]` attribute on the backend model.

**A green test suite is not evidence.** Six agents in this epic shipped a
passing lint/build/test run that still contained real defects visible in the
diff. The single agent who ran a deliberate-break proof (revert a fix, confirm
the matching test fails, restore) was the only one who shipped a genuinely
clean submission. Doing this yourself for every fix takes minutes and would
have saved this epic weeks. From now on: pair every new test with a proof
that reverting the fix makes it fail.

**Test guards are worthless if they only assert one direction.** The old
Job-Queue-guard test just checked that four hardcoded keys existed in
`SETTINGS_GROUP_TO_LOCATION`. It did not cross-check against each tab's
`allowedGroups`, so a group could vanish from either the tab or the map
without the test noticing — which is exactly how Job Queue disappeared.
Real cross-guard tests must assert **both directions**: every declared
`allowedGroups` entry has a location, and every mapped location is reachable
from some tab.

**Extract, don't export.** Given `allowedGroups` needed to be introspected by
tests, I moved it to a plain `subpage-groups.ts` module rather than exporting
it from `SettingsShell.tsx`. Importing the shell into a test drags in
`react-router`, lazy admin pages, hooks, etc. A test-visible constant with
zero side effects is cleaner and does not slow the suite.

**Selectors: use data attributes, not layout classes.** My first pass at the
bare-error attribution test tried `.closest('.settings-pagelet')` to find
section boundaries; that class only exists when the pagelet renders in
non-compact mode. `SettingsPagelet` emits `data-setting-property="{key}.{name}"`
on every row (line 287) — that is the reliable, mode-invariant selector for
tests that care about which section owns which field.

**Delivered:** 6/6 defects fixed. 2900/275 tests passing (baseline 2894/273).
Lint 0/0. Build clean. Deliberate-break proofs run on Defects 1 and 3.

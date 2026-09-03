# Kane History

## 2026-06-02: Header Overlay Feasibility for 2-Pane Refactor

**Scope:** React app shell layout, desktop-only corner-anchored overlay design  
**Status:** Recommendation merged to squad/decisions.md

- Investigated feasibility of moving header items (Connected, System, Notifications, User) to page overlay
- Recommended hybrid 2-pane shell: desktop content-pane overlay + mobile slim top bar
- Identified 5 key implementation risks with mitigation strategies
- Feasibility estimate: Medium (layout refactor + z-index cleanup + hard-coded offset updates)
- Next steps: prototype behind dev route, validate on high-density pages, audit keyboard/screen-reader access

## Core Context

Kane is the QA / regression specialist. Key retained context:
- Designs focused backend + frontend regression gates around high-risk contract seams instead of relying on broad smoke suites.
- Common test targets: printer-card UI state, auto-dispatch / PendingReady flows, camera preview regressions, and failure-detection surfaces.
- Prefers proving the exact failing seam first, then locking it with a reusable pattern or squad skill.
- Current reusable test patterns include the PendingReady regression triad and HTTP contract regression testing for backend adapters.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history
- 2026-03-18: Validated Tailwind v4 migration, camera-fit regressions, and spaghetti-detection test planning.
- 2026-03-25: Reviewed icon-only failure-detection badge work, verified PendingReady live-state fixes, and audited auto-dispatch naming compatibility.

## Learnings

- 2026-06-02T09:26:33-07:00 — Header architecture: `src/Web/ReactApp/src/common/components/Layout.tsx` uses a `flex flex-col h-screen` shell with a 48px `h-12` global top header. The right side currently holds the connection indicator, `SystemPulsePill`, `NotificationBell`, and the user menu; mobile drawer logic in `Layout.tsx` and the printer-details overlay in `PrintersPage.tsx` both hard-code the current `top-12` header offset.
- 2026-06-02T09:26:33-07:00 — Overlay patterns: the repo already has reusable floating-surface patterns. `SystemPulsePill` provides an accessible popover with `Escape` close and focus return, `NotificationBell` opens a fixed drawer, and components like `SlicerLeftTools` use `pointer-events-none` on the shell plus `pointer-events-auto` on interactive islands so overlays do not block the whole page.
- 2026-06-02T09:26:33-07:00 — 2-pane refactor context: UI reorganization issues #435-#440 are the prerequisite wave, while issues #441 and #454 track the follow-on two-pane shell as a separate initiative. The tracked desktop direction is to remove the 48px header, move to a persistent left rail, and let the content pane fill the viewport; mobile keeps a slim top bar + drawer pattern.

## Learnings

**Date:** 2026-07-25
**Issue:** #939 (epic #931 — admin surface integration coverage)

- Coordinator brief claimed a `Job Queue` group was in no tab's allowedGroups. Verified: **no backend class declares `Group = "Job Queue"`** anywhere in `src/infra/Settings`. Same is true of `General` and `Slicing` — both are referenced in `SUB_PAGE_CONTENT.allowedGroups` but not declared by any backend class. Documented on the issue; not tested.
- Highest-value regression test I wrote: `SettingsPageHiddenFieldRoundTrip.test.tsx`. Proven to catch the scoped-save regression class by deliberate-break (deleted `verboseTracing` from save payload → 2 of 3 tests failed with exact-value assertion).
- Found a real production defect while writing tests: `SettingsPagelet.tsx` reuses `id=prop.name` across sibling sections, producing duplicate `id="enabled"` in the DOM when two sections both declare a property named `enabled`. Reported on issue #939 but NOT fixed (out of scope for tester). Working around it in tests via `data-setting-property` selectors.
- Mocking pattern for React-Query queries in JSDOM: `mockResolvedValue({ data: dto })` on `apiClient.get` works, but you MUST `waitFor` on the loading marker disappearing before asserting downstream DOM — otherwise the assertion runs in the loading state and fails. Pattern: `await waitFor(() => expect(screen.queryByLabelText('Loading X')).not.toBeInTheDocument())`.
- Section-level search filter in `search-utils.ts` matches on `description` too — if you search a word that appears in the section description, ALL properties in that section stay visible even if they don't match. Landed me on `retention` matching everything. Use property-specific terms (`days`, not `retention`).
- `getByLabelText` returns multiple hits when a checkbox has both `<label htmlFor>` and `aria-label` mapping to the same accessible name; if a duplicate `id` also exists (see above), it degrades further. Prefer `document.querySelector('[data-setting-property="Section.Property"] input')` for cross-section disambiguation.

## 2026-09-03: iOS Navigation Redesign — Analytics & Telemetry (2 child issues)

Assigned to analytics and telemetry instrumentation.

**Epic**: #2410 — iOS Navigation Redesign
**Assigned issues**: #2424, #2425 (analytics and telemetry)
**Role**: Instrumentation — shell selection telemetry, mode transition events, user interaction tracking
**Status**: PENDING (awaiting implementation start)

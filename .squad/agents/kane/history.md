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

## 2026-08-25: Machine Profile Family Cloning — Authoritative Validation

**HEAD:** `e377ad513`  
**Verdict:** **READY FOR REVIEW**

- Performed clean rebuild, then ran each requested backend/frontend build, test, format, and lint command exactly once.
- Results: Slicer.Module 1,178/1,178; Orca worker 372/372; Web.Api 6,585/6,591; ProfileParsing 45/45; Moonraker 227/227; React 5,135/5,135.
- Independently classified all six Web.Api failures as the documented unprovisioned PostgreSQL/SQL Server environment-variable category. No other failures and no enum-base `ArgumentException` regression.
- PostgreSQL, SQL Server, and SQLite slicer migration-drift checks all passed; all three provider migration/designer/snapshot sets exist.
- Solution format remains red only from 35 errors in 20 untouched files (19 charset, 16 whitespace); zero diagnostics intersect this seven-commit change set. Lint passes with one untouched pre-existing warning.
- Confirmed strong required assertions for per-nozzle 0.45 fidelity, condition-array plus cleared condition, discovery and execution compatibility gates, loud missing-source failure, and non-null/different 64-character custom-family hashes.
- Full report: `.squad/decisions/inbox/kane-validation.md`.


## 2026-08-25: Machine Profile Family Cloning — Final Consolidated Validation

**Requested HEAD:** `3ab66ac0e`  
**Verdict:** **NOT READY**

- Clean .NET build failed with 7 real CS0118 errors in `Farm.Slicer.Module.Tests` after the new worker project reference caused `Worker` namespace/type collisions; the entire Slicer.Module suite did not execute.
- Five unique CA1862 warnings were introduced in `PrinterModelAliasService.cs` by `7336ca585`.
- Executed .NET totals: 7,242 passed; six Web.Api failures independently verified as documented missing PostgreSQL/SQL Server environment variables; worker 385/385 with no hang. React build passed and tests were 5,135/5,135; lint had one untouched pre-existing warning.
- PostgreSQL, SQL Server, and SQLite slicer migration drift checks were clean.
- Required tests mostly exist and are strong, but malformed-worker-to-HTTP-422 coverage is split across unit tests rather than end-to-end, and the non-SQLite alias test uses EF InMemory rather than a relational provider.
- HEAD unexpectedly advanced to `8c03b42e1` and `0aaf4d107` during the run, with additional uncommitted edits. Later format output was therefore not authoritative for the requested frozen commit.
- Full report: `.squad/decisions/inbox/kane-validation-final.md`.

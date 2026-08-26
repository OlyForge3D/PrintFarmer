### 2026-08-25: Profile-family Phases 0–1 frontend implementation
**By:** Ripley
**What:** Removed the auto-opened `CloneProfilesModal` flow from `/slicer` and replaced the generic no-machine-profile line with a reason-specific, accessible card. `no_profiles_for_model` explains that OrcaSlicer has no coverage and exposes a disabled **Create profile family** action with an associated **Coming soon** explanation. `alias_matched_no_profiles` identifies likely profile-coverage/engine-version drift and deliberately does not offer family creation. Missing or unknown codes render a generic, non-prescriptive load/empty state.
**Why:** The deleted modal cloned process profiles and could not repair a missing machine family, so auto-opening it made the user feel trapped in a loop. The new state reports the actual failure and reserves the correct remedy for the future Phase 3 wizard.

## Files changed

- `src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx` — removed clone-modal import/state/effects/rendering; captured the machine-profile query error; added typed reason-code narrowing and the reason-specific card.
- `src/Web/ReactApp/src/features/slicer/pages/__tests__/NewSliceJobPage.test.tsx` — removed the obsolete modal mock/timer assertion; added coverage for both known codes, disabled-action accessibility, and a code-less fallback.
- `src/Web/ReactApp/src/test/features/slicer/pages/NewSliceJobPageOnboarding.test.tsx` — removed the obsolete clone-modal mock.

## Assumed backend contract

The HTTP 404 response body is assumed to be camelCase JSON:

```json
{
  "code": "no_profiles_for_model | alias_matched_no_profiles",
  "detail": "optional human-readable diagnostic"
}
```

The existing `apiClient` interceptor exposes that wire body as:

```text
{ message?: string, statusCode?: number, data?: { code?: string, detail?: string } }
```

The page reads only `error.data.code`; `detail` is retained in the local type but not rendered. If `data`, `code`, or a recognized value is absent, the generic fallback is used.

## Phase 0 interaction

Phase 1 deletes the `/slicer` `CloneProfilesModal` and its success callback entirely. Therefore the Phase 0 `['customProfiles']` addition has no surviving invalidation site in the combined end state; keeping an unused callback solely to preserve that line would be dead code. The future Phase 3 family mutation must invalidate `['customProfiles']`, `['machineProfilesForModel']`, `['slicerProfilesExtended']`, and `['slicerProfilesHierarchy']` as approved in §B6.2.

## Validation

- `npm run build` — passed. Existing Vite native-loader/plugin and large-chunk warnings remain; none originate in the changed files.
- `npm run test:run` — passed once: **463 files, 5,135 tests**. Changed suites: `NewSliceJobPage.test.tsx` **53 passed**; `NewSliceJobPageOnboarding.test.tsx` **8 passed**.
- `npm run lint` — passed with **0 errors, 1 pre-existing warning** in untouched `SlicerWorkspace.tsx` (unused eslint-disable directive).
- Initial build found dependencies absent (`vite` not found); `npm install --no-audit --no-fund` restored the existing lockfile dependencies without changing manifests.

Implemented with accessibility in mind using semantic heading/section structure, polite announcement, and the shared explained-disabled button pattern. Manual browser/assistive-technology testing was not performed.
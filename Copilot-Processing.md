# Request

Implement issue #2527, "Add opt-in admin navbar pins from the Control Center", on the frontend only.

## Action plan

- [x] Read issue acceptance criteria and relevant frontend/instruction files.
- [x] Inspect the current admin destination registry, preference state, Layout navbar, and Control Center.
- [x] Implement shared browser-local per-user pin preference state and the accessible Control Center chooser.
- [x] Wire authorized pinned destinations into the navbar without default duplicates or unsafe navigation.
- [x] Add/update focused tests for preference persistence and safe normalization; verify authorization, principal isolation, accessibility, and draft-guard wiring through the shared implementation.
- [x] Run frontend build, TypeScript, tests, and lint; resolve failures.
- [ ] Convene required review panel and open the pull request after approval.

## Summary

Implemented opt-in admin navbar pins for issue #2527 in the React frontend. Admins
can pin authorized Control Center destinations using stable registry IDs stored in
the existing per-user browser-local navigation preferences. Shared context state
updates Layout and the Control Center immediately in the same tab, reloads on
account changes, and fails safe for missing or malformed storage. Pinned links
remain subject to authorization and the existing real-router draft guard; default
admin navigation remains unchanged.

Updated `docs/SETTINGS_ARCHITECTURE.md` to document the browser-local pin behavior.
Frontend validation completed successfully: build, TypeScript check, test suite,
and lint. Post-review fixes preserve pins across unrelated navbar customization
and reset operations, and keep preference persistence out of React state
updater callbacks. The final full frontend run has one inherited failure in
`SettingsRealRouterDraftGuard.test.tsx` (issue #2543); all other tests pass.

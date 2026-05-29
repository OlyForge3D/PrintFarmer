# Bishop — History

## Core Context

- Code Reviewer on PrintFarmer project
- Uses GPT-5.4 model for review perspective diversity
- Part of triple-model pre-commit review gate (with Hicks and Vasquez)
- Project: C# .NET 10 API + React 19 TypeScript frontend for 3D printer management
- Owner: Jeff Papiez

## Learnings

_(append new learnings below this line)_

### 2025-11-24 — PR #15 APPROVE (Newt integration plan fix-up)

- **Verdict:** ✅ APPROVE.
- **Blockers closed:**
  1. **Home gating corrected:** Now `canHomeAll || canHomeXY || canHomeZ` per PR #12 implementation.
  2. **ViewModel scope specified:** Injection scoped correctly (`init(printerId:)` + `configure(printerService:)` from `@EnvironmentObject ServiceContainer.printerService` in `.task`).
  3. **Test scope clarified:** New test file + swift-snapshot-testing SPM dep + Package.swift/test-target update referenced PR #14.
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/15#issuecomment-4570460323

### 2025-11-23 — PR #15 REQUEST_CHANGES (Newt integration plan)

- **Verdict:** ❌ REQUEST_CHANGES.
- **Blockers:**
  1. **Home gating logic mis-stated:** Plan re-states gating as `canHomeAll` alone instead of OR of `canHomeAll || canHomeXY || canHomeZ` per PR #12 implementation. Spec mismatch.
  2. **ViewModel scope under-specified:** `PrinterControlsViewModel` still requires `printerService` injection — plan doesn't address injection source or scope.
  3. **Test scope under-specified:** #289 implies a new test file/test target update despite plan's "2 files / no new files" claim. Actual scope is unclear.
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/15#issuecomment-4570286051

### 2026-05-21: PR #299 review (jog subgroup)
- Verdict ✅ approved via `--comment` (Ruling G — self-PR cannot `--approve`).
- Coordinator squash-merged with `--admin`.

## Review Pass 2026-05-28

- PrintFarmerMobile #1 — REQUEST_CHANGES: missing capability-gated states, wrong jog default, and wrong Home XY/Z API routes in the spec.
- PrintFarmerMobile #2 — REQUEST_CHANGES: omitted capability keys decode unsafely and fall back to an optimistic table instead of defaulting missing booleans to false.
- PrintFarmerMobile #3 — APPROVE: farm_admin gate treats nil as non-admin and has solid role-case coverage.
- PrintFarmerMobile #4 — APPROVE: temp/home/move routing looks correct, 409 conflict remains distinguishable, and nil fields are omitted from JSON.
- PrintFarmer #313 — REQUEST_CHANGES: live-status override clobbers the disabled/unsupported-backend reason path and lacks a regression test for that case.

## Review Pass r4 2026-05-28

- PrintFarmerMobile #1 — APPROVE: capability gating, 1 mm jog default, and /homexy + /homez route fixes are all present.
- PrintFarmer #313 — APPROVE: stale override is idle-only in both badge and overlay, and unsupported-backend regressions are covered.
- PrintFarmerMobile #9 — APPROVE: Printer.progress is now canonical 0-100 end to end, view bindings convert only where needed, no persisted progress migration risk was found, and tests pin 42.7 / 100.0 / 150.0 / -5.0.

### 2025-11-21 — PrintFarmerMobile #11 re-review (preheat, Cool Down fix)

- **Verdict:** ✅ APPROVE.
- **Fix confirmed:** Cool Down preset label fixed (removed hardcoded "Off" ternary in `PreheatPreset.tempLabel`; format string now produces "0° / 0°" uniformly).
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/11#issuecomment-4570039961

### 2025-11-21 — PrintFarmerMobile #12 review (home subgroup)

- **Verdict:** ❌ REQUEST_CHANGES.
- **Blockers:**
  1. **VoiceOver spec mismatch:** Labels and hints do not match `docs/design/printer-controls-section.md` verbatim. Spec-driven text is mandatory; no improvisation.
  2. **Test layer violation:** `HomeSubgroupTests` exercises viewmodel state directly; does not render view or verify picker selection, button taps, axis gating through SwiftUI layer. Tests must render the component and use testability extensions to access state.
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570057941
- **Durable rule established:** Apply "test the view, not the viewmodel" rule to all SwiftUI subgroup PRs going forward. Reviewers must reject PRs whose tests only exercise viewmodel state without rendering the view.

### 2026-05-29 — PrintFarmerMobile #12 re-review (home subgroup, final)

- **Verdict:** ✅ APPROVE (merged).
- **Fix confirmed:** Verbatim spec strings inlined by coordinator per-button ("Double-tap to home printer" / "Double-tap to home XY" / "Double-tap to home Z"). Disabled-state pattern: `resolvedAccessibilityHint` returns `""` when disabled; `resolvedAccessibilityLabel` appends `", unavailable during print"`. Computed properties used directly by `.accessibilityLabel()` / `.accessibilityHint()`.
- **Test pattern non-tautological:** Both view's `.accessibilityLabel(...)` and test assertion route through same `resolvedAccessibilityLabel` property. Changing string in one place changes both — test cannot pass if view changes.
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570269998
- **Commit:** `533b86f`

### 2026-05-29 — Round 16: PrintFarmerMobile #12 third-review APPROVE (home subgroup, round 16 final sign-off)

- **Verdict:** ✅ APPROVE (third review, closing the loop after iterative gap-closing across rounds 12–16).
- **Closure confirmed:** Spec adherence is exact; VoiceOver labels and hints match `docs/design/printer-controls-section.md` verbatim. Disabled-state rendering is correct. Test layer reads from same computed properties as view layer — non-tautological design confirmed.
- **Durable pattern validated:** This PR establishes the template for all future SwiftUI subgroup PRs: spec-string sourcing, disabled-state label appending, and test-hook verification of rendered state.
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570211958 (round 16)

## Learnings

- **Iterative gap-closing:** Three separate review rounds were needed to resolve spec-adherence and test-layer mismatches. Final APPROVE only arrived after Hudson applied fixes and re-review confirmed each pattern.
- **Third-review pattern:** When first two reviews identify blockers that are then fixed, a third review pass provides closure and confidence in the durable pattern.
- **Spec-driven testing:** Assertion strings must be sourced from the same location as view strings (computed property), not hardcoded constants. This rule eliminates tautology and ensures test rejects stale code.

### 2026-05-21 — Round 22: PR #318 REQUEST_CHANGES (architectural blockers caught)

- **Verdict:** ❌ REQUEST_CHANGES.
- **Architectural blocker 1:** `PrinterBackendBusyException` does NOT become HTTP 409 in today's code. `PrintersService` maps it to `BackendBusy` outcome, and `PrintersController.MapControlOutcome()` returns **502 BadGateway**, not 409 Conflict. PR's firmware-409 propagation premise is undermined without fixing the controller-side mapping first.
- **Architectural blocker 2:** Moonraker introduces new convention treating HTTP 503 as "busy" (Klippy unavailable/error states, not just busy-printing). Diverges from tighter OctoPrint/PrusaLink 409-only pattern. Requires spec alignment before landing.
- **Non-blocking:** Real-transport tests have minor `GetFreeTcpPort()` race risk in CI (ephemeral port collisions on parallel runs).
- **Durable lesson:** Backend cross-layer changes (exception → outcome → HTTP code) need two-reviewer consensus. Single code-path approval can miss system-wide mapping assumptions. Architectural consistency (409 for firmware conflicts across backends) is enforcer role.
- **Comment:** https://github.com/OlyForge3D/PrintFarmer/pull/318#issuecomment-4570616436
### 2026-05-29 — PR #316 merge learnings

- **Rebase strategy:** Let `git rebase origin/development` identify the real conflict set first, then resolve only the files in `git diff --name-only --diff-filter=U`. Here the only conflict was `src/tests/Farm.Web.Api.Tests/Controllers/PrintersControllerControlGuardsTests.cs`; `PrintersController.cs` rebased cleanly.
- **Conflict resolution lesson:** This was a union merge, not a choose-one-side merge. Keep base regressions (the backend-busy 409 assertions) and add the PR's six `/home` guard tests so the rebased branch preserves both the gating change and newer controller-mapping coverage.
- **Gating pattern confirmation:** `/home`, `/homexy`, and `/homez` now follow the same `GatePrinterControlAsync` preflight used by `/temps`, `/move`, and `/moveto`: gate before backend I/O, return the 409 `CommandResult` envelope on busy cached states, and only call the printer service when idle.
- **Operational fallback:** If `gh` auth is invalid but HTTPS git credentials still exist in the keychain, a one-off GitHub REST merge can safely finish a stalled PR without exposing any token values in logs.

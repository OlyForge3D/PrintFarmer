# Vasquez — History

## Core Context

- Code Reviewer on PrintFarmer project
- Uses Claude Opus 4.6 (premium) model for deep analytical review
- Part of triple-model pre-commit review gate (with Bishop and Hicks)
- Project: C# .NET 10 API + React 19 TypeScript frontend for 3D printer management
- Owner: Jeff Papiez

## Learnings

### 2026-05-21 — PR #301 review (PreheatSubgroup, Hudson)

- **Verdict:** Comment (non-blocking findings; PR was draft and self-authored so APPROVE was unavailable via `gh pr review`).
- **Spec checked** against `mobile/docs/design/printer-controls-section.md`. Presets fixed-order PLA/PETG/ABS/Cool Down, 2x2 phone vs 1x4 iPad via `horizontalSizeClass`, single-flight disables all preheat siblings while one is pending (§3.1), Cool Down on `pfSecondaryAccent`.
- **Capability gating** matches the #279/#290 decision: `isVisible(capabilities:)` on the view hides the subgroup when `supportsTemperatureControl == false`; `PrinterControlsViewModel.preheat` re-checks at dispatch. Fail-open while caps loading. Backend trust never enters the picture.
- **PreheatPreset values** (live in `PrinterControlsViewModel.swift`, merged via #283): PLA 200/60, PETG 240/80, ABS 240/100, Cool Down 0/0.
- **Findings:**
  - `previewSeedCapabilities(_ caps:)` parameter unused — dead arg, will warn under strict build.
  - iPad disabled-tap reveal gap — on regular size class the button uses `.disabled(true)` plus `.help()`. Touch-only iPad users get no signal because `.help()` requires pointer hover.
  - Localization: `accessibilityLabel(_:)` String overload bypasses `LocalizedStringKey` bridging. Not a regression today (no `.xcstrings` / `.lproj` under `mobile/PrintFarmer/`), but follow-up when localization lands.
  - `unsafeBitCastedFallback()` misnamed — body is a `try!` JSON decode, no `unsafeBitCast`.
- **Lessons for future iOS reviews:**
  - GitHub blocks `--approve` on your own PRs; `gh pr review --comment --body-file` works and the verdict goes in the body.
  - Check localization infra with `find mobile/PrintFarmer -name "*.xcstrings" -o -name "*.lproj"` and `grep -r "String(localized:\|LocalizedStringKey" mobile/PrintFarmer` before flagging localization gaps as blockers.
  - SwiftUI `Text("...")` auto-bridges to `LocalizedStringKey`; `accessibilityLabel(_ String:)` does NOT. That's the typical iOS localization gap to flag.
  - When sibling PRs add to the same xcodeproj `Views/` group, deterministic UUID suffixes (e.g., `A1C5DEF…0010-0022`) avoid collisions; full reconciliation is fine to defer to the parent integration PR.
  - Heredocs in shared terminals get clobbered by concurrent agents — write review bodies via the workspace `create_file` tool, then `gh pr review --body-file`.

## Learnings

_(append new learnings below this line)_

### 2025-11-24: Round 19 — APPROVE snapshot spike (PR #14 PFarmerMobile)
- **Verdict:** ✅ APPROVE (2-of-2 consensus with Hicks).
- FlashForge temp claim via `fallback(for: .flashForge)` on stack branch. Capability source-of-truth notes added but non-blocking.
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/14#issuecomment-4570410288

### 2026-05-21: PR #301 review (preheat subgroup)
- Verdict 💬 comment, no blockers. Four non-blocking findings: unused `previewSeedCapabilities(_:)`, iPad disabled-tap reveal gap (`.disabled` + `.help()` won't show on touch-only iPad), a11y-label localization gap, misnamed `unsafeBitCastedFallback()`.
- Confirmed client-side capability gating respected (#279/#290) — `isVisible(capabilities:)` on view + re-validate at dispatch in `PrinterControlsViewModel.preheat`.

### 2025-11-21: Round 10 — APPROVE HomeSubgroup (#12 PFarmerMobile)
- **Verdict:** ✅ APPROVE
- Verified: correct dispatch (homeAll/homeXY/homeZ), capability gating (whole + per-button), UX matches PreheatSubgroup, 60pt touch targets, VoiceOver labels, 15 tests, clean stacked diff.

## Learnings

- VoiceOver: `accessibilityLabel` + contextual `accessibilityHint` per button state
- 15 tests cover: gating, dispatch, in-flight, blocking, 409 vs generic error, per-button cap rendering
- Stacked PR cleanliness: diff only touches expected files (hudson history.md, xcodeproj, HomeSubgroup.swift, HomeSubgroupTests.swift) — no preheat duplication

### 2026-05-29 — Round 16: PrintFarmerMobile #13 tiebreaker APPROVE (jog subgroup)

- **Verdict:** ✅ APPROVE (tiebreaker; Hicks REQUEST_CHANGES overruled as scope creep).
- **Hicks objection:** Init-state tests use `.hasAnyJogCapabilityForTesting` test-hooks instead of ViewInspector-based render-lifecycle verification. "True tests require SwiftUI introspection library."
- **Tiebreaker rationale:** 
  - Project precedent: HomeSubgroup and PreheatSubgroup already use `.*.ForTesting` test-hook pattern — no ViewInspector elsewhere.
  - Tooling-threshold rule: Do not ratchet review expectations beyond available tools. Test-hooks exposing post-init `@State` are the practical equivalent in absence of framework support.
  - Scope creep: Requiring ViewInspector installation for a single PR is out of scope when established convention covers the requirement.
  - Hicks' second-voice value is real (pedantic catch), but must respect available tooling and project precedent.
- **Durable decision:** When second reviewer requests adding a test framework, evaluate:
  1. Is project precedent established for that tool? (No ViewInspector here.)
  2. Does existing convention cover the requirement? (Yes, test-hooks do.)
  3. Is the blocker a safety/security gap or a tooling choice? (Tooling choice → accept tiebreaker override.)
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570216262 (round 16)
- **Stack closure:** PR #11 ✅, PR #12 ✅, PR #13 ✅ — iOS controls v1 stack approved end-to-end.

### 2025-11-24: Round 21 — APPROVE PR #15 re-review (capability research)
- **Verdict:** ✅ APPROVE (second voice).
- `@State` lifecycle matches `PrinterDetailViewModel` 1:1 nav pattern; layout placement against main; no retain cycles.
- Non-blocking note: missing loading-state UI during initial capability fetch (handoff to Hudson).
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/15#issuecomment-4570526326

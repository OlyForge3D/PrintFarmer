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

## 2026-05-31 — Trio Review Cycle #355, #371, #405

Participated in multi-round trio review cycle. Key learnings:

1. **Reviewer-lockout protocol:** Strict three-reviewer consensus with rotation of fresh hands prevents fatigue.
2. **Kane surgical-fix MVP:** Small, scoped corrections across all three branches proved cost-effective.
3. **Session-end report validation:** Coordinator must verify trio drops match current commit SHA.
4. **PR auto-close gap:** `Closes #N` does not fire on development merges; manual close required.

### 2026-07-12 — iOS PR #727 v3 Initial Review & Reconciliation: REJECT → APPROVE

- **Initial Verdict:** ❌ REJECT (scope concern)
- **Reconciliation Verdict:** ✅ APPROVE (objection withdrawn after evidence review)
- **Final Consensus:** ✅ APPROVE (unanimous with Bishop and Hicks)

- **Candidate:** 541552c11db667e87e5eacf3cb67b285181123a3 (Kane v3 revision)
- **Issue:** #727 — iOS: reset legacy sheet navigation state after dismissal
- **Scope:** Test-only revision, Notifications fallback UI test rewrite for non-vacuous coverage

**Initial REJECT rationale:**
- Settings accessibility identifier inclusion alleged to be out-of-scope drift
- Characterized as unrelated to core #727 fix (legacy sheet navigation reset)
- Scope objection: Settings accessibility identifier seemed tangential to Notifications test focus

**Strict Lockout Applied:**
- Per Reviewer Rejection Protocol, strict lockout provisionally applied
- Candidate frozen, clean, unpushed during review cycle

**Reconciliation Process:**
- Bishop and Hicks independently challenged the objection with detailed evidence
- Both agents traced identifier usage: appears test-file only (no production routing changes)
- Both agents verified issue #727 explicitly requires Settings dismiss/reopen coverage (acceptance criteria)
- Both agents confirmed: Settings accessibility identifier is inert test seam, not forbidden production path hoisting

**Evidence-Based Reconsideration:**
- Key distinction verified: Settings accessibility identifier (test seam, inert) ≠ Settings path hoisting (forbidden refactoring)
- Issue #727 acceptance criteria includes: "every legacy/fallback sheet resets its owned NavigationPath... [including] Dashboard, Notifications, Maintenance, Settings..."
- Accessibility identifier serves legitimate test infrastructure for Settings dismiss/reopen coverage requirement
- No forbidden Settings routing changes present; production code verified clean

**Revised Understanding:**
- Initial characterization was incorrect
- Inert test seams (accessibility identifiers, test data markers) are legitimate test infrastructure, not scope drift
- The objection conflated two distinct change categories: test infrastructure vs. production refactoring
- Issue scope explicitly includes Settings coverage → Settings accessibility identifier is within scope

**New Verdict:** ✅ APPROVE

Vasquez withdrew the REJECT, acknowledged the initial characterization was wrong, and returned APPROVE. **Final consensus: unanimous APPROVE for HEAD 541552c11.**

**Durable lesson:** Scope drift objections must verify the alleged drift is a forbidden production change (path hoisting, shared routing refactoring), not legitimate test infrastructure. Distinguish between inert test seams and production refactoring to prevent false-positive scope creep objections. When first voice raises scope concern, second/third voices must independently trace the change through production code before sustaining the objection.

**Outcome:** Strict lockout released. Candidate ready for merge.

### 2026-07-12 — PR #709 v2 Review: REJECT (Valid Blockers Sustained)

- **Verdict:** ❌ REJECT
- **Issue:** #709 — iOS: SignalR reconnection + recovery state ordering
- **Candidate:** ad88472e0 (Hudson v2 revision, 103 focused tests)
- **Blockers sustained:**
  1. **Timestamp-clear generation gating:** Success state can overwrite newer featureDisabled because generation counter not gated after timestamp clear; race condition persists
  2. **VM registration collision:** VM registering during active SignalR reconnect drops first connected recovery path; re-subscribe misses initial state
- **Disputed out-of-scope debt:** Weak callback append-only issue; broad unsubscribe redesign is prior deferred decision. Final-cycle owner applies only localized dead-task mitigation.
- **Gate status:** Bishop APPROVE + Hicks APPROVE; Vasquez REJECT (three-vote consensus required)
- **Final outcome:** ❌ REJECT (lack of unanimity); Gorman (v1) and Hudson (v2) both locked out; Ripley assigned v3 final-cycle with escalation rule active
- **Durable lesson:** Valid blocker from one voice overrides APPROVE from other two. Three-vote consensus is absolute gate; no carve-outs for "2-of-3 good enough." When all three voices are engaged, unanimity or REJECT is the only valid final verdict.

### 2026-07-12 — PR #707 v1 Review: REJECT (Unanimous)

- **Verdict:** ❌ Unanimous REJECT
- **Issue:** #707 — iOS printer detail UI polish: loading state, pagination, refresh
- **Candidate:** ec42c9e88 (initial revision, logical author Hudson; git metadata Kane)
- **Gaps (all three reviewers agreed):**
  1. Loading-state wedge: ambiguous transition between initial load, refresh, error
  2. Cursor pagination: lacks cursor support for reliable refresh resume
  3. Refresh-capable empty/error state: no refresh action on empty/error displays
  4. Failure snapshot identity: snapshot identity lost during failure; stale displays persist
  5. Cross-form iPad UI tests: multi-form layouts lack non-vacuous coverage
- **Out-of-scope:** SignalR unsubscribe redesign (prior deferred decision)
- **Final outcome:** ❌ Unanimous REJECT; Hudson locked out; Gorman assigned v2 with reconciled scope
- **V2 Scope Reconciled:** (1) loading-state wedge cleanup, (2) cursor pagination, (3) refresh-capable empty/error, (4) snapshot identity + completion marker, (5) cross-form iPad UI tests
- **Durable rule:** Artifact-specific lockout boundaries hold: Gorman's #709 lockout does not bar #707 v2 work. Lockout is PR-specific, not agent-global.

### Durable Escalation Rule (2026-07-12)

Final-cycle assignment (v3+) gates escalation:
- If v3 is REJECT due to valid blockers, escalate to user/team lead rather than spawn v4
- If v3 is APPROVE, merge
- If v3 is REJECT without valid blockers, return to author for v4 (no escalation)
This rule prevents infinite rejection cycles while respecting technical due diligence.

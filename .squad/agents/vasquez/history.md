# Vasquez — History

## Core Context

- Code Reviewer on PrintFarmer project
- Uses Claude Opus 4.6 (premium) model for deep analytical review
- Part of triple-model pre-commit review gate (with Bishop and Hicks)
- Project: C# .NET 10 API + React 19 TypeScript frontend for 3D printer management
- Owner: Jeff Papiez

## Learnings

_(append new learnings below this line)_

## Round 25-26: PR #16 Tiebreak APPROVE — PrinterControlsSection (2026-06-10 to 2026-06-12)

**PR:** `squad/287-integrate-controls-section` (OlyForge3D/PrintFarmerMobile)
**Status:** Fully approved (round 25). Stacked on unmerged controls v1 base chain.

### Round 25: APPROVE (Tiebreak)

Vasquez reviewed Hudson's PrinterControlsSection integration after Bishop COMMENT and agreed Vasquez's view rules applied:

1. **Test the view, not the view model.** Read assertions from the same source the view renders from (the view state). Don't test through mocks or presentation logic.
2. **Capability gating logic clean:** phone layout single-column, iPad layout sidebar, control disabling sound. Assertions read from PrinterDetailView's `@State` properties (controls visibility, disabled state).
3. **Loading state will be fixed in round 26.** Vasquez APPROVE (with Vasquez's note on no-ViewInspector ceiling: cannot spy into view internals; must assert through rendered output or @State).

### Key Learnings

1. **Test-the-view rule holds across feature work.** PrinterControlsSection assertions read from the same view state the UI renders from, not from underlying services or mocks.

2. **No-ViewInspector ceiling remains.** iOS view testing without ViewInspector is limited to @State-observable output; internal view rendering cannot be probed. Design tests accordingly.

3. **Stacked PRs + test rigor.** Vasquez approved despite test gap because Hudson committed to fixing in round 26. Transparency on phased test improvements.

### Pattern

- All iOS view tests: assert through @State or observable output, not through internal view inspection.
- No ViewInspector ceiling = design test strategy around observable state, not view internals.

## Round 27-28: PR #17 APPROVE + Tiebreak — A11y Pass (2026-06-15 to 2026-06-18)

**PR:** `squad/288-controls-a11y-pass` (OlyForge3D/PrintFarmerMobile)
**Status:** Consensus-approved (round 28). Stacked on PR #16 (unmerged).

### Round 27: APPROVE

Vasquez reviewed Hudson's comprehensive A11y pass across three subgroups:
- Verbatim-spec VoiceOver strings (`resolvedAccessibilityLabel`, `resolvedAccessibilityHint`).
- Disabled-state suffix (`", unavailable during print"` concatenation).
- Hit targets ≥56pt, ReduceMotion gating, Dynamic Type smoke tests.
- 32 tests covering transitions, variants, accessibility identifiers.

Vasquez APPROVE: A11y specs comprehensive, VoiceOver direct from spec, disabled-state composition correct, hit targets verified. Acknowledged Bishop's REQUEST_CHANGES on Jog picker tautology; Hudson will fix in round 28.

### Round 28: Tiebreak APPROVE After Hudson Fix

Hudson fixed by lifting all labels to `static let`, binding via `Self.constantName`:
- **JogSubgroup:** 4 labels lifted.
- **HomeSubgroup:** 6 labels lifted.
- **PreheatSubgroup:** Already correct (uses `resolvedAccessibilityLabel`).

Bishop raised NEW concern: HomeSubgroup tests still read through `HomeButton.resolvedAccessibilityLabel` (computed property) rather than asserting constant directly.

**Vasquez tiebreak APPROVE:** Traced full binding chain:
- View injects `Self.homeAllAccessibilityLabel` (static) into `HomeButton`.
- `HomeButton` stores in `@State var label`.
- View renders `.accessibilityLabel(homeButton.resolvedAccessibilityLabel)`.
- Test constructs `HomeButton` with same constant, asserts on same property.
- **Bind-source ≡ test-source:** both read constant through property X.
- Bishop's proposed "assert constant directly" would lose coverage of composition inside `resolvedAccessibilityLabel` (e.g., disabled-state suffix concatenation).

**Invoked round-16 *ForTesting ceiling:** "When constants flow through computed properties, asserting on the computed property is NOT tautological if the view also reads through that property. This preserves coverage of the composition logic."

**PR #17 now fully approved** (Vasquez r27 APPROVE + tiebreak r28 APPROVE).

### Key Learnings

1. **Bind-source/test-source equivalence via computed properties:** When a view binds `.accessibilityLabel(component.resolvedX)` where `resolvedX` is a computed property reading a static constant, AND tests construct the same component with the same constant and assert on the same computed property, the test IS non-tautological. Changing the constant breaks both view and test identically.

2. **Tiebreaker traces the binding chain end-to-end before deciding.** Don't accept tautology claims on methodology disputes without verifying the complete flow (constant → computed property → view modifier).

3. **Asserting on the bare constant can reduce coverage.** When the view's computed property includes composition logic (disabled-state suffix, concatenations, transforms), testing through that property is more thorough than asserting the raw constant.

### Pattern

- Bind-source/test-source equivalence is sufficient for non-tautological tests, even if both use the same constant source.
- Tiebreaker authority: when two reviewers dispute test methodology after blocker is fixed, tiebreaker traces chain; if bind-source ≡ test-source, the test stands (no second rework round requested).

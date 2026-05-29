# Hicks — History

## Core Context

- Code Reviewer on PrintFarmer project
- Uses Gemini 3 Pro Preview model for review perspective diversity
- Part of triple-model pre-commit review gate (with Bishop and Vasquez)
- Project: C# .NET 10 API + React 19 TypeScript frontend for 3D printer management
- Owner: Jeff Papiez

## Learnings

_(append new learnings below this line)_

### 2025-11-24: Round 19 — PR #14 APPROVE + PR #318 REQUEST_CHANGES

- **PR #14 (snapshot spike):** ✅ APPROVE (2-of-2 consensus with Vasquez).
- **PR #318 (error-translation tests):** ❌ REQUEST_CHANGES. SDCP + FlashForge tests cover parsing/helper logic only; full path (reject → exception → outcome) unverified. Moonraker OK. Requires mutation-level end-to-end test, not just helper logic.
- Comment: https://github.com/OlyForge3D/PrintFarmer/pull/318#issuecomment-4570450469

### 2025-11-23 — PR #14 APPROVE (Brett snapshot spike)

- **Verdict:** ✅ APPROVE.
- **Context:** FlashForge temp claim matches `fallback(for: .flashForge)` on stack branch. Noted: older `PrinterBackendCapabilitiesTests` fixture JSON shows FlashForge temp support off — Brett should describe source more precisely in any revision.
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/14#issuecomment-4570277291

### 2025-11-22 — PR #13 re-review (jog subgroup init-state bug)

- **Verdict:** ❌ REQUEST_CHANGES (same PR, real init bug uncovered on second pass).
- **Init-state bug:** `selectedAxis` defaults to `.x` in `JogSubgroup` and only snaps on user `onChange`. If `JogSubgroup` is created in subset-capability state (e.g., backend supports Z-only jog), the initial render shows Z buttons only (correct), but the bound action/feedrate still targets X (wrong — the default). User must manually tap a new axis to snap state, but by then they've likely sent a command with wrong axis.
- **Root cause:** `@State` defaults computed for full-capability case, not validated for subset-capability case. Constructor receives caps but only uses them in `onChange` reactive closure, not in `init`.
- **Fix pattern:** Compute SwiftUI `@State` defaults in initializer from the **actual initial capability subset**, not a generic default. Use `init(capabilities:) { selectedAxis = capabilities.supportsZ && !capabilities.supportsXY ? .z : .x }` (or similar domain logic).
- **Lesson:** Catching SwiftUI `@State` initialization bugs requires second-voice review that simulates the **initial render** in a **subset-capability state**, not just the transition (onChange). Initial mount is a distinct execution path from state changes.
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570098227

### 2026-05-21T09:45-07:00 — PR #300 review (HomeSubgroup, Hudson)

- **Verdict:** ✅ Approved (posted as `--comment` because GitHub blocks self-approval when reviewer and PR author share a gh account).
- **Spec source:** `mobile/docs/design/printer-controls-section.md` §2.3 Home, §2.4 state variants, §3.5 capability gating.
- **Spec adherence confirmed:** three buttons in fixed order (Home All prominent + 2-up Home XY/Home Z), correct icons (`house.fill`, `move.3d`, `arrow.up.and.down`), capability-gated removal via `shouldHide(supportsMovement && supportsHoming)`, single-flight per subgroup, a11y labels with pending state.
- **Capability gating contract (#279/#290):** correctly client-side. `viewModel.canControl` = `isOnline && !isPrintingOrPaused`. Backend not trusted. ✓
- **Findings (all nice-to-have, none blocking):**
  - Disabled state missing 8% diagonal-stripe colorblind cue (spec §2.4 / #15) — current impl is opacity-only. Cross-subgroup concern (Preheat/Jog will need same); refactor candidate as a shared `DisabledControlOverlay` modifier.
  - No error-border treatment (spec §2.4: 1.5pt `pfError` border for 4s) — view doesn't observe `viewModel.lastError`. May be parent's responsibility.
  - Tests are smoke-render only; no per-button disabled/pending observable assertions.
  - `.accessibilityAddTraits(.isButton)` on a `Button` is redundant (info-level nit).
- **Confirmation dialog:** not required per spec — homing is gated by `canControl` lockout, not a modal. Confirmed.
- **Lessons / patterns:**
  - When asked to review whether a "destructive" action needs a confirmation, check the spec first. PrintFarmer's mobile spec uses state-based lockout (`canControl`) not modals — don't invent confirmation requirements.
  - GitHub blocks `--approve` on your own PRs — fall back to `gh pr review --comment` and put the verdict (✅/❌/💬) at the top of the body.
  - When other agents run in parallel via the same parent shell, heredocs (`cat << EOF`) get clobbered. Write review bodies with the workspace `create_file` tool to a `/tmp/...` path, then run `gh pr review --body-file`. Don't use shell heredocs in shared sessions.

### 2026-05-21: PR #300 review (home subgroup)
- Verdict ✅ approved via `--comment`.
- Required Hudson rebase after #299 merged (pbxproj sibling conflicts).

### 2025-11-21 — PrintFarmerMobile #13 review (jog subgroup)

- **Verdict:** ❌ REQUEST_CHANGES. Two critical gaps identified.
- **Blocker 1 — Per-axis capability gating:** `JogSubgroup` always renders X/Y/Z buttons regardless of backend capability state. Should differentiate: if backend supports only Z-axis jog (not XY), view should hide XY buttons or show disabled state. Spec requires axis-level gating, not subgroup-level binary.
- **Blocker 2 — Test coverage gap:** Jog tests bypass SwiftUI view layer entirely. Tests drive `viewModel.move()` directly; they do not verify:
  - Picker selection (X/Y/Z axis, step mm) affects correct button press.
  - Button tap routing to correct `move(axis:distance:)` variant.
  - Capability-gated button visibility/disabling in the actual view.
  - These are SwiftUI-layer concerns and must be tested at the view level (not mocked).
- **Lesson:** Capability gating is per-axis, not per-subgroup. Mobile spec #279/#280 distinguish between `supportsXYJog`, `supportsZJog`, `supportsHomingXY`, `supportsHomingZ`. View must reflect all four flags at button level.
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570039264

### 2026-05-29 — Round 16: PrintFarmerMobile #13 re-check (jog subgroup, scope-creep acknowledgment)

- **Verdict:** ❌ REQUEST_CHANGES (re-issued after tiebreaker override by Vasquez).
- **Re-check focus:** Init-state tests instantiate `JogSubgroup` and read `.hasAnyJogCapabilityForTesting`, `.availableAxisLabelsForTesting` via test-hook extensions — not via ViewInspector render lifecycle.
- **Pedantic rationale:** "No ViewInspector in project; true render-lifecycle tests require UI framework integration. Test-hooks are insufficient proxy."
- **Scope-creep acknowledgment (Vasquez tiebreaker override):**
  - Over-ratcheting risk acknowledged: Hicks' second-voice rigor is valuable for preventing test-layer erosion, but requested tooling (ViewInspector) does not exist in project.
  - Established precedent: HomeSubgroup and PreheatSubgroup both use `.*.ForTesting` test-hook pattern. Rejecting #13 for the same pattern creates inconsistent enforcement.
  - Project tooling threshold: Accept test-hooks as practical equivalent when ViewInspector/equivalent introspection library is unavailable.
  - **Durable lesson:** Second-voice value is real, but must respect available tooling and established convention. Do not require the impossible.
- **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570216262 (Vasquez tiebreaker, noting Hicks' dissent honored in log)

### 2025-11-24: Round 21 — APPROVE PR #318 re-review (real-transport tests)
- **Verdict:** ✅ APPROVE.
- Real-transport behavior tests 14/14 pass locally (Kestrel WebSocket for SDCP, TcpListener for FlashForge).
- Full rejected-mutation → status-roundtrip → exception path verified end-to-end as required in Round 19 decision.
- All tests pass; `dotnet format --verify-no-changes` clean.
- Comment: https://github.com/OlyForge3D/PrintFarmer/pull/318#issuecomment-4570558773

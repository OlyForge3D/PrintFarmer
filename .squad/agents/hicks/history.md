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

### 2026-05-21 — Round 22: PR #318 re-review blocked by Bishop's architectural CR

- **Context:** Hicks approved PR #318 in Round 21 based on real-transport test coverage (14/14 passing). Bishop then identified two architectural blockers on 2026-05-21.
- **Lesson — Cross-layer translation gap:** Individual diff review (service layer tests) can miss downstream controller mapping. `PrinterBackendBusy` exception → `BackendBusy` outcome → HTTP 502 code chain was not verified in test path inspection. Code-path tests pass; system-path translation fails.
- **Lesson — Two-reviewer consensus rule (durable):** Backend PRs spanning service logic + controller translation need two reviewers. Applies to: PrintersService → PrintersController exception/outcome flows; domain chains; worker-slicer routing. Single voice insufficient.
- **Action:** PR #318 requires fix at PrintersController mapping layer before re-review. Bishop's catch earns the rule.

## 2026-05-31 — Trio Review Cycle #355, #371, #405

Participated in multi-round trio review cycle. Key learnings:

1. **Reviewer-lockout protocol:** Strict three-reviewer consensus with rotation of fresh hands prevents fatigue.
2. **Kane surgical-fix MVP:** Small, scoped corrections across all three branches proved cost-effective.
3. **Session-end report validation:** Coordinator must verify trio drops match current commit SHA.
4. **PR auto-close gap:** `Closes #N` does not fire on development merges; manual close required.

### 2026-07-13 — Issue #708 Backend v3 Review (Double-Attempt, gpt-5.6-sol/max)

**Context:** First attempt on live worktree aborted when branch advanced mid-review. Coordinator isolated worktree at exact SHA `6ce67c89ead4da3d1457c336f1b79d7400298b71` (branch `jpapiez-squad-708-native-push-backend`); second attempt completed on immutable copy.

**Verdict:** ❌ REQUEST_CHANGES (3 blockers at SHA `6ce67c89ead4da3d1457c336f1b79d7400298b71`)

**Blockers:**
1. `TelemetryStartup.cs:97-102` — APNs token redaction incomplete. Registration accepts arbitrary token suffix; APNs sender interpolates raw token in log prefix. Query-like suffixes (e.g., `token?ver=2`) escape path-only redaction masking.
2. `NotificationsController.cs:291-307` + `NotificationService.cs:501-520` — Legacy PUT creates default attention opt-outs and now resets stored opt-outs. Existing contract test bypasses production controller path; mutation not verified end-to-end.
3. `NativePushDispatcher.cs:148-156` — Persisted `PushOn*` attention preferences not applied during dispatch. Native push can be sent despite opt-out flag in database.

**Verified:** B3 auth ✓, migrations ✓, build ✓, full suite clean ✓

**Handoff:** Revision assigned to Lambert. Jeff Papiez locked out for this cycle. Dallas recommended for next revision.

**Orchestration note:** Invalid attempt 1 created scratch `TestEnum/` + `test_enum.cs` artifacts during cleanliness check; coordinator removed and re-ran. Only valid retry verdict recorded above.

**Lesson — Immutable-review contract:** When live branch changes mid-review, abort immediately and isolate at exact SHA. The review verdict is only valid for the exact commit SHA reviewed.

### 2026-07-19 — Hudson #785 REQUEST_CHANGES (second-round review)

- **Verdict:** ❌ REQUEST_CHANGES.
- **Candidate:** SHA 536bce0650d24c186b8c12a939046212bd8fc5b6, parent 9a0a01bf2e809b71f1481c21ea033154c3dba73f
- **Blockers:**
  1. Server-switch auth strand and stale continuation problems
  2. Fire-and-forget namespace revoke ordering
  3. Offline cached authority issues
- **Status:** Re-review cycle ongoing; Vasquez contamination incident invalidated final consensus; Hudson locked out; Anvil authorized for independent third revision
- **Orchestration:** `.squad/orchestration-log/2026-07-19T22-29-26Z-scribe-hudson-785-review-cycle.md`

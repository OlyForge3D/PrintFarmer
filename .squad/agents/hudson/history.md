# Hudson — iOS Developer History

> Migrated from PFarm-Ios `.squad/agents/ripley/` on 2026-05-20 when the iOS squad was merged into the shared PrintFarmer uber-team.

## Key Learnings & Patterns

### Xcode Project Registration (2026-05-24)
When new Swift files are created but missing from `.pbxproj`, compiler errors cascade far from the actual problem. **Fix:** Add 4 entries to `pbxproj`: `PBXFileReference`, `PBXBuildFile`, group children entry, and target Sources build phase. Always validate with `plutil -lint` and `xcodebuild -list`.

**Binding cascade pitfall:** Unknown types in `@ObservedObject` produce `Binding<_>` errors in dependent views, not at the definition site.

### PrinterControls Section Composition (Issues #287, #284–#286)
- `PrinterControlsSection` is a composite owned via `@StateObject` in `PrinterDetailView`, holding `PrinterControlsViewModel`.
- No duplicate SignalR subscription — parent view's `configureSignalR` is the single source; section forwards updates via `.onChange` observers.
- Capability gating (movement, temperature, control ops) lives in the ViewModel; section-level visibility hides entirely when offline or printing.
- iOS layout: full-width preheat row + side-by-side Home/Jog on `HStack`.
- Three subgroups: Preheat (list, not grid), Home (axis segmented picker), Jog (axis + step pickers + ±mm buttons).

### Swift Encoding Patterns (Issue #281)
- **Nil-omit via custom Encodable:** Temperature setpoints use private `SetTemperaturesRequest` with conditional `encode(to:)` to omit nil fields.
- **Dictionary body for sparse structs:** Move requests use `[String: Double]` dict (cleaner than 4-field struct with custom encoder).

### Disabled Control Style (Issue #288)
- Centralize in `DisabledControlStyle.swift`: `.disabledControlStyle(isDisabled:cornerRadius:)`, `.errorBorderHighlight(isActive:)`, `.disabledTapReveal(isDisabled:reason:)`.
- Diagonal-stripe overlay via `Canvas` at 45°, 8% white. Respects `@Environment(\.accessibilityReduceTransparency)`.
- `.help()` does NOT fire on touch — use overlay tap detector + transient caption for error messaging (3s auto-dismiss).
- Per-button error matching via `case let .jog(axis, distance)` pattern matching on `viewModel.lastError?.command.kind`.
- VoiceOver hints: "Failed: {msg}. Double tap to retry" on errors, "Sending command" while pending, accessibility traits updated per state.

### Snapshot Testing Setup (Issue #289)
- Added `pointfreeco/swift-snapshot-testing ~1.18.x` to TEST target only (Package.swift + xcodeproj).
- Convention: `Printer` fixture via `TestFixtures.decodePrinter`, mock capabilities, `assertSnapshot(of: host(section), as: .image(on: .iPhone13))`.
- Baselines regenerate on CI due to local CoreSimulator drift (iOS 26.5 SDK / xcodebuild environmental).

### pbxproj Rebase Pattern
- Duplicate-name groups with distinct IDs coexist safely; use `git checkout --conflict=diff3 <file>` to preserve complete definitions before union-merging.
- Validation: `plutil -lint`, `xcodebuild -list -project`, balanced `{}` and `()`.

### Git/Shell Hygiene
- **Backtick danger in heredoc:** Never inline backticks in a heredoc; use file-edit tool to write the body, then reference with `--body-file` or `replace_string_in_file`.

### Stack Rebase Pattern (Round 13)
- When fixing a parent PR with a stacked child PR, rebase the child onto the updated parent and force-push after parent fix completes.
- Workflow: Parent fix merges → child PR branch rebases cleanly onto updated parent → force-push without conflicts.
- Confirmed round 13: PR #12 (HomeButton fix) merged → PR #13 (Jog subgroup) rebased onto updated `squad/285-home-subgroup` cleanly.
- Always use `git commit -F <tempfile>` for multi-line messages with shell metacharacters.

### Spec Branch Hazard (Round 14)
**Spec strings from #283 live on `squad/283-design-printer-controls-section`, NOT on feature branches stacked off main.** When implementing against a spec, either:
- `git show squad/283-design-printer-controls-section:docs/design/printer-controls-section.md` to extract exact spec strings,
- Merge the spec branch first, or
- Rely on coordinator-inlined exact strings in prompts.
**Problem:** Reconstructing spec strings from memory causes VoiceOver label mismatches (e.g., "Double-tap" → "double-tap", axis-specific wording drift). **Fix:** Always reference the source branch or get strings inlined by coordinator before coding.

### Authentication & Role Gating (Issue #274)
- `AuthViewModel` holds `currentUser: UserDTO?` with `currentUserRole: String?` computed property (returns "farm_admin" if present in roles array, else first role, else nil).
- `UserDTO.roles: [String]` already exists in /api/auth/me — no backend change needed.
- Role-gated UI: plain `if authViewModel.currentUserRole == "farm_admin" { ... }` conditional, not ViewModifier (Apple HIG: omit controls user can't use).
- Injected via `@Environment(AuthViewModel.self)` from `PFarmApp`.

### 2025-11-21: Round 10 — Cool Down label fix + Jog subgroup
- PR #11 (Cool Down): Removed hardcoded "Off" ternary; standard format produces "0° / 0°" uniformly.
- PR #13 (Jog subgroup): Axis picker, step picker (default 1mm per Newt), ±mm buttons, 15 tests.
- xcodeproj UUID collision fix: HomeSubgroupTests UUID duplicated PushNotificationManager.swift fileRef; resolved before xcodebuild.

- Controls: `PrintFarmer/Views/PrinterControls/`
- Theme: `PrintFarmer/Theme/ThemeColors.swift`
- ViewModels: `PrintFarmer/ViewModels/`
- Auth: `PrintFarmer/Views/Auth/`, `PrintFarmer/ViewModels/AuthViewModel.swift`

### 2025-11-21: Round 12 — Jog fix-up (per-axis capability scaffolding + view-level tests)
- **Per-axis capability pattern:** Hoist per-axis capability flags (`canJogX`, `canJogY`, `canJogZ`) into `PrinterControlsViewModel` even when currently all derived from one backend flag (e.g., `supportsMovement`). Leaves clean seam for future backend differentiation without view-layer churn.
- **View-layer test access:** Use `*ForTesting` extensions (e.g., `hasAnyJogCapabilityForTesting`, `canJogX/Y/ZForTesting`, `availableAxisLabelsForTesting`) to expose state to test harness. Pattern matches `PreheatSubgroup` / `HomeSubgroup` precedent.
- **Durable rule:** All SwiftUI subgroup tests must exercise rendered view, not viewmodel state directly; failure to do so is a blocker for code review.
- PR #13 commit: `6344c8f`

### 2026-05-29: Round 15 — PR #12 verbatim spec strings + HomeButton non-tautological tests

- **Spec strings finalized:** Coordinator inlined verbatim per-button hints from `docs/design/printer-controls-section.md`. Tests now route both view `.accessibilityLabel()` and assertions through same `resolvedAccessibilityLabel` computed property. **Changing the string in one place changes both — test fails if view strings change.**
- **Disabled-state pattern:** `resolvedAccessibilityHint` returns `""` when disabled; `resolvedAccessibilityLabel` appends `", unavailable during print"`. Computed properties used directly by `.accessibilityLabel()` / `.accessibilityHint()` modifiers.
- **PR #12 merged** (`533b86f`). PR #13 (Jog) rebased 4/4 commits cleanly, awaiting Hicks re-review.

## Learnings

### 2026-05-30: PR #329 iOS Unit Tests package-product failure

- Failure mode: `xcodebuild test` can fail before XCTest runs with `Missing package product 'SnapshotTesting'` even when the companion app build passes. App build resolves only app dependencies; the test target also needs every test-only package registered in `PBXProject.packageReferences`.
- Secondary failure mode: Swift 6/XCTest rejects `XCTAssertEqual(optionalDouble, expected, accuracy:)`; unwrap the optional first with `XCTUnwrap` before using the accuracy overload. Prefer helper defaults of `#filePath` over `#file` to avoid XCTest source-location warnings.
- Fix pattern: when adding an SPM product to a test target in `project.pbxproj`, verify all three links exist: `PBXBuildFile` in test Frameworks, `XCSwiftPackageProductDependency` in target `packageProductDependencies`, and the `XCRemoteSwiftPackageReference` listed under project `packageReferences`.
- CI coupling: PFarm1 PR #329's iOS workflow runs the checked-in `mobile/` project directly, not `/Users/jpapiez/s/PFarm-Ios`; workflow-only assumptions should be verified from `.github/workflows/ios-pr-ci.yml` before switching repos.
- Environmental follow-up: a macOS job can fail with no steps/logs if the account has failed payments or a spending-limit block. Check the check-run annotations when `gh run view --log-failed` says `log not found`.

### 2026-05-31: PR Issue-Linkage Gate (Governance)

- **Problem:** Session 2026-05-31 merged 17 PRs; 0 auto-closed their linked issues. Root cause: agents wrote PR titles like `feat(x): thing (#350)` or commit footers like `[closes PFarm1-350]` (legacy beads syntax). GitHub only auto-closes on `Closes #N` / `Fixes #N` / `Resolves #N` **in the PR body**. Parenthetical refs and bead-style refs do NOT trigger auto-close. Brady manually closed all 17 issues.
- **Solution:** Installed process gate across 5 deliverables:
  1. `.github/pull_request_template.md` — Added "Linked issues" section with required `Closes #N` format + pre-merge checklist item.
  2. **Builder charters** (9 agents: lambert, ripley, ash, brett, kane, parker, newt, dallas, gorman) — Added STANDING RULE section requiring `--body` to contain `Closes #<issue>` when opening PR.
  3. **Reviewer trio** (vasquez, hicks, bishop) — Added PRE-PR REVIEW GATE checklist bullet requiring verification with `gh pr view <num> --json closingIssuesReferences` (REJECT if missing).
  4. `.squad/decisions/inbox/hudson-pr-issue-linkage.md` — Documented decision, root cause, and enforcement.
  5. `.squad/skills/pr-issue-linkage/SKILL.md` — Extracted skill covering auto-close syntax, why parenthetical/bead-style fail, reviewer gate, and recovery procedure (bulk-close with `gh issue close N -c "Resolved by #PR"`).
- **Verification command:** `gh pr view <num> --json closingIssuesReferences` must list the issue(s); if empty, link didn't register.
- **Confidence:** Medium (observed once in production; gate now prevents recurrence).

## Milestone Summary
- 2026-05-20: iOS squad merged; mobile controls v1 issues assigned (#274 role gate, #275 drift, #284–#286 controls, #288 polish).
- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291–#298).
- 2026-05-24: Beta 74 released; Xcode registration patterns solidified.
- 2026-05-28: Issue #274 re-run complete (role-gating decision finalized for PR #3).
- 2026-05-29: PR #12 merged (verbatim spec strings); PR #13 rebased + pending Vasquez tiebreaker on test-tooling gap.
- 2026-05-31: PR issue-linkage gate installed (governance); process closes the 17 auto-close miss from merged PRs.

### 2026-05-31: Issue 289 close-out

- PR 306 was already merged 2026-05-21 with all 6 snapshot tests but issue 289 was not auto-closed (closingIssuesReferences empty — PR title not body carried Closes reference).
- Resolution: manually closed 289 via gh issue close with comment referencing PR 306 + commit 7f02d6a3.
- Pattern: check gh pr list --state merged + gh pr view --json closingIssuesReferences before doing duplicate work when re-assigned an open issue.
## 2026-05-31T22:57:52-07:00 — iOS Accessibility Pass (COMPLETE)

**Issue:** Q2 backlog — iOS controls accessibility
**Deliverable:** PR #423 with 23 new accessibility tests
**Design Spec:** §4.1 reconciliation complete
**Orchestration Log:** .squad/orchestration-log/2026-05-31T225752-hudson.md

Status: Backlog item cleared. No blockers for review.

### 2026-09-01T14:14:40-07:00: Startup readiness prefetch handoff

- Readiness responses can be reused safely only after the whole current gate succeeds; stage per attempt, seal on failure/cancellation/supersession, and publish immediately before `.ready`.
- Reuse `FarmSnapshotSession` plus `FarmSnapshotAuthority.withPromotion` for ephemeral handoffs. This fences server, user, generation, relogin token, and tombstones without a parallel identity model.
- One-shot consumption must remove the field atomically under authority, but apply view-model state only after releasing authority/store locks.
- Persist a prefetched response with its fetch timestamp, not its later consume time, so an older prefetch can never overwrite a newer SignalR-driven cache record.
- Attention’s usable canonical first page is `getFeed(cursor: nil, limit: nil)` (server default), not the former readiness-only `limit: 1` request.
- On Windows, Swift 6/XCTest correctness must be reviewed statically; in particular, never put `await` inside XCTest assertion autoclosures.

### 2026-09-01T14:37:34.268-07:00: Startup prefetch freshness and canonical printer fencing

- Ephemeral startup handoffs still need an age bound: a lazy `TabView` may not activate a tab until long after launch. Enforce freshness in the store (30 seconds here), inject the clock, and invalidate the whole attempt payload when any entry expires.
- A prefetched response is still a canonical response. Never assign printer state directly; mint/install `CanonicalAuthority` and reuse `completeCanonicalPass` so load tokens, pending demand, waiters, auto-status guards, and observable loading state retain one ordering model.
- When prefetch adoption races active or queued canonical demand, canonical demand wins. Consume/drop the handoff and join the normal load instead of trying to reorder an existing pass.
- Attention readiness now fetches the server-default first page (up to 100 items versus one), while probes still run concurrently under the existing 10-second per-probe timeout. The larger response can therefore time out on a slow backend and should receive macOS/real-server review.

### 2026-09-01T14:46:16.550-07:00: Best-effort Attention warming

- Never let an opportunistic canonical prefetch redefine a readiness verdict. Bound it with a shorter nested race, capture only success, and fall back to the pre-existing cheap request for the actual reachability decision.
- Nest the prefetch budget inside the existing outer probe timeout. This prevents budgets from stacking: full-page warming and fallback together remain capped by the original outer duration.
- Preserve one network request on the happy path. A slow or failed full page may issue the original cheap request second, but must publish no canonical handoff so the tab follows normal hydrate-then-load behavior.

### 2026-09-01T15:09:56.766-07:00: Concurrent readiness and prefetch requests

- A sequential best-effort prefetch still narrows the real probe's usable outer timeout. To preserve verdict semantics exactly, start the original cheap request and warming request concurrently; only the cheap request may throw into readiness.
- Awaiting both yields `max(gating, min(warming, cap))`, not summed latency. A one-second cap adds at most one second versus the original probe and is masked when a sibling readiness probe is slower.
- With concurrent mock calls, never script by arrival order. Record calls under a lock and branch test responses by request arguments (`limit == 1` versus `nil`).

### 2026-09-01T15:33:16.232-07:00: Causal concurrency regression proof

- When concurrency itself closes a user-facing timeout regression, call-count assertions are insufficient: sequential code may produce the same eventual calls. Park both operations on one `AsyncBarrier` and causally await two simultaneous release waiters.
- An XCTest expectation timeout can bound only the regression/failure path while the passing path remains deterministic and free of sleeps, yields, polling, or wall-clock ordering.

>> **Scribe Note (2026-09-04)**: Issue #2364 closed as not-warranted (Dallas).  identified as dead code; verify call sites in next review.

>> **Scribe (2026-09-04)**: Issue #2364 closed. etaFormatted dead code; next review should verify.

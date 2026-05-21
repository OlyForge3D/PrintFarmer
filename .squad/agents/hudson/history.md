# Hudson — History

## Core Context

- Senior iOS/SwiftUI engineer on PrintFarmer Mobile (iOS)
- Drives full-stack feature delivery: specs → design → implementation → tests → PR review
- Part of sprint delivery engine
- Project: PrintFarmer mobile (OlyForge3D/PrintFarmerMobile)
- Owner: Jeff Papiez

## Learnings

_(append new learnings below this line)_

## Round 25-26: PR #16 Integration — PrinterControlsSection Shipped (2026-06-10 to 2026-06-12)

**PR:** `squad/287-integrate-controls-section` base `squad/286-jog-subgroup`
**Status:** Fully approved (Vasquez APPROVE round 25, Bishop re-APPROVE round 26). Stacked on unmerged controls v1 chain (#11/12/13).

### Round 25: Integration Complete, Loading State Gap Detected

Hudson composed all three PrinterControlsSection subgroups (jog, feed rate, safety) into PrinterDetailView:
- Phone layout: 1-column controls below printer status.
- iPad layout: 2-column with sidebar controls.
- Capability gating: hide controls if printer lacks axes/e-stop.
- 12 tests initially: view state transitions, layout variance, accessibility identifiers.

**Bishop COMMENT (not blocking):** Loading-state assertion is weak. Mock returns immediately, so test cannot observe `isLoadingCapabilities == true` mid-flight — only sees start (false) → end (false).

**Vasquez APPROVE:** Controls composition correct, capability gating sound, layout logic clean. Ready to ship.

### Round 26: Async Loading-State Testing Fixed

Hudson surgical fix at commit `3da6249`:
- Implemented `HoldablePrinterService` (private to test file).
- Uses `withCheckedThrowingContinuation` to suspend mid-fetch.
- Two new tests assert `isLoadingCapabilities == true` mid-flight.
- One new test confirms `isLoadingCapabilities == false` after resolve.
- Total: 14 tests (12 → 14).

Bishop re-reviewed and re-APPROVE. PR #16 fully approved with zero blockers.

### Key Learnings

1. **Async loading-state transitions require hold-point mocks.** If mock returns immediately, test only verifies endpoints, not the transition. Use `CheckedContinuation`-suspended mocks to hold a real async pause point so tests can assert mid-flight conditions.

2. **Capability gating + loading states compound test complexity.** Future feature gate work should preemptively design for testable async holds.

3. **Stacked PRs on unmerged base chains need clear approval signals.** Vasquez + Bishop signal approval despite merge queue blockage; helps unblock dependent work visibility.

### Pattern

- All future view-state async transitions: require continuation-based hold-point in tests to assert both in-flight and post-resolve states.
- Stacked feature work: document clear APPROVE markers even if base chain unmerged, so teammates know status.

## Round 27-28: PR #17 A11y Pass — PreheatSubgroup/HomeSubgroup/JogSubgroup (2026-06-15 to 2026-06-18)

**PR:** `squad/288-controls-a11y-pass` (OlyForge3D/PrintFarmerMobile)
**Commit (r27):** 872e4db
**Status:** Consensus-approved (round 28). Stacked on PR #16 (unmerged).

### Round 27: A11y Pass Shipped; Jog Picker Tautology Caught

Hudson shipped comprehensive accessibility pass across three PrinterControlsSection subgroups:
- **Verbatim-spec VoiceOver strings** via `resolvedAccessibilityLabel` and `resolvedAccessibilityHint` computed properties.
- **Disabled-state suffix:** All controls append `", unavailable during print"` when `printer.isPrinting == true`.
- **Hit targets:** All buttons/sliders verified ≥56pt (accessibility minimum).
- **ReduceMotion gating:** Animations respect accessibility motion preferences.
- **Dynamic Type smoke tests:** Text content gracefully scales to accessibility sizing.
- **+32 tests** covering all combinations.

**Vasquez APPROVE:** A11y specs comprehensive, VoiceOver strings directly from spec, disabled-state composition correct, hit targets verified.

**Bishop REQUEST_CHANGES:** Jog picker labels (4 constants) were defined inline in struct, then lifted to file scope in tests only. View renders `Self.jogLabelX` (struct instance), but tests asserted the constants directly. Tautological: changing constant breaks test assertion but view would still render old value from struct field.

### Round 28: Static-Constant Lift + Bind-Source/Test-Source Equivalence Established

Hudson surgical fix (commit 2-3 commits):
1. **JogSubgroup:** Lifted all 4 picker labels (e.g., `jogXAxisLabel`, `jogYAxisLabel`) from inline to `static let`.
2. **HomeSubgroup:** Lifted 6 button labels (e.g., `homeAllAccessibilityLabel`, `homeXAccessibilityLabel`) to `static let`.
3. **PreheatSubgroup:** Already correct (uses `resolvedAccessibilityLabel` on model).
4. View binds via `Self.homeAllAccessibilityLabel` (static reference).
5. Tests construct the component and assert on the same constant in the same struct.

**Bishop raised NEW concern:** HomeSubgroup tests still went through `HomeButton.resolvedAccessibilityLabel` property wrapper rather than asserting the constant directly.

**Vasquez tiebreak APPROVE:** Traced the full binding chain:
- View injects `Self.homeAllAccessibilityLabel` (static) into `HomeButton` initializer.
- `HomeButton` stores in `@State var label: String`.
- View renders `.accessibilityLabel(homeButton.resolvedAccessibilityLabel)` where `resolvedAccessibilityLabel` reads from that state.
- Test constructs the same `HomeButton` with the same constant.
- Test asserts on the same `resolvedAccessibilityLabel` property.
- **Bind-source ≡ test-source** (both read the constant through the same computed property).

Vasquez invoked **round-16 *ForTesting ceiling:** "When constants flow through computed properties, asserting on the computed property is NOT tautological if the view also reads through that property. Bishop's proposed 'assert the constant directly' would actually LOSE coverage of the composition logic inside `resolvedAccessibilityLabel` (e.g., disabled-state suffix concatenation)."

**PR #17 now fully approved** (Vasquez r27 APPROVE + tiebreak r28 APPROVE).

**Follow-up issue #18 filed:** VoiceOver element grouping (combining Home buttons + Preheat into semantically-unified container for nav efficiency). Vasquez r27 raised in review; Hudson escalated to backlog.

### Key Learnings

1. **Constant lift-and-bind pattern:** When multiple UI components reference the same constant, lift to `static let` and bind via `Self.constantName`. Tests construct the component and assert through the view's computed property, not the bare constant. This preserves coverage of composition logic (disabled-state suffix, property-based transforms).

2. **Bind-source ≡ test-source rule:** If view reads from constant through computed property X, and test constructs the component and asserts through the same property X, the test is non-tautological even if both use the same constant source. Changing the constant breaks both view and test identically.

3. **Tiebreaker authority on testing standards:** When two reviewers disagree on test methodology after the original blocker is fixed, the tiebreaker traces the binding chain end-to-end and decides. Coordinator does not request additional rework rounds on that point; the tiebreaker conclusion stands (round-16 *ForTesting ceiling).

### Pattern

- All string constants used by multiple subgroup components: lift to `static let`, bind via `Self.constantName`.
- A11y composition (disabled-state suffix, VoiceOver nesting): assert through view's computed property to preserve coverage of composition logic.
- Test disputes on methodology: tiebreaker traces chain end-to-end; if bind-source ≡ test-source, tautology claim is overruled.
# Hudson — iOS Developer History

> Migrated from PFarm-Ios `.squad/agents/ripley/` on 2026-05-20 when the iOS squad was merged into the shared PrintFarmer uber-team.

## Learnings

### Issue #287 — PrinterControlsSection integration (PR #304, 2026-05-21)
**Files:**
- `mobile/PrintFarmer/Views/PrinterControls/PrinterControlsSection.swift` (new, 106 lines) — composite section owning `PrinterControlsViewModel` via `@StateObject`; hosts Preheat/Home/Jog subgroups.
- `mobile/PrintFarmer/Views/Printers/PrinterDetailView.swift` (+2 lines) — inserted in both phone (`printerContent`) and iPad (`iPadPrinterContent`) layouts right after `temperatureSection(printer)`.
- `mobile/PrintFarmer.xcodeproj/project.pbxproj` — registered the new file under existing PrinterControls group (UUIDs `A3E72870…`).

**Design decisions:**
- **No duplicate SignalR subscription.** Parent `PrinterDetailViewModel.configureSignalR` already handles `printerupdated` and rebuilds the `printer` value. Section forwards updates into its VM via two `.onChange` observers (`printer.isOnline`, `printer.state`) → `viewModel.handlePrinterUpdate(printer)`. Keeps a single source of truth and avoids leaking hub registrations.
- **Section-level visibility** (`!printer.isOnline || state ∈ {printing,paused,starting}`) returns `EmptyView()` so the whole control surface collapses; per-subgroup capability gating + per-button pending state continue to work underneath when the section is shown.
- **iPad layout:** Preheat full-width on its own row, then `HStack(HomeSubgroup, JogSubgroup)` each `.frame(maxWidth: .infinity)` — fills the left column without crowding.
- **VM ownership:** `@StateObject` inside the section (lazy, lifetime-bound to the section instance). VM constructed with parent's `services.printerService` to match `PrinterDetailViewModel.configure(printerService:)`.

**Validation:** `plutil -lint` clean; `xcodebuild -list` resolves; `swiftc -parse` clean on new + modified files. Local xcodebuild simulator build still blocked by iOS 26.5 SDK / CoreSimulator drift — relying on CI.

**Lesson (terminal hygiene):** `git commit -m` with a multi-line message containing backticks (e.g. around `printerupdated`) corrupted the zsh shell — backticks triggered command substitution and the shell hung consuming output from other workspace terminals. Fix: always use `git commit -F <tempfile>` for any message that has shell metacharacters or multiple lines. Matches Scribe's existing protocol; treating it as universal going forward.

---

## Key File Paths (PFarm-Ios)
- Reusable components: `PrintFarmer/Views/Components/`
- Theme/colors: `PrintFarmer/Theme/ThemeColors.swift`
- Auth views: `PrintFarmer/Views/Auth/`
- ViewModels: `PrintFarmer/ViewModels/`
- Navigation: `PrintFarmer/Navigation/AppDestination.swift`

- 2026-05-20: Assigned mobile controls v1 issues #274 (maintenance toggle role gate), #275 (drift cleanup), #284 (preheat UI), #285 (jog UI), #286 (home UI), #288 (polish). See decisions.md "Mobile API Drift + Basic Printer Controls v1".

### Issue #276 — Surface homedAxes in PrinterStatusDetail (2026-05-21)
**Files Modified:**
- `mobile/PrintFarmer/Models/Models.swift` — added `homedAxes: String?` to `PrinterStatusDetail` with explicit memberwise init defaulting `homedAxes = nil`.
- `mobile/PrintFarmer/Services/Demo/DemoPrinterService.swift` — pass through `p.homedAxes` from demo `Printer`.
- `mobile/PrintFarmer/ViewModels/PrinterDetailViewModel.swift` — thread `homedAxes` through `applyStatusUpdate` (SignalR) and propagate `detail.homedAxes` to `printer.homedAxes` in `applyStatusDetail` only when present (avoids clobbering with nil from partial payloads).
- `mobile/PrintFarmer/Views/Printers/PrinterDetailView.swift` — new `homedAxesBadges(_:)` rendering compact X/Y/Z capsules in the gradient header trailing column. Green for homed (string contains axis letter, case-insensitive), gray otherwise. Hidden entirely when both `printer.homedAxes` and `viewModel.statusDetail?.homedAxes` are nil.
- `mobile/PrintFarmerTests/Models/ModelDecodingTests.swift` — three decoder tests covering present (`"xyz"`), absent (key omitted → nil), and empty (`""`).

**Wire format:** Backend sends compact `string?` ("xyz", "xy", "" or nil) — confirmed via `MoonrakerSubscriptionService.cs` (`state.HomedAxes` is `string`) and existing fixtures. Issue text suggested `[String]?` array shape, but mirrored the actual wire format to stay consistent with existing `Printer.homedAxes` and fixtures.

**Build:** `swift build` (library target) clean. xcodebuild simulator builds blocked locally on iOS 26.5 platform / CoreSimulator drift — environmental, not code-side.

- 2026-05-21: Ralph Round 1 (Phase 0) completed — see `.squad/log/2026-05-21T09-00-00Z-ralph-round-1-phase-0.md`.
### 2026-05-21: Issue #274 — Gated Maintenance toggle on farm_admin role
- Added `currentUserRole: String?` computed property on `AuthViewModel` (returns "farm_admin" if present in `currentUser.roles`, else first role, else nil). `UserDTO.roles: [String]` already exists on the /api/auth/me response — no backend change needed.
- Wrapped Maintenance toggle in `PrinterDetailView` with `if authViewModel.currentUserRole == "farm_admin"`. Injected `@Environment(AuthViewModel.self)`.
- Added 4 unit tests in `AuthViewModelTests` covering currentUserRole (nil when no user, returns farm_admin when present in multi-role array, returns first role for non-admin, nil for empty roles).
- Build verification: local Xcode SDK is broken (iOS 26.5 SDK not installed, CoreSimulator out of date). Used `swiftc -parse` on changed files — exit 0, no syntax errors. Full xcodebuild left to CI.


- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291, #292, #293, #294, #295, #296, #297, #298). See `.squad/log/2026-05-21T08-15-00Z-ralph-rounds-2-5-phase-1-complete.md`. Phase 2 launching (#284 preheat, #285 home, #286 jog).

### 2026-05-21: Issue #286 — PrinterControlsSection jog subgroup (PR #299)
**Files Created:**
- `mobile/PrintFarmer/Views/PrinterControls/JogSubgroup.swift` — segmented axis (X/Y/Z, filtered by `capabilities.supportedAxes`) + step picker (0.1/1/10/100 mm, default 1) + 60pt `+`/`-` buttons calling `viewModel.jog(axis:distanceMm:)` with signed step. Hides when `!supportsMovement` or `supportedAxes` empty. Disables while `pendingCommand != nil` or `!canControl`. Loads capabilities on `.task`. Snaps `selectedAxis` if the supported set narrows.
- `mobile/PrintFarmerTests/Views/JogSubgroupTests.swift` — covers `visibleAxes(for:)` (nil → full canonical, X/Y-only filters Z, empty → empty), `isHidden(for:)` (false on nil, true on `!supportsMovement`, true on empty axes, false on full), and locked v1 step option set.

**xcodeproj registration:** Added new `PrinterControls` group under Views and `Views` group under PrintFarmerTests. UUIDs prefixed `A2B62861…`. `plutil -lint` clean. Folder creation may conflict with #284/#285; #287 reconciles section composition.

**Verification:** `swiftc -parse` clean on both files. Local xcodebuild still blocked by iOS 26.5 SDK / CoreSimulator drift — full build runs in CI on the PR.

**Design notes:**
- Feedrate is internal to `PrinterControlsViewModel` per Newt's spec (3000 mm/min XY, 600 mm/min Z) — no UI surface in v1.
- Pending side is identified by matching `viewModel.pendingCommand?.kind == .jog` against `selectedAxis` and the sign of the distance, so only the tapped side spinners while both buttons disable to prevent burst-spam.


### 2026-05-21: PRs #300 and #301 rebased onto development
- After #299 (jog subgroup) merged, PRs #300 (home) and #301 (preheat) had pbxproj conflicts in two regions: PrintFarmerTests group children (Views ref) and PrintFarmerTests Sources build phase.
- Resolution: union both sides. Each branch defines its own Views group with a distinct ID (jog A2B62...D1, home A2D4EF...0006, preheat A1C5DEF...0022); group definition bodies exist independently in the file, so keeping both refs is non-destructive. Xcode tolerates duplicate-name groups with distinct IDs.
- Local xcodebuild blocked by iOS 26.5 SDK / CoreSimulator drift; plutil -lint passed both. Force-pushed both branches with --force-with-lease. Both PRs: mergeable=MERGEABLE, mergeStateStatus=UNSTABLE (CI running).
- Pattern recorded as decision (hudson-pbxproj-rebase-pattern).

- 2026-05-23 pbxproj rebase: use `git checkout --conflict=diff3 <file>` before union-merging conflicts. Default 2-way markers factor common suffixes out, splitting PBXGroup definitions across boundaries — regex union then loses closing braces. diff3 keeps each side complete.
- pbxproj validation: `plutil -lint` rejects OpenStep comments. Use `xcodebuild -list -project foo.xcodeproj` instead. Quick sanity: balanced { / } and ( / ) counts.

### 2026-05-21: Issue #289 — Snapshot tests for PrinterControlsSection (research-only PR #306)
**Outcome:** Stopped before implementing. Repo has NO snapshot testing infra. Verified: only dep in `mobile/Package.swift` is `keychain-swift`; no `Package.resolved`; no `__Snapshots__/` anywhere; existing `getSnapshot` references in MockPrinterService are camera image data, unrelated to UI snapshots. Issue is labeled `go:needs-research`. Acceptance criteria assume `swift-snapshot-testing` is already present.

**Action:** Worktree `/Users/jpapiez/s/PFarm1-289`, branch `squad/289-controls-snapshot`. Committed `mobile/docs/snapshot-testing-research.md` with the full plan (Package.swift + pbxproj + test file matrix + CI baseline flow). Decision drop-file at `.squad/decisions/inbox/hudson-289-snapshot-testing-dep.md` proposing `pointfreeco/swift-snapshot-testing` ~1.18.x on the test target only. Draft PR #306 against `development`, gated on Lead approval.

**Why no implementation now:** (1) pbxproj surgery for a new Swift Package reference is easier via Xcode UI on a machine with a working SDK; (2) local xcodebuild blocked by iOS 26.5 SDK/CoreSimulator drift (recurring), so baselines must be captured on CI; (3) the `go:needs-research` label is explicit signal to stop and propose.

**Process lesson reinforced:** the heredoc backtick hazard bit twice in one session. PR body and history append both corrupted the shell when piped through heredoc with backticks. Always write the body/content to a file with the file-edit tool, then reference it with `--body-file` or `replace_string_in_file`. Never inline anything with backticks in a heredoc.

## Learnings — 2026-05-21 (issue #288)

- **Shared disabled treatment lives in `DisabledControlStyle.swift`** under `Views/PrinterControls/`. Three modifiers: `.disabledControlStyle(isDisabled:cornerRadius:)`, `.errorBorderHighlight(isActive:cornerRadius:)`, `.disabledTapReveal(isDisabled:reason:onReveal:)`. Use these on every new controls button instead of open-coding `.opacity(0.5)`.
- **Diagonal-stripe overlay uses `Canvas`** drawn at 45°, 8% white, 6pt spacing. Honors `@Environment(\.accessibilityReduceTransparency)` — falls back to flat grey when on. Spec §2.4.
- **`.help()` does NOT fire on touch.** For iPad/iPhone disabled affordance you must wire an overlay tap detector (`disabledTapReveal` or local `handleTap` helper) that surfaces `viewModel.blockedReason` as a transient caption. Pattern: `@State var disabledTapMessage: String?` + 3s auto-dismiss via `Task.sleep`.
- **Per-button error matching** against `viewModel.lastError?.command.kind`:
  - Preheat: `if case .preheat(let p) = ..., p == preset`
  - Home: `if case let .home(axes) = ..., axes == ["X","Y","Z"]` (or `["X","Y"]` / `["Z"]`)
  - Jog: `if case let .jog(axis, distance) = ..., axis.uppercased() == selectedAxis && sign(distance) == direction`
- **All a11y strings go through `String(localized: "...", comment: "...")`** — no hardcoded English. The `comment:` parameter shows up in `.xcloc` exports for translators.
- **Pattern for VoiceOver hint on error:** "Failed: {message}. Double tap to retry." Pending value: "Sending command". Disabled hint: surfaces `viewModel.blockedReason`.
- **`accessibilityAddTraits(isPending ? .updatesFrequently : .isButton)`** so VoiceOver re-announces while a command is in flight.
- **`Printer.previewStub` is now `Printer.previewFallbackPrinter`.** The decode-with-`try!` is the actual concern — name reflects "fallback" not "stub". 3 call sites in PreheatSubgroup (definition + 2 callers).
- **pbxproj registration for new Views/PrinterControls files:** 4 entries — PBXBuildFile, PBXFileReference, PBXGroup child in `PrinterControls`, and Sources build phase entry. Always `plutil -lint` after.
- **Local `xcodebuild build` is unreliable here** because CoreSimulator drifts vs Xcode (iOS 26.5 SDK build version mismatch). CI is authoritative. Use `swiftc -parse` + `xcodebuild -list` as the local smoke test.
- **`create_file` will not overwrite existing files** — use `replace_string_in_file` / `multi_replace_string_in_file`, or `rm` then `create_file`. The latter is fine when the rewrite is structural.

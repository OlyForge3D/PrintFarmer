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
- Always use `git commit -F <tempfile>` for multi-line messages with shell metacharacters.

### Authentication & Role Gating (Issue #274)
- `AuthViewModel` holds `currentUser: UserDTO?` with `currentUserRole: String?` computed property (returns "farm_admin" if present in roles array, else first role, else nil).
- `UserDTO.roles: [String]` already exists in /api/auth/me — no backend change needed.
- Role-gated UI: plain `if authViewModel.currentUserRole == "farm_admin" { ... }` conditional, not ViewModifier (Apple HIG: omit controls user can't use).
- Injected via `@Environment(AuthViewModel.self)` from `PFarmApp`.

## Key Files
- Reusable components: `PrintFarmer/Views/Components/`
- Controls: `PrintFarmer/Views/PrinterControls/`
- Theme: `PrintFarmer/Theme/ThemeColors.swift`
- ViewModels: `PrintFarmer/ViewModels/`
- Auth: `PrintFarmer/Views/Auth/`, `PrintFarmer/ViewModels/AuthViewModel.swift`

## Milestone Summary
- 2026-05-20: iOS squad merged; mobile controls v1 issues assigned (#274 role gate, #275 drift, #284–#286 controls, #288 polish).
- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291–#298).
- 2026-05-24: Beta 74 released; Xcode registration patterns solidified.
- 2026-05-28: Issue #274 re-run complete (role-gating decision finalized for PR #3).

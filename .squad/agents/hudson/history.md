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

### 2025-11-21: Round 10 — Cool Down label fix + Jog subgroup
- PR #11 (Cool Down): Removed hardcoded "Off" ternary; standard format produces "0° / 0°" uniformly.
- PR #13 (Jog subgroup): Axis picker, step picker (default 1mm per Newt), ±mm buttons, 15 tests.
- xcodeproj UUID collision fix: HomeSubgroupTests UUID duplicated PushNotificationManager.swift fileRef; resolved before xcodebuild.

- Controls: `PrintFarmer/Views/PrinterControls/`
- Theme: `PrintFarmer/Theme/ThemeColors.swift`
- ViewModels: `PrintFarmer/ViewModels/`
- Auth: `PrintFarmer/Views/Auth/`, `PrintFarmer/ViewModels/AuthViewModel.swift`

## Milestone Summary
- 2026-05-20: iOS squad merged; mobile controls v1 issues assigned (#274 role gate, #275 drift, #284–#286 controls, #288 polish).
- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291–#298).
- 2026-05-24: Beta 74 released; Xcode registration patterns solidified.
- 2026-05-28: Issue #274 re-run complete (role-gating decision finalized for PR #3).

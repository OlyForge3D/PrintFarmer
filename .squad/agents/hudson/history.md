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

## 2026-05-21T15:00 — #289 snapshot tests implemented

Implemented PrinterControlsSectionSnapshotTests (5 cases: Moonraker / FlashForge / SDCP backend profiles + capabilities-nil loading state + disabled state via state "error"). Wired pointfreeco/swift-snapshot-testing ~1.18.x to TEST target only via both Package.swift (testTarget dep) and PrintFarmer.xcodeproj (XCRemoteSwiftPackageReference + XCSwiftPackageProductDependency + packageProductDependencies on PrintFarmerTests + frameworks build phase).

Key conventions: Printer fixture decoded via TestFixtures.decodePrinter from TestJSON.printer (no memberwise init), MockPrinterService.capabilitiesToReturn seeds caps, assertSnapshot(of: host(section), as: .image(on: .iPhone13)) per case. plutil -lint passes. xcodebuild -resolvePackageDependencies hit local CoreSimulator drift — baselines must regenerate on CI. PR #306 updated and marked ready.

Note for future: shell heredoc + backticks is a recurring foot-gun. Just use file-edit tools for content that has any markdown chars.

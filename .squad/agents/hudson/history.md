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

### OpenTag3D NFC Format Support (2026-04-01)
**Files Modified:**
- `PrintFarmer/Utilities/NFCTagParser.swift` — Added `NFCTagFormat` enum (OpenSpool/OpenTag3D) and `createOpenTag3DPayload(from:)` stub
- `PrintFarmer/Views/Settings/SettingsView.swift` — Added "NFC Tags" section with format picker, stored via `@AppStorage("nfcTagFormat")`
- `PrintFarmer/ViewModels/SpoolInventoryViewModel.swift` — Reads `nfcTagFormat` from UserDefaults and passes to NFCService
- `PrintFarmer/Services/NFCService.swift` — `writeSpoolTag` now accepts `format: NFCTagFormat` parameter; switches between OpenSpool dual-record and OpenTag3D single media-record

**Design Decisions:**
- `NFCTagFormat` enum lives in NFCTagParser.swift (shared utility, accessible to views + services — no new file needed)
- `@AppStorage("nfcTagFormat")` for persistence — consistent with `hasSeenOnboarding` pattern
- ViewModel reads UserDefaults directly (not @AppStorage) since it's @Observable, not a View
- `writeSpoolTag` defaults to `.openSpool` so all existing callers are unaffected
- OpenTag3D stub returns nil — Gorman will implement the binary encoder; call site is ready
- Legacy `writeTag(spool:)` left unchanged — it's only used internally with single-record OpenSpool format

**Build Status:** ✅ Build succeeded on iPhone 17 Pro simulator (iOS 26.3.1)

---

### Demo Mode UI — Phase 5 & 6 (2026-03-18)
**Files Created:**
- `PrintFarmer/Utilities/DemoMode.swift` — Singleton with UserDefaults-backed `isActive` bool
- `PrintFarmer/Views/Components/DemoModeBanner.swift` — Persistent amber banner with tappable info alert

**Files Modified:**
- `PrintFarmer/Views/Auth/LoginView.swift` — Added "Try Demo Mode" button below sign-in
- `PrintFarmer/ViewModels/AuthViewModel.swift` — Added `loginAsDemo()` and `exitDemoMode()` methods
- `PrintFarmer/PFarmApp.swift` — Added demo mode branch in init (stub until Gorman's `ServiceContainer.demo()` is ready)
- `PrintFarmer/Views/RootView.swift` — Added DemoModeBanner overlay at top when demo mode active
- `PrintFarmer/Views/Settings/SettingsView.swift` — Added "Exit Demo Mode" section when in demo mode

**Design Decisions:**
- Demo button uses `.plain` buttonStyle with `.secondary` foreground — visible but not competing with Sign In
- DemoModeBanner uses `Color.pfWarning` (amber) background, full-width, tappable to show explanation alert
- Banner is in a VStack above the main Group in RootView (not overlay) so it doesn't cover content
- `DemoMode` is `@MainActor @Observable` singleton for SwiftUI reactivity
- Mock user created with "demo_user" username and "viewer" role
- PFarmApp init creates a real ServiceContainer as placeholder; marked TODO for Gorman's `.demo()` factory

---

### Onboarding Screens Implementation (2026-03-12)
**Files Created:** `PrintFarmer/Views/Auth/OnboardingView.swift`
**Files Modified:** `PrintFarmer/Views/RootView.swift`, `PrintFarmer.xcodeproj/project.pbxproj`

- 3-page swipeable onboarding flow using `TabView` with `.tabViewStyle(.page(indexDisplayMode: .never))`
- Reused existing `PageIndicator` component
- `@AppStorage("hasSeenOnboarding")` for first-launch state tracking
- Onboarding shown before login, after auth check completes
- Design: SF Symbol icons 72pt `.pfAccent`, `.title .bold` headlines, `.body .pfTextSecondary` body
- "Skip" top-right, "Get Started" `.borderedProminent .tint(.pfAccent)` on last page

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### Task Lifecycle Crash Sweep (2026-03-13)

Back-button crashes from untracked/uncancelled async Tasks mutating state after dismissal.

**Solution:**
1. Added Task tracking with `.onDisappear` cancellation for button-driven async work across settings, auth, maintenance, NFC, dashboards, and lists.
2. Added `isViewActive` guards in spool ViewModels with `.onAppear/.onDisappear` toggles.
3. Updated `PendingReadyMonitor.stopMonitoring()` to use `[weak self]` in cleanup Task.

---

### SignalR Real-Time Updates for Dashboard (2026-03-12)
**Files Modified:** `PrintFarmer/ViewModels/DashboardViewModel.swift`, `PrintFarmer/Views/Dashboard/DashboardView.swift`

- `DashboardViewModel` was not subscribing to SignalR updates — only loaded data on initial load/refresh.
- Added `configureSignalR()` and `applyPrinterUpdate()` following `PrinterListViewModel` pattern.
- All printer-displaying ViewModels (list, detail, dashboard) now consistently subscribe to SignalR's `onPrinterUpdated`.

**Architecture Note:** SignalRService uses a broadcast pattern — all subscribed ViewModels receive all printer updates. Each filters by printer ID as needed.

---

### Farm Status Integration into Dashboard (2025-01-20)
**Key Patterns:**
- Used separate helper functions (`modelStatRow`, `activePrintRow`, `upNextRow`) to break up complex VStack expressions for faster type-checking
- `ForEach` with `Array()` wrapper and explicit `id:` for non-Binding collections
- Color references: `Color.pfAccent` (not `.pfAccent`) in `.foregroundStyle()` to avoid ShapeStyle type inference issues
- Responsive: 4-column grid on iPad, 2-column on iPhone for queue stats
- `JobAnalyticsView.swift` deleted — functionality moved to Dashboard

---

## Key File Paths (PFarm-Ios)
- Reusable components: `PrintFarmer/Views/Components/`
- Theme/colors: `PrintFarmer/Theme/ThemeColors.swift`
- Auth views: `PrintFarmer/Views/Auth/`
- ViewModels: `PrintFarmer/ViewModels/`
- Navigation: `PrintFarmer/Navigation/AppDestination.swift`

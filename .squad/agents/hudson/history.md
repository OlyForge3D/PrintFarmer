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

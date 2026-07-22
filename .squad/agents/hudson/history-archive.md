# Hudson — iOS Developer History Archive

> Older learnings rotated out of history.md by Scribe on 2026-05-21 to keep working memory under 15KB. Pre-2026-05-20 entries (PFarm-Ios era) live here for reference.

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


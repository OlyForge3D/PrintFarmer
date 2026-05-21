# Ripley - iOS Developer History

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
- OpenTag3D stub returns nil — Lambert will implement the binary encoder; call site is ready
- Legacy `writeTag(spool:)` left unchanged — it's only used internally with single-record OpenSpool format
- No new files created — all changes in existing files

**Build Status:** ✅ Build succeeded on iPhone 17 Pro simulator (iOS 26.3.1)

---

### Demo Mode UI — Phase 5 & 6 (2026-03-18)
**Files Created:**
- `PrintFarmer/Utilities/DemoMode.swift` — Singleton with UserDefaults-backed `isActive` bool
- `PrintFarmer/Views/Components/DemoModeBanner.swift` — Persistent amber banner with tappable info alert

**Files Modified:**
- `PrintFarmer/Views/Auth/LoginView.swift` — Added "Try Demo Mode" button below sign-in
- `PrintFarmer/ViewModels/AuthViewModel.swift` — Added `loginAsDemo()` and `exitDemoMode()` methods
- `PrintFarmer/PFarmApp.swift` — Added demo mode branch in init (stub until Lambert's `ServiceContainer.demo()` is ready)
- `PrintFarmer/Views/RootView.swift` — Added DemoModeBanner overlay at top when demo mode active
- `PrintFarmer/Views/Settings/SettingsView.swift` — Added "Exit Demo Mode" section when in demo mode

**Design Decisions:**
- Demo button uses `.plain` buttonStyle with `.secondary` foreground — visible but not competing with Sign In
- DemoModeBanner uses `Color.pfWarning` (amber) background, full-width, tappable to show explanation alert
- Banner is in a VStack above the main Group in RootView (not overlay) so it doesn't cover content
- `DemoMode` is `@MainActor @Observable` singleton for SwiftUI reactivity
- Mock user created with "demo_user" username and "viewer" role
- PFarmApp init creates a real ServiceContainer as placeholder; marked TODO for Lambert's `.demo()` factory

**Build Status:**
- Build fails only due to Lambert's in-progress protocol conformances (`AuthServiceProtocol`, `LocationServiceProtocol` not defined yet) — not from demo UI changes

### Onboarding Screens Implementation (2026-03-12)
**Files Created:**
- `PrintFarmer/Views/Auth/OnboardingView.swift`

**Files Modified:**
- `PrintFarmer/Views/RootView.swift`
- `PrintFarmer.xcodeproj/project.pbxproj`

**Feature Added:**
Implemented 3-page swipeable onboarding flow shown before login on first app launch using the existing TabView paging pattern and PageIndicator component.

**Implementation:**
1. **OnboardingView Structure:**
   - 3 swipeable pages using `TabView` with `.tabViewStyle(.page(indexDisplayMode: .never))`
   - Reused existing `PageIndicator` component for dots + labels
   - Each page: large SF Symbol icon (72pt), headline (`.title .bold`), body text (`.body .pfTextSecondary`)
   - Page 1: "Monitor Your Farm" — `cube.fill` icon
   - Page 2: "Smart Job Queue" — `tray.full.fill` icon  
   - Page 3: "Stay Informed" — `bell.badge.fill` icon + "Get Started" button
   - "Skip" button in top-right corner on all pages

2. **First-Launch State Tracking:**
   - Added `@AppStorage("hasSeenOnboarding")` boolean in RootView
   - Defaults to `false` (show onboarding)
   - Set to `true` when user taps "Get Started" or "Skip"
   - Persists across app restarts via UserDefaults

3. **RootView Integration:**
   - Added onboarding check: `else if !hasSeenOnboarding { OnboardingView(hasSeenOnboarding: $hasSeenOnboarding) }`
   - Flow: launch screen → onboarding (if `!hasSeenOnboarding` + `hasCheckedAuth` + `!isAuthenticated`) → login → main app
   - Onboarding shown before login, after auth check completes

**Key Patterns:**
- TabView paging pattern consistent with DashboardView, JobListView, MaintenanceView
- PageIndicator component reused (no new UI components needed)
- @AppStorage for simple boolean persistence (UserDefaults wrapper)
- Binding passed to child view to update parent state: `OnboardingView(hasSeenOnboarding: $hasSeenOnboarding)`

**Design Consistency:**
- Icons: 72pt, `.pfAccent` color
- Headlines: `.title .bold`
- Body text: `.body`, `.pfTextSecondary`, centered, 32pt horizontal padding
- "Get Started" button: `.borderedProminent`, `.tint(.pfAccent)`
- "Skip" button: `.plain` style, `.pfTextSecondary`, top-right placement
- Layout: centered VStack with `Spacer()` for vertical centering

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### Task Lifecycle Crash Sweep (2026-03-13)
**Files Modified:**
- `PrintFarmer/Views/Settings/SettingsView.swift`
- `PrintFarmer/Views/Auth/LoginView.swift`
- `PrintFarmer/Views/Auth/LocalNetworkPermissionView.swift`
- `PrintFarmer/Views/Filament/NFCWriteView.swift`
- `PrintFarmer/Views/Maintenance/UptimeView.swift`
- `PrintFarmer/Views/Maintenance/MaintenanceAnalyticsView.swift`
- `PrintFarmer/Views/Components/MJPEGStreamView.swift`
- `PrintFarmer/Views/RootView.swift`
- `PrintFarmer/Views/Notifications/NotificationsView.swift`
- `PrintFarmer/Views/Filament/SpoolInventoryView.swift`
- `PrintFarmer/Views/Filament/SpoolPickerView.swift`
- `PrintFarmer/Views/Filament/AddSpoolView.swift`
- `PrintFarmer/Views/Dashboard/DashboardView.swift`
- `PrintFarmer/Views/Printers/PrinterListView.swift`
- `PrintFarmer/Views/Jobs/JobListView.swift`
- `PrintFarmer/Views/Maintenance/MaintenanceView.swift`
- `PrintFarmer/ViewModels/SpoolInventoryViewModel.swift`
- `PrintFarmer/ViewModels/SpoolPickerViewModel.swift`
- `PrintFarmer/ViewModels/PendingReadyMonitor.swift`

**Issue Fixed:**
Back-button crashes caused by untracked or uncancelled async Tasks mutating view or ViewModel state after dismissal.

**Solution:**
1. Added Task tracking with `.onDisappear` cancellation for button-driven async work across settings, auth, maintenance, NFC, dashboards, and lists.
2. Added `isViewActive` guards in spool ViewModels with view-level `.onAppear/.onDisappear` toggles to prevent mutations during teardown.
3. Updated `PendingReadyMonitor.stopMonitoring()` to use `[weak self]` in its cleanup Task.

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

### SignalR Real-Time Updates for Dashboard (2026-03-12)
**Files Modified:**
- `PrintFarmer/ViewModels/DashboardViewModel.swift`
- `PrintFarmer/Views/Dashboard/DashboardView.swift`

**Bug Fixed:**
Printer cards in the Dashboard and Printer List were showing stale state after print completion, while the Printer Detail page showed accurate real-time state.

**Root Cause:**
- `PrinterDetailViewModel` ✅ subscribed to SignalR updates via `configureSignalR()`
- `PrinterListViewModel` ✅ subscribed to SignalR updates via `configureSignalR()`
- `DashboardViewModel` ❌ did NOT subscribe to SignalR updates — only loaded data on initial load/refresh

**Solution:**
Added SignalR subscription to `DashboardViewModel` following the same pattern used in `PrinterListViewModel`:

1. **DashboardViewModel Changes:**
   - Added `signalRService` property
   - Created `configureSignalR()` method to subscribe to printer updates
   - Created `applyPrinterUpdate()` method to update printer state in the printers array
   - Updates all printer fields: state, progress, job name, temps, targets, spool info

2. **DashboardView Changes:**
   - Added `viewModel.configureSignalR(services.signalRService)` call in `.task` block
   - Now receives real-time updates alongside initial data load

**Key Pattern:**
All ViewModels that display printer data (list, detail, dashboard) now consistently subscribe to SignalR's `onPrinterUpdated` handler and apply updates to their local printer state. This ensures all views stay in sync with real-time printer status changes.

**Architecture Note:**
SignalRService uses a broadcast pattern — all subscribed ViewModels receive all printer updates. Each ViewModel filters updates by printer ID if needed (e.g., PrinterDetailViewModel only processes updates for its printer, while PrinterListViewModel and DashboardViewModel process all updates).

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### Farm Status Integration into Dashboard (2025-01-20)
**Files Modified:**
- `PrintFarmer/Views/Dashboard/DashboardView.swift`
- `PrintFarmer/ViewModels/DashboardViewModel.swift`
- `PrintFarmer/Extensions/Formatting+Extensions.swift`
- `PrintFarmer/Views/Jobs/JobListView.swift`
- `PrintFarmer/Navigation/AppDestination.swift`

**Files Deleted:**
- `PrintFarmer/Views/Jobs/JobAnalyticsView.swift`

**Changes Made:**
1. **Integrated Farm Status Sections into Dashboard:**
   - Added Queue Health card showing queued/printing/paused counts + average wait time
   - Added "By Printer Model" breakdown with job counts per model
   - Added "Active Print ETAs" showing currently printing jobs with progress
   - Added "Up Next" showing next 5 queued jobs with assigned printers and queue position
   - All sections use existing JobAnalyticsService methods

2. **Updated DashboardViewModel:**
   - Added `queueStats`, `modelStats`, and `upcomingJobs` properties
   - Added `jobAnalyticsService` dependency injection
   - Extended `loadDashboard()` to fetch farm status data alongside printers/queue
   - Added `activePrintingPrinters` computed property

3. **Layout Strategy:**
   - iPad: 2-column layout for Model Breakdown + Active ETAs side-by-side
   - iPhone: stacked vertical layout
   - Farm Status sections placed after Active Jobs, before Dispatch link (iPhone) or alongside Dispatch (iPad)
   - Consistent card styling with `.pfCard`, `.pfBorder`, rounded corners

4. **Added TimeInterval Extension:**
   - Created `etaFormatted` in `Formatting+Extensions.swift`
   - Shows "2:45 PM" for today, "Tomorrow 10:00 AM" for tomorrow, relative format for further dates
   - Complements existing `durationFormatted` for elapsed time

5. **Removed Job Analytics Page:**
   - Deleted `JobAnalyticsView.swift` (functionality now on Dashboard)
   - Removed `case jobAnalytics` from `AppDestination.swift`
   - Removed toolbar button from `JobListView.swift` (lines 39-43)
   - Removed navigation case from `destinationView` (lines 319-320)
   - Updated Xcode project.pbxproj to remove file references

**Key Patterns:**
- Used separate helper functions (`modelStatRow`, `activePrintRow`, `upNextRow`) to break up complex VStack expressions for faster type-checking
- ForEach with `Array()` wrapper and explicit `id:` for non-Binding collections
- Color references use `Color.pfAccent` instead of `.pfAccent` in `.foregroundStyle()` to avoid ShapeStyle type inference issues
- Responsive design: 4-column grid on iPad, 2-column on iPhone for queue stats

**Architecture Decisions:**
- DashboardViewModel now aggregates both fleet status and job analytics data
- JobAnalyticsViewModel kept intact (reusable for future analytics features)
- Farm Status is now the dashboard's primary view of queue health, not a separate navigation destination

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### Temperature Display Enhancements (2025-01-20)
**Files Modified:**
- `PrintFarmer/Views/Components/PrinterCardView.swift` (iPhone)
- `PrintFarmer/Views/Components/iPadPrinterCardView.swift` (iPad)

**Changes Made:**
1. **Dynamic Temperature Format:**
   - Created `temperatureText(current:target:)` function to display "current → target" when heating (target > 0)
   - Shows only "current" when heater off, "---°C" when no data
   - Changed separator from "/" to "→" on iPad to match iPhone
   - Kept `.monospacedDigit()` for consistent text width alignment

2. **Dynamic Icon Colors:**
   - Created `iconColor(for:)` function returning `.pfWarning` (orange) when heater on, `.pfTextTertiary` (gray) when off
   - Applied to both NozzleIcon (hotend) and RadiatorIcon (bed) on both cards
   - Replaced static colors `.pfNotHomed` and `.pfHomed`

3. **Fixed 2-Column Layout:**
   - Added `.frame(maxWidth: .infinity, alignment: .leading)` to each temperature Label
   - Prevents bed icon from shifting when hotend temp text width changes (e.g., "25°C" vs "215°C → 220°C")
   - Both columns now have equal width regardless of content

**Patterns Used:**
- SwiftUI `.frame(maxWidth: .infinity, alignment:)` for equal-width columns without Grid complexity
- Conditional rendering based on optional target value: `if let target, target > 0`
- Extracted helper functions for reusable logic (temperatureText, iconColor)
- Maintained consistent font sizing: `.caption` on iPhone, `.subheadline` on iPad

**Key Decisions:**
- Used simple frame-based layout instead of Grid for cleaner code and broad iOS compatibility
- Arrow "→" styled as `.foregroundStyle(.tertiary)` to match the previous "/" styling
- Target temp shown as `.foregroundStyle(.secondary)` to visually distinguish from current temp
- Heater state determined by target value (> 0) rather than adding new model properties

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### Swipeable Paged Layouts for iPhone (2026-03-12)
**Files Created:**
- `PrintFarmer/Views/Components/PageIndicator.swift`

**Files Modified:**
- `PrintFarmer/Views/Dashboard/DashboardView.swift`
- `PrintFarmer/Views/Jobs/JobListView.swift`
- `PrintFarmer/Views/Maintenance/MaintenanceView.swift`
- `PrintFarmer.xcodeproj/project.pbxproj` (added PageIndicator, removed JobAnalyticsView)

**Changes Made:**
1. **Created PageIndicator Component:**
   - Reusable view showing dot indicators + page title labels
   - Active dot uses `.pfAccent`, inactive uses `.pfTextTertiary.opacity(0.5)`
   - Labels in `.caption2` with active page highlighted
   - Tapping dots/labels animates to that page
   - 8pt dots, 6pt spacing between dots

2. **DashboardView - 3 Swipeable Pages (iPhone only):**
   - **Page 0 "Overview"**: Fleet Overview summary cards + Queue Health
   - **Page 1 "Active"**: Active Jobs + Active Print ETAs sections
   - **Page 2 "Queue"**: Up Next + By Printer Model + Dispatch link
   - **Pinned content**: Maintenance Alert banner (always visible above TabView when alerts exist)
   - iPad keeps existing 2-column ScrollView layout unchanged
   - Each page has independent `.refreshable` support

3. **JobListView - 3 Swipeable Pages (iPhone only):**
   - **Page 0 "Printing"**: Currently printing/active jobs
   - **Page 1 "Queue"**: Queued/pending jobs with swipe actions (Start, Cancel)
   - **Page 2 "Recent"**: Completed/failed/cancelled jobs (last 10)
   - Each page shows empty state when no jobs in that category
   - iPad keeps existing single-List layout with sections
   - All swipe actions preserved on Queue page

4. **MaintenanceView - 2 Swipeable Pages (iPhone only):**
   - **Page 0 "Alerts"**: Active maintenance alerts + Analytics link + Uptime link
   - **Page 1 "Tasks"**: Upcoming maintenance tasks
   - Empty states for each page when no content
   - iPad keeps existing ScrollView layout
   - Navigation links to MaintenanceAnalytics and UptimeReliability preserved

**Implementation Pattern:**
```swift
if sizeClass == .compact {
    VStack(spacing: 0) {
        TabView(selection: $currentPage) {
            Page1().tag(0)
            Page2().tag(1)
        }
        .tabViewStyle(.page(indexDisplayMode: .never))
        
        PageIndicator(currentPage: $currentPage, pageCount: 2, labels: ["Label1", "Label2"])
    }
} else {
    // iPad: existing layout
}
```

**Key Decisions:**
- Used `.page(indexDisplayMode: .never)` to hide default page dots, replaced with custom PageIndicator
- Each page is an extracted `@ViewBuilder` function for clarity
- Branch on `horizontalSizeClass == .compact` for iPhone, `.regular` for iPad
- Each page content wrapped in ScrollView with independent `.refreshable`
- Empty states shown per-page when appropriate
- 16pt padding on all page content
- Smooth animation on page change (`.easeInOut(duration: 0.25)`)

**Design Adherence:**
- Active page indicator: `.pfAccent` color
- Inactive dots: `.pfTextTertiary.opacity(0.5)`
- Page labels: `.caption2`, `.pfTextSecondary` (inactive), `.pfAccent` (active)
- 8pt dot diameter, 6pt spacing
- 16pt padding on page content
- Dark theme compatible

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### Dispatch Page Navigation Crash Fix (2026-03-12)
**Files Modified:**
- `PrintFarmer/ViewModels/DispatchViewModel.swift`

**Bug Fixed:**
App crashed when pressing the back button on the Dispatch Dashboard page (accessed via `AppDestination.dispatchDashboard` from Dashboard).

**Root Cause:**
The `DispatchViewModel` had async methods (`loadQueueStatus()` and `loadHistory()`) that were called from SwiftUI's `.task` and `.refreshable` modifiers. When the user navigated back while these async operations were in progress, the tasks continued execution after the view was dismissed. This caused property updates on the `@Observable` ViewModel during view tear-down, leading to a crash.

**Solution:**
Added `Task.isCancelled` checks in both async methods:
- Check before updating properties after async network calls complete
- Check before updating state properties like `isLoading` and `error`
- Early return if task is cancelled, preventing state mutation during view dismissal

**Pattern Applied:**
```swift
func loadQueueStatus() async {
    // ... async network call
    let status = try await dispatchService.getQueueStatus()
    guard !Task.isCancelled else { return }
    queueStatus = status  // Safe: only update if task not cancelled
}
```

**Key Insight:**
With SwiftUI's `.task` modifier, tasks are automatically cancelled when the view disappears. However, async operations that complete *just as* the view is being dismissed can try to mutate `@Observable` properties during tear-down. Always guard property updates with `Task.isCancelled` checks in async methods called from `.task` or `.refreshable`.

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator


---

### App Icon Branding Integration (2026-03-12)
**Assets Created:**
- `PrintFarmer/Assets.xcassets/AppLogo.imageset/` (new image set)
- `PrintFarmer/Assets.xcassets/AppLogo.imageset/AppLogo.png` (1024x1024 PNG)
- `PrintFarmer/Assets.xcassets/AppLogo.imageset/Contents.json`

**Files Modified:**
- `PrintFarmer/Views/Auth/LoginView.swift`
- `PrintFarmer/Views/Auth/OnboardingView.swift`
- `PrintFarmer/Views/RootView.swift`

**Changes Made:**
Replaced generic SF Symbol placeholder icons (`printer.fill`, `cube.fill`) with the actual PrintFarmer app icon across all pre-authentication screens:

1. **Login Screen (`LoginView.swift`):**
   - Changed from `Image(systemName: "printer.fill")` with 56pt font
   - Changed to `Image("AppLogo")` with 56pt frame, rounded corners (12pt radius)
   - Removed `.foregroundStyle(Color.pfAccent)` since app logo has its own colors

2. **Onboarding Page 1 (`OnboardingView.swift`):**
   - Changed from `Image(systemName: "cube.fill")` with 72pt font
   - Changed to `Image("AppLogo")` with 72pt frame, rounded corners (16pt radius)
   - Pages 2 and 3 kept their feature-specific SF Symbols (`tray.full.fill`, `bell.badge.fill`)

3. **Launch Screen (`RootView.swift`):**
   - Changed from `Image(systemName: "printer.fill")` with 56pt font
   - Changed to `Image("AppLogo")` with 56pt frame, rounded corners (12pt radius)
   - Consistent branding during auth session restore

**Implementation:**
Created separate `AppLogo.imageset` because iOS `AppIcon` asset catalogs cannot be directly loaded in SwiftUI views via `Image("AppIcon")`. The AppLogo image set references the same 1024x1024 app icon PNG, making it accessible in-app.

**Image Modifiers:**
- `.resizable()` — allows scaling
- `.scaledToFit()` — maintains aspect ratio
- `.frame(width:height:)` — explicit sizing (56pt or 72pt depending on screen)
- `.clipShape(RoundedRectangle(cornerRadius:))` — iOS-standard rounded app icon corners (12pt for small, 16pt for large)

**Key Pattern:**
For in-app use of the app icon, create a separate image set (e.g., `AppLogo.imageset`) that references the same PNG file as `AppIcon.appiconset`. This enables `Image("AppLogo")` to work in SwiftUI views while keeping the standard AppIcon for system use (home screen, app switcher, etc.).

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### PendingReady Visual Prominence and Sorting (2026-03-12)
**Files Modified:**
- `PrintFarmer/Views/Components/PrinterCardView.swift` (iPhone card)
- `PrintFarmer/Views/Components/iPadPrinterCardView.swift` (iPad card)
- `PrintFarmer/ViewModels/PrinterListViewModel.swift` (list sorting)
- `PrintFarmer/ViewModels/DashboardViewModel.swift` (dashboard sorting)
- `PrintFarmer/Views/Dashboard/DashboardView.swift` (active jobs sorting)

**Feature Added:**
Made printers in `pendingready` state visually obvious with bright yellow headers and sorted them to the top of all printer lists.

**Changes Made:**
1. **Header Color Change - PendingReady → Yellow:**
   - Changed `headerBaseColor` for `pendingready` state from brown `#b45309` to bright yellow `#eab308`
   - Applied to both iPhone and iPad printer card views
   - Matches the warning/attention color scheme — visually distinct from printing (blue), paused (brown), error (red), ready (green)

2. **Printer List Sorting:**
   - Added `sortPriority()` function to `PrinterListViewModel`
   - Priority order: PendingReady (0) → Printing (1) → Ready/Idle (2) → Everything else (3) → Offline (100)
   - Applied to `filteredPrinters` computed property via `.sorted { sortPriority($0) < sortPriority($1) }`
   - Ensures PendingReady printers always appear at the top of the list, demanding attention

3. **Dashboard Sorting:**
   - Added identical `sortPriority()` function to `DashboardViewModel` for `activePrintingPrinters`
   - Added `sortPriority()` function to `DashboardView` for local `activeJobsSection` filtering
   - Updated `activeJobsSection` filter to include `"pendingready"` alongside `"printing"` and `"paused"`
   - Ensures PendingReady printers show in "Active Jobs" section and sort to top

**Priority Logic:**
```swift
private func sortPriority(_ printer: Printer) -> Int {
    guard printer.isOnline else { return 100 }
    switch printer.state?.lowercased() {
    case "pendingready": return 0  // Top priority
    case "printing": return 1
    case "ready", "idle": return 2
    default: return 3
    }
}
```

**Color Reference:**
- PendingReady: `#eab308` (bright yellow — attention-grabbing)
- Printing: `#1d4ed8` (blue)
- Paused: `#b45309` (brown/amber)
- Error: `#dc2626` (red)
- Ready/Idle: `#059669` (green)
- Offline: `#4b5563` (gray)

**Key Decisions:**
- Used bright yellow (`#eab308`) instead of `.pfWarning` (`#d97706`) for higher contrast and visibility
- Sorted across all printer display contexts (PrinterListView, DashboardView active jobs) for consistency
- PendingReady printers now included in "Active Jobs" section since they need user action
- Offline printers always sorted last (priority 100) regardless of state

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

### LaunchScreen Logo Fix (2025-07-25)
- Replaced the `UILabel` with 🌾 wheat emoji in `LaunchScreen.storyboard` with a `UIImageView` referencing the `AppLogo` asset from the catalog
- Added 56×56 explicit size constraints on the image view to match RootView.swift's SwiftUI launch screen
- Added `<image>` resource declaration and `Image references` capability to storyboard dependencies
- Kept existing LaunchBackground/LaunchText named colors and centered vertical stack layout
- Verified RootView.swift SwiftUI launch screen already uses `Image("AppLogo")` correctly — no changes needed there
- **Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### Local Network Permission Step (2026-03-12)
**Files Created:**
- `PrintFarmer/Utilities/LocalNetworkAuthorization.swift`
- `PrintFarmer/Views/Auth/LocalNetworkPermissionView.swift`

**Files Modified:**
- `PrintFarmer/Views/RootView.swift`
- `PrintFarmer/Info.plist`
- `PrintFarmer.xcodeproj/project.pbxproj`

**Bug Fixed:**
On first launch, signing in to a local-network PrintFarmer server would fail with "No internet connection. Check your network." because the iOS Local Network permission dialog raced with the login HTTP request.

**Root Cause:**
iOS shows the Local Network permission prompt lazily — only when the app first accesses the local network. The login request fires before the user responds to the dialog, causing URLSession to report `.notConnectedToInternet` which maps to `NetworkError.noConnection`.

**Solution:**
Added a new step in the first-launch flow (between onboarding and login) that proactively triggers the permission dialog:

1. **LocalNetworkAuthorization** — Uses `NWBrowser` with a `_printfarmer._tcp` Bonjour service type to trigger the system permission prompt. Wrapped in `async/await` with `withCheckedContinuation`. Swift 6 concurrency-safe (`@MainActor`, callbacks dispatched to main queue).

2. **LocalNetworkPermissionView** — Friendly screen with network icon, explanation text, and "Enable Network Access" button. After the system dialog is resolved, button changes to "Continue" to proceed to login.

3. **RootView flow update:** splash → onboarding → **network permission** → login. New `@AppStorage("hasCompletedNetworkPermission")` gate; only shown once.

4. **Info.plist** — Added `NSBonjourServices` array with `_printfarmer._tcp` (required for NWBrowser to trigger the permission dialog).

**Key Patterns:**
- NWBrowser Bonjour lookup to trigger local network permission (standard iOS pattern)
- `withCheckedContinuation` to bridge callback-based NWBrowser API to async/await
- `@AppStorage` boolean for one-time permission gate (same pattern as onboarding)
- Swift 6 strict concurrency: `DispatchQueue.main.async` wrapper for NWBrowser callbacks to satisfy `@MainActor` isolation

**Design Consistency:**
- 72pt SF Symbol icon (`network`) with `.pfAccent` color
- `.title .bold` headline, `.body .pfTextSecondary` description
- `.borderedProminent` button with `.pfAccent` tint, 32pt horizontal padding
- Matches onboarding page styling exactly

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### MJPEG Livestream for Active Printers (2026-07-24)
**Files Created:**
- `PrintFarmer/Views/Components/MJPEGStreamView.swift`

**Files Modified:**
- `PrintFarmer/ViewModels/PrinterDetailViewModel.swift`
- `PrintFarmer/Views/Printers/PrinterDetailView.swift`
- `PrintFarmer.xcodeproj/project.pbxproj`

**Feature Added:**
Implemented MJPEG livestream display when printers are actively printing, with automatic fallback to static snapshots.

**Implementation:**
1. **MJPEGStreamView (UIViewRepresentable):**
   - WKWebView wrapper that natively renders MJPEG streams from `cameraStreamUrl`
   - Supports camera rotation via CSS transforms
   - Disables scrolling/bouncing, transparent background for dark mode
   - `MJPEGStreamContainer` wrapper adds loading indicator with 2s auto-dismiss
   - `#if canImport(UIKit)` guard for platform compatibility

2. **PrinterDetailViewModel Changes:**
   - Added `showLivestream: Bool` toggle (default false, auto-enabled when printing)
   - Added `isActivelyPrinting` computed: true when state is "printing", "starting", or "paused"
   - Added `canShowLivestream` computed: `isActivelyPrinting && cameraStreamUrl != nil`
   - `applyLiveUpdate()` auto-toggles livestream on/off when state changes
   - `loadPrinter()` auto-enables livestream on initial load when printing

3. **PrinterDetailView Camera Section:**
   - LIVE/SNAPSHOT badge next to "Camera" header (red capsule for live, gray for snapshot)
   - Toggle button (video.fill ↔ photo icon) to switch between stream and snapshot
   - Refresh button hidden when in livestream mode (not needed for streams)
   - Rotation button always visible for both modes
   - Falls back to snapshot/placeholder when not printing or no stream URL

**Architecture Decisions:**
- WKWebView approach chosen over custom multipart HTTP parsing — Safari handles MJPEG natively
- Rotation applied via CSS in WebView (not SwiftUI `.rotationEffect`) to work within the web context
- Auto-toggle pattern: livestream turns on when printing starts (via SignalR), off when printing stops
- Manual override preserved: user can toggle back to snapshot even while printing

**Key Patterns:**
- `#if canImport(UIKit)` for platform-specific WebKit code
- `UIViewRepresentable` coordinator pattern for WKNavigationDelegate
- CSS injection via `evaluateJavaScript` for styling the MJPEG stream
- URL change detection in `updateUIView` via coordinator state

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### Always-Visible Filament Info on Printer Cards (2026-07-24)
**Files Modified:**
- `PrintFarmer/Views/Components/PrinterCardView.swift` (iPhone)
- `PrintFarmer/Views/Components/iPadPrinterCardView.swift` (iPad)

**Changes Made:**
1. **iPhone PrinterCardView — Added filament row:**
   - Added `filamentSection` computed property below temperature row
   - When `spoolInfo != nil && hasActiveSpool`: shows color circle, material, filament name, remaining weight (matching iPad layout)
   - When no spool: shows `Label("No spool loaded", systemImage: "cylinder")` in `.caption` / `.secondary`

2. **iPad iPadPrinterCardView — Always-visible filament:**
   - Removed conditional `if let spool = printer.spoolInfo, spool.hasActiveSpool` guard
   - Added else branch showing "No spool loaded" label with cylinder icon (matches PrinterDetailView pattern)

**No changes needed:**
- PendingReady yellow header: already implemented on both cards (`headerBaseColor` has `case "pendingready": return Color(hex: "#eab308")`)
- Sort order: already implemented in `PrinterListViewModel.sortPriority()` (pendingReady=0, printing=1, ready/idle=2, offline=100)
- PrinterDetailView: already handles both spool/no-spool states

**Key Patterns:**
- Filament empty state uses `Label` with `cylinder` SF Symbol, `.caption` font, `.secondary` foreground — consistent with PrinterDetailView
- `@ViewBuilder` used for `filamentSection` to support conditional content without `AnyView`
- iPhone filament row uses slightly smaller spacing (6pt) vs iPad (8pt) for compact layout

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

---

### Back Button Crash Fix + PendingReady Yellow Headers + Bed Clear Feedback (2026-03-14)
**Files Modified:**
- 13 View files across Views/Dashboard, Views/Printers, Views/Jobs, Views/Filament, Views/Maintenance, Views/Notifications
- 5 ViewModel files: DispatchViewModel, PrinterDetailViewModel, JobDetailViewModel, JobHistoryViewModel, AutoDispatchViewModel
- 2 Card components: PrinterCardView, iPadPrinterCardView
- PrinterListViewModel (sort priority fix)

**Bug Fixed (Task 1): Back button crashes on all pushed views**
Root cause: Unstructured `Task { }` blocks in Button actions survived view dismissal. When views are popped from NavigationStack, these tasks mutate `@Observable` ViewModels for deallocated views → crash.

Fix applied consistently across ALL views:
1. **Fix 1 (Store & Cancel):** Added `@State private var activeTasks: [Task<Void, Never>] = []` (or named task refs for simpler views). All `Task { }` in button actions now store task refs. `.onDisappear` cancels all tasks.
2. **Fix 2 (isActive guard):** Added `var isViewActive = true` to ViewModels of pushed views. Async load methods guard on `isViewActive`. `.onDisappear` sets `viewModel.isViewActive = false`.

**Bug Fixed (Task 2): Bed Clear button no immediate feedback**
In AutoDispatchSection, the "Confirm Bed Clear" button now includes `viewModel.isMarkingReady` in its disabled condition, preventing double-taps. The existing ProgressView spinner was already wired to `isMarkingReady`; the disabled state now also gates on it.

**Bug Fixed (Task 3): PendingReady printers not showing yellow headers**
Root cause: `headerBaseColor` checked `!printer.isOnline` BEFORE checking state. When the API returns `isOnline: false` for PendingReady printers, the card showed gray instead of yellow.

Fix: In both PrinterCardView and iPadPrinterCardView, pendingReady state is now checked FIRST, before the isOnline check. Same fix applied to:
- `statusLabel` — "Bed Clear" shown regardless of isOnline
- `statusAccentColor` — `.pfWarning` returned for pendingReady regardless of isOnline
- `sortPriority` in PrinterListViewModel and DashboardView — pendingReady sorts to top regardless of isOnline

Added debug logging (`print("DEBUG headerBaseColor: ...")`) to both card views to help diagnose if issues persist.

**Key Pattern (Task Lifecycle):**
- Pushed views: `activeTasks` array + `.onDisappear { activeTasks.forEach { $0.cancel() }; viewModel.isViewActive = false }`
- Root tab views: Named `retryTask` ref (less crash-prone but still good practice)
- ViewModels: `guard isViewActive else { return }` at top of async load methods

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

## 2026-03-14 — Back-Button Crashes & Notification Fixes (Lambert cross-pollination)

**Related fixes from Lambert:**
- Notification deduplication now uses printer UUID identifiers instead of random UUIDs
- This ensures proper cleanup when printers leave PendingReady state
- Impact: Filament indicator cards with yellow "pending" headers benefit from cleaner notification lifecycle

### PrinterDetailView Back-Button Crash Fix (2026-03-14)
**Files Modified:**
- `PrintFarmer/ViewModels/PrinterDetailViewModel.swift`
- `PrintFarmer/ViewModels/JobDetailViewModel.swift`

**Bug Fixed:** App crashed when pressing back on the printer detail page while it was showing a live printing job.

**Root Cause:**
The View-side fix (activeTasks + onDisappear) was already in place from the previous batch, but the ViewModel had 12+ unguarded crash vectors:
1. **SignalR callback** — `configureSignalR()` registered an `onPrinterUpdated` handler that called `applyLiveUpdate()` without checking `isViewActive`. When the view was popped and a SignalR update arrived, it mutated `@Observable` properties on a dead view → crash.
2. **`applyLiveUpdate()`** — directly mutated `printer`, `statusDetail`, `showLivestream` with no guard.
3. **All async action methods** — `performAction`, `ejectFilament`, `setActiveSpool`, `loadSpoolById`, `markPrinterReady`, `refreshSnapshot`, `toggleMaintenance`, `confirmAction` — none had `isViewActive` guards after await suspension points.
4. **NFC Task blocks** — `writeNFCPrinterTag()` and `handleNFCScanToLoad()` created fire-and-forget `Task {}` blocks that could mutate state after dismissal.

**Fix Applied:**
- Added `guard isViewActive` to SignalR callback before calling `applyLiveUpdate()`
- Added `guard isViewActive` at entry of `applyLiveUpdate()` 
- Added `guard isViewActive` at entry and after every await point in all 10 async methods
- Added `guard isViewActive` in NFC methods before state mutations
- Also fixed `JobDetailViewModel.performAction()` with same pattern

**Key Insight:**
The previous batch fix correctly added `activeTasks` tracking and `isViewActive = false` on disappear, but missed that the ViewModel's SignalR listener operates OUTSIDE the task lifecycle — it's a callback registered once that fires indefinitely. The `isViewActive` guard inside the callback is the only defense against post-dismissal mutations from push-based updates (SignalR, websockets, timers).

**Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

### PendingReady Yellow Headers Fix (2025-07-24)
* **Root Cause:** `printer.state` is NEVER "PendingReady" — the printer stays "Ready" while awaiting bed clear. The PendingReady state only exists in the auto-dispatch status endpoint (`GET /api/autoprint/status`).
* **Fix:** `PrinterListViewModel` now fetches auto-dispatch statuses via `autoPrintService.getAllStatus()` and stores them in `autoDispatchStatuses: [UUID: AutoDispatchStatus]`. Both `PrinterCardView` and `iPadPrinterCardView` accept an `isPendingReady: Bool` parameter instead of checking `printer.state`.
* **Key Insight:** The web UI already does this correctly — it fetches auto-dispatch status separately via `useAutoDispatchStatus(printer.id)` hook. The iOS app was incorrectly checking a field that can never contain the expected value.
* **Build Result:** ✅ Build succeeded on iPhone 17 Pro simulator

## Learnings

### CarPlay Scene Crash Prevention (2025-01-XX)

**Pattern:** When an iOS app with no CarPlay support connects to CarPlay, iOS calls `application(_:configurationForConnecting:options:)` on the AppDelegate. Without this method, the auto-generated scene configuration tries to create a standard WindowGroup scene for the CarPlay connection, which crashes because CarPlay expects a CPTemplateApplicationScene, not a UIWindowScene.

**Fix Implementation:**
```swift
func application(
    _ application: UIApplication,
    configurationForConnecting connectingSceneSession: UISceneSession,
    options: UIScene.ConnectionOptions
) -> UISceneConfiguration {
    if connectingSceneSession.role == .windowApplication {
        let config = UISceneConfiguration(name: "Default Configuration", sessionRole: .windowApplication)
        config.delegateClass = nil // SwiftUI manages scene lifecycle
        return config
    } else {
        // CarPlay or other unsupported scene types get minimal config
        return UISceneConfiguration(name: nil, sessionRole: connectingSceneSession.role)
    }
}

func application(
    _ application: UIApplication,
    didDiscardSceneSessions sceneSessions: Set<UISceneSession>
) {
    // Clean up any resources for discarded scenes if needed
}
```

**Key Insights:**
- Check `connectingSceneSession.role` to distinguish between `.windowApplication` (standard app) and `.carTemplateApplication` (CarPlay)
- For unsupported roles, return a minimal `UISceneConfiguration` with the correct role but no delegate/storyboard
- Also implement `didDiscardSceneSessions` for proper cleanup
- This prevents crashes WITHOUT adding actual CarPlay functionality — graceful degradation

**Files Modified:**
- `PrintFarmer/App/AppDelegate.swift`

**Build Result:** ✅ Build succeeded on iPhone 17 simulator

## 2026-03-19T01:45Z — Demo Mode UI Implementation (Background Execution)

**Status:** ✅ COMPLETE — All UI flows wired, build succeeds

### Deliverables

#### 1. Demo Mode Infrastructure
- **Created:** `PrintFarmer/Utilities/DemoMode.swift` — `@MainActor @Observable` singleton
- **Storage:** UserDefaults-backed `isActive` bool
- **Methods:** `activate()`, `deactivate()`, `shared` accessor

#### 2. Demo Login UI
- **Modified:** `LoginView.swift` — Added "Try Demo Mode" button below Sign In
- **Style:** `.plain` buttonStyle, `.secondary` foreground, play icon
- **Action:** Calls `authViewModel.loginAsDemo()` on tap
- **Visibility:** Always visible on login screen for Apple reviewer discoverability

#### 3. Demo Mode Banner
- **Created:** `PrintFarmer/Views/Components/DemoModeBanner.swift`
- **Design:** Amber (`Color.pfWarning`) background, full-width
- **Behavior:** Tappable to show info alert explaining demo limitations
- **Placement:** VStack above main content in RootView (not overlay — prevents interaction issues)
- **Binding:** Reacts to `DemoMode.shared.isActive` changes

#### 4. Settings Exit Flow
- **Modified:** `SettingsView.swift` — Added "Exit Demo Mode" section
- **Visibility:** Only shown when `DemoMode.shared.isActive`
- **Style:** Destructive red to signal clear termination
- **Action:** Calls `authViewModel.exitDemoMode()` → clears demo flag → returns to login

#### 5. AuthViewModel Integration
- **Modified:** `AuthViewModel.swift` — Added two methods:
  - `loginAsDemo()` — Sets demo user, triggers app state refresh
  - `exitDemoMode()` — Clears auth state, triggers return to login screen
- **Type:** Changed init to accept `any AuthServiceProtocol` for future demo services integration

#### 6. App Initialization Branch
- **Modified:** `PFarmApp.swift` — Added demo mode init branch
- **Logic:** Check `DemoMode.isActive` → use `ServiceContainer.demo()` + demo auth service
- **Stub:** Currently creates real `ServiceContainer` as placeholder (marked TODO for Lambert integration)

#### 7. Root View Integration
- **Modified:** `RootView.swift` — Added demo banner overlay
- **Placement:** VStack wrapping banner + main Group
- **Reactivity:** `@Environment(\.scenePhase)` guard prevents banner during inactive state

### Build Status
✅ All UI pieces compile successfully  
✅ Zero errors, zero warnings

### Design Decisions Documented
1. **Banner placement (VStack, not overlay)** — Prevents content overlap issues
2. **Demo button styling** — Visible but not competing with Sign In (secondary color)
3. **Singleton ownership** — DemoMode is UI infrastructure, created immediately (not waiting for Lambert)
4. **Exit via Settings** — Consistent with app patterns, destructive red signals state change

### Cross-Team Integration Points
- **Lambert:** Wire up `ServiceContainer.demo()` in PFarmApp.init() TODO
- **Dallas:** Orchestration verified; architecture validated across UI layer
- **Ash:** Ready for demo UI tests (banner visibility, button actions, state persistence)

### Learnings
- @MainActor @Observable singletons work well for cross-layer state (better than EnvironmentKey)
- VStack layout for persistent UI elements avoids SwiftUI overlay timing issues
- Demo user pattern (username="demo_user", role="viewer") enables role-based UI branching

### Demo Mode Hot-Swap Fix (2026-03-18)
**Bug:** Tapping "Try Demo Mode" caused infinite spinners because ServiceContainer was initialized with real services at app startup and never swapped.

**Files Modified:**
- `PrintFarmer/Services/ServiceContainer.swift` — Changed `let` properties to `var`, added `switchToDemo()` and `switchToReal(baseURL:)` methods for runtime service swapping
- `PrintFarmer/ViewModels/AuthViewModel.swift` — `loginAsDemo(services:)` and `exitDemoMode(services:)` now accept ServiceContainer and call switchToDemo/switchToReal
- `PrintFarmer/Views/Auth/LoginView.swift` — Pulls ServiceContainer from @Environment, passes to loginAsDemo
- `PrintFarmer/Views/Settings/SettingsView.swift` — Pulls ServiceContainer from @Environment, passes to exitDemoMode

**Key Pattern:**
- ServiceContainer is @Observable, so mutating its properties triggers SwiftUI view updates automatically
- The container stays the same reference (same @State in PFarmApp), only its service implementations swap
- No need to replace the container or re-inject — @Observable handles propagation

### AuthViewModel Stale Service Reference Fix (2026-03-18)
**Bug:** After exiting demo mode and logging in with real credentials, the app still showed demo data. AuthViewModel held a stale reference to the old DemoAuthService captured at init time.

**Root Cause:** `private let authService: any AuthServiceProtocol` was set once in `init(authService:)`. When `switchToReal()` swapped the container's services, AuthViewModel still called the old DemoAuthService.

**Fix:**
- `AuthViewModel` now stores `ServiceContainer` instead of a bare `AuthServiceProtocol`, accessing `services.authService` dynamically at each call site
- `init(services:)` replaces `init(authService:)`
- `loginAsDemo()` and `exitDemoMode()` no longer take a `services` parameter — the VM already owns the container
- LoginView and SettingsView no longer need `@Environment(ServiceContainer.self)` for those calls

**Files Modified:**
- `PrintFarmer/ViewModels/AuthViewModel.swift` — store ServiceContainer, access .authService dynamically
- `PrintFarmer/PFarmApp.swift` — pass container to AuthViewModel init
- `PrintFarmer/Views/Auth/LoginView.swift` — removed services environment, simplified loginAsDemo call
- `PrintFarmer/Views/Settings/SettingsView.swift` — removed services environment, simplified exitDemoMode call

## Learnings
- When using @Observable service containers with hot-swappable services, ViewModels must read from the container dynamically — never capture a protocol reference at init
- The previous hot-swap fix was incomplete: it made the container swappable but left consumers holding stale references

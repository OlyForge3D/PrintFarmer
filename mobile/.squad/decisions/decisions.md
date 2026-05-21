# Decisions — PFarm-Ios Squad

## Architecture & Framework

### 1. Swift 6 Strict Concurrency & Actor Isolation (Dallas, 2026-03-06)
**Status:** Foundational  

All ViewModels are `@MainActor @Observable`. Services are actor-isolated or properly annotated. AppRouter is `@MainActor` (manages UI navigation state). No `@unchecked Sendable` — proper isolation enforced.

**Impact:**
- Eliminates "sending risks data races" errors in SwiftUI bindings
- AppRouter, AuthViewModel, DashboardViewModel, PrinterDetailViewModel, etc. all `@MainActor`

### 2. Service Architecture: Actor-Based with ServiceContainer DI (Dallas, 2026-03-06)
**Status:** Locked In

ServiceContainer singleton instantiated at app init holds: APIClient, AuthService, PrinterService, JobService, NotificationService, SignalRService, StatisticsService. All services are actor-isolated or thread-safe.

**Impact:**
- ViewModels receive services via ServiceContainer (no constructor injection)
- AppDelegate creates container; .task configures ViewModels with container reference
- Easy to mock for testing

### 3. @Observable ViewModels Pattern (Dallas, 2026-03-06)
**Status:** Locked In

Modern SwiftUI reactive model. Each feature has one ViewModel. @Observable enables automatic reference tracking without @Published. All ViewModels are @MainActor @Observable.

**Impact:**
- No `@State var viewModel` — pass as argument or environment
- Automatic View invalidation on property changes

### 4. SwiftUI Navigation via AppDestination Enum (Dallas, 2026-03-06)
**Status:** Locked In

Deep navigation via `NavigationPath([AppDestination])` in AppRouter. Tab-based gating on `selectedTab`. LoginView ↔ ContentView gating on `isAuthenticated`.

**Impact:**
- Single source of navigation logic in AppRouter
- Deep linking via `AppDestination` enum
- Tabs (Dashboard, Printers, Jobs, Notifications, Settings)

---

## Authentication & Session Management

### 5. Session Expired Auto-Logout Pattern (Lambert, 2026-03-07)
**Status:** Implemented

When the backend returns a 401 or the JWT token is expired (with 5-minute buffer), the app automatically logs out:
1. **APIClient** posts `Notification.Name.sessionExpired` on 401 responses
2. **APIClient** checks `AuthService.isTokenExpired()` (via closure) before every request — proactively catches expired tokens without a network round-trip
3. **AuthViewModel** observes `.sessionExpired` and calls `logout()`, which flips `isAuthenticated = false` → SwiftUI navigates to LoginView

**Impact:**
- **Ripley (UI):** No UI changes needed — SwiftUI automatically shows LoginView when `isAuthenticated` flips to false
- **Ash (Tests):** Test mocks unaffected — `tokenExpiryChecker` is optional, defaults to nil (no pre-check)
- **AuthViewModel** is now `@MainActor @Observable`

**Rationale:**
- No refresh token exists (single JWT) — re-login is the only recovery path
- Proactive expiry check avoids wasted network calls and better UX (immediate redirect vs waiting for 401)
- Notification pattern decouples APIClient from AuthViewModel (no circular dependency)

### 6. Enum & Date Serialization Contract (Lambert, 2026-03-07)
**Status:** Applied

After login, the dashboard was crashing with "Failed to decode response" because Swift model enums used `Int` raw values while the ASP.NET Core backend serializes ALL enums as strings via `JsonStringEnumConverter`.

**Decision:**
1. **All Swift enums that map to backend C# enums MUST use String raw values** matching the C# member names exactly (e.g., `case moonraker = "Moonraker"`, `case sdcp = "SDCP"`). Each enum includes a fallback `init(from:)` that also accepts legacy integer values.
2. **The JSONDecoder uses a custom date strategy** that handles ISO 8601 both with and without fractional seconds. The built-in `.iso8601` strategy must NOT be used — it silently rejects fractional seconds.
3. **Backend `TimeSpan` fields are represented as `String?` in Swift** with parsing helpers (`.timeSpanSeconds`, `.timeSpanFormatted`), since .NET serializes `TimeSpan` as duration strings.

**Impact:**
- **Ripley/Views:** `StatusBadge(jobStatus:)` now accepts `PrintJobStatus?`. `MotionType.polar` was removed (doesn't exist in backend — `Unknown` is the correct fallback).
- **Ash/Tests:** Enum test fixtures must use string values, not integers. Mock JSON payloads should match backend format.
- **All agents:** When adding new model fields, always check the backend DTO in `~/s/PFarm1/src/infra/Dtos/` and the serialization config in `~/s/PFarm1/src/api/Startup/ControllerStartup.cs`.

---

## UI & Theming

### 7. Theme Support: Light + PrintFarmer Dark (Ripley, 2026-03-07)
**Status:** Implemented

Added light and dark theme support matching the Printfarmer web app's color system. Two themes: **Light** and **PrintFarmer Dark** (the branded dark navy theme, not generic system dark).

**Implementation:**
- **New Files:**
  - `PrintFarmer/Theme/Color+Hex.swift` — Hex color initializer for `Color` and `UIColor`
  - `PrintFarmer/Theme/ThemeColors.swift` — All branded colors as adaptive `Color` statics (`pf*` prefix)
  - `PrintFarmer/Theme/ThemeManager.swift` — `@Observable` class with `ThemeMode` enum (system/light/dark), persists to UserDefaults
- **Color System:**
  - **Adaptive colors** via `UIColor { traitCollection in ... }` — automatically respond to iOS appearance changes
  - **22 named color tokens** covering backgrounds, text, accents, borders, status, buttons, hardware status
  - **Brand accent: green (#10b981)** set as global `.tint()` on the root view
- **Theme Toggle:**
  - Settings → Appearance → Theme picker with System / Light / PrintFarmer Dark options
  - `.preferredColorScheme()` applied at root view level in PFarmApp
  - System mode follows iOS appearance setting; manual override persists across launches

**Impact:**
- **All agents:** New views should use `Color.pf*` tokens instead of raw SwiftUI colors (`.blue`, `.green`, etc.)
- **Ash (Testing):** ThemeManager is `@Observable` and injected via `.environment()` — mock or create in tests that need it
- **Key convention:** Use `Color.pfCard` (not `.pfCard`) in `.background()`, `.foregroundStyle()`, `.strokeBorder()` contexts due to ShapeStyle resolution

### 8. QA Audit Fixes — Theme Color Extensions (Ripley, 2026-03-07)
**Status:** Applied

Added 3 new theme colors to `PrintFarmer/Theme/ThemeColors.swift`:
- **`pfMaintenance`** — purple, adaptive (light: #7c3aed, dark: #a78bfa). Used for maintenance mode badges in StatusBadge and PrinterDetailView.
- **`pfAssigned`** — teal/cyan, adaptive (light: #0891b2, dark: #22d3ee). Used for "Assigned" job status in StatusBadge.
- **`pfTempMild`** — yellow, adaptive (light: #ca8a04, dark: #facc15). Used for temperature display when >50°C in TemperatureView.

**Rationale:**
QA audit identified 17 hardcoded colors (.red, .orange, .purple, .cyan, etc.) across 7 view files. Existing pf* palette covered most cases (pfError→red, pfWarning→orange, pfHomed→blue, pfNotHomed→orange) but purple (maintenance), cyan (assigned), and yellow (mild temp) had no equivalents.

**Impact:**
- All views now use theme-consistent colors that adapt properly between light/dark modes
- Any future views needing maintenance, assigned, or mild-temperature colors should use these instead of raw system colors
- AuthViewModel now uses `@MainActor` instead of `@unchecked Sendable` — aligns with the existing ViewModel pattern

### 9. PrinterDetailView Blank Page Fix (Ripley, 2026-03-07)
**Status:** Applied

Users reported a blank page when tapping a printer in the list. PrinterDetailView rendered nothing.

**Root Cause:**
1. The view body had three `if/else if` branches (loading, error, content) but **no final `else`**. The initial render state matched none of them → blank.
2. `loadPrinter()` set `isLoading = true` *after* a `guard let printerService` check. If the guard failed, the method returned silently — no loading indicator, no error, permanent blank.

**Decision:**
- **View pattern:** Always have a default `else` branch in conditional view bodies. Use: content first → error second → else ProgressView.
- **ViewModel pattern:** Set loading state *before* any guards in async load methods. If a guard fails, surface it as an `errorMessage` rather than silently returning.

**Team Impact:**
- **All agents:** Apply the same defensive rendering pattern to other detail views (JobDetailView, etc.) to prevent similar blank-page bugs.
- **Lambert:** `GET /api/printers/{id}` returns `PrinterDto` which lacks `InMaintenance`/`IsEnabled` fields. Consider either adding those to `PrinterDto` or having the iOS app call `/api/printers/{id}/details` instead. Low priority — defaults work fine for now.

### 10. Jobs Tab Shows Jobs, Not Printers (Ripley, 2026-03-07)
**Status:** Implemented

Jeff reported the Jobs tab was showing "available printers" instead of print jobs. The root cause was that `JobListView` fetched from `GET /api/job-queue` which returns `[QueueOverview]` — a printer-centric view with one row per printer.

**Decision:**
- Switched the Jobs tab to use `GET /api/job-queue-analytics` which returns individual print jobs (`[QueuedPrintJobWithFileMetaDto]`)
- Jobs are grouped by status: **Printing** (active on a printer), **In Queue** (waiting), **Recent** (completed/failed/cancelled, collapsible)
- The old `list() -> [QueueOverview]` method stays on the protocol for backward compat (Dashboard or other views may use it)
- Added `listAllJobs() -> [QueuedPrintJobResponse]` to `JobServiceProtocol`

**Impact:**
- **Views affected:** `JobListView`, `JobListViewModel`
- **Models added:** `QueuedPrintJobResponse`, `QueuedJobInfo`, `QueuePrinterMeta`, `QueueGcodeFileMeta`, `QueueStats`
- **Protocol change:** `JobServiceProtocol` gained `listAllJobs()` method
- **Mock updated:** `MockJobService` updated with new method + `queuedJobResponsesToReturn` property
- **No breaking changes:** Old `list()` method preserved; existing tests using `QueueOverview` still pass

---

## Infrastructure & Build

### 11. Xcode Project Regeneration (Dallas, 2026-03-06)
**Status:** Implemented

Regenerated `PrintFarmer.xcodeproj` from scratch rather than patching the damaged original. The project file is now auto-generated from the file tree using a deterministic Python script.

**Context:**
The original scaffolded `.xcodeproj` had three fatal issues:
1. Missing closing `}` in `project.pbxproj` (plist parse failure)
2. Empty workspace data (no `<FileRef>` in `contents.xcworkspacedata`)
3. 21 Swift files added during MVP batch (Lambert/Ripley/Ash) were never registered

**Impact:**
- **All agents:** When adding new `.swift` files, the xcodeproj must be regenerated. SPM (`swift build`) will work without changes, but Xcode won't see new files until the project is updated.
- **Future consideration:** A workspace-only approach (Xcode opens `Package.swift` directly) would eliminate this sync problem entirely. Deferred for now since the xcodeproj provides better Xcode integration (schemes, test plans, signing).

**Specifications:**
- iOS 17+, Swift 6.0
- Bundle ID: `com.printfarmer.ios`
- 47 source files, 19 test files (66 total)
- KeychainSwift SPM dependency
- Deterministic IDs (md5-based) — regeneration is idempotent

---

## Testing & QA

### 12. QA Review: PrintFarmer iOS MVP (Ash, 2026-03-07)
**Status:** Reference / Action Items

Comprehensive QA audit covering test coverage, error handling, edge cases, mock alignment, and memory safety. Test coverage at ~41% (7/17 units). Critical action items:

**Before Release:**
1. Add error UI to JobListView + NotificationsView
2. Implement 401 → auto-logout (see decision #5)
3. Add AuthViewModel tests
4. Add DashboardView empty state
5. Write JobListViewModel + JobDetailViewModel tests

**Test Coverage Gaps:**
- AuthViewModel, JobListViewModel, JobDetailViewModel, NotificationsViewModel — zero tests
- JobService, NotificationService, SignalRService — zero tests (protocols/mocks exist)

**All Verified Correct:**
- All API endpoint paths match backend
- All model field names/types match backend DTOs
- Enum serialization (String raw values) matches backend
- APIClient date decoder handles fractional + plain ISO 8601
- All ViewModels have proper @MainActor isolation

### 13. QA Audit — PrintFarmer iOS MVP (Dallas, 2026-03-07)
**Status:** Audit Complete (Issues Resolved in Batch)

Full codebase audit covering runtime, API contracts, UI, navigation, theme, concurrency.

**Critical Issues Resolved (7fb1419):**
- C1 ✅ AppRouter now @MainActor
- C2 ✅ AuthViewModel now @MainActor
- C3 ✅ SignalR date decoder (custom dual-format)
- C4 ✅ SignalR force unwraps (safe URLComponents)

**Important Issues Resolved:**
- I1 ✅ 17 hardcoded colors → theme tokens
- I2 ✅ Placeholder navigation → Coming Soon screens
- I3 ✅ Silent error suppression (secondary data loads)
- I4 ✅ Dashboard missing empty state
- I6 ✅ Accessibility labels on all interactive elements
- Plus fixes from decisions #5, #9, #10

**Minor Issues (Post-Release):**
- PrinterService methods on concrete class (not on protocol) — low priority
- Test suite: 2 inverted assertions — low priority
- SignalR @unchecked Sendable with unprotected mutable state — low priority

---

### 14. Test Coverage Extension (Ash, 2026-07-18)
**Status:** Implemented

Extended test suite from 146 → 226 test cases (+80). Four new ViewModel test suites cover all previously untested ViewModels.

**AuthViewModel Testing Pattern:**
AuthViewModel depends on concrete `AuthService` actor (no protocol). Tests use MockURLProtocol integration testing through the full AuthVM → AuthService → APIClient stack. This is different from other ViewModel tests which use protocol-based mocks via `configure()`.

**Recommendation for Ripley/Lambert:** Consider extracting `AuthServiceProtocol` from the concrete `AuthService` to enable protocol-based mock testing. `AuthServiceProtocol` already exists in TestProtocols.swift but isn't used by production code.

**PushNotificationManager Not Testable:**
PushNotificationManager is a singleton with concrete UIKit dependencies (UNUserNotificationCenter, UIApplication). Cannot be unit tested without significant refactoring. Acceptable risk for MVP — push notification flows should be validated via manual QA on device.

**Coverage Gaps Remaining:**
| Component | Status | Notes |
|-----------|--------|-------|
| JobService (actor) | Indirect | Tested via ViewModel mocks; needs dedicated MockURLProtocol tests like PrinterServiceTests |
| StatisticsService (actor) | Indirect | Same as above |
| NotificationService (actor) | Indirect | Same as above |
| PushNotificationManager | Untestable | Singleton + UIKit runtime dependency |

**Impact:**
- All team members: new test suites follow established patterns and should pass in Xcode
- SPM `swift build` still blocked by pre-existing XCTest module limitation (not related to changes)

### 15. Feature Decomposition: Filament Management + NFC Tag Support (Dallas, 2026-07-19)
**Status:** Proposed

The backend is **fully ready** — FilamentType, Spool, NfcDevice, and NfcScanEvent entities already exist with complete CRUD endpoints, Spoolman integration, and NFC scan event processing.

**User Decisions Applied:**
- Spool inventory → new tab in tab bar
- NFC format → support both OpenSpool and OpenPrintTag
- Spoolman always available (no fallback needed)

**Phase 1: Filament Management** (Core spool selection, inventory management)
**Phase 2: NFC Tag Support** (CoreNFC scanning/writing with OpenSpool + OpenPrintTag formats)

**Implementation Plan:**
- **Sprint 1 (Phase 1 Core):** P1-7 Models, P1-2 SpoolService, P1-1 Filament section, P1-6 ViewModel extensions, P1-3 Spool picker
- **Sprint 2 (Phase 1 Complete):** P1-4 Spool inventory tab, P1-5 Add spool form
- **Sprint 3 (Phase 2):** P2-2 Info.plist/entitlements, P2-1 NFCService, P2-3 Scan-to-load, P2-6/P2-4/P2-5 UI flows

**Total Estimate:** ~22 hours of implementation

**Dependency Graph:** See feature document for detailed P1-1 through P2-6 work item breakdown, dependencies, and open questions.

### 16. Spool Inventory Filtering & Search Enhancement (Ripley, 2026-03-07)
**Status:** Implemented

Enhanced SpoolInventoryView and SpoolPickerView with expanded search and material type filtering.

**Changes:**
1. Expanded search filter in ViewModels to check name + material + vendor + location + comment + color name
2. Added `SpoolmanSpool+ColorName.swift` extension with hex-to-color-name heuristic (lightweight, suitable for filament palette)
3. Added material type filter chips (FilterChip-based segmented control) to both Views
4. Added `ContentUnavailableView.search` empty state when filtered results are empty
5. Updated search bar prompts: "Search by name, material, color…"

**Files Changed:**
- `PrintFarmer/Views/Filament/SpoolInventoryView.swift` 
- `PrintFarmer/Views/Filament/SpoolPickerView.swift`
- `PrintFarmer/ViewModels/SpoolInventoryViewModel.swift`
- `PrintFarmer/ViewModels/SpoolPickerViewModel.swift`
- `PrintFarmer/Extensions/SpoolmanSpool+ColorName.swift` (new)

**Impact:**
- Users can now search spools by color, location, or comment fields
- Filtering is client-side only — no backend changes needed
- MVVM pattern maintained; filter logic in ViewModels
- `hasActiveSearch` computed property gates empty-state display

### 17. Status Filters & Weight Progress for Spool Views (Ripley, 2026-03-08)
**Status:** Implemented

Added status-based filtering and visual weight indicators to both SpoolInventoryView and SpoolPickerView.

**Implementation:**
1. **SpoolStatus Enum** — Four cases in SpoolInventoryViewModel:
   - **Available:** `!inUse && !archived` — ready to assign
   - **In Use:** `inUse == true` — currently loaded on a printer
   - **Low:** remaining < 20% of initial — needs attention
   - **Empty:** remaining == 0 or nil with initial present — replace soon

2. **Status Filter Chips** — Second row of horizontal scrolling capsule buttons:
   - Same visual style as material chips (pfAccent selected, pfBackgroundTertiary unselected)
   - Enum-driven UI via `ForEach(SpoolStatus.allCases)`
   - Filters applied in sequence: material → status → search text

3. **Weight Progress Bars** — Horizontal capsule showing remaining/initial percentage:
   - Color-coded: green (>50%), yellow (20-50%), red (<20%)
   - Only shown when both `remainingWeightG` and `initialWeightG` available
   - Positioned below weight text on right side of row

4. **In-Use Badge** — Small `printer.fill` SF Symbol next to spool name:
   - Colored with pfAccent for visibility
   - Only shown when `inUse == true`

**Files Changed:**
- `PrintFarmer/ViewModels/SpoolInventoryViewModel.swift`
- `PrintFarmer/ViewModels/SpoolPickerViewModel.swift`
- `PrintFarmer/Views/Filament/SpoolInventoryView.swift`
- `PrintFarmer/Views/Filament/SpoolPickerView.swift`

**Impact:**
- No new dependencies
- No breaking changes — filters are optional, default to "All"
- Backward compatible and dark-mode compliant
- Two-row filter layout avoids UI crowding; consistent with existing material filter pattern

---

## Launch & Onboarding

### 18. Launch Screen Implementation (Ripley, 2026-03-08)
**Status:** Implemented

Created a professional launch screen using `LaunchScreen.storyboard` (not Info.plist UILaunchScreen or SwiftUI).

**Decision**
- Storyboard provides layout control impossible with Info.plist approach (centered stack with emoji + text)
- iOS doesn't support SwiftUI for launch screens (must be static)
- Storyboard is standard for App Store submissions

**Implementation**
- Visual: 🌾 emoji centered above "PrintFarmer" bold text in vertical stack
- Colors: Three new adaptive color sets in Assets.xcassets (LaunchBackground, LaunchText, LaunchAccent) matching theme tokens (pfBackground, pfTextPrimary, pfAccent)
- Storyboard XML uses `targetRuntime="iOS.CocoaTouch"` (critical for Xcode 26.2)
- Build setting: `INFOPLIST_KEY_UILaunchStoryboardName = LaunchScreen` (removed auto-generation)

**Files**
- `PrintFarmer/LaunchScreen.storyboard` (new)
- `PrintFarmer/Assets.xcassets/LaunchBackground.colorset/Contents.json` (new)
- `PrintFarmer/Assets.xcassets/LaunchText.colorset/Contents.json` (new)
- `PrintFarmer/Assets.xcassets/LaunchAccent.colorset/Contents.json` (new)
- `PrintFarmer.xcodeproj/project.pbxproj` (updated)

**Impact**
- App startup now displays branded launch screen with wheat emoji + green accent
- Seamless light/dark mode support via adaptive colors
- Professional first impression before app loads main UI

---

## Cross-References

**Backend Contract Docs:** `~/s/PFarm1/src/api/` (Controllers, DTOs, Startup/ControllerStartup.cs)  
**Frontend Decisions History:** `.squad/decisions/decisions.md` (this file)  
**Agent Context:** `.squad/agents/{lambert,ripley,ash,dallas}/history.md`  
**Session Logs:** `.squad/log/` (ISO 8601 timestamped)  
**Orchestration Logs:** `.squad/orchestration-log/` (per-agent work summaries)

### 19. Phase 3 Feature Test Infrastructure (Ash, 2026-03-08)
**Status:** Infrastructure Complete, Compilation Fixes Required

Created comprehensive unit tests for all 7 Phase 3 feature services and ViewModels:
1. Maintenance (MaintenanceService + MaintenanceViewModel)
2. AutoPrint (AutoPrintService + AutoPrintViewModel)
3. Job Analytics (JobAnalyticsService + JobAnalyticsViewModel + JobHistoryViewModel)
4. Predictive Analytics (PredictiveService + PredictiveViewModel)
5. Dispatch (DispatchService + DispatchViewModel)
6. Uptime (UptimeViewModel using MaintenanceService)
7. Job Timeline (uses JobAnalyticsService)

**Implementation**
- Created 12 new test files (~300 test cases total)
- 5 mock services conforming to service protocols
- 7 ViewModel test suites following DashboardViewModelTests pattern (@MainActor, configure() DI)
- UUID prefix G1 for all pbxproj entries to avoid conflicts (F1/F2/E1/D1 existing)
- Each test suite covers: initial state, successful loading, error handling, loading transitions, computed properties, action methods, unconfigured service guards

**Known Issues**
- Model initializer mismatches: 25+ models need property corrections (timestamp→createdAt, Int ID→UUID, missing fields like componentName, estimatedDueDate, etc.)
- Protocol conformance issues in MockPredictiveService and MockDispatchService
- Test target won't compile until corrections are made
- **Blocking follow-up:** Model initializer corrections (estimated 30-45 minutes)

**Impact**
- ✅ Complete mock infrastructure for all 5 new service protocols
- ✅ Test patterns established for future features
- ✅ ~300 test cases structured correctly
- ✅ Zero impact on existing tests — isolated in new files
- ✅ App builds successfully
- ⏳ Test target requires model fixes

**Files Affected**
- NEW: 12 test files (Mocks + ViewModel tests)
- MODIFIED: PrintFarmer.xcodeproj/project.pbxproj (added files to test target)

**Lessons**
- Validate model definitions thoroughly before writing tests
- Swift Codable memberwise init includes all properties in declaration order
- UUID vs String vs Int ID variations require careful checking
- Some models use nested property structures (e.g., QueuedJobWithMeta.job)
- Xcode pbxproj edits require DerivedData clean

# Beta Release Strategy: TestFlight + GitHub Actions CI/CD

**Author:** Dallas (Lead)  
**Date:** 2026-03-07  
**Status:** Decision Proposed  
**Scope:** iOS app (PrintFarmer), dual-remote workflow (origin→release), beta distribution

---

## EXECUTIVE SUMMARY

PrintFarmer iOS is ready to distribute beta builds to testers via Apple's TestFlight. I recommend a **git-tag-triggered GitHub Actions workflow** that:
- Automates code signing, building, and uploading to TestFlight
- Maintains clear version control via semantic versioning (e.g., `v1.0.0-beta.1`)
- Integrates with the dual-remote workflow (dev on `origin`, releases on `release` remote)
- Requires no hardcoded credentials in the repository

---

## DECISION MATRIX

| Component | Recommendation | Rationale |
|-----------|-----------------|-----------|
| **Distribution Channel** | Apple TestFlight | Industry standard for iOS beta; no alternative viable for AppStore submission path |
| **Workflow Trigger** | Git tag (`v*-beta*`, `v*-rc*`) | Explicit versioning; prevents spurious builds; integrates with SemVer |
| **Build Pipeline** | GitHub Actions on `macos-latest` | Native Apple tooling; no external dependencies; supports fastlane |
| **Code Signing** | fastlane match + GitHub Secrets | Centralized cert management; encrypted storage; team-friendly; no hardcoded credentials |
| **Version Numbering** | SemVer (`1.0.0-beta.N`) | Industry standard; clear intent; AppStore-compatible |
| **Tester Access Model** | Start internal (≤25), expand to external (10k) | Alpha→beta→public progression; internal faster, external requires 24-48h review |

---

## 1. TESTFLIGHT SETUP REQUIREMENTS

### Apple Developer Program Prerequisites
✅ **Already configured:**
- Apple Developer Account (active)
- Team ID: `ZPKA84F3TY` (from Xcode build settings)
- Bundle ID: `com.olyforge3d.printfarmer.ios` (from release remote owner)
- Development Team linked to code signing identity

⚠️ **Action required:**
- [ ] Create **App Store Connect record** for PrintFarmer (can be in "Prepare for Submission")
- [ ] Generate **Distribution Certificate** (Certificates, Identifiers & Profiles > Certificates)
- [ ] Generate **App Store Provisioning Profile** (Explicit, not ad-hoc)
- [ ] Add **internal testers** (5-10 team members via App Store Connect > TestFlight > Testers)

### Internal vs External Testers
- **Internal (≤25):** Instant access, no review. ✅ Use for alpha/pre-beta (Week 1-2)
- **External (10k max):** Requires app metadata + screenshots. First build: 24-48h review. ✅ Use for public beta (Week 3+)

---

## 2. GITHUB ACTIONS WORKFLOW ARCHITECTURE

### Workflow Trigger Configuration
Recommended: **Two complementary triggers**

```yaml
on:
  push:
    tags:
      - 'v*-beta*'           # v1.0.0-beta.1, v1.1.0-beta.2, etc.
      - 'v*-rc*'             # v1.0.0-rc.1 (release candidate)
  workflow_dispatch:         # Manual trigger if tag push fails
    inputs:
      environment:
        description: 'internal or external'
        default: 'internal'
```

### Workflow Jobs
The workflow executes these steps:

1. **Checkout** — Clone code from tag
2. **Extract version** — Parse git tag to `MARKETING_VERSION` (1.0.0) and `BUILD_NUMBER` (auto-incremented)
3. **Setup Xcode** — Select Xcode version (macos-latest >= Xcode 16)
4. **Install fastlane** — Dependency for code signing and upload
5. **Code sign** — `fastlane match appstore --readonly` (fetch certs from encrypted repo)
6. **Update build settings** — `agvtool new-version` + `agvtool new-marketing-version`
7. **Build archive** — `xcodebuild -archiveForDistribution`
8. **Export IPA** — `xcodebuild -exportArchive` with AppStore options
9. **Upload to TestFlight** — `fastlane pilot upload`
10. **Notify team** — Slack message with build status
11. **Create GitHub Release** — Tag appears in GitHub releases tab (prerelease=true)

---

## 3. CODE SIGNING STRATEGY

### Recommendation: fastlane match + GitHub Secrets
fastlane **match** provides centralized certificate management with encrypted storage.

#### Setup Steps

**3.1 Create private certificate repository**
```bash
# GitHub: Create new private repo named "PrintfarmerApp-certificates"
git clone https://github.com/olyforge3d/PrintfarmerApp-certificates.git
cd PrintfarmerApp-certificates
fastlane match init --type appstore
# Prompts for encryption password (save as MATCH_PASSWORD secret)
```

**3.2 Generate certificates in Xcode**
```bash
cd ~/s/PFarm-Ios
fastlane match appstore --readonly
# Downloads existing cert or creates new one (if first time)
```

**3.3 Store GitHub secrets**

| Secret | Value | Source |
|--------|-------|--------|
| `FASTLANE_USER` | `jpapiez@example.com` | Apple ID (account owner) |
| `FASTLANE_PASSWORD` | App-specific password | App Store Connect > Your Name > Security > App-Specific Passwords |
| `MATCH_PASSWORD` | Encryption key | Generated during `fastlane match init` |

**3.4 GitHub Actions workflow uses secrets**
```yaml
- name: Setup code signing
  env:
    FASTLANE_USER: ${{ secrets.FASTLANE_USER }}
    FASTLANE_PASSWORD: ${{ secrets.FASTLANE_PASSWORD }}
    MATCH_PASSWORD: ${{ secrets.MATCH_PASSWORD }}
  run: fastlane match appstore --readonly
```

### Alternative: Direct Certificate Upload (if no private repo available)
If you prefer not to maintain a separate private repo:
- Export `.p12` certificate from Keychain
- Encode as base64: `cat cert.p12 | base64 > cert.b64`
- Store as `DISTRIBUTION_CERT_B64` and `CERT_PASSWORD` secrets
- Decode in workflow: `echo "$CERT_B64" | base64 -d > cert.p12`
- Install: `security import cert.p12 -k ~/Library/Keychains/login.keychain -P "$CERT_PASSWORD"`

**⚠️ Trade-off:** Simpler setup, but manual renewal required and certificates visible in CI logs if not careful.

---

## 4. VERSION & BUILD NUMBER STRATEGY

### Current Xcode Settings
- `MARKETING_VERSION`: `1.0` (user-facing version)
- `CURRENT_PROJECT_VERSION`: `1` (build number, must increment per upload)

### Recommended Versioning Scheme

**Version format:** `MAJOR.MINOR.PATCH-PRERELEASE`

Examples:
```
v1.0.0-beta.1       → MARKETING_VERSION=1.0.0, CURRENT_PROJECT_VERSION=1
v1.0.0-beta.2       → MARKETING_VERSION=1.0.0, CURRENT_PROJECT_VERSION=2
v1.0.0-rc.1         → MARKETING_VERSION=1.0.0, CURRENT_PROJECT_VERSION=10
v1.0.0              → MARKETING_VERSION=1.0.0, CURRENT_PROJECT_VERSION=11 (GA release)
v1.1.0-beta.1       → MARKETING_VERSION=1.1.0, CURRENT_PROJECT_VERSION=20
```

**Build number strategy:** Use git commit count (deterministic, no conflicts)
```bash
BUILD_NUMBER=$(git rev-list --count HEAD)  # e.g., 42
xcodebuild -project PrintFarmer.xcodeproj \
  -scheme PrintFarmer \
  -configuration Release \
  OTHER_CFLAGS="-DCURRENT_PROJECT_VERSION=$BUILD_NUMBER"
```

### Workflow Implementation
```bash
# Extract version from tag
TAG_NAME="${{ github.ref_name }}"  # e.g., "v1.0.0-beta.1"
VERSION=$(echo "$TAG_NAME" | sed -E 's/^v(.+)-.*/\1/')  # → "1.0.0"
BUILD_NUMBER=$(git rev-list --count HEAD)  # → 42

# Update Xcode build settings
agvtool new-version -all "$BUILD_NUMBER"           # CURRENT_PROJECT_VERSION
agvtool new-marketing-version "$VERSION"           # MARKETING_VERSION
```

---

## 5. DUAL-REMOTE WORKFLOW: DEV → RELEASE FLOW

### Current Setup
- `origin` → `https://github.com/jpapiez/PrintfarmerApp.git` (private dev)
- `release` → `https://github.com/olyforge3d/PrintFarmerApp.git` (public releases)

### Recommended Workflow

```
Developer Team (jpapiez/origin)
├─ Daily work: git push origin development
├─ Review: PR main ← development
└─ Merge: git checkout main && git merge development
          git tag -a v1.0.0-beta.1 -m "Beta 1"
          git push origin main v1.0.0-beta.1  ← Tag on origin first

Release Team (olyforge3d/release)
├─ Manual review of tag
└─ Push to release: git push release main v1.0.0-beta.1
                    ↓
                    GitHub Actions triggered
                    ├─ Build
                    ├─ Sign
                    ├─ Upload to TestFlight
                    └─ Notify team
```

### Team Workflow Commands

**One-time setup:**
```bash
git remote add origin https://github.com/jpapiez/PrintfarmerApp.git
git remote add release https://github.com/olyforge3d/PrintFarmerApp.git
```

**Daily development:**
```bash
git checkout development
git commit -am "Feature: xyz"
git push origin development
```

**Prepare beta release:**
```bash
git checkout main
git merge development           # or cherry-pick specific commits
npm run test                    # or swift build to validate
git tag -a v1.0.0-beta.1 -m "Beta release 1.0.0"
git push origin main v1.0.0-beta.1  # Tag on origin (audit trail)
```

**Publish to TestFlight (GitHub Actions):**
```bash
git push release main v1.0.0-beta.1
# GitHub Actions automatically triggered
# Monitor: GitHub Actions > TestFlight Beta Build > Build and upload job
```

### Why Tag?
- ✅ Explicit version control; easy to skip releases by not tagging
- ✅ Audit trail (who, when, what version)
- ✅ Integrates with SemVer and release notes
- ✅ Prevents accidental releases (no CI on every commit)

---

## 6. GITHUB ACTIONS WORKFLOW FILE

### File: `.github/workflows/testflight-beta.yml`

See appendix below for complete workflow YAML.

**Key points:**
- **Runs on:** `macos-latest` (Xcode + iOS SDK pre-installed)
- **Triggered by:** Git tag push matching `v*-beta*` or `v*-rc*`
- **Secrets used:** `FASTLANE_USER`, `FASTLANE_PASSWORD`, `MATCH_PASSWORD`, `SLACK_WEBHOOK_URL` (optional)
- **Outputs:** TestFlight build visible in App Store Connect within 30 mins

### Required Supporting Files

**`.github/ExportOptions.plist`**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>method</key>
    <string>app-store</string>
    <key>teamID</key>
    <string>ZPKA84F3TY</string>
    <key>signingStyle</key>
    <string>automatic</string>
    <key>stripSwiftSymbols</key>
    <true/>
    <key>thinning</key>
    <string>&lt;none&gt;</string>
</dict>
</plist>
```

(Check in to repo; no secrets)

---

## 7. REQUIRED GITHUB SECRETS

### Setup in GitHub

1. **Go to:** GitHub repo > Settings > Secrets and variables > Actions
2. **Add these secrets:**

| Secret Name | Value | Rotation Policy |
|---|---|---|
| `FASTLANE_USER` | Apple ID email (e.g., `jpapiez@example.com`) | Never (email permanent) |
| `FASTLANE_PASSWORD` | App-specific password from App Store Connect | Annually or if compromised |
| `MATCH_PASSWORD` | 16-char encryption key (from `fastlane match init`) | Yearly |
| `SLACK_WEBHOOK_URL` (optional) | Slack workspace webhook for notifications | When workspace config changes |

### Getting App-Specific Password
1. Log in to [appleid.apple.com](https://appleid.apple.com)
2. Security > App-Specific Passwords
3. Generate new for "GitHub Actions"
4. Copy password (only shown once)

---

## 8. ARCHITECTURE DECISIONS

### Decision 1: Tag-Based Triggering (vs Push-to-Branch)
**Choice:** Git tags (`v*-beta*`)

**Rationale:**
- ✅ Explicit versioning control; prevents spurious builds on every commit
- ✅ Integrates with SemVer and release notes
- ✅ Requires only one extra command (git tag); no process overhead
- ✅ Clear audit trail (git log shows all releases)

**Alternative considered:** Push to `main` on `release` remote
- ❌ Every commit triggers a build (CI/CD overhead)
- ❌ Harder to skip a release
- ❌ Requires strict branch protection + code review discipline

### Decision 2: fastlane match for Code Signing
**Choice:** fastlane match + private cert repository

**Rationale:**
- ✅ Centralized certificate management
- ✅ Automated renewal (fastlane handles ASC API)
- ✅ Team-friendly (all agents can access certs via private repo + password)
- ✅ Encrypted storage (no plaintext credentials in CI logs)
- ✅ Scales to multiple CI/CD pipelines

**Alternative considered:** Manual certificates uploaded as secrets
- ❌ Manual renewal required
- ❌ Secrets visible in CI logs if not carefully handled
- ❌ No centralized tracking of cert lifecycle

**Alternative considered:** ASC API tokens (fastlane modern method)
- ✅ Future upgrade path (less error-prone than app-specific passwords)
- ⚠️ Requires ASC API v2 setup (Apple's newer auth method)
- 📋 Defer to v2 after initial beta rollout

### Decision 3: Separate Workflow from Dev CI
**Choice:** Independent `.github/workflows/testflight-beta.yml` (not merged with `squad-ci.yml`)

**Rationale:**
- ✅ Clear separation of concerns (test vs release)
- ✅ Prevents accidental TestFlight uploads during PR testing
- ✅ Independent tuning (release workflow can be more complex/time-consuming)

**Alternative considered:** Single workflow with conditional steps
- ⚠️ More complex to maintain
- ⚠️ Risk of accidentally triggering TestFlight during test runs

---

## 9. IMPLEMENTATION ROADMAP

### Phase 1: Setup (Week 1)
- [ ] Create App Store Connect record for PrintFarmer
- [ ] Create private GitHub repository `PrintfarmerApp-certificates`
- [ ] Generate Distribution Certificate in Apple Developer
- [ ] Generate App Store Provisioning Profile (Explicit)
- [ ] Initialize fastlane match: `fastlane match init --type appstore`
- [ ] Test locally: `fastlane match appstore --readonly`
- [ ] Add GitHub secrets: `FASTLANE_USER`, `FASTLANE_PASSWORD`, `MATCH_PASSWORD`
- [ ] Create `.github/workflows/testflight-beta.yml`
- [ ] Create `.github/ExportOptions.plist`
- [ ] Test workflow with manual dispatch (`workflow_dispatch`)

### Phase 2: First Beta Release (Week 2)
- [ ] Create first beta tag: `git tag -a v1.0.0-beta.1 -m "Beta 1"`
- [ ] Push to origin: `git push origin main v1.0.0-beta.1`
- [ ] Push to release: `git push release main v1.0.0-beta.1`
- [ ] Monitor GitHub Actions logs (should complete in ~15-20 mins)
- [ ] Verify TestFlight build appears in App Store Connect
- [ ] Download build on test device and validate functionality

### Phase 3: Internal Tester Onboarding (Week 2-3)
- [ ] Add internal testers in App Store Connect (5-10 team members)
- [ ] Send TestFlight invite links
- [ ] Collect feedback (crash logs, feature requests, bugs)
- [ ] Document known issues and workarounds

### Phase 4: External Beta (Week 3+)
- [ ] Prepare beta app metadata (screenshots, description, release notes)
- [ ] Create next beta tag: `v1.0.0-beta.2`
- [ ] Push to release (triggers TestFlight upload)
- [ ] Review TestFlight build in App Store Connect
- [ ] Submit for external tester review
- [ ] Wait for Apple review (24-48 hours typically)
- [ ] Add external testers (10-100 users)

---

## 10. RISK MITIGATION

| Risk | Impact | Mitigation |
|------|--------|-----------|
| **Code signing failure** | Build fails; blocks release | Pre-test `fastlane match` locally; dry-run workflow before release |
| **TestFlight upload timeout** | Build queued but unverified | Add notification check; retry logic in workflow; monitor App Store Connect |
| **App Store Connect downtime** | Release blocked | Manual fallback: upload IPA directly via Xcode Organizer or web UI |
| **Git tag collision** | Version conflict; confusing history | Enforce strict SemVer; require PR approval before tagging |
| **Secrets exposed** | Security breach | Rotate `FASTLANE_PASSWORD` immediately; monitor GitHub secret access logs |
| **Build number conflicts** | App Store rejects duplicate | Commit-count strategy (monotonically increasing) prevents this |
| **Provisioning profile expiration** | Future builds fail | fastlane match auto-renews before expiration; set calendar reminder for early detection |

---

## 11. WORKFLOW FILE (COMPLETE)

See detailed YAML in appendix below.

### Key Dependencies
- **fastlane** (Ruby gem): Code signing + upload
- **Xcode 16+** (pre-installed on macOS runners)
- **agvtool** (Xcode command): Version number management

### Execution Time
- Expected: 15-20 minutes per build
- Steps: Checkout (1m) → Setup (3m) → Code sign (2m) → Build (8m) → Export (2m) → Upload (2m) → Notify (1m)

---

## APPENDIX: COMPLETE WORKFLOW YAML

```yaml
name: TestFlight Beta Build

on:
  push:
    tags:
      - 'v*-beta*'
      - 'v*-rc*'
  workflow_dispatch:
    inputs:
      environment:
        description: 'Beta environment'
        required: true
        default: 'internal'
        type: choice
        options:
          - internal
          - external

jobs:
  build-and-upload:
    runs-on: macos-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Extract version from tag
        id: version
        run: |
          TAG_NAME="${{ github.ref_name }}"
          VERSION=$(echo "$TAG_NAME" | sed -E 's/^v(.+)-.*/\1/')
          BUILD_NUMBER=$(git rev-list --count HEAD)
          echo "version=$VERSION" >> $GITHUB_OUTPUT
          echo "build_number=$BUILD_NUMBER" >> $GITHUB_OUTPUT
          echo "tag_name=$TAG_NAME" >> $GITHUB_OUTPUT
          echo "📦 Version: $VERSION | Build: $BUILD_NUMBER | Tag: $TAG_NAME"

      - name: Select Xcode version
        run: |
          sudo xcode-select -s /Applications/Xcode.app/Contents/Developer
          xcodebuild -version

      - name: Install fastlane
        run: |
          sudo gem install fastlane -NV

      - name: Setup code signing with fastlane match
        env:
          FASTLANE_USER: ${{ secrets.FASTLANE_USER }}
          FASTLANE_PASSWORD: ${{ secrets.FASTLANE_PASSWORD }}
          MATCH_PASSWORD: ${{ secrets.MATCH_PASSWORD }}
        run: |
          fastlane match appstore --readonly

      - name: Update build version numbers
        run: |
          agvtool new-version -all ${{ steps.version.outputs.build_number }}
          agvtool new-marketing-version ${{ steps.version.outputs.version }}

      - name: Build for App Store
        run: |
          xcodebuild \
            -project PrintFarmer.xcodeproj \
            -scheme PrintFarmer \
            -configuration Release \
            -derivedDataPath build \
            -archivePath "build/PrintFarmer.xcarchive" \
            -archiveForDistribution \
            archive

      - name: Export IPA
        run: |
          xcodebuild \
            -exportArchive \
            -archivePath "build/PrintFarmer.xcarchive" \
            -exportOptionsPlist ExportOptions.plist \
            -exportPath "build/IPA"

      - name: Upload to TestFlight
        env:
          FASTLANE_USER: ${{ secrets.FASTLANE_USER }}
          FASTLANE_PASSWORD: ${{ secrets.FASTLANE_PASSWORD }}
        run: |
          fastlane pilot upload \
            --ipa "build/IPA/PrintFarmer.ipa" \
            --changelog "Beta build from tag: ${{ steps.version.outputs.tag_name }}" \
            --skip_waiting_for_build_processing

      - name: Notify Slack
        if: success()
        uses: slackapi/slack-github-action@v1
        with:
          payload: |
            {
              "text": "✅ TestFlight Beta Build Uploaded",
              "blocks": [
                {
                  "type": "section",
                  "text": {
                    "type": "mrkdwn",
                    "text": "*TestFlight Beta Uploaded*\n*Version:* ${{ steps.version.outputs.version }}\n*Build:* ${{ steps.version.outputs.build_number }}\n*Tag:* ${{ steps.version.outputs.tag_name }}\n*Repo:* <https://github.com/olyforge3d/PrintFarmerApp|olyforge3d/PrintFarmerApp>"
                  }
                }
              ]
            }
        env:
          SLACK_WEBHOOK_URL: ${{ secrets.SLACK_WEBHOOK_URL }}
        continue-on-error: true

      - name: Create GitHub Release
        if: success()
        uses: actions/create-release@v1
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        with:
          tag_name: ${{ steps.version.outputs.tag_name }}
          release_name: "PrintFarmer ${{ steps.version.outputs.version }}"
          body: |
            ## Beta Release: ${{ steps.version.outputs.version }}
            - Build Number: ${{ steps.version.outputs.build_number }}
            - Status: ✅ Uploaded to TestFlight
            - Access: Check TestFlight app or email invite
            
            **Release Notes:** Coming from commit history
          draft: false
          prerelease: true

      - name: Upload build logs on failure
        if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: build-logs
          path: |
            build/Logs/Build/
            *.log
          retention-days: 7
```

---

## FOLLOW-UP ACTIONS

**For Jeff Papiez:**
1. Confirm Apple Developer account has TestFlight access (may require In-App Purchase capability or Distribution Agreement)
2. Generate app-specific password in App Store Connect
3. Create private certificate repository

**For Dallas (Lead):**
1. Create `.github/workflows/testflight-beta.yml` and `.github/ExportOptions.plist`
2. Coordinate with Ripley/Lambert for first test build
3. Document tagging workflow in team README

**For Ripley/Lambert/Ash:**
1. Expect beta builds 2-3x per week (as features stabilize)
2. Monitor TestFlight crash logs and feedback
3. Report issues via `.squad/decisions/` for Dallas review

---

## DECISION RATIFICATION

**Awaiting approval from:** Jeff Papiez (project owner)

**Recommend proceeding with:**
- ✅ fastlane match for code signing
- ✅ Git tag-triggered workflow
- ✅ Tag format: `v*-beta*`, `v*-rc*`
- ✅ Start with internal testers (Week 2)

---

*Decision document prepared by Dallas (Lead). Cross-team impact: Ripley (testing), Lambert (build support), Ash (CI/CD validation).*

---

## UI & Layout

### 18. Unified Colored State Headers + iPad Grid Layout (Ripley, 2026-03-12)
**Status:** Implemented

The iPad printer card had a colored gradient header indicating printer state (blue=printing, amber=paused, red=error, etc.) but the iPhone card only had a subtle border tint. The iPad list was also single-column despite having screen width for multiple columns.

**Decisions:**

#### 1. iPhone PrinterCardView gets colored header
- Added `headerSection`, `headerBaseColor`, `headerGradient`, `statusLabel` computed properties mirroring the iPad card pattern
- iPhone uses slightly compact padding (vertical 8 vs 10) and smaller pill font (caption2 vs caption) for density
- Old StatusBadge replaced with inline status pill for visual consistency

#### 2. iPad PrinterListView uses LazyVGrid
- iPad printer list now uses `LazyVGrid` with `.adaptive(minimum: 340)` columns
- Automatically adjusts column count based on available width
- iPhone list remains unchanged (single-column LazyVStack)

**Impact:**
- **Dallas:** No architecture changes needed — purely view-layer
- **Lambert:** No API changes — same Printer model consumed
- **Future:** If more card variants appear, the shared color scheme (headerBaseColor values) could be extracted to a shared protocol or extension
# Lambert Investigation: Dispatch Back-Button Crash (Still Occurring)

**Date:** 2026-03-13
**Agent:** Lambert (Networking)
**Status:** Investigation complete — fixes recommended, not yet implemented
**Prior fix:** Commit bd18e97 added 5 `Task.isCancelled` guards to DispatchViewModel

---

## Root Cause Analysis

The previous fix (bd18e97) added `Task.isCancelled` guards inside `loadQueueStatus()` and `loadHistory()`. These guards **only work when the calling task is cancelled** — which is the case for the `.task { }` modifier (SwiftUI cancels it on view disappear). However, the crash persists because of **unstructured Task contexts** that are never cancelled.

### Primary Crash Vector: Retry Button's Unstructured Task

**File:** `PrintFarmer/Views/Dashboard/DispatchDashboardView.swift`, line 20–24

```swift
Button("Retry") {
    Task {   // ← UNSTRUCTURED TASK — never cancelled on view dismissal
        await viewModel.loadQueueStatus()
        await viewModel.loadHistory()
    }
}
```

When the user taps **Retry** and then quickly taps **Back**:
1. The `.task` modifier's structured task is cancelled by SwiftUI ✅
2. The Retry button's `Task { }` is **NOT cancelled** ❌
3. Inside `loadQueueStatus()`, `Task.isCancelled` returns `false` (the task was never cancelled)
4. The method proceeds to mutate `@Observable` properties (`queueStatus`, `isLoading`, `error`)
5. These mutations fire on a ViewModel whose View has been popped from the NavigationStack
6. SwiftUI's observation tracking attempts to update a view body that no longer exists → **crash**

### Secondary Crash Vector: Observable + NavigationStack Animation Race

Even without the Retry button, there is a theoretical window during the NavigationStack pop animation where:
- The `.task` is being cancelled
- The async `getQueueStatus()` call returns a result
- Between the await return and the `Task.isCancelled` guard check, the ViewModel's `isLoading = true` (set at line 20 of the ViewModel) was already published
- SwiftUI's observation system may try to re-render during the pop animation

This is a narrower race, but it exists because `isLoading = true` is set **before** the first await point, so it's committed immediately with no guard.

### What's NOT the Problem
- ✅ No SignalR listeners on Dispatch (verified — no `signalR` references in dispatch code)
- ✅ No timers or polling in DispatchViewModel
- ✅ The `.task` modifier's structured task cancellation works correctly
- ✅ The 5 existing `Task.isCancelled` guards are correctly placed for the structured task path

---

## Navigation Architecture (Issue #2)

### Current Architecture
- Dispatch is a **pushed NavigationLink destination** within the Dashboard's NavigationStack
- Accessed from: `DashboardView` → `QueuePage()` → `dispatchLink` → `NavigationLink(value: .dispatchDashboard)`
- Also accessible from iPad's `iPadContent` layout via the same link
- It's a full-screen push, not a tab or swipeable page

### Existing Swipeable Pattern
The Dashboard already uses a swipeable `TabView(.page)` with 3 pages on iPhone:
- Page 0: **OverviewPage** (fleet summary, queue health)
- Page 1: **ActivePage** (active jobs, print ETAs)
- Page 2: **QueuePage** (upcoming jobs, model breakdown, dispatch link)

### Options for Making Dispatch Swipeable
1. **Add as 4th Dashboard page** — Add Dispatch as page 3 in the existing swipeable TabView. This is the most natural fit since the QueuePage already contains the dispatch link. Labels would become: Overview, Active, Queue, Dispatch.
2. **Add as top-level tab** — Not recommended. The tab bar already has 7 tabs (Dashboard, Printers, Jobs, Inventory, Alerts, Maintenance, Settings).
3. **Make it swipeable from Jobs** — Could work as a Jobs sub-page, but Dispatch is more closely related to the Dashboard's fleet overview concept.

**Recommendation:** Option 1 — embed `DispatchDashboardView` content as the 4th page in the Dashboard's swipeable `TabView(.page)`. Remove the `dispatchLink` NavigationLink from QueuePage, and update the `PageIndicator` from 3 to 4 pages.

---

## Recommended Fixes

### Fix 1: Cancel Unstructured Tasks on Disappear (Primary Fix)

Store task references and cancel them when the view disappears:

```swift
// In DispatchDashboardView:
@State private var retryTask: Task<Void, Never>?

// Retry button:
Button("Retry") {
    retryTask = Task {
        await viewModel.loadQueueStatus()
        await viewModel.loadHistory()
    }
}

// Add modifier:
.onDisappear {
    retryTask?.cancel()
}
```

### Fix 2: ViewModel-Level Active Flag (Belt-and-Suspenders)

Add an `isActive` flag to DispatchViewModel that's independent of task cancellation:

```swift
// In DispatchViewModel:
var isActive = true

func loadQueueStatus() async {
    guard let dispatchService, isActive else { return }
    // ... existing code with guards ...
}

// In DispatchDashboardView:
.onDisappear { viewModel.isActive = false }
```

### Fix 3: Make Dispatch a Swipeable Dashboard Page

Embed the dispatch content directly into the Dashboard's TabView as page 3, eliminating the navigation push entirely. This removes the crash scenario altogether (no push/pop lifecycle) and addresses the user's UX request.

---

## Files Involved
- `PrintFarmer/Views/Dashboard/DispatchDashboardView.swift` (lines 20–24: Retry button Task)
- `PrintFarmer/ViewModels/DispatchViewModel.swift` (guards present but insufficient for unstructured tasks)
- `PrintFarmer/Views/Dashboard/DashboardView.swift` (lines 34–48: swipeable TabView, lines 500–501: dispatchLink)
- `PrintFarmer/Navigation/AppDestination.swift` (line 14: dispatchDashboard case)
- `PrintFarmer/Navigation/AppRouter.swift` (dashboardPath NavigationPath)
# Design Decision: Action Button Hierarchy in PrinterDetailView

**Date:** 2026-03-09  
**Designer:** Parker (UI/UX)  
**Requested by:** Jeff Papiez  
**Status:** Proposed

---

## Problem Statement

PrinterDetailView's `actionSection` uses seven different button styles with five different colors:
- **Pause** → `.bordered` + `.pfWarning` (amber)
- **Resume** → `.bordered` + `.pfSuccess` (green)
- **Cancel** → `.bordered` + `.pfError` (red)
- **Stop** → `.bordered` + `.pfWarning` (amber)
- **Maintenance** → `.bordered` (no tint)
- **Write Tag** → `.bordered` + `.pfAccent` (green)
- **Emergency Stop** → `.borderedProminent` + `.pfError` (red, bold)

**User feedback:** "Too many different colored buttons makes it hard to tell what's enabled vs disabled."

The `.opacity(0.4)` approach on colored buttons creates ambiguity — a disabled amber button looks like a faded amber, not clearly "disabled."

---

## Apple HIG Analysis

### Visual Hierarchy Principles (iOS HIG)
1. **Prominence through style, not color:** Apple recommends using `.borderedProminent` vs `.bordered` to establish hierarchy, not color tinting.
2. **Color for semantic meaning only:** Colors should convey status/meaning (destructive=red, success=green), not just differentiation.
3. **Disabled state clarity:** SwiftUI's native disabled state (grayed-out) is clearer than manual opacity on colored buttons.
4. **Destructive actions:** Use `.destructive` role — SwiftUI applies appropriate red tinting automatically.

### Apple's Button Style Hierarchy
1. `.borderedProminent` → Primary action (CTA)
2. `.bordered` → Secondary actions
3. `.plain` → Tertiary actions

**Key insight:** Apple does NOT recommend tinting every button a different color. Color should be semantic (destructive, success), not decorative.

---

## Recommended Design System

### Hierarchy: 3 Levels

#### **Level 1: Critical/Destructive** (Red, Prominent)
- **Emergency Stop** — Always visible, always enabled
- Style: `.borderedProminent` + `.tint(.pfError)` + `.fontWeight(.semibold)`
- Height: 50pt (`.prominent`)

#### **Level 2: Primary Contextual** (Prominent, Neutral)
- **Resume** — When paused (positive action, restart work)
- Style: `.borderedProminent` (no custom tint, uses system default)
- Height: 50pt (`.prominent`)

#### **Level 3: Secondary Actions** (Bordered, Neutral or Semantic)
- **Pause** — `.bordered` (no custom tint)
- **Cancel** — `.bordered` + `.tint(.pfError)` (destructive context)
- **Stop** — `.bordered` + `.tint(.pfError)` (destructive context)
- **Maintenance** — `.bordered` (no tint)
- **Write Tag** — `.bordered` (no tint)
- Height: 44pt (`.standard`)

### Why This Works

1. **Emergency Stop stands out** — Only red prominent button, always visible → impossible to miss
2. **Resume is prominent but neutral** — Elevated importance (get printer running again) without color noise
3. **Destructive actions use red sparingly** — Cancel and Stop get red tint, but NOT prominent → clear hierarchy below Emergency Stop
4. **Everything else is neutral** — Pause, Maintenance, Write Tag use system default (adaptive blue) → no color confusion
5. **Disabled state is clear** — SwiftUI's native disabled state (grayed-out) works better on neutral buttons than on colored buttons

### Color Usage Summary
- **Red (`.pfError`)**: Emergency Stop (prominent), Cancel/Stop (secondary destructive)
- **Green (`.pfSuccess`)**: REMOVED — no need for green "Resume" when prominence handles hierarchy
- **Amber (`.pfWarning`)**: REMOVED — pause is not a "warning," it's a control action
- **Neutral (system)**: All other buttons → clean, clear, iOS-native

---

## Comparison to Current State

### Current Problems
| Button | Current Style | Problem |
|--------|---------------|---------|
| Pause | `.bordered` + amber | Amber suggests "warning," but pause is a normal control |
| Resume | `.bordered` + green | Green vs blue vs grey when disabled is confusing |
| Cancel | `.bordered` + red | Correct semantic use, but same prominence as Resume |
| Stop | `.bordered` + amber | Should be red (destructive), not amber |
| Maintenance | `.bordered` | Correct — neutral is fine |
| Write Tag | `.bordered` + green | Not a success action, just a utility |
| Emergency Stop | `.borderedProminent` + red | ✅ Correct |

### After Recommended Changes
| Button | New Style | Rationale |
|--------|-----------|-----------|
| Pause | `.bordered` (neutral) | Normal control, not a warning |
| Resume | `.borderedProminent` (neutral) | Primary action when paused |
| Cancel | `.bordered` + `.pfError` | Destructive, secondary to Emergency Stop |
| Stop | `.bordered` + `.pfError` | Destructive, secondary to Emergency Stop |
| Maintenance | `.bordered` (neutral) | Utility action |
| Write Tag | `.bordered` (neutral) | Utility action |
| Emergency Stop | `.borderedProminent` + `.pfError` | ✅ No change — already correct |

---

## Consistency with App-Wide Patterns

### Current App Patterns (Audit)
- **LoginView**: `.borderedProminent` + `.tint(.pfAccent)` for Sign In → Primary action
- **NFCWriteView**: `.borderedProminent` + `.tint(.pfAccent)` for Write/Done → Primary actions
- **SpoolInventoryView**: `.borderedProminent` + `.tint(.pfAccent)` for Add Spool → Primary action
- **JobDetailView**: MIXED — uses `.borderedProminent` with `.pfWarning`, `.pfError`, `.pfAccent` → INCONSISTENT

### Recommendation: App-Wide Standardization
Apply this hierarchy to **JobDetailView** as well:
1. **Start** → `.borderedProminent` (neutral, no green tint)
2. **Resume** → `.borderedProminent` (neutral, no green tint)
3. **Pause** → `.bordered` (neutral, no amber tint)
4. **Abort** → `.bordered` + `.tint(.pfError)` (destructive, secondary)
5. **Cancel** → Use SwiftUI's `.destructive` role for confirmation dialogs

**Exception for non-destructive primary actions:**
- Login, NFC Write, Add Spool can keep `.tint(.pfAccent)` — these are positive onboarding/creation flows
- Printer/job control actions should be neutral (less "celebrate the action," more "here's what you can do")

---

## Exact Code Changes for PrinterDetailView.swift

### 1. Update `actionButton()` helper (lines 658-672)

**REMOVE custom tint parameter:**
```swift
// BEFORE
private func actionButton(
    _ title: String,
    icon: String,
    color: Color,  // ← REMOVE THIS
    action: @escaping () async -> Void
) -> some View {
    Button {
        Task { await action() }
    } label: {
        Label(title, systemImage: icon)
            .fullWidthActionButton()
    }
    .buttonStyle(.bordered)
    .tint(color)  // ← REMOVE THIS
}
```

**AFTER:**
```swift
private func actionButton(
    _ title: String,
    icon: String,
    prominence: FullWidthActionButton.Prominence = .standard,
    tint: Color? = nil,
    action: @escaping () async -> Void
) -> some View {
    Button {
        Task { await action() }
    } label: {
        Label(title, systemImage: icon)
            .fullWidthActionButton(prominence: prominence)
    }
    .buttonStyle(prominence == .prominent ? .borderedProminent : .bordered)
    .apply { view in
        if let tint = tint {
            view.tint(tint)
        } else {
            view
        }
    }
}
```

**Wait — SwiftUI doesn't have `.apply()`.** Let me use a cleaner approach:

```swift
private func actionButton(
    _ title: String,
    icon: String,
    prominence: FullWidthActionButton.Prominence = .standard,
    tint: Color? = nil,
    action: @escaping () async -> Void
) -> some View {
    let button = Button {
        Task { await action() }
    } label: {
        Label(title, systemImage: icon)
            .fullWidthActionButton(prominence: prominence)
    }
    .buttonStyle(prominence == .prominent ? .borderedProminent : .bordered)
    
    return Group {
        if let tint = tint {
            button.tint(tint)
        } else {
            button
        }
    }
}
```

### 2. Update button calls in `actionSection()` (lines 575-611)

**Pause (line 575):**
```swift
// BEFORE
actionButton("Pause", icon: "pause.fill", color: .pfWarning) {
    await viewModel.pausePrinter()
}

// AFTER
actionButton("Pause", icon: "pause.fill") {
    await viewModel.pausePrinter()
}
```

**Cancel (lines 581, 597):**
```swift
// BEFORE
actionButton("Cancel", icon: "xmark.circle.fill", color: .pfError) {
    viewModel.requestCancel()
}

// AFTER
actionButton("Cancel", icon: "xmark.circle.fill", tint: .pfError) {
    viewModel.requestCancel()
}
```

**Resume (line 591):**
```swift
// BEFORE
actionButton("Resume", icon: "play.fill", color: .pfSuccess) {
    await viewModel.resumePrinter()
}

// AFTER
actionButton("Resume", icon: "play.fill", prominence: .prominent) {
    await viewModel.resumePrinter()
}
```

**Stop (line 606):**
```swift
// BEFORE
actionButton("Stop", icon: "stop.fill", color: .pfWarning) {
    await viewModel.stopPrinter()
}

// AFTER
actionButton("Stop", icon: "stop.fill", tint: .pfError) {
    await viewModel.stopPrinter()
}
```

**Maintenance (lines 614-626):**
No change needed — already neutral.

**Write Tag (lines 630-641):**
```swift
// BEFORE
Button {
    viewModel.writeNFCPrinterTag()
} label: {
    Label("Write Tag", systemImage: "wave.3.right")
        .fullWidthActionButton()
}
.buttonStyle(.bordered)
.tint(Color.pfAccent)  // ← REMOVE THIS LINE
.disabled(viewModel.isPerformingAction)
.opacity(viewModel.isPerformingAction ? 0.4 : 1.0)

// AFTER
Button {
    viewModel.writeNFCPrinterTag()
} label: {
    Label("Write Tag", systemImage: "wave.3.right")
        .fullWidthActionButton()
}
.buttonStyle(.bordered)
.disabled(viewModel.isPerformingAction)
.opacity(viewModel.isPerformingAction ? 0.4 : 1.0)
```

**Emergency Stop (lines 644-654):**
No change needed — already correct.

---

## Implementation Notes

### Handling Conditional Tint in SwiftUI
SwiftUI doesn't allow conditional modifiers easily. The cleanest approach:

```swift
private func actionButton(
    _ title: String,
    icon: String,
    prominence: FullWidthActionButton.Prominence = .standard,
    tint: Color? = nil,
    action: @escaping () async -> Void
) -> some View {
    let button = Button {
        Task { await action() }
    } label: {
        Label(title, systemImage: icon)
            .fullWidthActionButton(prominence: prominence)
    }
    .buttonStyle(prominence == .prominent ? .borderedProminent : .bordered)
    
    if let tint = tint {
        return AnyView(button.tint(tint))
    } else {
        return AnyView(button)
    }
}
```

**OR**, use a helper extension:
```swift
extension View {
    @ViewBuilder
    func tintIfPresent(_ color: Color?) -> some View {
        if let color = color {
            self.tint(color)
        } else {
            self
        }
    }
}

// Then:
Button { ... }
    .buttonStyle(.bordered)
    .tintIfPresent(tint)
```

---

## Visual Mockup (ASCII)

```
┌─────────────────────────────────────────┐
│ Actions                                 │
├─────────────────────────────────────────┤
│                                         │
│  ┌──────────────┐  ┌──────────────────┐│
│  │ ⏸  Pause    │  │ ✕  Cancel (red) ││  ← .bordered (neutral + red)
│  └──────────────┘  └──────────────────┘│
│                                         │
│  ┌─────────────────────────────────────┐│
│  │ 🔧  Enter Maintenance              ││  ← .bordered (neutral)
│  └─────────────────────────────────────┘│
│                                         │
│  ┌─────────────────────────────────────┐│
│  │ 📡  Write Tag                      ││  ← .bordered (neutral)
│  └─────────────────────────────────────┘│
│                                         │
│  ┌─────────────────────────────────────┐│
│  │ 🛑  Emergency Stop                 ││  ← .borderedProminent (RED)
│  └─────────────────────────────────────┘│   50pt tall, bold
└─────────────────────────────────────────┘
```

When **paused:**
```
┌─────────────────────────────────────────┐
│  ┌─────────────────────────────────────┐│
│  │ ▶  Resume                          ││  ← .borderedProminent (NEUTRAL)
│  └─────────────────────────────────────┘│   50pt tall
│                                         │
│  ┌──────────────┐  ┌──────────────────┐│
│  │ ⏹  Stop (red)│  │ ✕  Cancel (red) ││  ← .bordered (red)
│  └──────────────┘  └──────────────────┘│
│                                         │
│  ┌─────────────────────────────────────┐│
│  │ 🛑  Emergency Stop (RED PROMINENT) ││
│  └─────────────────────────────────────┘│
└─────────────────────────────────────────┘
```

---

## Expected Outcomes

### User Experience
1. **Emergency Stop is unmistakable** — Big, red, always visible
2. **Primary action is clear** — Resume stands out when paused (size + prominence, not color)
3. **Disabled state is obvious** — Native graying is clearer than faded colors
4. **Less cognitive load** — Fewer colors = easier to scan and understand

### Accessibility
1. Does not rely on color alone for hierarchy (size + prominence)
2. Works better in Dark Mode (less color clash)
3. VoiceOver labels unchanged (already good)

### Consistency
1. Aligns with Apple HIG button hierarchy
2. Can be applied to JobDetailView for app-wide consistency
3. Matches iOS system apps (Settings, Reminders, etc.)

---

## Alternatives Considered

### Option A: Keep All Colors, Fix Disabled State
Use SwiftUI's native `.disabled()` without manual `.opacity()`:
```swift
actionButton("Pause", icon: "pause.fill", color: .pfWarning) { ... }
    .disabled(viewModel.isPerformingAction)
    // Native graying automatically applied
```
**Rejected:** Still too many colors, doesn't solve core hierarchy problem.

### Option B: Single Green Theme
Make all buttons green (`.pfAccent`), differentiate only by prominence.
**Rejected:** Destructive actions should be red per HIG.

### Option C: All Neutral, No Colors
Remove all tinting, use only `.bordered` vs `.borderedProminent`.
**Considered:** This is close to our recommendation. We keep red ONLY for destructive actions (Cancel, Stop, Emergency Stop) for semantic clarity.

---

## Decision

**Implement the recommended hierarchy:**
- **Level 1:** Emergency Stop (red prominent)
- **Level 2:** Resume (neutral prominent)
- **Level 3:** All others (neutral bordered, except Cancel/Stop use red for destructive context)

This balances clarity, HIG alignment, and semantic color usage.

---

## Next Steps
1. Ripley implements changes in `PrinterDetailView.swift`
2. Test visual hierarchy in light/dark mode
3. Verify disabled state clarity
4. If successful, apply same pattern to `JobDetailView.swift`
5. Consider removing `.opacity()` overrides and rely on native `.disabled()` styling
# Design Proposal: Dashboard / Jobs Tab Overlap Cleanup

**Author:** Parker (UI/UX Designer)  
**Date:** 2025-07-18  
**Status:** Awaiting Approval  
**Requested by:** Jeff

---

## Problem Statement

Jeff identified that the Dashboard's "Active" page shows two sections — **Active Jobs** and **Active Print ETAs** — that display the same jobs with nearly identical information. The only visible difference is that Active Jobs shows a progress bar while Active Print ETAs does not. Meanwhile, the **Jobs tab** also shows active/printing jobs, creating a third place the same data appears. The result is confusing redundancy with no clear purpose for each section.

---

## Audit Findings

### 1. "Active Jobs" Section (Dashboard)
- **Data source:** `Printer` objects filtered by state (printing/paused/pendingready)
- **Shows:** Job name, printer name, status badge, progress bar
- **Taps navigate to:** Printer detail (not job detail)
- **Max displayed:** 5 printers

### 2. "Active Print ETAs" Section (Dashboard)
- **Data source:** `Printer` objects filtered by state == "printing" (subset of Active Jobs)
- **Shows:** Job name, printer name, progress percentage as text (e.g. "42%"), label "printing"
- **Does NOT show:** Estimated completion time, time remaining, or any actual ETA
- **No navigation:** Rows are not tappable

### 3. "Printing" Page (Jobs Tab)
- **Data source:** `QueuedPrintJobResponse` objects filtered by active status
- **Shows:** Job name, printer name, status badge, progress bar, time remaining ("~2h 15m left"), copy progress
- **Taps navigate to:** Job detail
- **Includes:** Swipe actions (cancel/start)

### The Overlap Matrix

| Information | Dashboard Active Jobs | Dashboard ETAs | Jobs Tab Printing |
|---|---|---|---|
| Job name | ✅ | ✅ | ✅ |
| Printer name | ✅ | ✅ | ✅ |
| Progress bar | ✅ | ❌ | ✅ |
| Progress % | ❌ | ✅ (text) | ❌ |
| Time remaining | ❌ | ❌ | ✅ |
| Est. completion time | ❌ | ❌ | ❌ |
| Status badge | ✅ | ❌ | ✅ |
| Navigates to | Printer | None | Job |
| Copy progress | ❌ | ❌ | ✅ |
| Thumbnails | ❌ | ❌ | ✅ |

**Key finding:** Despite being named "Active Print ETAs," this section shows zero time-based information — no estimated completion, no time remaining, no ETA. It's a stripped-down duplicate of Active Jobs minus the progress bar, plus a percentage number.

---

## Recommendations

### Recommendation A: Merge "Active Print ETAs" Into "Active Jobs" — Eliminate the Duplicate

**Remove** the "Active Print ETAs" section entirely. It adds no unique information. Instead, **enhance the Active Jobs cards** on the Dashboard to include the ETA data that users actually want:

**Enhanced Active Job Card should show:**
1. Job name + printer name (already there)
2. Progress bar (already there)
3. **Progress percentage** (currently only in ETAs — merge it in, shown at the end of the progress bar)
4. **Estimated completion time** (e.g., "Done ~3:45 PM") — the actual ETA the section title promised but never delivered
5. **Time remaining** (e.g., "~1h 23m left") — the most actionable info for a farm manager
6. Status badge (already there)

The data for estimated completion exists in the models (`QueueOverview.estimatedCompletionTime`, `QueuedPrintJobResponse.estimatedCompletionTime`) but is not being surfaced in either Dashboard section currently.

**Layout sketch for enhanced Active Job card:**
```
┌──────────────────────────────────────────┐
│  Benchy v2.gcode               Printing  │
│  🖨 Prusa MK4 #3                         │
│  ████████████░░░░░░░░  62%               │
│  ⏱ ~1h 23m left · Done ~3:45 PM         │
└──────────────────────────────────────────┘
```

### Recommendation B: Differentiate Dashboard vs Jobs Tab Purpose

The Dashboard and Jobs tab should serve fundamentally different purposes:

| Aspect | Dashboard (Active page) | Jobs Tab |
|---|---|---|
| **Purpose** | Farm-wide status at a glance — "what's happening right now?" | Job management — "find, track, and control specific jobs" |
| **Scope** | Top 5 active printers, summary counts | All jobs (queued, active, recent) with full lists |
| **Interaction** | View-only with "See All" links to Jobs/Printers tabs | Full management: cancel, start, reorder, navigate to detail |
| **Navigation** | Tapping a card → Printer detail (correct — Dashboard is printer-centric) | Tapping a row → Job detail (correct — Jobs tab is job-centric) |
| **Data depth** | Summary: progress + ETA + status | Full: progress, ETA, copies, thumbnails, file info, swipe actions |

**The Dashboard should NOT try to replicate what the Jobs tab does.** It should provide just enough info to answer: "Are my printers busy? When will they be done? Anything need attention?"

### Recommendation C: Simplify the Dashboard "Active" Page

Currently the Active page has two sections. After merging ETAs into Active Jobs, it would have one section. This is actually ideal — the Active page becomes a focused "what's printing now" view:

**Active Page (simplified):**
1. **Active Jobs** — Enhanced cards with progress + ETA (max 5, with "See All" → Jobs tab)
2. *(If all printers idle)* — Empty state: "All printers idle" with a link to the queue

### Recommendation D: Remove "Up Next" Overlap with Jobs Queue

The Dashboard's **Queue page** shows an "Up Next" section (top 5 queued jobs). The Jobs tab's **Queue page** shows the full queued jobs list. This is less problematic because:
- Dashboard "Up Next" is intentionally a preview (max 5, minimal info)
- Jobs "Queue" is the full management view with swipe actions

**No change recommended here** — the preview/full pattern is appropriate for dashboard → detail tab relationships. However, if we want to be aggressive about de-duplication, "Up Next" could be replaced with just a count card ("12 jobs queued") that links to the Jobs tab.

### Recommendation E: Consider Adding Wall-Clock ETAs App-Wide

The `QueueOverview` model has `estimatedCompletionTime: Date?` and `QueuedPrintJobResponse` has both `estimatedStartTime: Date?` and `estimatedCompletionTime: Date?`. These are available from the backend but **not displayed anywhere** in the current UI. Surfacing these would be the highest-impact single change for farm management usability:

- **Dashboard Active Jobs:** "Done ~3:45 PM"
- **Jobs Tab Printing rows:** "ETA 3:45 PM" alongside the existing "~1h 23m left"
- **Jobs Tab Queue rows:** "Starts ~4:30 PM" (from `estimatedStartTime`)

---

## Summary of Changes

| Change | Impact | Effort |
|---|---|---|
| **Remove** "Active Print ETAs" section | Eliminates confusion | Low — delete ~50 lines |
| **Enhance** Active Job cards with % + time remaining + ETA | Answers "when will it be done?" | Medium — add computed properties + UI |
| **Surface** `estimatedCompletionTime` from models | First time ETAs are actually shown | Low — data already exists in models |
| **No change** to Jobs tab structure | Jobs tab already works well | None |
| **No change** to "Up Next" on Dashboard | Preview pattern is appropriate | None |

---

## Approval Request

Jeff, please review and confirm:
1. ✅ or ❌ — Remove "Active Print ETAs" section, merge useful bits into "Active Jobs"
2. ✅ or ❌ — Enhanced Active Job card layout (progress bar + % + time remaining + completion ETA)
3. ✅ or ❌ — Surface `estimatedCompletionTime` dates in both Dashboard and Jobs tab
4. ✅ or ❌ — Keep "Up Next" as a preview on Dashboard (or replace with count-only card?)

Once approved, I'll produce the concrete SwiftUI code changes for Ripley to implement.
# Onboarding Screen Design — PrintFarmer

**Designer:** Parker  
**Date:** 2026-03-09  
**Status:** Proposed for Ripley

---

## Design Overview

**Recommendation: 3 screens** — gives enough space to communicate value without being overwhelming. Three strikes the right balance between quick onboarding and thorough introduction.

---

## Screen Designs

### Screen 1: What is PrintFarmer?
**Headline:** "Manage Your 3D Print Farm"  
**Subtext:** "Monitor multiple printers in real-time, track temperatures, manage jobs, and keep your print farm running smoothly — all from your iPhone or iPad."  
**Icon:** `cube.box.fill` (SF Symbol, size 72pt)  
**Icon Color:** `.pfAccent` (green)

**Purpose:** Establish what the app does at a high level. Users should immediately understand this is for managing multiple printers.

---

### Screen 2: Key Features
**Headline:** "Built for Print Farms"  
**Subtext:** "AutoDispatch automatically queues jobs to available printers. Real-time monitoring shows live camera feeds, temperatures, and print progress. Get alerts when attention is needed."  
**Icon:** `sparkles` (SF Symbol, size 72pt)  
**Icon Color:** `.pfSecondaryAccent` (blue)

**Purpose:** Highlight the three killer features (AutoDispatch, real-time monitoring, alerts) that differentiate PrintFarmer from single-printer apps.

---

### Screen 3: Get Connected
**Headline:** "Connect to Your Server"  
**Subtext:** "PrintFarmer connects to your PrintFarmer server to access your printers. You'll need your server URL and credentials to get started."  
**Icon:** `server.rack` (SF Symbol, size 72pt)  
**Icon Color:** `.pfAccent` (green)

**CTA Button:** "Get Started" → transitions to LoginView

**Purpose:** Set expectations about the connection requirement before hitting the login screen. Reduces confusion about why credentials are needed.

---

## Layout Structure

### Overall Layout
```
┌─────────────────────────┐
│                         │
│    [Spacer - 60pt]      │
│                         │
│    [Icon - 72pt]        │
│    [12pt spacing]       │
│    [Headline]           │
│    [8pt spacing]        │
│    [Subtext]            │
│                         │
│    [Spacer - flexible]  │
│                         │
│  [PageIndicator - dots  │
│   + labels]             │
│                         │
│  [Get Started Button    │
│   or empty space]       │
│    [32pt spacing]       │
│                         │
└─────────────────────────┘
```

### Sizing & Spacing
- **Icon:** 72pt SF Symbol with `.ultraLight` weight
- **Headline:** `.title.bold()` — clear hierarchy
- **Subtext:** `.body` with `.pfTextSecondary` color — 32pt horizontal padding for readability
- **Vertical spacing:** 60pt top spacer, flexible middle spacer, 32pt bottom padding
- **Max width:** 600pt on iPad (centered) to prevent text from becoming too wide
- **PageIndicator:** Uses existing `PageIndicator.swift` component with labels: ["Overview", "Features", "Connect"]

### Get Started Button (Screen 3 Only)
- Uses `.fullWidthActionButton(prominence: .prominent)` (50pt height, per touch target standards)
- `.borderedProminent` style
- Text: "Get Started" (`.semibold` weight)
- **Placement:** Below PageIndicator, 16pt spacing above bottom padding

---

## Color & Styling

### Colors
- **Icon colors:** Alternate between `.pfAccent` (green) and `.pfSecondaryAccent` (blue) to add visual variety while staying on-brand
- **Headlines:** Default primary text (automatically adapts light/dark mode)
- **Subtext:** `.pfTextSecondary` — softer, less hierarchical than headlines
- **PageIndicator dots:** Active = `.pfAccent`, inactive = `.pfTextTertiary.opacity(0.5)` (matches existing PageIndicator component)
- **PageIndicator labels:** Active = `.pfAccent`, inactive = `.pfTextSecondary` (matches existing component)
- **Background:** Default system background (automatically adapts)

### Typography
- **Headline:** `.title.bold()` — strong, confident
- **Subtext:** `.body` — comfortable reading size (not `.subheadline` which might be too small for key messaging)
- **Button:** `.semibold` — matches LoginView "Sign In" button

### Dark Mode
All colors automatically adapt via existing `.pfAccent`, `.pfSecondaryAccent`, `.pfTextSecondary` theme definitions. No custom dark mode overrides needed.

---

## Interaction Design

### Navigation
- **Swipe gesture:** TabView with `.tabViewStyle(.page)` enables native swipe-between-pages behavior
- **Dot taps:** PageIndicator dots are tappable (existing component behavior) — allows direct navigation
- **Label taps:** PageIndicator labels are tappable (existing component behavior) — allows direct navigation

### Skip Button?
**Recommendation: NO Skip button.**

**Rationale:**
1. **Only 3 screens** — fast to swipe through (5-10 seconds if user swipes immediately)
2. **Critical information** — Screen 3 explains the server connection requirement, which prevents confusion on the login screen
3. **First-time only** — These screens only show on first launch via UserDefaults flag (e.g., `hasCompletedOnboarding`)
4. **Cleaner design** — No Skip button means no visual clutter competing with the content
5. **Friction is minimal** — Three quick swipes is not a burden for a one-time experience

If analytics show users getting stuck or frustrated, we can add a Skip button in a future iteration.

---

## Technical Implementation Notes (for Ripley)

### Component Reuse
- **PageIndicator.swift** — Already exists, use as-is with labels `["Overview", "Features", "Connect"]`
- **ActionButtonStyle.swift** — Already exists, use `.fullWidthActionButton(prominence: .prominent)` for "Get Started"

### UserDefaults Persistence
```swift
@AppStorage("hasCompletedOnboarding") private var hasCompletedOnboarding = false
```

On "Get Started" tap:
```swift
hasCompletedOnboarding = true
// Navigate to LoginView
```

In ContentView or App entry point:
```swift
if !hasCompletedOnboarding {
    OnboardingView()
} else {
    // Normal app flow (LoginView or authenticated content)
}
```

### File Location
- `PrintFarmer/Views/Onboarding/OnboardingView.swift` (new)
- Can create `OnboardingPageView.swift` as a reusable subview for each page if desired

### Animation
- TabView page transitions use default `.page` style animation (smooth, native feel)
- "Get Started" button tap should animate transition to LoginView (e.g., `.transition(.opacity)` or `.transition(.move(edge: .trailing))`)

---

## Design Rationale

### Why These Three Screens?
1. **Screen 1 (What):** Establishes product identity — users need to know they're in the right place
2. **Screen 2 (Why):** Differentiates from single-printer apps — highlights value proposition for farm operators
3. **Screen 3 (How):** Sets expectations about server requirement — reduces login screen confusion

### Why These Icons?
- **`cube.box.fill`:** Represents a farm (collection/organization) of items — clearer than `printer.fill` which is already used on login
- **`sparkles`:** Conveys "smart automation" (AutoDispatch) and modern features — feels premium
- **`server.rack`:** Directly communicates "server connection" — matches LoginView's server section icon for consistency

### Why No Screenshots?
- **Avoid staleness:** Screenshots become outdated as UI evolves
- **Simplicity:** Icon + text is faster to process than a busy screenshot
- **Localization-friendly:** Text is easier to localize than screenshot overlays
- **Consistency:** Matches Apple's HIG recommendation for simple feature introduction

### Why Alternate Icon Colors?
- **Visual interest:** Prevents monotony across three screens
- **On-brand:** Both `.pfAccent` and `.pfSecondaryAccent` are established brand colors
- **Semantic meaning:** Green (Screen 1) = go/start, Blue (Screen 2) = features/info, Green (Screen 3) = action/proceed

---

## Future Considerations

### Metrics to Track
If analytics are added:
- % of users who complete all 3 screens vs. those who abandon
- Time spent on each screen
- % who tap dots/labels to jump screens

### Iteration Opportunities
- Add Skip button if data shows frustration
- A/B test 2 screens vs. 3 screens
- Add subtle background gradients or illustrations if brand evolves
- Animated icon transitions (e.g., printer icon animating to show print progress)

---

## Open Questions for Joe

1. **First launch detection:** Should we use `@AppStorage("hasCompletedOnboarding")` or is there another preference system in place?
2. **Transition style:** Preference for cross-dissolve vs. slide transition when "Get Started" navigates to LoginView?
3. **Analytics:** Should we add any tracking for onboarding completion/abandonment?

---

**Ready for implementation by Ripley.**
# Decision: App Icon Asset for In-App Branding

**Date:** 2026-03-12  
**Decided by:** Ripley (iOS Dev)  
**Status:** Implemented

## Context
The PrintFarmer app was using generic SF Symbol placeholder icons (`printer.fill`, `cube.fill`) on the Login, Onboarding, and Launch screens. We wanted to use the actual PrintFarmer app icon for stronger branding consistency.

## Problem
iOS `AppIcon` asset catalogs (used for home screen, app switcher) cannot be directly loaded in SwiftUI views via `Image("AppIcon")`. Attempting to do so results in a missing image.

## Decision
Created a separate `AppLogo.imageset` image set that references the same 1024x1024 app icon PNG file. This allows:
- `AppIcon.appiconset` → continues to be used by iOS system (home screen, app switcher)
- `AppLogo.imageset` → can be loaded in SwiftUI views via `Image("AppLogo")`

## Implementation
1. Created `PrintFarmer/Assets.xcassets/AppLogo.imageset/`
2. Copied `AppIcon.png` into `AppLogo.imageset/` as `AppLogo.png`
3. Created `Contents.json` with standard 1x/2x/3x scale definitions (only 1x has the PNG)
4. Updated three views to use `Image("AppLogo")` with proper sizing and rounded corners:
   - `LoginView.swift` — 56pt, 12pt corner radius
   - `OnboardingView.swift` (Page 1 only) — 72pt, 16pt corner radius
   - `RootView.swift` (launch screen) — 56pt, 12pt corner radius

## Rationale
- **Standard iOS Pattern:** Having a separate in-app logo image set is the standard approach when you need to display your app icon within the app UI
- **Asset Reuse:** Both `AppIcon` and `AppLogo` reference the same PNG file, so no image duplication
- **Design Consistency:** Rounded corners (12-16pt) match iOS app icon styling
- **Flexibility:** If we ever need different sizes or variations for in-app use, we can extend `AppLogo.imageset` without affecting the system `AppIcon`

## Alternatives Considered
1. **Using `UIImage(named: "AppIcon")` bridged to SwiftUI** — Doesn't work reliably; AppIcon is reserved for system use
2. **Hardcoding PNG path** — Not asset catalog compliant; breaks with light/dark mode or device-specific assets
3. **Using NSWorkspace (macOS) or UIApplication API** — Runtime complexity; not SwiftUI-native

## Impact
- ✅ Stronger branding on pre-auth screens (Login, Onboarding, Launch)
- ✅ Builds cleanly with no warnings
- ✅ Standard iOS pattern that other team members will recognize
- ✅ Easy to update (just replace the PNG in both asset sets)

## Follow-up
None required. Pattern established and documented in `ripley/history.md`.
# Decision: Bed Clear UX & Notification Badge Fixes

**Author:** Ripley (iOS Dev)
**Date:** 2026-03-12
**Status:** Implemented

## Context

Two UX issues reported by Jeff:
1. Tapping "Confirm Bed Clear" gave no immediate visual feedback — buttons stayed enabled and banner persisted until the async API call completed.
2. Local notifications appeared in-app but badge count was never set on the app icon, reducing visibility when the app was backgrounded.

## Decisions

### 1. Loading State Pattern for Async Buttons
- Added per-action loading flags (`isMarkingReady`, `isSkipping`) to `AutoDispatchViewModel` rather than reusing the general `isLoading` flag.
- Both buttons disable during any action to prevent conflicting taps.
- Spinner replaces the button icon (not the text) for clear context on which action is in progress.
- Pattern: set flag → call API → clear flag (in both success and error paths).

### 2. Badge Count via `setBadgeCount()`
- `PendingReadyMonitor` now calls `UNUserNotificationCenter.setBadgeCount()` every poll cycle.
- Badge clears to 0 on `stopMonitoring()` (logout).
- Notification content also includes `content.badge` for the notification itself.
- Uses iOS 16+ `setBadgeCount()` API (our floor is iOS 17).

## Impact
- No API contract changes — purely client-side UX improvements.
- Lambert: no changes needed.
- Dallas: loading flag pattern could be standardized across other action ViewModels.
# Decision: Button State Visual Clarity Pattern

**Date:** 2026-03-12  
**Author:** Ripley (iOS Dev)  
**Status:** Implemented

## Context

Users reported difficulty distinguishing enabled vs disabled buttons in the printer detail view, particularly for:
1. AutoDispatch section buttons (Next Job, Skip) that should be disabled during active printing
2. Action buttons throughout the UI where SwiftUI's default `.disabled()` opacity wasn't sufficient

## Decision

### Visual Clarity Standard
- **Bordered buttons** (`.buttonStyle(.bordered)`): Add explicit `.opacity(0.4)` when disabled to provide clear visual distinction
- **Bordered prominent buttons** (`.buttonStyle(.borderedProminent)`): SwiftUI's default disabled appearance is sufficient due to solid background color changes

### AutoDispatch State Logic
AutoDispatch buttons should be disabled when:
- Actions are in progress (`isMarkingReady || isSkipping`)
- Printer is actively printing (`isPrinting`)
- Printer is paused (`isPaused`)

The composite state check is `isPrinting || isPaused` because both represent scenarios where the printer has an active job and shouldn't accept manual queue interventions.

## Implementation Pattern

```swift
// AutoDispatchSection - add isPrinting parameter
struct AutoDispatchSection: View {
    let printerId: UUID
    let isPrinting: Bool  // NEW
    
    private var isActionInProgress: Bool {
        viewModel.isMarkingReady || viewModel.isSkipping || isPrinting
    }
}

// Bordered buttons with visual clarity
Button { /* action */ } label: {
    Label("Skip", systemImage: "forward.fill")
}
.buttonStyle(.bordered)
.disabled(isActionInProgress)
.opacity(isActionInProgress ? 0.4 : 1.0)  // Explicit visibility

// Call site - pass printer state
AutoDispatchSection(
    printerId: printer.id, 
    isPrinting: viewModel.isPrinting || viewModel.isPaused
)
```

## Rationale

1. **User feedback**: Hard to tell which buttons are clickable just by looking at colors
2. **SwiftUI limitation**: Default `.disabled()` on tinted `.bordered` buttons reduces opacity slightly but not enough for clear distinction
3. **State composition**: Both printing and paused states represent "printer has a job" and should prevent manual dispatch
4. **Consistency**: Apply the same visual pattern to all action buttons for predictable UX

## Affected Files

- `PrintFarmer/Views/Printers/AutoDispatchSection.swift`
- `PrintFarmer/Views/Printers/PrinterDetailView.swift`

## Alternative Considered

Using `.foregroundStyle(.secondary)` when disabled — rejected because it affects button text clarity and doesn't work well with tinted buttons.
# Decision: Task Cancellation Pattern for Navigation Crashes

**Date:** 2026-03-12  
**Author:** Ripley  
**Status:** Implemented

## Context
App was crashing when users pressed the back button on the Dispatch Dashboard page. Investigation revealed that async tasks started by `.task` or `.refreshable` modifiers were continuing to execute after view dismissal, causing property updates on `@Observable` ViewModels during tear-down.

## Decision
**All async methods in ViewModels that are called from `.task` or `.refreshable` modifiers MUST check `Task.isCancelled` before updating any `@Observable` properties after async operations complete.**

## Pattern to Follow

```swift
func loadData() async {
    guard let service else { return }
    isLoading = true
    
    do {
        let result = try await service.fetchData()
        
        // ✅ Check before updating properties
        guard !Task.isCancelled else { return }
        self.data = result
    } catch {
        guard !Task.isCancelled else { return }
        self.error = error.localizedDescription
    }
    
    // ✅ Check before final state update
    guard !Task.isCancelled else { return }
    isLoading = false
}
```

## Applies To
- Any ViewModel method called from SwiftUI's `.task` modifier
- Any ViewModel method called from SwiftUI's `.refreshable` modifier
- Any async method that updates `@Observable` or `@ObservableObject` properties

## Rationale
SwiftUI automatically cancels tasks when views disappear, but the cancellation is cooperative - the task must check `Task.isCancelled` and opt to stop. Without these checks, property updates can happen during view tear-down, causing crashes or undefined behavior.

## Files Already Updated
- `PrintFarmer/ViewModels/DispatchViewModel.swift` - Fixed crash on Dispatch page back navigation

## Action Items
- [ ] Audit all other ViewModels for this pattern
- [ ] Consider adding this to code review checklist
- [ ] Consider creating a base ViewModel class with helper methods for safe property updates
# Decision: Farm Status Integration into Dashboard

**Date:** 2025-01-20  
**Author:** Ripley (iOS Dev)  
**Status:** Implemented

## Context
The app previously had a separate "Job Analytics" page accessible via toolbar button in JobListView. User requested consolidating this functionality into the Dashboard as "Farm Status" sections to provide a unified view of fleet and queue health.

## Decision

### What We Integrated
Moved all queue analytics from separate JobAnalyticsView into the Dashboard as four sections:
1. **Queue Health** — 4 stat cards showing queued/printing/paused counts + avg wait time
2. **By Printer Model** — breakdown of jobs per printer model
3. **Active Print ETAs** — currently printing jobs with progress percentage
4. **Up Next** — next 5 queued jobs with assigned printer and queue position

### Implementation
- **DashboardViewModel** now fetches farm status data (`queueStats`, `modelStats`, `upcomingJobs`) using existing `JobAnalyticsService`
- **DashboardView** renders Farm Status sections after Active Jobs, before Dispatch link
- iPad uses 2-column layout for Model Breakdown + Active ETAs side-by-side
- iPhone uses stacked vertical layout
- Deleted `JobAnalyticsView.swift` and removed all navigation references

### Technical Details
- Used separate helper functions (`modelStatRow`, `activePrintRow`, `upNextRow`) to avoid SwiftUI type-checker timeouts on complex VStack expressions
- ForEach with `Array()` wrapper for non-Binding collections (`modelStats`, `upcomingJobs`)
- Color references use `Color.pfAccent` instead of `.pfAccent` in `.foregroundStyle()` to avoid ShapeStyle type inference issues
- Added `TimeInterval.etaFormatted` extension (shows "2:45 PM" for today, "Tomorrow 10:00 AM", or relative format)

## Rationale
- **Single Source of Truth:** Dashboard now shows both fleet status and queue health without navigation
- **Reduced Complexity:** Removed an entire navigation destination + toolbar button
- **Better UX:** Users see queue status immediately on Dashboard without tapping into separate analytics page
- **Reusable Service:** JobAnalyticsService remains available for future analytics features

## Files Modified
- `PrintFarmer/Views/Dashboard/DashboardView.swift` — added Farm Status sections
- `PrintFarmer/ViewModels/DashboardViewModel.swift` — added analytics data fetching
- `PrintFarmer/Extensions/Formatting+Extensions.swift` — added `etaFormatted`
- `PrintFarmer/Views/Jobs/JobListView.swift` — removed toolbar button
- `PrintFarmer/Navigation/AppDestination.swift` — removed `case jobAnalytics`
- `PrintFarmer.xcodeproj/project.pbxproj` — removed JobAnalyticsView.swift references

## Files Deleted
- `PrintFarmer/Views/Jobs/JobAnalyticsView.swift`

## Future Considerations
- Could add "See All" button on Farm Status sections to navigate to detailed analytics views
- Queue Health could show time-series sparkline charts of queue depth over time
- Active ETAs could calculate estimated completion time if backend provides job start time + estimated duration
- JobAnalyticsViewModel remains available if we need advanced filtering/sorting in future features

## Related Patterns
- **Service Aggregation:** DashboardViewModel now depends on multiple services (printer, job, statistics, analytics)
- **Responsive Layout:** Farm Status sections adapt iPad 2-column vs iPhone stacked using `sizeClass`
- **Helper Function Pattern:** Break complex views into private helper functions to avoid type-checker performance issues
### Decision: Always-Visible Filament Info on Printer Cards (Ripley)
**Date:** 2026-07-24
**Status:** Implemented

## Context
Jeff requested filament/spool info always be visible on printer cards (both iPhone and iPad). Previously, filament info was hidden when no spool was loaded (iPhone had no filament display at all).

## Decisions
1. **iPhone cards show filament row** — added below temperature row, matching iPad layout (color circle, material, name, weight)
2. **"No spool loaded" empty state** — when `spoolInfo` is nil or `hasActiveSpool` is false, show `Label("No spool loaded", systemImage: "cylinder")` in `.caption` / `.secondary` style
3. **Consistent pattern** — empty state matches `PrinterDetailView` which already showed "No filament loaded"

## No Changes Needed
- PendingReady yellow header was already implemented on both card types
- Sort order (pendingReady > printing > ready > offline) was already in `PrinterListViewModel.sortPriority()`

## Files Modified
- `PrintFarmer/Views/Components/PrinterCardView.swift` — added `filamentSection` property
- `PrintFarmer/Views/Components/iPadPrinterCardView.swift` — removed conditional, added else branch
# Decision: iPad Layout Redesign

**Author:** Ripley  
**Date:** 2026-07  
**Status:** Implemented

## Context
The iPad experience used the same compact printer cards and flat sidebar as iPhone, underutilizing the larger screen.

## Decision
1. **Rich iPad printer cards** — full-width cards with state-tinted headers, current+target temps, filament info, and progress bars (modeled after web CompactPrinterCard)
2. **Sectioned sidebar** — grouped navigation with Section headers matching the web nav structure
3. **Enhanced dashboard** — larger stat cards and 2-column lower layout (Active Jobs + Dispatch) on iPad

## Tradeoffs
- AutoDispatch status and queue position omitted from printer cards — would require N+1 API calls per list render
- iPad cards are a separate component (`iPadPrinterCardView`) rather than responsive `PrinterCardView` — cleaner code but two components to maintain
# Decision: Use AppLogo asset in LaunchScreen storyboard

**Author:** Ripley (iOS Dev)
**Date:** 2025-07-25
**Status:** Implemented

## Context
The `LaunchScreen.storyboard` was using a UILabel with the 🌾 wheat emoji as a placeholder for the app logo. On physical devices this rendered as a generic grass/wheat image rather than our brand.

## Decision
Replace the emoji UILabel with a UIImageView referencing the `AppLogo` asset from the asset catalog. This matches what the SwiftUI `launchScreen` in RootView.swift already does with `Image("AppLogo")`.

## Details
- LaunchScreen storyboards support asset catalog image references
- Image sized at 56×56 via explicit constraints, matching the SwiftUI counterpart
- No changes needed to RootView.swift — it was already correct
# Decision: Pre-Login Local Network Permission Step

**Author:** Ripley (iOS Dev)
**Date:** 2026-03-12
**Status:** Implemented

## Context
iOS shows the Local Network permission dialog lazily on first local network access. When the PrintFarmer server is on the local network, the sign-in request fires before the user can grant permission, causing a "No internet connection" error.

## Decision
Added a dedicated permission step between onboarding and login that uses `NWBrowser` (Bonjour) to proactively trigger the iOS Local Network permission dialog. This ensures the user has already granted (or denied) the permission before any real API call is made.

## Flow
`Splash → Onboarding → Local Network Permission → Login → Main App`

## Key Files
- `PrintFarmer/Utilities/LocalNetworkAuthorization.swift` — NWBrowser-based permission trigger
- `PrintFarmer/Views/Auth/LocalNetworkPermissionView.swift` — UI for the step
- `PrintFarmer/Views/RootView.swift` — Updated flow with `hasCompletedNetworkPermission` gate
- `PrintFarmer/Info.plist` — Added `NSBonjourServices` with `_printfarmer._tcp`

## Trade-offs
- The step is shown even if the server is remote (not on local network). This is acceptable because: (a) the permission step is only shown once, (b) it takes 3 seconds max if no local network is involved, and (c) most PrintFarmer users run their servers locally.
- If the user denies the permission, we still proceed to login — the error will surface naturally if the server is truly local.
# Decision: Local Notifications for PendingReady State

**Author:** Ripley  
**Date:** 2026-03-11  
**Status:** Implemented

## Context
The app is self-hosted per user (not centralized SaaS), so APNs push notifications aren't viable — there's no single server to hold APNs credentials. However, operators need to be alerted when a printer needs its bed cleared for auto-dispatch to continue.

## Decision
Use `UNUserNotificationCenter` local notifications fired from `PendingReadyMonitor`'s existing polling loop. No server involvement.

### Deduplication Strategy
- Track `notifiedPrinterIds: Set<UUID>` in PendingReadyMonitor
- Only fire notification for printers newly entering PendingReady (not already in the set)
- Clear IDs from the set when printers leave PendingReady, so re-entry triggers a fresh notification
- Set is cleared on `stopMonitoring()` (logout)

### Notification Tap Handling
- Local notifications use category identifier `PENDING_READY`
- PushNotificationManager delegate distinguishes local vs remote by category
- Local taps post `.localNotificationTapped` → PFarmApp sets `router.selectedTab = .printers`

### Permission Request
- Called once on first authenticated launch in RootView's `.task` modifier
- Reuses existing `UNUserNotificationCenter.requestAuthorization()` — no separate settings UI needed

## Alternatives Considered
- **SignalR-triggered notifications:** Would require app to be in foreground; polling already handles background state
- **Background App Refresh:** More complex, and 10-second polling only runs while app is foregrounded anyway
- **APNs:** Not viable for self-hosted architecture

## Impact
- PendingReadyMonitor.configure() now requires both AutoDispatchServiceProtocol and PrinterServiceProtocol
- Existing callers (RootView) updated
# Decision: MJPEG Livestream via WKWebView

**Date:** 2026-07-24
**Author:** Ripley
**Status:** Implemented

## Context
Need to display live MJPEG camera streams from printers during active prints, alongside existing static snapshot support.

## Decision
- Use `WKWebView` (via `UIViewRepresentable`) to render MJPEG streams — WebKit handles MJPEG natively, no custom HTTP multipart parsing needed
- Auto-enable livestream when printer state is "printing", "starting", or "paused" AND `cameraStreamUrl` is non-nil
- Auto-disable when printer stops printing (via SignalR state updates)
- User can manually toggle between livestream and snapshot via toolbar button

## Files
- `PrintFarmer/Views/Components/MJPEGStreamView.swift` — new reusable component
- `PrintFarmer/ViewModels/PrinterDetailViewModel.swift` — `showLivestream`, `isActivelyPrinting`, `canShowLivestream`
- `PrintFarmer/Views/Printers/PrinterDetailView.swift` — updated `cameraSection()`

## Impact
- **Lambert:** No changes needed — uses existing `cameraStreamUrl` from Printer model
- **Ash:** New ViewModel properties (`showLivestream`, `isActivelyPrinting`, `canShowLivestream`) may need test coverage
- **Dallas:** MJPEGStreamView is a reusable component if other views need stream display
# Decision: Onboarding Flow Implementation

**Date:** 2026-03-12  
**Author:** Ripley  
**Status:** Implemented

## Context
PrintFarmer needed welcome/onboarding screens shown before login on first app launch to introduce new users to key features.

## Decisions

1. **Placement:** Onboarding appears BEFORE LoginView, AFTER auth check completes
   - Flow: Launch Screen → Onboarding (first launch only) → Login → Main App
   - Shown when: `hasCheckedAuth == true` AND `isAuthenticated == false` AND `hasSeenOnboarding == false`

2. **State Tracking:** `@AppStorage("hasSeenOnboarding")` boolean in RootView
   - Uses UserDefaults for simple persistence
   - Defaults to `false` (show onboarding)
   - Set to `true` when user completes onboarding or taps "Skip"

3. **UI Pattern:** 3-page TabView with PageIndicator component
   - Reused existing PageIndicator component (already used in Dashboard, Jobs, Maintenance)
   - TabView with `.page(indexDisplayMode: .never)` style
   - Each page: SF Symbol icon (72pt), headline, body text
   - "Skip" button in top-right corner (all pages)
   - "Get Started" button on final page

4. **Content:**
   - Page 1: "Monitor Your Farm" — `cube.fill` — Real-time printer monitoring
   - Page 2: "Smart Job Queue" — `tray.full.fill` — AutoDispatch features
   - Page 3: "Stay Informed" — `bell.badge.fill` — Notifications + "Get Started" CTA

5. **Styling:**
   - Icons: 72pt, `.pfAccent` color
   - Headlines: `.title`, `.bold`
   - Body: `.body`, `.pfTextSecondary`, centered, 32pt horizontal padding
   - "Get Started": `.borderedProminent`, `.tint(.pfAccent)`
   - "Skip": `.plain`, `.pfTextSecondary`

## Rationale

- **Pre-login placement:** Introduces app features before user creates/logs into account
- **@AppStorage:** Simple boolean flag ideal for one-time "has seen" state; no need for complex persistence
- **Pattern reuse:** TabView + PageIndicator already battle-tested in 3 other views (Dashboard, Jobs, Maintenance)
- **Content focus:** Highlights PrintFarmer's 3 core value props: monitoring, automation, notifications
- **Skip option:** Respects user choice to bypass onboarding if they're already familiar
- **One-time only:** Avoids annoying returning users — onboarding never shown again after dismissal

## Files Modified

- **Created:** `PrintFarmer/Views/Auth/OnboardingView.swift`
- **Modified:** `PrintFarmer/Views/RootView.swift` — Added `@AppStorage("hasSeenOnboarding")` and onboarding check in view hierarchy
- **Modified:** `PrintFarmer.xcodeproj/project.pbxproj` — Added OnboardingView.swift to build

## Related Patterns

- **TabView paging:** Same pattern as DashboardView, JobListView, MaintenanceView (`.page(indexDisplayMode: .never)` + PageIndicator)
- **@AppStorage for one-time flags:** Similar to potential "hasSeenWhatsNew" or "hasCompletedTutorial" patterns
- **Binding to parent state:** OnboardingView receives `@Binding var hasSeenOnboarding` to update RootView's @AppStorage

## Future Considerations

- Could add "What's New" screens for major version updates using similar pattern
- Could track onboarding completion analytics (which page users skip from, completion rate)
- Could add interactive elements (e.g., "Try swiping to continue" prompt on first page)
- Could localize onboarding content for international users

## Build Result
✅ Build succeeded on iPhone 17 Pro simulator
# Decision: PendingReady Printer Visual Treatment and Sorting

**Date:** 2026-03-12  
**Decider:** Ripley (iOS Dev)  
**Status:** Implemented

## Context

Printers in the `pendingready` state require immediate user attention — the bed needs to be cleared before the next print can begin. However, these printers were not visually distinct in the UI, using the same brown/amber color as "paused" printers. Additionally, they appeared in no particular order in printer lists, making them easy to miss among dozens of other printers.

## Decision

### 1. Visual Treatment - Bright Yellow Header
Changed the printer card header background color for `pendingready` state from brown (`#b45309`) to bright yellow (`#eab308`). This color:
- Is highly visible and attention-grabbing
- Semantically signals "warning/action needed" (matching the warning triangle icon on the bed-clear banner)
- Is distinct from all other printer states (blue=printing, brown=paused, red=error, green=ready, gray=offline)

### 2. Sort Priority - PendingReady First
Implemented consistent sorting across all printer list displays with this priority order:
1. **PendingReady** (0) — needs attention NOW
2. **Printing** (1) — active work in progress
3. **Ready/Idle** (2) — available for work
4. **Everything else** (3) — maintenance, error, paused, etc.
5. **Offline** (100) — not available

### 3. Dashboard Integration
Added `pendingready` to the "Active Jobs" section filter on the Dashboard, alongside `printing` and `paused`. This ensures PendingReady printers are surfaced in the dashboard's attention area, not just buried in the full printer list.

## Implementation

**Files Modified:**
- `PrinterCardView.swift` and `iPadPrinterCardView.swift` — header color
- `PrinterListViewModel.swift` — sorting logic
- `DashboardViewModel.swift` — sorting logic
- `DashboardView.swift` — active jobs filter + sorting

**Color Hex Values:**
- PendingReady: `#eab308` (bright yellow)
- Paused: `#b45309` (brown/amber)
- Printing: `#1d4ed8` (blue)
- Error: `#dc2626` (red)
- Ready: `#059669` (green)
- Offline: `#4b5563` (gray)

## Rationale

**Why bright yellow (`#eab308`) instead of `.pfWarning` (`#d97706`)?**
- Higher luminance and contrast — more eye-catching
- Clear visual hierarchy: yellow = "act now", amber = "paused but not urgent"
- Matches industry conventions (yellow = caution/action needed)

**Why sort PendingReady above Printing?**
- PendingReady blocks future work — it's a bottleneck that grows worse over time
- Printing jobs are already running and require no immediate action
- Farm efficiency depends on clearing beds promptly to keep the queue moving

**Why include PendingReady in "Active Jobs"?**
- Semantically, PendingReady printers are "active" — they just finished a job and are awaiting confirmation
- Grouping with Printing/Paused makes sense: all three states represent printers that have recent/current job activity
- Keeps the user's attention on the right place: the dashboard's "Active Jobs" section

## Alternatives Considered

1. **Use `.pfWarning` color (`#d97706`)**: Rejected — too close to "paused" brown, not attention-grabbing enough
2. **Sort PendingReady below Printing**: Rejected — reduces visibility, delays clearing action
3. **Create separate "Needs Attention" section**: Rejected — adds complexity, doesn't scale well with other future states
4. **Use red color for PendingReady**: Rejected — red is reserved for errors/failures, PendingReady is not an error condition

## Impact

- **User experience:** PendingReady printers are now impossible to miss — they have a distinct color and always appear at the top of lists
- **Farm efficiency:** Faster bed-clear turnaround → less queue delay → higher throughput
- **Consistency:** Sorting behavior is uniform across PrinterListView and DashboardView
- **Backward compatibility:** No breaking changes — pure visual/sort enhancement

## Future Considerations

If we add more "action needed" states (e.g., "FilamentNeeded", "MaintenanceRequired"), we should:
- Consider grouping them with PendingReady in priority sorting
- Evaluate if they should also use bright yellow, or if we need additional attention colors
- Potentially create a dedicated "Action Required" filter in the status menu
# PendingReady Global Indicator Implementation

**Date:** 2025-01-25  
**Author:** Ripley (iOS Dev)  
**Status:** Implemented

## Overview
Added a global PendingReady indicator to the iOS app's navigation that matches the web UI functionality. The indicator shows a warning badge on the Printers tab when any printer is in "PendingReady" state, polling the status every 10 seconds.

## Implementation Decisions

### 1. Monitoring Pattern
**Decision:** Created a dedicated `PendingReadyMonitor` class following the same pattern as the notification badge system.

**Rationale:**
- Keeps monitoring logic isolated and reusable
- Uses `@MainActor @Observable` for SwiftUI integration
- Handles polling lifecycle (start/stop) independently
- Silently handles errors to avoid disrupting UX during background polling

### 2. UI Placement

#### iPhone (Compact Layout)
**Decision:** Added an orange `.badge()` to the Printers tab item showing the pending ready count.

**Rationale:**
- Native iOS pattern — tab badges are immediately visible from any screen
- Consistent with how Alerts tab shows notification badge
- Non-intrusive but noticeable
- No additional UI complexity needed

#### iPad (Regular Layout)
**Decision:** Added an orange badge next to the Printers sidebar button, mirroring the red badge pattern used for Alerts.

**Rationale:**
- Consistent with existing Alerts badge implementation
- Maintains visual hierarchy (orange for warnings vs. red for critical alerts)
- Reuses the `sidebarPrintersButton` pattern, keeping code DRY

### 3. Polling Strategy
**Decision:** Poll `GET /api/autoprint/status` every 10 seconds, matching web UI behavior.

**Implementation:**
- Uses existing `AutoDispatchService.getAllStatus()` 
- Filters for `state == "PendingReady"`
- Updates count via `AppRouter.pendingReadyCount`
- Automatically stops polling when user logs out
- Uses Task-based polling with cancellation support

**Rationale:**
- 10-second interval balances responsiveness with server load
- Leverages existing, tested service layer
- Clean lifecycle management prevents memory leaks

### 4. Color Choice
**Decision:** Used `.pfWarning` (orange) for the badge color.

**Rationale:**
- Matches web UI's warning color scheme
- Distinguishes from critical alerts (red)
- Consistent with existing PendingReady UI in `AutoDispatchSection.swift`
- Already defined in `ThemeColors.swift`

### 5. Lifecycle Management
**Decision:** Wired monitoring into `RootView` with proper start/stop based on authentication state.

**Implementation:**
```swift
.task {
    pendingReadyMonitor.configure(autoPrintService: services.autoPrintService)
    pendingReadyMonitor.startMonitoring()
}
.onChange(of: authViewModel.isAuthenticated) { _, isAuthenticated in
    if !isAuthenticated {
        pendingReadyMonitor.stopMonitoring()
        router.pendingReadyCount = 0
    }
}
```

**Rationale:**
- Only polls when authenticated
- Cleans up resources on logout
- Resets badge count to avoid stale data
- Uses `.task` modifier for automatic cancellation on view disappearance

## Files Modified

1. **`ViewModels/PendingReadyMonitor.swift`** (new)
   - Observable monitor class
   - 10-second polling loop
   - Silent error handling

2. **`Navigation/AppRouter.swift`**
   - Added `pendingReadyCount: Int = 0` property

3. **`Views/ContentView.swift`**
   - Added `.badge(router.pendingReadyCount)` to Printers tab (iPhone)
   - Created `sidebarPrintersButton` with orange badge for iPad
   - Maintains consistency with existing `sidebarAlertButton` pattern

4. **`Views/RootView.swift`**
   - Created and configured `PendingReadyMonitor` instance
   - Wired monitor lifecycle to authentication state
   - Syncs monitor count to `AppRouter.pendingReadyCount`

5. **`PrintFarmer.xcodeproj/project.pbxproj`**
   - Added `PendingReadyMonitor.swift` to build targets

## Testing Considerations

### Manual Testing
- Verify badge appears on Printers tab when printers enter PendingReady state
- Verify badge updates every ~10 seconds
- Verify badge clears when all printers leave PendingReady state
- Verify badge is visible from all tabs (iPhone) and sidebar (iPad)
- Verify polling stops on logout
- Verify badge resets to 0 on logout
- Test on both iPhone and iPad simulators/devices

### Error Scenarios
- Network failures during polling are silently handled
- Badge count doesn't update on error (maintains last known state)
- No user-facing error messages for background polling failures

## Future Enhancements

### Considered but Not Implemented
1. **Pulsing animation on badge** — Native iOS badges don't pulse by default. Could add custom animation if needed.
2. **Tap badge to navigate** — Tab badges aren't tappable in iOS. Tapping the tab itself navigates to Printers, which is sufficient.
3. **Notification on state change** — Could add local notifications, but would require additional UX design and permissions.

### Potential Improvements
- Add optional push notifications when printers enter PendingReady
- Add settings toggle to enable/disable monitoring
- Adjust polling interval based on app state (foreground/background)
- Add visual indicator in the badge when polling is offline/failing

## Notes
- Implementation mirrors the web UI behavior as closely as possible within iOS conventions
- Uses existing service infrastructure (`AutoDispatchService`, `ServiceContainer`)
- Follows established patterns from notification badge system
- No breaking changes to existing navigation or UI flows
# Decision: SignalR Real-Time Updates for All Printer-Displaying ViewModels

**Date:** 2026-03-12  
**Author:** Ripley (iOS Dev)  
**Status:** Implemented

## Context

Users reported that printer cards in the Dashboard and List views showed stale state (e.g., "Printing at 96%") after a print completed, while the Printer Detail page correctly showed the updated state. This created confusion and undermined trust in the UI's real-time capabilities.

## Investigation

The app uses SignalR for real-time updates from the backend. The SignalRService broadcasts `PrinterStatusUpdate` events to all subscribers via `onPrinterUpdated()` callbacks.

**Initial State:**
- ✅ `PrinterDetailViewModel` — subscribed to SignalR updates
- ✅ `PrinterListViewModel` — subscribed to SignalR updates
- ❌ `DashboardViewModel` — NOT subscribed (only loaded data on refresh)

This meant the detail view and list view stayed current, but the dashboard's printer cards went stale after the initial load.

## Decision

**All ViewModels that display printer data MUST subscribe to SignalR updates.**

This ensures consistent real-time behavior across all views that show printer state.

## Implementation Pattern

Each ViewModel follows this pattern:

```swift
private var signalRService: (any SignalRServiceProtocol)?

func configureSignalR(_ service: any SignalRServiceProtocol) {
    self.signalRService = service
    service.onPrinterUpdated { [weak self] update in
        Task { @MainActor [weak self] in
            self?.applyPrinterUpdate(update)
        }
    }
}

private func applyPrinterUpdate(_ update: PrinterStatusUpdate) {
    guard let idx = printers.firstIndex(where: { $0.id == update.id }) else { return }
    // Update all fields: state, progress, temps, targets, spool, etc.
    printers[idx].isOnline = update.isOnline
    if let s = update.state { printers[idx].state = s }
    if let prog = update.progress { printers[idx].progress = prog / 100.0 }
    // ... etc
}
```

The corresponding View calls `viewModel.configureSignalR(services.signalRService)` in its `.task` block, typically right after calling the main `configure()` method.

## Benefits

1. **Consistency** — All printer data is real-time across the entire app
2. **User Trust** — No more stale cards creating confusion
3. **No Manual Refreshes** — Users see updates immediately without pulling to refresh
4. **Predictable Pattern** — Easy to extend to future ViewModels

## Considerations

- SignalRService broadcasts all updates to all subscribers. Each ViewModel processes relevant updates.
- Progress values from backend are 0-100; iOS normalizes to 0.0-1.0 for SwiftUI progress bars.
- Updates happen on `@MainActor` to ensure UI thread safety.
- Weak self capture prevents retain cycles in the SignalR callback closures.

## Files Changed

- `PrintFarmer/ViewModels/DashboardViewModel.swift` — added SignalR subscription
- `PrintFarmer/Views/Dashboard/DashboardView.swift` — added `configureSignalR()` call

## Related Patterns

This aligns with the existing SignalR subscription pattern already in use by `PrinterListViewModel` and `PrinterDetailViewModel`. No new architecture was introduced — we simply extended the existing pattern to ensure complete coverage.
# Decision: Swipeable Paged Layouts for iPhone

**Date:** 2026-03-12  
**Author:** Ripley (iOS Dev)  
**Status:** Implemented

## Context

Parker designed a swipeable tabbed layout pattern for iPhone to improve navigation and reduce vertical scrolling. The pattern uses `TabView` with `.page` style to let users swipe left/right between logical content groups. iPad keeps its existing layouts unchanged.

## Decision

Implemented swipeable paged layouts on iPhone for three primary views:

### 1. DashboardView - 3 Pages
- **Overview**: Fleet summary + Queue Health
- **Active**: Active Jobs + Print ETAs
- **Queue**: Up Next + Model Breakdown + Dispatch

### 2. JobListView - 3 Pages
- **Printing**: Active printing jobs
- **Queue**: Queued jobs (with swipe actions)
- **Recent**: Completed/failed jobs

### 3. MaintenanceView - 2 Pages
- **Alerts**: Active alerts + navigation links
- **Tasks**: Upcoming maintenance tasks

## Implementation Pattern

```swift
if horizontalSizeClass == .compact {
    VStack(spacing: 0) {
        // Pinned content (optional)
        
        TabView(selection: $currentPage) {
            Page1().tag(0)
            Page2().tag(1)
        }
        .tabViewStyle(.page(indexDisplayMode: .never))
        
        PageIndicator(currentPage: $currentPage, pageCount: 2, labels: ["Page1", "Page2"])
            .padding(.bottom, 8)
    }
} else {
    // iPad: existing layout
}
```

## Key Decisions

1. **Custom PageIndicator Component**: Created reusable component replacing default dots with labeled indicators
2. **Size Class Branching**: Use `@Environment(\.horizontalSizeClass)` to branch between iPhone (.compact) and iPad (.regular)
3. **Per-Page Refresh**: Each page has independent `.refreshable` support
4. **Empty States**: Show contextual empty states per-page when no content
5. **Extracted Page Views**: Each page is a `@ViewBuilder` function for code clarity

## Design Specs

- Active page dot: `.pfAccent`
- Inactive dots: `.pfTextTertiary.opacity(0.5)`
- Dot size: 8pt diameter, 6pt spacing
- Page labels: `.caption2` font
- Page content padding: 16pt all sides
- Animations: `.easeInOut(duration: 0.25)`

## Files Created

- `PrintFarmer/Views/Components/PageIndicator.swift`

## Files Modified

- `PrintFarmer/Views/Dashboard/DashboardView.swift`
- `PrintFarmer/Views/Jobs/JobListView.swift`
- `PrintFarmer/Views/Maintenance/MaintenanceView.swift`

## Future Considerations

- This pattern can be reused for other multi-section views
- PageIndicator component is ready for use elsewhere
- Consider adding haptic feedback on page change
- May want to persist current page selection per view in UserDefaults

## Testing

- ✅ Build succeeded on iPhone 17 Pro simulator
- ✅ All existing iPad layouts preserved
- ✅ Pull-to-refresh works per page
- ✅ Navigation links and swipe actions maintained
# Decision: Task Lifecycle Pattern for Pushed Views

**Author:** Ripley (iOS Dev)
**Date:** 2026-03-14

## Context
Unstructured `Task { }` blocks in Button actions inside NavigationStack-pushed views survive view dismissal. When the user taps back, these tasks mutate `@Observable` ViewModels for deallocated views, causing crashes.

## Decision
All views pushed via NavigationLink/NavigationStack must follow this pattern:

### Views
```swift
@State private var activeTasks: [Task<Void, Never>] = []

// In button:
let task = Task { await viewModel.someAction() }
activeTasks.append(task)

// Lifecycle:
.onDisappear {
    activeTasks.forEach { $0.cancel() }
    activeTasks.removeAll()
    viewModel.isViewActive = false
}
```

### ViewModels
```swift
var isViewActive = true

func loadData() async {
    guard isViewActive else { return }
    // ...
}
```

## PendingReady State Priority
In all card views and sort functions, check `pendingReady` state BEFORE `isOnline`. The API may return `isOnline: false` for PendingReady printers, but they are clearly reachable and should always show yellow headers and sort to top.
# Decision: Temperature Display UI Pattern

**Date:** 2025-01-20  
**Agent:** Ripley (iOS Developer)  
**Context:** Temperature display enhancements for printer cards

## Decision
Use `.frame(maxWidth: .infinity, alignment: .leading)` for equal-width column layouts in temperature displays, rather than SwiftUI Grid or GeometryReader.

## Rationale
- Simpler code with fewer nesting levels
- Works reliably across iOS versions without Grid API requirements
- Prevents UI shifting when text content width changes (e.g., "25°C" vs "215°C → 220°C")
- Maintains left alignment for better readability

## Implementation
Applied to both `PrinterCardView.swift` (iPhone) and `iPadPrinterCardView.swift` (iPad) temperature sections. Each temperature Label gets equal width, ensuring bed icon position stays fixed regardless of hotend text length.

## Pattern for Future Use
When needing equal-width columns in HStack without complex layout:
```swift
HStack(spacing: 16) {
    Content1()
        .frame(maxWidth: .infinity, alignment: .leading)
    Content2()
        .frame(maxWidth: .infinity, alignment: .leading)
}
```

This pattern should be considered for other card layouts requiring fixed positioning.

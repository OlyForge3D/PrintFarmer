# Lambert — History

## Core Context (Archived)

### Project Setup & Architecture (Dallas, 2026-03-06)
- **Project:** PFarm-Ios — Native iOS client for Printfarmer backend (~/s/PFarm1)
- **User:** Jeff Papiez
- **Stack:** Swift, SwiftUI, iOS 17+, MVVM + Repository Pattern, @Observable ViewModels, Actor-based services, ServiceContainer DI, KeychainSwift token storage
- **Build:** SPM (Package.swift) + Xcode (.xcodeproj), target iOS 17+, Swift 6.0

### Backend Integration & Patterns (Verified across 2026-03-06 → 2026-03-08)
- **Authentication:** Single JWT token (no refresh) via POST /api/auth/login; stored in Keychain; validated via GET /api/auth/me; auto-logout on 401
- **API Contracts:** 40+ endpoints across 7 services; backend uses JsonStringEnumConverter (enums as strings, not ints); ISO 8601 dates with fractional seconds; TimeSpan as "HH:MM:SS" strings
- **Service patterns:** All services conform to protocols in PrintFarmer/Services/Protocols/; MockServices available for testability; ServiceContainer provides DI
- **Printer DTOs:** CompletePrinterDto (list endpoint, includes live status) vs PrinterDto (detail endpoint, includes serverUrl/apiKey); custom init(from:) handles both
- **Resilient decoding:** Custom dual-format ISO8601 decoder (fractional → plain fallback); enum String raw values with fallback; silent error suppression for secondary data loads

### Completed Service Layers (2026-03-06 → 2026-03-08)
1. **MVP (2026-03-06):** APIClient, AuthService, PrinterService, JobService, NotificationService, StatisticsService, SignalRService (7 services, 6+ service models)
2. **Push Notifications (2026-07-17):** PushNotificationManager (@MainActor @Observable singleton), AppDelegate adapter, NotificationService extensions (registerDeviceToken/unregisterDeviceToken)
3. **Phase 1 Filament/Spool (2026-07-17):** SpoolService (CRUD + pagination), PrinterService extensions (setActiveSpool/loadFilament/unloadFilament/changeFilament), FilamentModels (SpoolmanSpool/Filament/Vendor/Material), APIClient.patch()
4. **Phase 2 Scanning (Completed 2026-03-07T16:34Z):** SpoolScannerProtocol abstraction, QRSpoolScannerService, NFCService (CoreNFC + NFC tag parsing), QR/NFC parsers, ServiceContainer conditional registration
5. **New Service Layers (2026-03-08):** MaintenanceService, AutoPrintService, JobAnalyticsService, PredictiveService, DispatchService (5 services, 30+ DTOs, all registered in ServiceContainer)
6. **Phase 3 Features (2026-03-08):** Spool NFC tag writing (`writeSpoolTag()` method); Predictive Insights graceful empty state (decodeIfPresent, optional returns)

### Key Technical Decisions Codified (2026-03-07 → 2026-03-08)
- **Spoolman naming & pagination:** Model prefix `Spoolman` (avoid future collisions), limit/offset pagination (not page/pageSize), SetActiveSpoolRequest returns CommandResult, APIClient.patch() for updates
- **Filament UI architecture:** Filament section in PrinterDetailView (between Camera and Actions), SpoolService for list/create/delete, PrinterService for active spool assignment, SpoolPickerView as modal sheet, phase 2 NFC hook ready
- **iPad layout architecture:** @Environment(\.horizontalSizeClass) for adaptive layouts; NavigationSplitView (iPad) vs TabView (iPhone); sidebar with explicit Button-based rows (List(selection:) unavailable on iOS)
- **Service layer design:** PredictionRequest optional fields adapted to match existing ViewModel (not breaking existing code); FleetPrinterStatistics computed Identifiable (id backed by printerId); date query params use ISO8601Plain format; request models Encodable-only (never decoded)

### Testing Infrastructure (2026-07-18 → 2026-03-08)
- **Unit tests:** MockURLProtocol for in-process mocking; MockServices for all protocols; 145+ test cases validating MVP endpoint coverage; 61 test cases for parser contracts (QR/NFC)
- **XCUITest infrastructure:** MockAPIServer (NWListener-based TCP server); environment variable injection; wildcard route matching; canned JSON responses; Spoolman test fixtures
- **Build verification:** 33 new files added to Xcode.pbxproj; ~10 source mismatches reconciled between Lambert models/protocols and Ripley ViewModels

#### Known Issues & Resolutions (2026-03-06 → 2026-03-08)
- **Spoolman "Available" filter (Issue #1, 2026-07-18):** Fixed fallback logic in SpoolmanJsonParser — was incorrectly setting inUse=true for all non-archived spools
- **XCUITest target setup (Decision, 2026-07-20):** Files ready; target creation requires manual Xcode step (deferred to Ripley)
- **NFCService Sendable warning:** Fixed using nonisolated(unsafe) rebinding pattern for @Sendable closures
- **SwiftLint violations (2026-03-08):** Fixed 28 violations across 10 files

#### New Service Layers — 5 Services (2026-03-08)
- **Built:** MaintenanceService, AutoPrintService, JobAnalyticsService, PredictiveService, DispatchService (5 services, 30+ DTOs, all registered in ServiceContainer)
- **Models:** 5 model files with 30+ DTOs across service domains
- **Protocols:** 5 protocol files with default-parameter extensions (pattern: StatisticsServiceProtocol)
- **Services:** 5 actor-based implementations using apiClient.get/post/put
- **PredictionRequest adapted:** Flexible optional fields to match existing ViewModel expectations without breaking changes
- **Date query params:** Used APIClient.iso8601Plain.string(from:) for consistent URL serialization
- **Build verified:** Zero errors, zero new warnings

---

## Recent Work (2026-03-08, Completed 2026-03-08T05:16Z → 2026-03-08T21:51Z)

---

## 2026-03-08T17:32Z — NFC Navigation Fix (Ripley) — Cross-Agent Impact

**Status:** ✅ Completed (Ripley)

### Impact on Lambert
- **No service changes needed** — PushNotificationManager already posts `.pushNotificationTapped` notification correctly
- **Verification:** AppRouter.swift and PFarmApp.swift now properly observe and handle the notification in the UI layer
- **No impact on existing service protocols** — Push notification infrastructure remains unchanged; only the consumer (AppRouter) was missing

### Context
- Ripley fixed bug where app stayed on current printer when tapping NFC notification for a different printer
- Root cause #1: NavigationPath race condition in AppRouter.navigate() — added 50ms async delay between reset and append
- Root cause #2: Missing `.pushNotificationTapped` observer in PFarmApp — server push deep links were silently dropped
- Files changed: `AppRouter.swift`, `PFarmApp.swift`
- Decision logged: `.squad/decisions.md` — NFC/Deep Link Navigation Race Condition Fix (Ripley)

---

## Upcoming Work: Spool NFC Tag Writing Feature (2026-03-08)

**Status:** Scoped by Dallas, ready for dev  
**Owned by:** Dallas (lead), WI-2/5/7 assigned to Lambert  
**Effort:** 2 hours total (model + viewmodel + integration)  
**Blocking on:** Backend WI-1 (Jeff, 1h)

### Your Responsibilities (WI-2, WI-5, WI-7)

**WI-2: iOS Model — Add `hasNfcTag` Field (15m)**
- Edit `PrintFarmer/Models/FilamentModels.swift`
- Add field to `SpoolmanSpool` struct:
  ```swift
  let hasNfcTag: Bool?
  ```
- Mark as optional (`Bool?`) for backward compat with old backend responses
- Unit test: Decode JSON with/without `hasNfcTag` field

**WI-5: iOS ViewModel — Add Write Action (1.5h)**
- Edit `SpoolInventoryViewModel`
- Add state:
  ```swift
  var isWritingNFC: Bool = false
  var writeNFCError: String?
  var highlightedSpoolId: Int?
  ```
- Add method:
  ```swift
  func writeNFCTag(for spool: SpoolmanSpool) async {
      guard let nfcScanner = nfcScanner as? NFCService else {
          writeNFCError = "NFC not available"
          return
      }
      
      isWritingNFC = true
      writeNFCError = nil
      
      do {
          try await nfcScanner.writeTag(spool: spool)
          highlightedSpoolId = spool.id
          try await Task.sleep(nanoseconds: 500_000_000) // 0.5s highlight
          await loadSpools() // Reload to get updated hasNfcTag
      } catch {
          writeNFCError = error.localizedDescription
      }
      
      isWritingNFC = false
  }
  ```
- Unit tests: Success path (calls NFCService.writeTag, reloads spools), error path (sets writeNFCError, user can retry), highlight behavior

**WI-7: Integration — Wire Services (30m)**
- Ensure `SpoolInventoryViewModel.configureNFC(scanner:)` is called:
  ```swift
  // In SpoolInventoryView.onAppear or .task
  viewModel.configureNFC(scanner: services.spoolScanner)
  ```
- Verify `ServiceContainer.spoolScanner` is initialized as `NFCService` (already is)
- Cross-check: `#if canImport(UIKit)` guards in NFCService (already present)
- Integration test: Verify scanner injection works

### Parallel Work (While Waiting for WI-1)
- Review SpoolInventoryViewModel structure (existing filters, refresh logic)
- Plan `writeNFCTag` method error handling (network timeouts, tag write failures, etc.)
- Check existing `NFCService.writeTag(spool:)` method signature (is it already there? needs wrapping?)
- Prepare mock implementations for unit tests

### Key Architecture Notes
- `hasNfcTag: Bool?` — optional to handle old backend without field
- `nfcScanner as? NFCService` cast is acceptable (printer tag writing is NFC-specific)
- Post-write refresh via `loadSpools()` will fetch updated `hasNfcTag` from backend
- `highlightedSpoolId` stored, auto-clears after 0.5s highlight (Ripley uses this in view)
- Error messages should be user-friendly (reuse existing localization patterns)

### What Backend Provides (WI-1, Jeff)
- New field: `hasNfcTag: bool` on SpoolmanSpoolDto
- Endpoint: GET /api/spoolman/spools returns items with `hasNfcTag: true/false`
- DB backing: Recommend `Spool.HasNfcTag` boolean column (vs querying NfcScanEvents count)

### What Ripley Will Deliver (Needed for Your WI-5)
- **WI-3/4 (2.5h):** Badge indicator + "No NFC Tag" filter chip in SpoolInventoryView
- **WI-6 (2h):** Write button + error/success flows in SpoolInventoryView (depends on your WI-5)

### Coordination Notes
- You deliver WI-2/5 in parallel with Ripley's WI-3/4; she gates WI-6 on your WI-5 completion
- WI-7 (integration) touches both ViewModels, so coordinate with Ripley before/after WI-6 completion
- Ash's tests (WI-8) will mock NFCService.writeTag — provide clear method signature for her test stubs

### Success Criteria
- SpoolmanSpool decodes `hasNfcTag` field (JSON with/without field both work)
- ViewModel method `writeNFCTag(for:)` calls NFCService.writeTag, reloads spools
- Error handling is graceful (user-friendly messages, retry option)
- Highlight behavior works (sets/clears spoolId after delay)
- All unit tests pass (success + error paths)
- No regression in existing spool filtering/loading

### Next Steps
1. **Await:** Jeff's backend WI-1 API contract confirmation
2. **Start:** WI-2 (model) — no blocker, start immediately (15m)
3. **Start:** WI-5 (viewmodel) — can parallelize with WI-2
4. **Coordinate:** Ripley's WI-3/4 in parallel (they gate on WI-2 completion)
5. **After:** WI-7 (integration) once WI-6 nears completion

---

---

## 2026-03-08T21:51Z — Spool NFC Tag Writing Feature — CROSS-AGENT IMPACT

**Status:** ✅ Ripley completed all 6 work items (model, NFCService, DeepLinkHandler, AppRouter, UI badge, UI filter, UI write flow)

### Cross-Agent Coordination Completed

**Model (WI-1):** Added `hasNfcTag: Bool?` to `SpoolmanSpool`
- Optional field for backward compatibility with backend responses
- All existing MockSpoolService fixtures updated to include `hasNfcTag: nil` parameter
- No changes needed to existing service protocols

**NFCService (WI-2):** New `writeSpoolTag(spool:)` method for dual-record NDEF write
- Record 1: URI `printfarmer://spool/{id}` for deep links
- Record 2: OpenSpool JSON text record (material, color_hex, brand, weight_g, spoolman_id)
- Reuses existing `NFCMessageWriteDelegate` infrastructure
- No new dependencies or breaking changes to existing services

**ViewModel Integration (WI-5):** SpoolInventoryViewModel enhancements
- Added `isWritingNFC: Bool`, `writeNFCError: String?`, `highlightedSpoolId: Int?` state
- Added `writeNFCTag(for:)` method that calls `NFCService.writeSpoolTag()` and reloads spools
- Added `markSpoolNFCWritten()` helper to reconstruct SpoolmanSpool with updated `hasNfcTag`

### Integration Points
- DeepLinkHandler parses `printfarmer://spool/{id}` URLs (same pattern as `printfarmer://printer/{uuid}`)
- AppRouter uses `pendingSpoolHighlightId` handoff (same pattern as `pendingNFCReadyPrinterId` for printer tags)
- SpoolInventoryView wires write button to `viewModel.writeNFCTag(for:)` with loading/error states

### No Service Changes Required
- All existing `SpoolServiceProtocol`, `PrinterServiceProtocol`, and `NFCService` contracts remain unchanged
- `ServiceContainer` needs no modifications (already registers all services)
- MockSpoolService fixtures just need `hasNfcTag: nil` parameter added to initializers

### Architecture Consistency
- Spool NFC writing follows exact same pattern as printer NFC writing (dual-record, deep link, AppRouter handoff)
- All implementations use established @Observable/@MainActor/@Sendable patterns (no new concurrency patterns)
- Error handling follows existing UI conventions (alert + retry)

### Ready for Testing (Ash — WI-8)
- Test fixtures: `SpoolmanSpool(..., hasNfcTag: nil)` for backward compat tests
- Mock NFCService: `writeSpoolTag(spool:)` method for write flow tests
- Integration tests: Verify highlight behavior, error handling, reload on success

---

### Cross-Agent Update: Ripley — Predictive Insights Fix (2026-03-08T2356)
**From:** Ripley  
**Task:** Fixed Predictive Insights decode error
**Status:** Implementation complete  
**Service Updates:**
- `predictJobFailure` returns `JobFailurePrediction?` instead of throwing on empty body
- `getActiveAlerts`/`getMaintenanceForecast` coalesce empty/null body to `[]`
- All predictive model fields use `decodeIfPresent` with defaults
- **Impact to Lambert:** No protocol changes required; `PredictiveServiceProtocol` already designed for this

---

### Cross-Agent Update: Ripley — Set Filament Button Visibility Fix (2026-03-09T00:08)
**From:** Ripley  
**Task:** Fixed "Set Filament" button remaining visible after spool assignment  
**Impact to Lambert:**
- `PrinterSpoolInfo` already has memberwise init (no changes needed)
- No protocol or service contract changes required
- Backend follow-up: Ideally `GET /api/printers/{id}` should also return `spoolInfo` long-term to avoid local override workaround

---

## Learnings

### CI/CD Code Signing on GitHub Actions macOS Runners (2026-03-09)

**Problem:** TestFlight Beta Build workflow consistently hung at `xcodebuild archive` step (1.5+ hours, no output) on both v0.1.0-beta.1 and v0.1.0-beta.2.

**Root Cause:** 
- `fastlane match` imports certificates into macOS keychain, but `xcodebuild` needs explicit keychain access on CI
- Without proper keychain setup, xcodebuild prompts for keychain access (hangs on headless CI)
- GitHub Actions runners require temporary keychain creation, unlocking, and explicit codesign partition list setup

**Solution Pattern (Standard for GitHub Actions iOS Code Signing):**

1. **Create temporary keychain** before match step:
   ```yaml
   - name: Setup Keychain
     env:
       KEYCHAIN_PASSWORD: ${{ secrets.MATCH_PASSWORD }}
     run: |
       KEYCHAIN_PATH=$RUNNER_TEMP/app-signing.keychain-db
       security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
       security set-keychain-settings -lut 21600 "$KEYCHAIN_PATH"  # 6hr timeout, no sleep lock
       security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
       security default-keychain -s "$KEYCHAIN_PATH"
       security list-keychains -d user -s "$KEYCHAIN_PATH" $(security list-keychains -d user | sed 's/"//g')
       security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
   ```

2. **Pass keychain to fastlane match**:
   ```yaml
   fastlane match appstore \
     --readonly \
     --keychain_name "$KEYCHAIN_PATH" \
     --keychain_password "$MATCH_PASSWORD"
   ```

3. **Tell xcodebuild where to find signing identity**:
   ```yaml
   xcodebuild archive \
     OTHER_CODE_SIGN_FLAGS="--keychain $KEYCHAIN_PATH" \
     ...
   ```

4. **Add timeout to prevent infinite hangs**:
   ```yaml
   - name: Build for App Store
     timeout-minutes: 30
   ```

5. **Always cleanup keychain** (even on failure):
   ```yaml
   - name: Cleanup keychain
     if: always()
     run: security delete-keychain "$KEYCHAIN_PATH" || true
   ```

**Key Technical Details:**
- `RUNNER_TEMP` environment variable provides temp directory path on GitHub runners
- `security set-key-partition-list` prevents codesign from prompting for keychain access
- `-lut 21600` sets 6-hour timeout and disables lock-on-sleep
- Keychain password can reuse `MATCH_PASSWORD` secret (same credential)
- Cleanup step with `if: always()` ensures keychain deletion even on job failure

**Files Modified:**
- `.github/workflows/testflight-beta.yml` — Added 5 steps: keychain setup, match keychain args, xcodebuild keychain flag, timeout, cleanup

**Testing Validation:**
- YAML syntax validated with `python3 -c "import yaml"`
- All existing workflow steps preserved (no regression)
- ExportOptions.plist verified (no changes needed)

**Architecture Decision:**
- Standard GitHub Actions pattern for iOS code signing (used widely across iOS CI/CD community)
- No custom tooling or workarounds needed
- Follows Apple security best practices for keychain access control

## 7. TestFlight Build Hang Fix — Keychain Setup (2026-03-09)

**Orchestration Log:** `.squad/orchestration-log/2026-03-09T0150-lambert.md`

Fixed xcodebuild archive hang (was 1.5+ hours) via standard GitHub Actions iOS keychain pattern:
- **Root cause:** xcodebuild on macOS runners requires explicit keychain access config; without it, codesign prompts (hangs headless)
- **Solution:** Temporary keychain management (`$RUNNER_TEMP/app-signing.keychain-db`) with explicit codesign flags
- **Steps:** Setup keychain + configure fastlane match + configure xcodebuild OTHER_CODE_SIGN_FLAGS + timeout protection + cleanup
- **Reuses secrets:** `MATCH_PASSWORD` double-duty (keychain + fastlane match)
- **Outcome:** ✅ Expected build time ~10–15 minutes (was 1.5+ hour hang); industry-proven pattern

**Ripley Integration:** No view/ViewModel changes; workflow improvements transparent to app code.
**Ash Integration:** No test infrastructure changes; CI improvements isolated to GitHub Actions.

### App Store Connect API Keys for TestFlight CI Upload (2026-03-09)

**Problem:** TestFlight upload step was using `FASTLANE_USER`/`FASTLANE_PASSWORD` environment variables, which Apple rejects with "Invalid username and password combination" error in CI environments. Apple's policy now requires API Key authentication for CI-based uploads.

**Why the rejection:**
- Apple deprecated iTunes Connect credentials for CI/CD in favor of API keys
- App Store Connect API Keys provide role-based access control (security)
- FASTLANE_USER/FASTLANE_PASSWORD were originally designed for local developer machines
- GitHub Actions CI environment triggers Apple's fraud detection (IP-based filtering)

**Solution Implemented:**
1. **Generate App Store Connect API Key:**
   - Create at `https://appstoreconnect.apple.com/access/integrations/api/` with Developer or Admin role
   - Download as `.p8` file (private key in EC format)
   - Extract `key_id` and `issuer_id` from API key details page

2. **Add GitHub Secrets (3 required):**
   - `APP_STORE_CONNECT_API_KEY_ID` — key ID from API key page
   - `APP_STORE_CONNECT_API_ISSUER_ID` — issuer ID (usually company ID)
   - `APP_STORE_CONNECT_API_KEY_CONTENT` — the `.p8` file base64-encoded

3. **Updated Workflow Step (Upload to TestFlight):**
   - Removed `FASTLANE_USER` and `FASTLANE_PASSWORD` env vars
   - Create temporary API key JSON file in fastlane's expected format:
     ```json
     {
       "key_id": "...",
       "issuer_id": "...",
       "key": "-----BEGIN EC PRIVATE KEY-----\n...\n-----END EC PRIVATE KEY-----",
       "in_house": false
     }
     ```
   - Base64-decode the .p8 content, inject into JSON template
   - Pass `--api_key_path` to `fastlane pilot upload` command
   - Cleanup temp JSON file in "Cleanup keychain" step

4. **Also removed FASTLANE_USER/FASTLANE_PASSWORD from fastlane match step:**
   - `fastlane match appstore` uses git-based certificate storage (via `MATCH_GIT_BASIC_AUTHORIZATION`)
   - Does not need Apple ID credentials; was unnecessary there
   - Simplified env vars to only: `MATCH_PASSWORD`, `MATCH_GIT_URL`, `MATCH_GIT_BASIC_AUTHORIZATION`

**Technical Details:**
- `jq -Rs '.'` escapes newlines in the PEM-format private key for JSON encoding
- `base64 -d` decodes the secret (CI stores secrets as base64 to avoid binary data issues)
- Temp files created in `$RUNNER_TEMP` (GitHub's isolated temp directory)
- Cleanup happens in `if: always()` block to ensure file is deleted even on upload failure

**Files Modified:**
- `.github/workflows/testflight-beta.yml` — Updated "Upload to TestFlight" and "Setup code signing with fastlane match" steps, enhanced "Cleanup keychain"

**Configuration Required:**
- Generate API key in App Store Connect and add 3 secrets to GitHub repo settings
- Confirm API key has "Developer" or "Admin" role (minimum required for pilot uploads)


### Fix: Spool Eject "Client error 400" (2025-07-24)

**Root Cause:** Backend endpoints like `filament-unload` and `active-spool` return `CommandResult` (`{"success": false, "message": "..."}`) with HTTP 400 on failure. The iOS `APIError` model only had `title`/`detail`/`status`/`errors` fields (matching ASP.NET ProblemDetails), so the `message` field from `CommandResult` was silently lost. Users saw generic "Client error (400)" instead of the actual backend reason.

**Fix:** Added `message: String?` to `APIError` and updated the `clientError` description fallback chain to `detail → message → title → "Client error (code)"`.

**Key Insight:** The backend uses two different error body shapes — `ProblemDetails` (ASP.NET validation) and `CommandResult` (printer operations). The `APIError` model must handle both. This is a general pattern: any endpoint using `MapCommandResult()` can return `CommandResult` bodies on 400.


### AutoPrint → AutoDispatch Terminology Rename (2025-03-11)

**Context:** The backend frontend renamed the concept from "AutoPrint" to "AutoDispatch" to better reflect its role in the dispatch system. The iOS client needed to match this terminology while preserving API compatibility.

**Changes Made:**
- **File Renames (via `git mv`):**
  - `AutoPrintModels.swift` → `AutoDispatchModels.swift`
  - `AutoPrintServiceProtocol.swift` → `AutoDispatchServiceProtocol.swift`
  - `AutoPrintService.swift` → `AutoDispatchService.swift`
  - `AutoPrintViewModel.swift` → `AutoDispatchViewModel.swift`
  - `AutoPrintSection.swift` → `AutoDispatchSection.swift`
  - Test/mock files also renamed

- **Type Renames:**
  - `AutoPrintStatus` → `AutoDispatchStatus`
  - `AutoPrintReadyResult` → `AutoDispatchReadyResult`
  - `AutoPrintNextJob` → `AutoDispatchNextJob`
  - `SetAutoPrintEnabledRequest` → `SetAutoDispatchEnabledRequest`
  - `AutoPrintServiceProtocol` → `AutoDispatchServiceProtocol`
  - `AutoPrintService` → `AutoDispatchService`
  - `AutoPrintState` → `AutoDispatchState` (in Models.swift)

- **API Compatibility:**
  - Added `CodingKeys` to `AutoDispatchStatus` to map `autoDispatchEnabled` (Swift property) to `autoPrintEnabled` (JSON key from API)
  - All API endpoint URLs remain `/api/autoprint/...` (backend API unchanged)

- **Files Updated:**
  - Models: `AutoDispatchModels.swift`, `Models.swift`
  - Services: `AutoDispatchService.swift`, `AutoDispatchServiceProtocol.swift`, `ServiceContainer.swift`
  - ViewModels: `AutoDispatchViewModel.swift`, `PrinterDetailViewModel.swift`
  - Views: `AutoDispatchSection.swift`, `PrinterDetailView.swift`
  - Tests: `MockAutoDispatchService.swift`, `AutoDispatchViewModelTests.swift`
  - Xcode project: `project.pbxproj` (file references updated)

**Key Decision:** Property naming convention — Swift property is `autoDispatchEnabled` (user-facing terminology) while JSON key is `autoPrintEnabled` (backend contract). This decouples client-side naming from backend API changes and prevents the need for backend migration.

**Files:**
- `PrintFarmer/Models/AutoDispatchModels.swift` — Renamed types and models
- `PrintFarmer/Services/AutoDispatchService.swift` — Service implementation
- `PrintFarmer/Protocols/AutoDispatchServiceProtocol.swift` — Service protocol
- `PrintFarmer/Services/ServiceContainer.swift` — Updated dependency injection
- `PrintFarmer/Models/Models.swift` — Renamed AutoDispatchState enum
- `PrintFarmer/ViewModels/AutoDispatchViewModel.swift` — ViewModel using new types

## Batch: AutoDispatch Terminology Rename (2026-03-11)

**Session Log:** `.squad/log/2026-03-11T16-00-22Z-autodispatch-rename.md`  
**Orchestration Log:** `.squad/orchestration-log/2026-03-11T16-00-22Z-lambert.md`

### Team Context
This batch deployed three agents in parallel:
- **Lambert (you):** Service/protocol/models layer rename with CodingKeys backward compatibility
- **Ripley:** View/ViewModel rename + PendingReady state UI implementation
- **Ash:** Test file renames + 28 test updates (22 refactored + 6 new PendingReady tests)

### Decision Merged
Documented in `.squad/decisions.md`: "AutoPrint → AutoDispatch Terminology Rename"
- Rationale: Platform alignment with React frontend
- Strategy: CodingKeys for API backward compatibility
- Impact: Zero breaking changes, terminology now consistent across iOS/React

### Related Agent Work
- **Ripley (Interdependent):** Used your renamed types for AutoDispatchViewModel + AutoDispatchSection
- **Ash (Dependent):** Refactored tests after your file renames; added coverage for Ripley's PendingReady state

### Cross-Team Learning
**CodingKeys Pattern for API Compatibility:** This approach (rename Swift types, use CodingKeys for backward compat) can be applied to future terminology migrations without backend changes. Document this pattern in team runbook.

---

## 2026-03-13 — Dispatch Back-Button Crash Investigation

**Status:** ✅ Investigation complete, fixes recommended (not yet implemented)

### Learnings
- **Task.isCancelled guards are insufficient alone**: They only protect against cancellation of the *calling* task. Unstructured `Task { }` blocks (e.g., Retry buttons) are never cancelled by SwiftUI's `.task` lifecycle — the guards silently pass through.
- **Crash root cause**: `DispatchDashboardView.swift` line 20 creates an unstructured `Task { }` in the Retry button that survives view dismissal. The `Task.isCancelled` guards added in commit bd18e97 are ineffective for this code path.
- **Pattern for future**: Any button action that launches `Task { }` inside a navigation destination view must either (a) store and cancel the task on disappear, or (b) use a ViewModel-level `isActive` flag.
- **Dashboard swipeable pages pattern**: DashboardView uses `TabView(.page)` with 3 pages on iPhone (Overview, Active, Queue) + `PageIndicator`. Dispatch could become a 4th page.
- **Navigation architecture**: Dispatch is a NavigationLink push from Dashboard's QueuePage. It uses `AppDestination.dispatchDashboard` resolved by `destinationView(for:)` free function.
- **No SignalR on Dispatch**: DispatchViewModel has no SignalR listeners or timers — crash is purely a task lifecycle issue.
- **AutoDispatchViewModel also lacks guards**: `AutoDispatchSection.swift` buttons create unstructured Tasks without cancellation handling — same pattern, potential same bug on PrinterDetailView back navigation.

### Key File Paths
- `PrintFarmer/Views/Dashboard/DispatchDashboardView.swift` — Dispatch view with Retry button (crash site)
- `PrintFarmer/ViewModels/DispatchViewModel.swift` — ViewModel with existing guards
- `PrintFarmer/Views/Dashboard/DashboardView.swift` — Parent view, swipeable TabView pattern
- `PrintFarmer/Navigation/AppDestination.swift` — Navigation enum
- `PrintFarmer/Views/Printers/AutoDispatchSection.swift` — Same unguarded Task pattern (related risk)

### Decision Filed
- `.squad/decisions/inbox/lambert-dispatch-crash.md` — Full root cause analysis, 3 recommended fixes

## Learnings

### iOS System Notifications (2025-07)
- Local notification infrastructure was already in place in `PendingReadyMonitor` (permission request, `UNUserNotificationCenter` posting, badge count, foreground display via `PushNotificationManager` delegate).
- Key improvement: switched from random UUID notification identifiers to printer-UUID-based identifiers (`pending-ready-{printerId}`). This enables per-printer deduplication and automatic removal when a printer leaves PendingReady state.
- `removeDeliveredNotifications(withIdentifiers:)` and `removePendingNotificationRequests(withIdentifiers:)` are used to clean up stale notifications from Notification Center.
- On `stopMonitoring()` (logout), all pending-ready notifications are now cleaned up.
- `AppDelegate` sets `PushNotificationManager.shared` as `UNUserNotificationCenter.delegate` — this is what enables foreground banner display and tap handling.
- Notification tap on a `PENDING_READY` category posts `.localNotificationTapped` which `PFarmApp` listens to and navigates to the printers tab.

## 2026-03-14 — Notification Deduplication (Ripley cross-pollination)

**Related fixes from Ripley:**
- Back-button crash fixes added `isViewActive` guards across 13 views and 5 ViewModels
- Task lifecycle fixes ensure state mutations only happen when views are on-screen
- Impact: Notification delivery is now safe during rapid navigation (no crashes during notification display)

## Learnings — Demo Mode Service Layer (Phase 0–3)

### What Was Done
- **Phase 0:** Created `AuthServiceProtocol` and `LocationServiceProtocol` in production `Protocols/`. Made `AuthService` and `LocationService` conform. Removed duplicate `AuthServiceProtocol` from `TestProtocols.swift`.
- **Phase 1:** Retyped all 14 `ServiceContainer` properties from concrete to `any XServiceProtocol`. Made `apiClient` optional (`APIClient?`). Added `static func demo() -> ServiceContainer` factory using a private protocol-based init. Updated `PFarmApp` to use `ServiceContainer.demo()` and `container.authService` instead of creating a separate AuthService. Changed `AuthViewModel` to accept `any AuthServiceProtocol`. Fixed `PushNotificationManager.configure()` to accept protocol type. Fixed 3 views (`SpoolInventoryView`, `PrinterDetailView`, `SpoolPickerView`) where `nfcService` became optional.
- **Phase 2:** `DemoMode` singleton already existed at `Utilities/DemoMode.swift` (from Ripley). Created `DemoData.swift` with consistent UUIDs, 6 printers, 8 spools, 10 notifications, statistics, and a `decodePrinter(from:)` helper for JSON-based Printer construction.
- **Phase 3:** Created 13 demo services in `Services/Demo/`: Auth, Printer, Job, Spool, Location, Notification, Statistics, Maintenance, AutoDispatch, JobAnalytics, Predictive, Dispatch, SignalR. All conform to existing protocols. `DemoSignalRService` uses `Task.detached` with `Task.sleep(nanoseconds:)` to simulate live printer progress every 5 seconds.

### Key Decisions
- **Printer creation via JSON:** `Printer` only has `init(from decoder:)`, no memberwise init. Used `JSONDecoder` + string interpolation for all mock Printer data.
- **NSLock forbidden in async:** Swift 6 concurrency disallows `NSLock.lock()` in async contexts. Replaced with local-state-in-detached-Task pattern for `DemoSignalRService`.
- **@MainActor access from non-MainActor:** `DemoMode.shared` is `@MainActor`. Used `await MainActor.run { }` to safely access it from `DemoAuthService.restoreSession()`.
- **Xcode project (pbxproj):** Files must be manually added to `project.pbxproj` — the project uses `.xcodeproj`, not pure SPM discovery. Used Python script to add 18 new file references, build file entries, group memberships, and a new "Demo" group under Services.

### Pre-existing Issues Found
- `PrinterListViewModelTests.swift:16` — test was already broken before our changes (missing `autoPrintService` arg). Not caused by demo mode work.
- `DemoMode.swift` and `DemoModeBanner.swift` existed on disk but were NOT in `project.pbxproj` — added them as part of this work.

## 2026-03-19T01:45Z — Demo Mode Session Log (Background Execution)

**Status:** ✅ COMPLETE — All three phases delivered, build succeeds

### Phases Executed
1. **Protocol Gaps Resolution** — AuthServiceProtocol + LocationServiceProtocol created and implemented
2. **ServiceContainer Retyping** — Changed 14 properties to `any XServiceProtocol`, added .demo() factory
3. **Demo Services Implementation** — 13 demo services built with canned data + realistic behavior

### Build Verification
✅ Zero errors, zero warnings on iPhone 17 Pro simulator

### Integration with Dallas & Ripley
- **Dallas (orchestration):** Coordinated timing, verified architecture assumptions
- **Ripley (UI):** Demo login button → calls LoginView → AuthViewModel.loginAsDemo() → triggers demo services flow
- **Build together:** All three agents' code compiles without conflicts

### Key Technical Achievements
- **ServiceContainer abstraction:** Now fully protocol-typed; can swap implementations at runtime
- **Demo data consistency:** UUIDs stable across service calls (same printer UUID in DemoPrinterService, DemoJobService, etc.)
- **Async/await patterns preserved:** All demo services use async/await like real services; interchangeable at protocol level
- **SignalR simulation:** Timer-driven printer progress + job queue updates; no real WebSocket

### Learnings
- Protocol-based injection is the "right" pattern for this codebase; existing ViewModels require zero changes
- ServiceContainer retyping is actually beneficial beyond demo mode (improves testability for ALL future work)
- @MainActor access requires `await MainActor.run { }` in Swift 6 async contexts
- `NSAllowsLocalNetworking` only covers .local/mDNS domains and bare IPs; need `NSAllowsArbitraryLoads` for HTTP to arbitrary hostnames
- Info.plist ATS config: both `NSAllowsArbitraryLoads` and `NSAllowsLocalNetworking` are enabled for dev/local server use
- ATS failures surface as URLError code `.appTransportSecurityRequiresSecureConnection` (-1022)

## 2026-03-27 — AutoDispatch API Contract Alignment

**Status:** ✅ Completed

### What Changed
Backend renamed AutoDispatch routes from `/api/autoprint/*` to `/api/auto-dispatch/*` and restructured the `AutoDispatchStatus` DTO with new fields. iOS models, services, protocol, and tests were out of sync.

### Changes Made
1. **AutoDispatchService.swift** — All 6 route strings updated from `/api/autoprint/...` to `/api/auto-dispatch/...`. Return type of `getAllStatus()` changed from `[AutoDispatchStatus]` to `AutoDispatchGlobalStatus`. Added `preClear(printerId:)` endpoint.
2. **AutoDispatchModels.swift** — `AutoDispatchStatus` struct rebuilt to match backend DTO: removed old `autoDispatchEnabled`/`queuedJobCount` fields and CodingKeys, added `printerName`, `enabled`, `isReady`, `currentJobName`, `queueDepth`, `readyGateChecks`, `lastActivity`, `bedPreConfirmed`, `attentionMessage`. Added `ReadyGateCheck` and `AutoDispatchGlobalStatus` structs. Convenience init with defaults provided for call-site compatibility.
3. **Models.swift** — Added `dismissed = "Dismissed"` case to `AutoDispatchState` enum (with fallback decoder for int value 3).
4. **AutoDispatchServiceProtocol.swift** — Updated `getAllStatus()` return type, added `preClear(printerId:)`.
5. **DemoAutoDispatchService.swift** — Updated to match new protocol and struct fields.
6. **MockAutoDispatchService.swift** — Updated for new protocol (added `preClear`, changed `getAllStatus` to return `AutoDispatchGlobalStatus`).
7. **AutoDispatchViewModel.swift** — Updated field references (`autoDispatchEnabled` → `enabled`, `queuedJobCount` → `queueDepth`), optimistic update carries forward all new fields.
8. **PrinterListViewModel.swift** — Changed `getAllStatus()` consumer to access `.printers` on `AutoDispatchGlobalStatus`.
9. **PendingReadyMonitor.swift** — Same `.printers` accessor fix.
10. **AutoDispatchSection.swift** — Updated `queuedJobCount` → `queueDepth` references.
11. **AutoDispatchViewModelTests.swift** — All ~19 `AutoDispatchStatus` constructions updated with new field names.

### Learnings
- When backend renames DTO fields, a convenience init with defaults on the Swift struct prevents touching every test call site while still matching the new JSON contract. The memberwise init still works for full-field constructions (API decoding), while the convenience init serves test/demo code.
- `getAllStatus()` returning a wrapper type (`AutoDispatchGlobalStatus`) instead of a bare array is a common backend pattern for adding metadata (like `globalEnabled`). All consumers that iterated the array need to access `.printers` first.
- The `autoprint` → `auto-dispatch` route rename didn't require CodingKeys this time because the JSON property names match Swift camelCase exactly — the previous CodingKeys were mapping `autoPrintEnabled` to `autoDispatchEnabled` which is now just `enabled`.

---

## 2026-03-09 — OpenTag3D Binary Encoder/Decoder

**Status:** ✅ Completed

### What Was Done
- Replaced stub `createOpenTag3DPayload(from:)` in `NFCTagParser.swift` with full 102-byte binary encoder per opentag3d.info spec
- Added `parseOpenTag3D(_ data:) -> ScannedSpoolData?` round-trip decoder
- Wired `application/opentag3d` MIME type handling into `NFCService.parseMessage()` so scanned OpenTag3D tags are recognized
- Updated error message in NFCService write path (no longer "not yet available")
- Build verified: zero errors, zero new warnings

### Files Changed
1. `PrintFarmer/Utilities/NFCTagParser.swift` — Full OpenTag3D encoder + decoder + binary helpers
2. `PrintFarmer/Services/NFCService.swift` — Added opentag3d parse branch + cleaned error message

### Learnings
- OpenTag3D uses a fixed 102-byte binary layout (NOT JSON) — all multi-byte integers are unsigned big-endian, all strings UTF-8 null-padded to fixed length
- The memory map has a 15-byte reserved gap at 0x0C–0x1A and a 1-byte gap at 0x4F between Color 1 and Color 2; zero-initializing the buffer handles these cleanly
- SpoolmanSpool lacks color name, diameter, and temperature fields — defaults used (1750µm diameter, 0 for unknown temps, material-based density lookup)
- Material modifiers (CF, Silk, HF) derived by stripping base material name from filamentName — simple but effective for common naming patterns
- Color hex parsing: `#RRGGBB` → RGBA with alpha=255; absent color defaults to mid-gray (128,128,128,255)
- The `parseOpenTag3D` reads `record.payload` directly (binary), unlike OpenSpool/OpenPrintTag which call `parsePayloadData(record)` first (text extraction)

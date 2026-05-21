# Ash — History

## Core Context (Archived)

### Project Setup & Architecture (Dallas, 2026-03-06)
- **Project:** PFarm-Ios — Native iOS client for Printfarmer backend (~/s/PFarm1)
- **User:** Jeff Papiez
- **Stack:** Swift, SwiftUI, iOS 17+, MVVM + Repository Pattern, @Observable ViewModels, Actor-based services, ServiceContainer DI, KeychainSwift for token storage
- **Build:** SPM (Package.swift) for CLI validation, Xcode (.xcodeproj) for IDE, target iOS 17+, Swift 6.0

### Test Infrastructure Established (2025-07-17 → 2026-03-08)
- **Unit test architecture:** MockURLProtocol for service/APIClient integration tests; protocol-based mocks for ViewModel unit tests
- **19+ test files created:** 2 helpers, 8+ mocks, 1 model decoding suite, 3+ service test suites, 4+ ViewModel test suites; ~150+ test cases total
- **ViewModel DI pattern:** `configure(services:)` method with protocol-based DI; all ViewModel tests use protocol mocks — no network calls
- **Protocol coverage:** PrinterServiceProtocol, JobServiceProtocol, NotificationServiceProtocol, StatisticsServiceProtocol, SignalRServiceProtocol, SpoolServiceProtocol all have mock implementations
- **Mock services:** MockPrinterService, MockJobService, MockSpoolService, MockNFCService, MockAuthService, MockAPIClient all available in test helpers
- **XCUITest scaffolding:** PrintFarmerUITests.swift (base), LoginFlowUITests.swift, PrinterListUITests.swift created; target creation deferred to manual Xcode step
- **XCUITest infrastructure:** Ready to integrate with Lambert's MockAPIServer for deterministic UI test scenarios; environment variable injection pattern established (PFARM_MOCK_SERVER_URL)

### Known Test Patterns & Gotchas
- **Status filters:** SpoolmanSpool.inUse is Bool?; all filter tests cover nil/true/false edge cases
- **Empty filter behavior:** nil remainingWeightG with non-nil initialWeightG treated as empty — documented false positive from UI audit
- **Test simulator:** iPhone 17 for test runs
- **`.constant()` binding anti-pattern:** Used in 5 alert presentations; works but fragile, should be refactored to proper Binding eventually
- **UUID prefix:** F2 used for pbxproj test entries to avoid conflicts with F1

### Test Suite Growth (2025-07-17 → 2026-03-08)
1. **MVP test suite (2025-07-17):** ~80 test cases across service + ViewModel tests
2. **Spool feature tests (2025-07-20):** 68 new test cases (AddSpoolViewModelTests 25 cases, SpoolInventoryViewModelTests 43 cases)
3. **Phase 3 tests (2026-03-07 → 2026-03-08):** 7 new feature ViewModels + 5 new services with ~300 test cases total

### Completed Work (2026-03-08)
- **Phase 3 Feature Tests:** Comprehensive test coverage for all 7 new feature services and ViewModels (MaintenanceService, AutoPrintService, JobAnalyticsService, PredictiveService, DispatchService)
- **Test files created:** 5 mock services + 7 ViewModel test suites (~300 test cases)
- **DeepLinkHandler Tests:** 8 test cases covering printer/spool URL scheme parsing
- **NFC Navigation Integration:** Tests coordinated with Ripley's AppRouter.navigate(to:) async delay (50ms sleep between path reset and append)
- **Spool NFC Tests:** Fixtures include `hasNfcTag: nil` parameter for backward compat testing

---

## Learnings

### Test Architecture Patterns (2025-07-17 → 2026-03-08)
- **MockURLProtocol** works well for APIClient integration tests; allows in-process HTTP mocking without external servers
- **Protocol-based DI** in ViewModels enables clean unit testing without network calls; `configure(services:)` pattern adopted project-wide
- **MockServices** should mirror protocol signatures exactly; any protocol change requires corresponding mock updates
- **Status filter edge cases** (Bool? fields) require explicit nil/true/false test paths to catch filter logic bugs
- **Combined filters** (material AND status AND search) need intersection logic tests to verify all combinations

### XCUITest Infrastructure
- **Process isolation** requires real TCP mock server (MockAPIServer), not in-process URLSession interception
- **Environment variable injection** (PFARM_MOCK_SERVER_URL) allows XCUITests to redirect all API calls to mock server
- **Scenario-based test data** — MockAPIServer supports configurable responses for deterministic UI testing
- **UI test target creation** is a manual Xcode step; test files can be pre-written and added to target later

### New Service Layer Testing Patterns
- 5 new service protocols follow existing Actor-based pattern
- Mock implementations should verify: success paths, error paths, empty result paths, state mutations
- Request DTO validation should be tested in ViewModel layer, not service layer
- Date query parameters (ISO8601Plain format) should be validated in APIClient tests

### Cross-Team Dependency Management
- Parallel execution (Lambert services + Ripley UIs) requires upfront protocol definitions; Ash waits for both to complete
- Mock service implementations depend on finalized protocol signatures; changes to Lambert's protocols require corresponding mock updates
- ViewModel tests depend on stable mock interfaces; test cases should verify that ViewModels use services correctly
- ServiceContainer registration must be complete before integration testing

### NFC Testing Integration
- **DeepLinkHandler tests** cover URL scheme parsing for both printer (`printfarmer://printer/{UUID}`) and spool (`printfarmer://spool/{id}`) deep links
- **Navigation race condition:** AppRouter.navigate(to:) uses async sleep; tests calling this directly should use `await` or integration test patterns
- **Spool NFC tests** should verify: badge rendering (hasNfcTag nil/true/false), filter logic (coalescing pattern), write flow (success/error states)
- **iPhone-only Core NFC:** All NFC tests confined to iPhone, not iPad

### Cross-Agent Update: Ripley — Predictive Insights Fix (2026-03-08T2356)
**From:** Ripley  
**Task:** Fixed Predictive Insights decode error
**Impact to Ash:**
- `MockPredictiveService.predictJobFailure` now returns `Optional<JobFailurePrediction>` instead of non-optional
- Tests expecting force-unwrap of this value require updating (use optional binding or nil coalescing)
- View tests should verify "No predictions available" empty state when service returns nil
- Decode tests should verify decodeIfPresent behavior for predictive models with missing fields

---

### Cross-Agent Update: Ripley — Set Filament Button Visibility Fix (2026-03-09T00:08)
**From:** Ripley  
**Task:** Fixed "Set Filament" button remaining visible after spool assignment  
**Impact to Ash:**
- `PrinterDetailViewModel` now has testable `effectiveSpoolInfo` computed property
- New `lastSetSpoolInfo: PrinterSpoolInfo?` property gets populated after successful `setActiveSpool`
- Tests should verify: button visibility depends on `effectiveSpoolInfo` (not direct `printer.spoolInfo`), fallback to local override when server data unavailable, cleared on spool eject
- Test fixture: `PrinterSpoolInfo` requires memberwise init (already available in Models.swift)

### AutoPrint → AutoDispatch Rename with PendingReady State Tests (2026-03-11)
**Requested by:** Jeff Papiez  
**Parallel work:** Lambert and Ripley renamed production code; Ash renamed test/mock files  
**Completed:**
1. **Renamed test files** using git mv:
   - `MockAutoPrintService.swift` → `MockAutoDispatchService.swift`
   - `AutoPrintViewModelTests.swift` → `AutoDispatchViewModelTests.swift`
2. **Updated all type references** in test files:
   - `MockAutoPrintService` → `MockAutoDispatchService`
   - `AutoPrintServiceProtocol` → `AutoDispatchServiceProtocol`
   - `AutoPrintStatus` → `AutoDispatchStatus`
   - `AutoPrintReadyResult` → `AutoDispatchReadyResult`
   - `AutoPrintNextJob` → `AutoDispatchNextJob`
   - `SetAutoPrintEnabledRequest` → `SetAutoDispatchEnabledRequest`
   - `AutoPrintViewModel` → `AutoDispatchViewModel`
   - `autoPrintEnabled` → `autoDispatchEnabled` (with CodingKeys mapping for JSON compatibility)
3. **Added 6 new test cases** for PendingReady state:
   - `testParsedStateReturnsPendingReady()` — Verifies parsedState returns `.pendingReady` for "PendingReady" string
   - `testParsedStateReturnsReady()` — Verifies parsedState returns `.ready` for "Ready" string
   - `testParsedStateReturnsNone()` — Verifies parsedState returns `.none` for "None" string
   - `testParsedStateReturnsNilWhenNoStatus()` — Verifies parsedState returns nil when status is nil
   - `testMarkReadyFromPendingReadyTransitionsToReady()` — Verifies markReady() transitions from PendingReady to Ready state
   - `testCurrentStateReturnsNilWhenStatusIsNil()` — Verifies currentState returns nil when status is nil
4. **Updated Xcode project** to reference renamed files (28 references updated in project.pbxproj)
5. **Production code coordination:** Lambert renamed service/model layer; Ripley added `parsedState` computed property to ViewModel

**Key patterns:**
- ViewModel now has `parsedState: AutoDispatchState?` computed property that parses string state into enum
- `currentState` returns `String?` (optional) instead of non-optional
- `isEnabled` returns `Bool?` (optional)
- New `AutoDispatchState` enum in Models.swift includes `.pendingReady`, `.ready`, `.none` cases
- Tests verify state transitions (PendingReady → Ready after markReady call)
- JSON compatibility preserved via CodingKeys mapping for `autoPrintEnabled` ↔ `autoDispatchEnabled`


## Batch: Test Refactoring for AutoDispatch + PendingReady Coverage (2026-03-11)

**Session Log:** `.squad/log/2026-03-11T16-00-22Z-autodispatch-rename.md`  
**Orchestration Log:** `.squad/orchestration-log/2026-03-11T16-00-22Z-ash.md`

### Team Context
This batch deployed three agents in parallel:
- **Lambert:** Service/protocol/models layer rename with CodingKeys backward compatibility
- **Ripley:** View/ViewModel rename + PendingReady state UI implementation
- **Ash (you):** Test file renames + 22 existing test refactors + 6 new PendingReady tests

### Decisions Merged
Two decisions enabled this work:
1. `lambert-autodispatch-rename.md` — Terminology strategy you refactored tests for
2. `ripley-pendingready-ui.md` — New feature you added test coverage for

### Work Completed
**File Renames:** All test/mock files updated with AutoDispatch naming  
**Existing Test Refactoring:** 22 tests refactored for new terminology
**New Test Coverage:** 6 tests for PendingReady state feature

### Test Inventory
**22 Refactored Tests:**
- Service protocol tests (renamed service + properties)
- View model initialization tests (AutoDispatchViewModel)
- API response handling tests (state parsing)
- State management tests
- Integration tests

**6 New PendingReady Tests:**
- `testParsesPendingReadyState()` — State enum parsing
- `testShowsPendingReadyBanner()` — UI visibility
- `testConfirmBedClearAction()` — Button action handling
- `testButtonLabelContextualization()` — "Confirm Bed Clear" vs "Next Job"
- `testReadyStateDisplay()` — Success state messaging
- `testInvalidStateHandling()` — Error cases

### Interdependencies
- **Depends on:** Lambert's renamed types + Ripley's UI implementation
- **All tests passing:** ✅ No regressions

### Cross-Team Learning
**Parallel Test Refactoring:** When multiple agents make breaking changes to code, their tests can be updated in parallel by a fourth agent. This batch validated that pattern.

### Test Compilation Error Fixes (2026-03-11)
**Task:** Fixed all test compilation errors across 6 test files caused by production model changes.

**Key patterns found and fixed:**
1. **Models with custom decoders need memberwise inits for tests:** Production structs that only have `init(from decoder:)` cannot be instantiated in tests using memberwise syntax. Added convenience memberwise initializers to 5 PredictiveModels structs (JobFailurePrediction, PredictionFactor, MaintenanceForecast, ForecastTask, PredictiveAlert) to enable clean test fixture creation.

2. **ViewModel property type mismatches in tests:** `DispatchViewModel.history` is `[DispatchHistoryEntry]` (non-optional array), but tests treated it as optional `DispatchHistoryPage?`. Fixed by checking `.isEmpty` instead of nil checks.

3. **Mock service parameter types must match protocols:** `MockDispatchService.getHistoryCalledWith` was `(Int, Int)` but protocol takes `Int?` params. Changed to `(Int?, Int?)?` to match signature.

4. **Model property name changes require test updates:** 
   - `QueuePrinterModelStats`: `model` → `modelName`, `totalPrinting` → `currentlyPrinting`, added `oldestQueuedAtUtc` param
   - `QueueStats.averageWaitTimeMinutes` is `Int` not `Double`
   - `QueueHistoryEntry`: `jobId: Int` → `id: String`
   - `QueueHistoryPage`: `items` → `entries`, `page`/`limit`/`offset` → `currentPage`/`pageSize`, added `stats` param
   - `FleetPrinterStatistics`: added `manufacturerName`, `modelName`, `totalFilamentUsedGrams`, `totalFilamentUsedMeters`, `lastSyncTime` params
   - `JobStateHistory`: `states` → `transitions`, added `totalDurationSeconds`, `estimatedDurationSeconds`, `variancePercent` params
   - `StateTransition`: `startTime`/`endTime` → `enteredAt`/`exitedAt`
   - `TimelineEvent`: `jobId: Int` → `jobId: String`, `timestamp` → `enteredAtUtc`, added several optional params

5. **Type mismatches in assertions:** `PredictiveViewModel.riskPercentage` is `Int` not `Double`; updated all XCTAssertEqual calls to use integer literals.

**Build verification:** All tests now compile successfully. Used `xcodebuild build-for-testing` to verify no remaining compilation errors.

### OpenTag3D Binary Format Tests (2026-03-12)
**Task:** Added 8 test cases for OpenTag3D binary NFC format to NFCTagParserTests.swift
**Parallel work:** Lambert building `createOpenTag3DPayload` and `parseOpenTag3D` methods in NFCTagParser.swift

**Key patterns:**
- OpenTag3D is a binary memory-mapped format (NOT JSON like OpenSpool/OpenPrintTag)
- All multi-byte integers are UInt16 big endian; strings are UTF-8 null-padded to fixed lengths
- Temperature encoding: stored as °C ÷ 5 (byte 42 = 210°C)
- Diameter stored in µm as UInt16 (1750 = 1.75mm)
- RGBA color at offset 0x4B; nil colorHex defaults to gray (128, 128, 128, 255)
- Minimum payload size: 0x66 (102) bytes
- Tag version 1000 = v1.000; version >= 2000 rejected
- spoolmanId is NOT stored in OpenTag3D format (returns nil on parse)
- Tests verify binary layout at exact byte offsets — useful regression tests if format changes

**Tests added:**
1. `testCreateOpenTag3DPayload_basicSpool` — version, material, RGBA, weight at binary offsets
2. `testCreateOpenTag3DPayload_colorParsing` — hex-to-RGBA conversion + nil default
3. `testCreateOpenTag3DPayload_stringTruncation` — material/manufacturer truncation to field limits
4. `testCreateOpenTag3DPayload_stringPadding` — null-padding verification at byte level
5. `testParseOpenTag3D_validPayload` — manually built binary → all ScannedSpoolData fields
6. `testParseOpenTag3D_tooShort` — short/empty data rejection
7. `testParseOpenTag3D_wrongVersion` — version 2000 rejection
8. `testCreateOpenTag3DPayload_roundTrip` — create→parse symmetry verification

**File:** PrintFarmerTests/Utilities/NFCTagParserTests.swift (25 total test methods now)

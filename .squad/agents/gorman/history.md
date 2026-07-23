# Gorman — iOS Networking & API Integration History

> Migrated from PFarm-Ios `.squad/agents/lambert/` on 2026-05-20 when the iOS squad was merged into the shared PrintFarmer uber-team.

## Learnings

### Core Architecture (2026-03-06)
- **Stack:** Swift 6, URLSession/async-await, Codable, Keychain, Actor-based services, ServiceContainer DI, KeychainSwift token storage
- **Auth:** Single JWT token (no refresh) via `POST /api/auth/login`; stored in Keychain; validated via `GET /api/auth/me`; auto-logout on 401
- **API contracts:** 40+ endpoints across 7 services; backend uses `JsonStringEnumConverter` (enums as strings, not ints); ISO 8601 dates with fractional seconds; TimeSpan as "HH:MM:SS" strings
- **DTOs:** `CompletePrinterDto` (list endpoint, includes live status) vs `PrinterDto` (detail endpoint, includes serverUrl/apiKey); custom `init(from:)` handles both
- **Resilient decoding:** Custom dual-format ISO8601 decoder (fractional → plain fallback); enum String raw values with fallback; silent error suppression for secondary data loads

### Completed Service Layers

**MVP (2026-03-06):** `APIClient`, `AuthService`, `PrinterService`, `JobService`, `NotificationService`, `StatisticsService`, `SignalRService` — 7 services, 6+ service models

**Push Notifications (2026-07-17):** `PushNotificationManager` (@MainActor @Observable singleton), `AppDelegate` adapter, `NotificationService` extensions (`registerDeviceToken`/`unregisterDeviceToken`)

**Phase 1 Filament/Spool (2026-07-17):** `SpoolService` (CRUD + pagination), `PrinterService` extensions (`setActiveSpool`/`loadFilament`/`unloadFilament`/`changeFilament`), `FilamentModels` (`SpoolmanSpool`/`Filament`/`Vendor`/`Material`), `APIClient.patch()`

**Phase 2 Scanning (2026-03-07):** `SpoolScannerProtocol` abstraction, `QRSpoolScannerService`, `NFCService` (CoreNFC + NFC tag parsing), QR/NFC parsers, ServiceContainer conditional registration

**New Service Layers (2026-03-08):** `MaintenanceService`, `AutoPrintService`, `JobAnalyticsService`, `PredictiveService`, `DispatchService` — 5 services, 30+ DTOs, all registered in ServiceContainer

**Phase 3 Features (2026-03-08):** Spool NFC tag writing (`writeSpoolTag()` method); Predictive Insights graceful empty state (`decodeIfPresent`, optional returns)

### Key Technical Decisions

**Spoolman naming & pagination:**
- Model prefix `Spoolman` to avoid future collisions
- Limit/offset pagination (not page/pageSize)
- `SetActiveSpoolRequest` returns `CommandResult`
- `APIClient.patch()` for updates

**iPad layout architecture:**
- `@Environment(\.horizontalSizeClass)` for adaptive layouts
- `NavigationSplitView` (iPad) vs `TabView` (iPhone)
- Sidebar with explicit `Button`-based rows (`List(selection:)` unavailable on iOS)

**Service layer design:**
- `PredictionRequest` optional fields adapted to match existing ViewModel (not breaking existing code)
- `FleetPrinterStatistics` computed `Identifiable` (`id` backed by `printerId`)
- Date query params use ISO8601Plain format
- Request models are `Encodable`-only (never decoded)

**NFCService Sendable:** Fixed using `nonisolated(unsafe)` rebinding pattern for `@Sendable` closures

### Testing Infrastructure (2026-07-18 → 2026-03-08)
- **Unit tests:** `MockURLProtocol` for in-process mocking; `MockServices` for all protocols; 145+ test cases validating MVP endpoint coverage; 61 test cases for parser contracts (QR/NFC)
- **XCUITest infrastructure:** `MockAPIServer` (NWListener-based TCP server); environment variable injection; wildcard route matching; canned JSON responses; Spoolman test fixtures
- **Build verification:** 33 new files added to `Xcode.pbxproj`

### Known Issues & Resolutions
- **Spoolman "Available" filter (2026-07-18):** Fixed fallback logic in `SpoolmanJsonParser` — was incorrectly setting `inUse=true` for all non-archived spools
- **XCUITest target setup:** Files ready; target creation requires manual Xcode step (deferred to Hudson)

### Pending Work
- OpenTag3D binary encoder for `NFCService.createOpenTag3DPayload(from:)` — stub is in place, Hudson's call site is ready

## Key File Paths (PFarm-Ios)
- Services: `PrintFarmer/Services/` — all service implementations
- Protocols: `PrintFarmer/Services/Protocols/` — service interfaces
- Models: `PrintFarmer/Models/` — Swift DTOs
- API client: `PrintFarmer/Services/APIClient.swift`
- ServiceContainer: `PrintFarmer/Services/ServiceContainer.swift`
- Keychain: via `KeychainSwift` (SPM dependency)

- 2026-05-20: Assigned mobile controls v1 issues #276, #277, #278 (drift cleanup), #280–#283 (services + viewmodel + capability gating foundation), #287 (E2E + SignalR re-sync integration), #289 (testing). See decisions.md "Mobile API Drift + Basic Printer Controls v1".

- 2026-05-20: Issue #277 — Pinned `Printer.progress` 0–100 backend contract → PR #292 (https://github.com/OlyForge3D/PrintFarmer/pull/292, draft). Added 8-case `PrinterProgressContractTests` (`mobile/PrintFarmerTests/Models/`) and clamped `progress` to `[0, 100]` inside `Printer.init(from:)` (`mobile/PrintFarmer/Models/Models.swift`) before normalizing to iOS internal `0…1.0`. Decoder behavior chosen: clamp (not reject/`nil`) — preserves printer card UX when backend overshoots `100.4` or undershoots `-0.0` (observed in production). Dual-scale contract documented: backend wire `0…100`, iOS internal `0.0…1.0`. Follow-up flagged: `DashboardViewModel:50`, `PrinterDetailViewModel:111`/`:141`, `PrinterListViewModel:46` SignalR paths divide by `100.0` without clamping — needs same helper for parity. Local `swift test` blocked (sibling app sources need UIKit / iOS-only SwiftUI; Simulator out of date locally). Relying on CI.

- 2026-05-21: Ralph Round 1 (Phase 0) completed — see `.squad/log/2026-05-21T09-00-00Z-ralph-round-1-phase-0.md`.

### 2026-05-21: Issue #278 — Removed dead int-branch decoders (string-only enums)
- Simplified `init(from:)` for 5 enums in `mobile/PrintFarmer/Models/Models.swift`: `PrinterBackend`, `MotionType`, `PrintJobStatus`, `PrintJobPriority`, `AutoDispatchState`. Each now decodes string-only with the same fallback default.
- Backend wire format confirmed string for all five: `MotionType` and `AutoDispatchState` rely on global `JsonStringEnumConverter` (registered in `ControllerStartup` + `SignalRStartup`); `PrinterBackend` and `PrintJobStatus` have permissive *read* converters but their `Write()` always emits string. `PrintJobPriority` is wire-serialized as raw `int` on `PrintJobDto.Priority`, so the Swift Codable enum is never invoked from real payloads — the `PrintJobPriority.from(intValue:)` helper IS still called from `JobDetailView`/`JobListView` and was preserved.
- `SignalRModels.swift`'s `AnyCodable.init(from:)` Int branch left in place — that wrapper handles heterogeneous JSON values, not enum fields.
- Verified with `swiftc -typecheck` on all `Models/*.swift`. Full Xcode build/test not runnable in this env (CoreSimulator out of date, iOS 26.5 SDK not installed).

### 2026-05-21: Issue #282 — PrinterControlsViewModel (PR #298)
- Built `mobile/PrintFarmer/ViewModels/PrinterControlsViewModel.swift` (~230 lines, Swift 6, `@MainActor` `ObservableObject`) + 14 XCTest cases in `mobile/PrintFarmerTests/ViewModels/PrinterControlsViewModelTests.swift`. Branch `squad/282-controls-viewmodel`, worktree `/Users/jpapiez/s/PFarm1-282`, draft PR https://github.com/OlyForge3D/PrintFarmer/pull/298 base `development`.
- Public surface: `PreheatPreset` (.pla=200/60, .petg=240/80, .abs=240/100, .coolDown=0/0), `ControlCommand { kind, startedAt }`, `ControlsError { command, message, isRetryable }`. `@Published private(set) capabilities/lastError/pendingCommand/isLoadingCapabilities`. Methods: `loadCapabilities`, `preheat`, `homeAll/XY/Z`, `jog(axis:distanceMm:)`, `dismissError`, `handlePrinterUpdate(_:)`. Computed: `canControl`, `blockedReason`. Constants: XY feedrate 3000, Z feedrate 600 mm/min.
- **Capability fetch errors are silent** — falls back to `PrinterBackendCapabilities.fallback(for:)` rather than surfacing via `lastError`. Capabilities are a backend probe, not a user action; surfacing fetch failure would block all commands behind a transient error.
- **`pendingCommand` only cleared by SignalR `handlePrinterUpdate(_:)` or by failure** — successful API call leaves it set so the spinner persists until the printer actually responds. SignalR is the source of truth for "command effect complete".
- **Bed temp silently dropped when `!supportsBedTemperature`** (FlashForge etc.). `coolDown` always sends 0/0 regardless — turning off is universal.
- **Single-flight, no queue** — second concurrent command returns silently. UI uses `pendingCommand != nil` to disable buttons; this is a safety net.
- **Test hook** added to `mobile/PrintFarmerTests/Mocks/MockPrinterService.swift`: `var beforeSetTemperatures: (@Sendable () async -> Void)?` invoked at top of `setTemperatures`. Single-flight test uses an `AsyncGate` actor to suspend the first call while firing the second.
- **pbxproj patching**: 4 anchor points per Swift file (PBXBuildFile, PBXFileReference, group children, Sources phase). Used `PrinterDetailViewModel(.swift|Tests.swift)` references as anchors. New IDs: `A1C5DEF1234567890ABC0001`–`0004`. `grep -c PrinterControlsViewModel project.pbxproj` → 8.
- **Validation gap**: local `xcodebuild test` not run — CoreSimulator/iOS SDK still broken locally. Relying on CI.


- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291, #292, #293, #294, #295, #296, #297, #298). See `.squad/log/2026-05-21T08-15-00Z-ralph-rounds-2-5-phase-1-complete.md`. Phase 2 launching (#284 preheat, #285 home, #286 jog).

### 2026-05-28: Issue #280 — PrinterBackendCapabilities model + service (PR #2)
- **Endpoint confirmed present**: `GET /api/printers/{printerId}/backend-capabilities` exists in `PrintersController.cs`. No follow-up GitHub issue required.
- **Backend DTO fields decoded**: `printerId`, `printerName`, `backend`, `supportsMovement`, `supportsTemperatureControl`, `supportsControlOperations`, `supportsCamera`, `supportsFileList`, `supportsFileUpload`, `supportsFileDownload`, `supportsStartPrint`, `supportsFileMetadata`, `supportsPrinterInformation`, `supportsHistory`, `supportsFilamentControl`.
- **Derived fields**: `supportsBedTemperature = supportsTemperatureControl` (locked decision); `supportsFanControl = supportsControlOperations`.
- **Fallback table** (keyed on `PrinterBackend`): Moonraker/PrusaLink/OctoPrint → full FFF (movement+temp+control); FlashForge → movement+temp, no fan/camera; SDCP → resin (movement=false, temp=false); Unknown → conservative all-false.
- **Service fallback path**: `NetworkError.notFound` or `DecodingError` → calls `get(id:)` then `fallback(for:backend)`.
- **Key files**:
  - `mobile/PrintFarmer/Models/PrinterBackendCapabilities.swift` — new model
  - `mobile/PrintFarmer/Protocols/PrinterServiceProtocol.swift` — added `getBackendCapabilities(printerId:)`
  - `mobile/PrintFarmer/Services/PrinterService.swift` — implementation with fallback
  - `mobile/PrintFarmerTests/Models/PrinterBackendCapabilitiesTests.swift` — 15 XCTest cases
- **Build note**: `swiftc -typecheck` clean. Local `xcodebuild` unavailable (CoreSimulator out of date). Relying on CI. PR: https://github.com/OlyForge3D/PrintFarmerMobile/pull/2

## Cross-Team Note (2026-05-29)

**Dallas** (#290 status-gating) complete: API guards for `/temps`, `/move`, `/moveto` live via PR #308. Status-gating orthogonal to capabilities.
**Newt** (#283 design spec) complete: Design decisions locked. UI gating ready to use capabilities endpoint.
**Unblocked:** Fallback table canonical; endpoint `GET /api/printers/{printerId}/backend-capabilities` confirmed live. PR OlyForge3D/PrintFarmerMobile#2 awaiting CI.

## Learnings

### 2026-05-28: Issue #278 — Dead int-branch decoders removed (PR OlyForge3D/PrintFarmerMobile#10)

**Files where dead int branches lived:**
- `mobile/PrintFarmer/Models/Models.swift` — 5 enums each had a `else if let num = try? container.decode(Int.self)` block:
  - `PrinterBackend` (cases 0–5, default `.unknown`)
  - `MotionType` (cases 0–2, default `.unknown`)
  - `PrintJobStatus` (cases 0–7, default `.queued`)
  - `PrintJobPriority` (cases 0–3, default `.normal`)
  - `AutoDispatchState` (cases 0–3, default `.none`)
  - Total: 45 lines of dead code deleted.

**No tests deleted.** Test fixtures using `"priority": 1` decode into `PrintJob.priority: Int` and `QueuedJobInfo.priority: Int` — plain `Int` fields, not `PrintJobPriority` enum. The enum decoder is never invoked by those fixtures.

**`PrintJobPriority.from(intValue:)` kept** — it is a UI helper converting the stored `Int` priority to a display enum in `JobDetailView` and `JobListView`. Not a JSON decode path.

**`AnyCodable` in `SignalRModels.swift` not touched** — its int branch is correct; it is a heterogeneous JSON value wrapper.

**Issue relabelling:** Removed `squad:🔧 lambert` (mis-routed, Lambert is backend-only), added `squad:gorman`.

**Branch contamination recurred** — commit initially landed on `squad/284-preheat-subgroup`. Recovered via `git cherry-pick` onto `squad/278-remove-dead-int-decoders`, then deleted the contaminated branch. Always verify `git branch` immediately before `git add/commit`.

### 2026-05-31: Issue #282 — isExecuting property gap (PR #419)
- Code was already on development from Phase 1 (PR #298, 2026-05-21). Issue remained open because target was development not default branch (no auto-close).
- **Gap identified**: AC required @Published var isExecuting: Bool; VM exposed pendingCommand: ControlCommand? only.
- **Fix**: Added ar isExecuting: Bool { pendingCommand != nil } in Computed section of PrinterControlsViewModel.swift — read-only Bool alias backed by the already-@Published pendingCommand.
- **Test added**: 	est_isExecuting_trueWhileInFlight_falseAfterError (15th test) — uses AsyncGate to assert isExecuting=true mid-flight, isExecuting=false after error clears pendingCommand.
- Branch: squad/282-controls-viewmodel; PR: https://github.com/OlyForge3D/PrintFarmer/pull/419
### 2026-05-31: Issues #280 + #281 — Wire DTO decoder tests + getBackendCapabilities service tests (PR #417)
- **Model + service already on development**: PrinterBackendCapabilities.swift, PrinterServiceProtocol.swift (getBackendCapabilities, setTemperatures, home, homeXY, homeZ, move), PrinterService.swift implementations all present.
- **Issue #280 AC gap**: 'Decoder test covers full-support, partial-support, and resin (movement=false) fixtures' was missing.
  - Added 4 wire DTO decoder tests to PrinterBackendCapabilitiesTests.swift (9→13):
    - testWireDto_fullSupport_moonraker
    - testWireDto_partialSupport_flashForge
    - testWireDto_resin_sdcp_movementFalse (critical: supportsMovement=false path)
    - testWireDto_missingOptionalFields_decodedAsNil
  - Added 3 PrinterService.getBackendCapabilities tests to PrinterServiceTests.swift (29→32):
    - testGetBackendCapabilities_happyPath_returnsMergedCapabilities
    - testGetBackendCapabilities_404_fallsBackToStaticTable (Moonraker fallback)
    - testGetBackendCapabilities_resin_sdcp_movementFalse (SDCP all-false fallback)
- **Issue #281**: All 11 happy-path tests already present on development (setTemperatures x3, home x3, homeXY/homeZ wrappers, move x3).
- **Branch**: squad/280-281-printer-controls-networking; **PR**: https://github.com/OlyForge3D/PrintFarmer/pull/417
- **Build note**: swiftc -typecheck not runnable locally (CoreSimulator out of date). Relying on CI.

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

### 2026-05-28: Issue #281 — PrinterService command methods (PR #4)

- **Endpoints confirmed** (`PrintersController.cs`):
  - `POST /api/printers/{id}/temps` — body `{ "hotend": double, "bed": double }` (both fields; TempTargets C# record). 409 gate (`GatePrinterControlAsync`) applies.
  - `POST /api/printers/{id}/home` — no body, homes all axes. **No 409 gate.**
  - `POST /api/printers/{id}/homexy` — no body, dedicated endpoint. **No 409 gate.**
  - `POST /api/printers/{id}/homez` — no body, dedicated endpoint. **No 409 gate.**
  - `POST /api/printers/{id}/move` — body `{ "x"?, "y"?, "z"?, "f"? }` (MoveRequest C# record, all nullable doubles). 409 gate applies.
- **409 handled by existing `NetworkError.conflict`** — `APIClient` already maps HTTP 409 → `.conflict`; no new error type needed.
- **`home(axes:)` dispatches to three routes** — sorted axes comparison: `["X","Y"]` → `/homexy`, `["Z"]` → `/homez`, else → `/home`. `homeXY`/`homeZ` are protocol extension defaults.
- **`setTemperatures` nil-omit via custom Encodable** — private `SetTemperaturesRequest` struct with custom `encode(to:)` that conditionally encodes each field. Pattern: `if let hotend { try container.encode(hotend, forKey: .hotend) }`.
- **`move` body via `[String: Double]`** — naturally omits unused axes; keyed by `axis.lowercased()` + `"f"` feedrate. Callers pass locked feedrates (3000 XY, 600 Z).
- **`DemoPrinterService` gap fixed** — `getBackendCapabilities` was missing from demo (overlooked in #280); added in this PR.
- PR: https://github.com/OlyForge3D/PrintFarmerMobile/pull/4

## Cross-Team Note (2026-05-29)

**Dallas** (#290 status-gating) complete: API guards for `/temps`, `/move`, `/moveto` live via PR #308. Status-gating orthogonal to capabilities.
**Newt** (#283 design spec) complete: Design decisions locked. UI gating ready to use capabilities endpoint.
**Unblocked:** Fallback table canonical; endpoint `GET /api/printers/{printerId}/backend-capabilities` confirmed live. PR OlyForge3D/PrintFarmerMobile#2 awaiting CI.

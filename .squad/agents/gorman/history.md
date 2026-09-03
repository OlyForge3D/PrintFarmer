# Gorman — iOS Networking & API Integration History

> Migrated from PFarm-Ios `.squad/agents/lambert/` on 2026-05-20 when the iOS squad was merged into the shared PrintFarmer uber-team.

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

## 2026-09-03: iOS Navigation Redesign — Farm-Shape API (1 child issue)

Assigned to backend API endpoint for farm-shape data exposure.

**Epic**: #2410 — iOS Navigation Redesign
**Assigned issue**: #2415 (farm-shape API endpoint)
**Role**: Backend API — PlatformCapabilitiesDto.farmShape field, anonymous redaction, cache headers
**Status**: PENDING (awaiting implementation start)

## 2026-09-03: Epic #2410 — iOS Navigation Redesign (overview)

**Direction**: A′ · Two Hats, adaptive shell architecture approved.
- Simple Shell (default): 4 tabs
- Two-Modes Shell (staffed): Floor + Oversight modes
- Farm-shape API endpoint assigned to Gorman
- 17 child issues, 8-agent team

**Reference**: Decisions recorded in `.squad/decisions.md` (2026-09-03 entry).

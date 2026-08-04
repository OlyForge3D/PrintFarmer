# Gorman History — Archive

This archive contains work entries from prior to 2026-05-15 for historical reference.

For current work, refer to `history.md`.

---

## Archived iOS Networking Work (pre-2026-05-20)

Various iOS API integration, service layer development, and push notification infrastructure work from early 2026. 

- Early networking foundation
- Service layer architecture
- Testing infrastructure setup
- Mobile app migration integration

Detailed entries have been summarized; refer to git history for full context.

---

*Archive created 2026-06-02 to maintain history.md size management.*

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

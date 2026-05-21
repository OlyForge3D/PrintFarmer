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

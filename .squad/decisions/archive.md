
# Gorman — Issue #280: hybrid backend-capabilities (endpoint + static fallback)

**Date:** 2026-05-21
**Agent:** Gorman (iOS Networking)
**PR:** https://github.com/OlyForge3D/PrintFarmer/pull/295
**Issue:** #280

## What

iOS `PrinterService.getBackendCapabilities(printerId:)` resolves the new `PrinterBackendCapabilities` model via a **hybrid** path:

1. GET `/api/printers/{id}/backend-capabilities` and decode the wire DTO.
2. Read `backend` (PrinterBackend) from the response, look up `PrinterBackendCapabilities.fallback(for: backend)` as the base.
3. Overlay the API's authoritative `supportsMovement` and `supportsTemperatureControl` on top of the fallback.
4. The remaining four fields (`supportsBedTemperature`, `supportsFanControl`, `supportsHoming`, `supportedAxes`) come entirely from the static fallback table — the backend DTO does not currently expose them.
5. On `.notFound` / `.serverError`, fetch the printer and return `fallback(for: printer.backend)` alone.
6. Cached in-memory in the `PrinterService` actor by `UUID`.

## Why this matters team-wide

- **Backend-side follow-up:** the iOS side now consumes `supportsBedTemperature`, `supportsFanControl`, `supportsHoming`, `supportedAxes` as if they were first-class fields. When the backend `PrinterBackendCapabilitiesDto` grows them, the wire decode will pick them up automatically and the overlay can tighten in one line. No iOS migration needed.
- **Static fallback table is the contract** until the API catches up. Backend changes that contradict the table (e.g. introducing a flag that says SDCP supports homing) need to update both the API DTO and the iOS table.
- **Wire DTO field naming:** iOS decodes `printerId`, `backend`, `supportsMovement`, `supportsTemperatureControl` plus all the other capability bools (forward-compat). Any rename on the backend will silently break decode — coordinate via this decision file.

## Static fallback table (authoritative for the four missing fields)

| Backend | Movement | Temp | Bed | Fan | Homing | Axes |
|---|---|---|---|---|---|---|
| Moonraker, PrusaLink, OctoPrint | ✓ | ✓ | ✓ | ✓ | ✓ | X,Y,Z |
| FlashForge | ✓ | ✓ | – | – | ✓ | X,Y,Z |
| SDCP | – | – | – | – | – | (none) |
| Unknown | – | – | – | – | – | (none) |

## Surfaces

- New: `mobile/PrintFarmer/Models/PrinterBackendCapabilities.swift`
- New: `mobile/PrintFarmerTests/Models/PrinterBackendCapabilitiesTests.swift` (8 cases)
- Edited: `PrinterServiceProtocol.swift`, `PrinterService.swift`, `DemoPrinterService.swift`, `MockPrinterService.swift`

## Conventions confirmed (for future iOS PRs)

- `PrinterService` methods take `UUID`, not `String`, regardless of issue spec wording.
- Pbxproj registration is deferred for new files; `Package.swift` SPM paths auto-discover them. CI uses `swift test`. Xcode users may need to drag files in manually until a coordinated pbxproj sweep.
- Pure-model fallback tables get pure-model XCTest cases (no `MockURLProtocol`).


---

# Decision: PrinterControlsViewModel public contract (#282)

**Author:** Gorman
**Date:** 2026-05-21
**Issue:** #282
**PR:** https://github.com/OlyForge3D/PrintFarmer/pull/298
**File:** `mobile/PrintFarmer/ViewModels/PrinterControlsViewModel.swift`

This freezes the public surface so Hudson can build views without reading source.

## Public types

- `PreheatPreset` — `.pla` (200/60), `.petg` (240/80), `.abs` (240/100), `.coolDown` (0/0). `coolDown` always sends 0/0 regardless of capabilities.
- `ControlCommand { kind: Kind, startedAt: Date }` with `Kind`:
  - `.preheat(PreheatPreset)`
  - `.home(axes: [String])` — uppercase axis names
  - `.jog(axis: String, distanceMm: Double)`
- `ControlsError { command: ControlCommand, message: String, isRetryable: Bool }`

## Constants

- `static let xyFeedrateMmMin: Int = 3000`
- `static let zFeedrateMmMin: Int = 600`

## Published state (all `@Published private(set)`)

- `capabilities: PrinterBackendCapabilities?`
- `lastError: ControlsError?`
- `pendingCommand: ControlCommand?`
- `isLoadingCapabilities: Bool`

## Init

`init(printerService: PrinterServiceProtocol, printer: Printer, clock: @escaping () -> Date = Date.init)`

## Methods

- `func loadCapabilities() async` — single-fetch cache; on error falls back to `PrinterBackendCapabilities.fallback(for: printer.backend)` **silently** (does not set `lastError`).
- `func preheat(_ preset: PreheatPreset) async` — gated on `supportsTemperatureControl` (except `.coolDown`); bed value silently dropped if `!supportsBedTemperature`.
- `func homeAll() async` / `func homeXY() async` / `func homeZ() async` — gated on `supportsHoming`.
- `func jog(axis: String, distanceMm: Double) async` — gated on `supportsMovement`. Axis uppercased. Feedrate: Z → 600, else 3000 mm/min.
- `func dismissError()` — clears `lastError`.
- `func handlePrinterUpdate(_ updated: Printer)` — SignalR hook. Replaces internal printer **and clears `pendingCommand`**.

## Computed

- `canControl: Bool` — `printer.isOnline && !isPrintingOrPaused`.
- `blockedReason: String?` — `"Printer is offline."` | `"Controls are locked while a print is active."` | `nil`.
- `isPrintingOrPaused` matches state strings `"printing"`, `"paused"`, `"starting"` (case-insensitive).

## Behavioral contract (do not change without coordination)

1. **SignalR is the truth.** `pendingCommand` is set when a command begins and is **only cleared by `handlePrinterUpdate(_:)` (SignalR) or by command failure**. A successful API return leaves `pendingCommand` set so the spinner persists until the printer actually responds.
2. **Single-flight, no queue.** A second command issued while `pendingCommand != nil` returns silently with no error and no state change. UI must disable controls based on `pendingCommand != nil`.
3. **Capabilities never block UX.** Fetch failures fall back silently. Per-command capability gates short-circuit (no API call, no error) when the backend doesn't support the action.
4. **Error mapping** (`static func mapError(_:) -> (message: String, isRetryable: Bool)`):
   - 5xx / conflict / network → `isRetryable = true`
   - 4xx → `isRetryable = false`
5. **No automatic retry.** UI surfaces `lastError`; user retries by reissuing the command (which is now allowed because `pendingCommand` was cleared on failure).


---

## Decision: Round 17 — Newt PR #15 integration plan, Brett PR #14 snapshot spike

**Date:** 2025-11-23
**Authors:** Newt (iOS design/integration), Brett (research/snapshot strategy)
**Status:** Prep PRs opened in PrintFarmerMobile

### Summary

- **Newt PR #15 (integration plan):** Composition strategy finalized — `controlsSection()` private helper on `PrinterDetailView` (matches `actionSection` convention), placed after `actionSection`. Single `@State var controlsViewModel: PrinterControlsViewModel`, lazy-injected via `.task` based on printer ID + caps. Hudson scope: ~40 lines `PrinterDetailView.swift` + ~10 lines `PrinterControlsViewModel.swift` additions; subgroup files (Preheat, Home, Jog) ship complete from #11–#13 stack. **PR:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/15
- **Brett PR #14 (snapshot spike):** Recommends `swift-snapshot-testing` (pointfreeco) via SPM for snapshot regression tests. 8-snapshot matrix: Moonraker/FlashForge/SDCP × {blocked, in-flight, error, dark-mode, iPhone SE}. Biggest risk: simulator OS version drift — CI must pin simulator OS version to match baseline environment. **PR:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/14

### Unblocked Decisions

**Newt integration pattern locked:** `controlsSection()` composition allows independent subgroup testing + future component reuse. No blocking review feedback.
**Brett snapshot strategy validated:** pointfreeco library meets framework requirements (Xcode/iOS 15+ compatible, SPM distribution). Recommend pinning simulator OS version in CI YAML.

---

## Decision: Round 22 — Bishop CR #318 architectural blockers; Parker dependabot triage

**Date:** 2026-05-21
**Authors:** Bishop (architectural review), Hicks (context), Parker (dependabot triage)
**Status:** ✅ DOCUMENTED; PR #318 blockers identified; dependabot pattern catalogued

### Summary

**Bishop REQUEST_CHANGES PR #318 (real-transport tests):**
- ❌ **Architectural blocker 1:** `PrinterBackendBusy` exception does **NOT** map to HTTP 409 in current code. `PrintersService` maps it to `BackendBusy` outcome, and `PrintersController.MapControlOutcome()` returns **502 BadGateway**, not 409 Conflict. PR's firmware-409 propagation premise is undermined without fixing the controller-side mapping first.
- ❌ **Architectural blocker 2:** Moonraker 503 (Service Unavailable) for Klippy unavailable/error states diverges from tighter OctoPrint/PrusaLink 409-only convention. Introduces wider "busy" semantic (not just busy-printing). Requires spec alignment before landing.
- ⚠️ **Non-blocking:** Real-transport tests have minor `GetFreeTcpPort()` race risk in CI (ephemeral port collisions on parallel runs).
- **Comment:** https://github.com/OlyForge3D/PrintFarmer/pull/318#issuecomment-4570616436
- **Hicks context:** PR #318 was APPROVE'd by Hicks before Bishop caught cross-layer mapping disconnect. Real-transport test coverage is good; architectural translation is not.

**Parker dependabot triage (2026-05-21, artifact `.squad/parker/triage-2026-05-21.md`):**
- 9 open PRs, all CI green.
- 2 safe auto-merge: #235 (FluentAssertions 6→7 test lib), #238 (Mvc.Testing 10→11).
- 3 need verification: #239 (System.Text.Json), #271 (System.Reflection.Metadata), #272 (System.ComponentModel.Annotations) — patch bumps on runtime libs.
- 5 need manual review: #240–244 (GitHub Actions majors: node, setup-dotnet, etc.).
- **Recommendation:** Jeff merge #235 + #238; build-test #239/#271/#272 for regression; changelog-check #240–244 before gh-actions updates.

### Durable Rule Captured

**Rule 7 — Two-reviewer consensus on backend cross-layer changes (effective immediately):**
- Backend PR's that span service-layer logic + controller-layer translation (HTTP mapping, error code propagation) require architectural sign-off from **two reviewers**.
- Single-voice approval insufficient; cross-layer disconnect (e.g., exception → outcome → HTTP code translation) is highest-risk refactoring class.
- **Applies to:** PrintersService → PrintersController exception/outcome flows; payment/subscription domain chains; worker-slicer routing layers.
- **Hicks lesson:** Individual diff review (service tests) sometimes misses downstream controller mapping. Always pair with second reviewer checking translation boundary.
- **Bishop lesson:** Architectural consistency (409 for firmware conflicts across backends) is enforcer role; single code-path approval can mask system-wide assumptions.

## Decision: main→development Sync Pattern and Disambiguation

**Date:** 2026-05-29  
**Author:** Parker  
**Status:** Documented

### Context

Performed corrective sync of `main` → `development` to pick up Dependabot security fixes (27 commits). Initial misread created wrong-direction branch (`sync/dev-to-main-2026-05-29`), which was deleted locally and remotely.

### Decision

**Establish and document main→dev sync as a distinct operational pattern.**

1. **Directional clarity for future requests:**
   - Use "main → development" as the canonical phrasing for pulling security fixes and upstream changes into the development branch
   - Use "development → main" (rare) only when explicitly requested; this is high-risk and requires explicit confirmation
   - When user says "sync X," always clarify target: "sync X into main?" or "sync X into development?"

2. **Conflict resolution strategy for main→dev:**
   - **Keep development versions** for:
     - All `.squad/` files and squad infrastructure (development owns these; main stripped them)
     - `.squad/templates/*` file locations (resolve location conflicts in favor of dev's structure)
   - **Keep main versions** for:
     - Dependency manifests (`.csproj`, `Directory.Packages.props`, package.json, etc.) — security fixes live here
     - `.gitignore` — main has cumulative changes
     - `.github/workflows/` — main has authoritative workflow versions
     - Scripts — main is canonical

3. **Automation opportunity:**
   - The existing `release.sh` script performs a similar (but opposite-direction) sync with stripping in one command
   - Consider creating a complementary `sync-main-to-dev.sh` script that:
     - Fetches latest, creates branch from `origin/development`
     - Merges `origin/main` with `--no-ff`
     - Automates conflict resolution (ours for `.squad/`, theirs for deps/workflows)
     - Validates clean state and opens PR
   - This would prevent misreads and reduce manual conflict resolution

### Related PR

- **PR #321:** `sync/main-to-dev-2026-05-29` → `development`
- **Commits synced:** 27
- **Status:** Opened, CI pending

### Learnings

- Wrong-direction branches are recoverable (local + remote delete); better to disambiguate upfront
- Main→dev is a common, low-risk operational pattern (all green if no application regressions)
- Dev→main is rare, high-risk, and requires explicit approval (gathers work for production release)
- Workflow scope token requirement applies to both directions; plan ahead

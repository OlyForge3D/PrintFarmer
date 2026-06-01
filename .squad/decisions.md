## Camera Management Endpoint Detection and Association UI (2026-05-26T09:45:35.148-07:00)

**Decision:** Camera management now treats printer association and endpoint discovery as first-class camera-editing workflows.

**Owner(s):** Lambert (Backend), Ripley (Frontend)

**Status:** Implemented on `development` in commits `384868e28`, `353cd7ecb`, and earlier Ripley commit `f0589aec0`.

### Backend Contract

- Added `POST /api/cameras/detect-endpoints` with request `{ "printerId": "<guid>" }`.
- Success response uses camelCase camera endpoint fields: `streamUrl`, `snapshotUrl`, `detected`, and `source`.
- Missing printers return `404`; unsupported backends and probe failures return `200` with `detected: false`.
- Added `IPrinterCameraProbe` in the discovery layer and concrete Moonraker/Klipper, OctoPrint, and SDCP/Elegoo probes.
- `CameraDto` now includes `printerId` and `printerName` so list/get/update responses can show linked printers.

### Frontend UX

- Camera cards expose farm-admin Edit and Delete actions using shared modal components.
- Edit Camera includes an Associated Printer dropdown and Detect Endpoints button.
- Detected endpoints populate Stream URL and Snapshot URL fields for the selected printer.
- Camera management table now includes a Printer column using linked `printerName`.
- Camera preview media uses `object-contain bg-black` so stream frames are not zoomed or cropped in fixed-aspect cards.

### Validation

- Ripley earlier dispatch: build, lint, and focused camera tests passed.
- Ripley-1: `npm run build` and `npm run lint` passed; no affected component tests existed.
- Lambert: restore and API build passed; focused camera tests passed. Full suite/format had pre-existing unrelated failures.

### Follow-up

- Add concrete endpoint probes for PrusaLink/Buddy companion cameras, FlashForge, and any future Bambu backend once backend-specific camera contracts are known.

---

## Decision: Printer Offline Classification (lambert-1, 2026-05-26)

Moonraker/Klipper online state for list/detail surfaces is cached by `MoonrakerSubscriptionService` and served by `PrintersService.GetAllCompleteDtosAsync`.

- Treat explicit Moonraker `webhooks.state != ready` as not-ready/offline, but do not require `webhooks` to be present on every subscription/status payload.
- A successful Moonraker status payload containing printer objects (`toolhead`, `print_stats`, `display_status`, etc.) proves the printer is reachable and should keep `IsOnline=true`.
- Transport failures, exhausted reconnect attempts, `notify_klippy_disconnected`, and `notify_klippy_shutdown` remain the paths that mark the printer offline.
- HTTP polling fallback must update `PrinterStatusCache`, not just SignalR, so REST clients and mobile clients do not read stale status.

---

## Decision: arco1 Runtime Evidence — List vs. Detail Cache Discrepancy (lambert-probe2, 2026-05-26)

UI `/printers` shows `ARCO1` as `Offline`, but API detail endpoint shows `isOnline: true` for the same printer. Direct Moonraker is reachable.

**Diagnosis:** The bad data is not Moonraker. Strongest inconsistency is inside PrintFarmer API/status composition: the list endpoint has stale or misclassified `isOnline: false` while the detail endpoint has `isOnline: true` moments later.

**Root cause candidate:** `src/backends/Farm.Backend.Plugin.Moonraker/MoonrakerSubscriptionService.cs` around `_klippyReadyState`, `EmitConsolidatedStatusAsync`, and offline updates, plus list endpoint merge logic that combines persisted printer rows with `PrinterStatusCache`.

**Artifacts:** captured under `arco1-probe2/` (printers-page.png, dashboard.png, arco1-detail/list JSON, moonraker endpoint responses, SignalR frames).

---

## Decision: Login Audit Log Backend (lambert-2, 2026-05-26)

**Status:** Implemented — awaiting review. Migrations committed for Postgres + SqlServer.

Added dedicated `LoginAuditEntry` table with `Username`, `IpAddress`, `UserAgent`, `Success`, `Timestamp`, `FailureReason` (indexed columns for fast queryable audit).

### API Contract

`GET /api/admin/security/login-audit` (requires `farm_admin` role).

Query params: `from` / `to`, `username` (substring), `success` (bool), `page` / `pageSize` (default 50, max 200).

Response: paginated `{ items: LoginAuditDto[], totalCount, page, pageSize }`.

### Hook Point

`AuthController.LoginAsync` — captures raw HTTP context (IP, User-Agent) at controller level.

### TODOs

- **Retention policy**: No cleanup job; recommend 30/90-day trim.
- **Rate-limit correlation**: Future work with `AuthenticationRateLimitMiddleware`.
- **Ripley UI**: See `ripley-2` decision below.

---

## Decision: Login Audit Log UI (ripley-2, 2026-05-26)

**Status:** Implemented on `development`. 23 tests passing.

Built `/admin/security/login-audit` page using project's Tailwind components (`Badge`, `DataTable`, `Tooltip`, `Select`, `Input`).

### Key Decisions

1. **UI library:** Project's custom `@/common/components/ui` (consistency with other admin pages).
2. **Navigation:** Added "Security" section header in admin nav as peer to "Settings".
3. **Tri-state success filter:** URL param stores `''` (all), `'true'` (success only), `'false'` (failure only).
4. **Filter state:** Batch updates with `setMany({ ...update, page: 1 })`; debounced username field via individual setter.
5. **API:** Direct `apiClient.get<T>()` in `securityAuditService.ts` (avoids modifying shared `api.ts` until pattern is stable).

---

# Decision: PrinterControlsViewModel Command Queue Design

**Date**: 2026-05-28  
**Author**: Gorman (iOS Networking & API Integration)  
**Issue**: [#282](https://github.com/OlyForge3D/PrintFarmerMobile/issues/282) — [iOS] Create PrinterControlsViewModel  
**PR**: [#7](https://github.com/OlyForge3D/PrintFarmerMobile/pull/7)  
**Status**: Implemented

---

## Context

`PrinterControlsViewModel` needs to serialize outbound printer commands (set temps, home, jog) so that rapid UI taps (tap-storm) don't fire multiple simultaneous HTTP calls to the same printer endpoint. The printer backend gates `/temps` and `/move` with HTTP 409 Conflict when a prior command is still in-flight.

Two approaches were evaluated: a dedicated `actor CommandQueue`, and a **Task-chain**.

---

## Options Considered

### Option A: Dedicated `actor CommandQueue`

```swift
actor CommandQueue {
    private var running: Task<Void, Never>?

    func enqueue(_ command: @escaping @Sendable () async throws -> Void) async {
        let prev = running
        running = Task {
            await prev?.value
            try? await command()
        }
        await running?.value  // caller awaits
    }
}
```

**Pros**: Strong isolation guarantee; actor protects its own state.  
**Cons**:
- All command-wrapper methods (`setTemperatures`, `home`, `move`, …) must become `async` since callers `await enqueue(...)`.
- This changes the view-layer API contract: SwiftUI `Button` closures can't `await` directly; they need `Task { await vm.move(...) }` wrappers everywhere.
- The ViewModel is already `@MainActor`-isolated — adding a second actor boundary adds hop overhead without concurrency benefit.
- Testing requires `await vm.move(...)` at every call site instead of fire-and-forget with a single `await vm.drainQueue()`.

### Option B: Task-chain (chosen)

```swift
private func enqueue(_ command: @escaping @Sendable () async throws -> Void) {
    let previousTail = queueTail
    queueTail = Task {
        await previousTail?.value   // wait for previous command
        guard !Task.isCancelled else { return }
        isCommandInFlight = true
        do { try await command() } catch { lastError = Self.userFacingMessage(for: error) }
        isCommandInFlight = false
    }
}
```

**Pros**:
- Command wrappers remain **synchronous** — `vm.move(...)` is a fire-and-forget call; view layer needs no `Task {}` wrappers.
- FIFO ordering guaranteed: each new task awaits the previous tail before starting.
- Cancel-on-deinit: `queueTail?.cancel()` tears down the chain when the ViewModel deallocates.
- No actor hop: everything runs on `@MainActor`; service calls suspend off MainActor via structured concurrency.
- Tests use `await vm.drainQueue()` (a single `await queueTail?.value`) to synchronize after any number of enqueues.

**Cons**:
- `isCommandInFlight` goes `false` briefly between consecutive commands (during the `await previousTail?.value` suspension). This is acceptable for the controls UI (aggregate indicator).
- `queueTail` must be `@ObservationIgnored nonisolated(unsafe)` to allow `deinit` access (see Swift 6 note below).

---

## Decision

**Task-chain** (Option B).

The synchronous call-site API is the deciding factor. SwiftUI button handlers are synchronous by design; making `move()`/`home()` async would require `Task { await vm.cmd() }` at every call site across Hudson's upcoming views (#284-286). The Task-chain avoids this entirely.

---

## Swift 6 Implementation Note: deinit Access

In Swift 6, `deinit` on a `@MainActor final class` is **not** automatically MainActor-isolated. Accessing `queueTail` from `deinit { queueTail?.cancel() }` raises:

> "main actor-isolated property 'queueTail' can not be referenced from a nonisolated context"

The fix requires **both** annotations:

```swift
@ObservationIgnored
nonisolated(unsafe) private var queueTail: Task<Void, Never>?
```

- `@ObservationIgnored` — prevents the `@Observable` macro from wrapping the property in `_$observationRegistrar`. Without this, `nonisolated(unsafe)` has no effect (the macro's synthesized accessors remain MainActor-isolated).
- `nonisolated(unsafe)` — declares that deinit (nonisolated context) may access the property. Safe here because `Task.cancel()` is a `Sendable`-safe operation callable from any concurrency context, and all other access to `queueTail` is strictly on the MainActor via `enqueue()` and `drainQueue()`.

---

## isCommandInFlight: Aggregate vs Per-Command

**Aggregate** (single `Bool`) was chosen.

Per-command tracking (`[CommandType: Bool]`) would require:
1. A `CommandType` enum covering all five command methods.
2. Additional state in `enqueue(_:)` to key the flag.
3. View layer knowledge of which command type is in flight.

The aggregate flag is sufficient for the controls UI: buttons are disabled while any command runs, and `lastError` identifies which command failed. Hudson can request per-command tracking in #284-286 if the design requires it.

---

## Conflict Error UX Convention

`NetworkError.conflict` (HTTP 409 — printer is busy executing a prior command) maps to:

```
"The printer is busy — please wait a moment and try again."
```

All other errors use `error.localizedDescription`.

**Rationale**: The generic `NetworkError.conflict.errorDescription` is `"Conflict — resource was modified"` — a developer-facing string that references HTTP semantics the user doesn't understand. The custom string is actionable: "wait and retry." Future ViewModels that enqueue printer commands should adopt this same string via a shared `PrinterControlsViewModel.userFacingMessage(for:)` call or a copy of the pattern.

---

## Files

| File | Purpose |
|---|---|
| `PrintFarmer/ViewModels/PrinterControlsViewModel.swift` | ViewModel implementation |
| `PrintFarmerTests/ViewModels/PrinterControlsViewModelTests.swift` | 14 XCTest cases |

---

## Test Coverage

| Scenario | Test Method |
|---|---|
| Capability cache loads and caches | `testLoadCapabilities_cachesResult` |
| Re-call refreshes from server | `testLoadCapabilities_recallRefetches` |
| Derived booleans for Moonraker (all true) | `testDerivedBooleans_moonraker_allTrue` |
| Derived booleans for SDCP (movement false) | `testDerivedBooleans_sdcp_movementFalse` |
| Derived booleans before load (all false) | `testDerivedBooleans_beforeLoad_allFalse` |
| Capability load error propagation | `testLoadCapabilities_propagatesError` |
| **FIFO queue serialization** (with delays) | `testCommandQueue_isFIFO` |
| isCommandInFlight false after drain | `testCommandQueue_isCommandInFlight_falseAfterDrain` |
| Conflict (409) → distinct "busy" message | `testConflict_surfacesDistinctBusyMessage` |
| Non-conflict → generic error description | `testNonConflict_usesGenericErrorDescription` |
| setTemperatures happy path | `testSetTemperatures_happyPath` |
| home(axes:) happy path | `testHome_allAxes_happyPath` |
| homeXY() happy path | `testHomeXY_happyPath` |
| homeZ() happy path | `testHomeZ_happyPath` |
| move() happy path | `testMove_happyPath` |
# Decision: iOS Printer.progress canonical scale = 0–100

**Author:** Dallas (Lead)
**Date:** 2026-05-28
**Status:** Decided
**References:** OlyForge3D/PrintFarmerMobile#5 (bug), #8 (fix issue), #6 (pinning PR)

---

## Decision

`Printer.progress` on iOS stores **0–100**, a passthrough of the backend wire value. Divide-by-100 happens **only at the SwiftUI render/binding site**, not at decode time.

## Rationale

The PFarm1 backend is unambiguously 0–100: every backend plugin (OctoPrint, Moonraker, FlashForge, SDCP, TestEmulator) normalizes to 0–100 before populating `PrinterStatusDto.Progress`. Code comments in `OctoPrintClient.cs` and `SdcpClient.cs` say "frontend expects 0–100." The `PrinterStatusDto` record carries `double? Progress` at that scale — no transformation before the JSON response.

Normalizing to 0–1 at `Models.swift:266` is a **leaky normalization anti-pattern**: it silently changes the scale at the model boundary, forcing every downstream consumer (ViewModels, tests, formatters) to agree on the transformed value. This directly caused the `PrinterDetailViewModel:141` 100× bug: `PrinterStatusDetail.progress` (no custom decoder, raw 0–100) mixed with the normalized `Printer.progress` (0–1) on the fallback path.

The correct pattern: model layer stores wire values; view/presentation layer applies display transforms.

## Migration

1. `Models.swift:266` — remove `/ 100.0` from `decodeIfPresent` map.
2. `PrinterListViewModel.swift:46`, `PrinterDetailViewModel.swift:111`, `DashboardViewModel.swift:50` — remove `/ 100.0` from SignalR update handlers.
3. Every `ProgressView` / `PrintProgressBar` binding — add `/ 100.0` at render site.
4. Tests: `ModelDecodingTests:35` expected value `45.5` becomes correct again; `PrinterProgressContractTests` pinning expectations flip to 0–100.

## Affected files

- `PrintFarmer/Models/Models.swift:266`
- `PrintFarmer/ViewModels/PrinterListViewModel.swift:46`
- `PrintFarmer/ViewModels/PrinterDetailViewModel.swift:111,141`
- `PrintFarmer/ViewModels/DashboardViewModel.swift:50`
- All `ProgressView` / `PrintProgressBar` render sites
- `PrintFarmerTests/Models/ModelDecodingTests.swift:35`
- `PrintFarmerTests/Models/PrinterProgressContractTests.swift`

## Squad assignment

Ripley (iOS Dev) owns implementation. Issue #8 filed.

# Shared Checkout Hazard — Recurring Pattern

**Date:** 2026-05-28  
**Incident:** Round 8 near-miss — Gorman's #278 commit landed on Hudson's `squad/284` branch (shared-checkout race condition).  
**Previous:** Round 6 design-spec leak via same mechanism.  

## Symptom

When multiple agents work in the same workspace directory without sequencing checkout operations:
- Agent A finishes work on branch X, pushes, but leaves checkout at branch X.
- Agent B expects to start on branch Y, runs `git commit` without verifying current branch.
- Commit lands on the *wrong* branch.

## Root Cause

**Shared `.git/` directory + async agent cleanup** — agents assume they're on the correct branch after `git push` but do not verify branch state before staging/committing.

## Mitigation (Each Agent)

Before staging changes:

```bash
git status
```

Verify:
1. **Current branch** matches intended scope (e.g., `squad/284`).
2. **Changed files** in the output match expected scope (no unexpected `.squad/` changes, etc.).
3. **No detached HEAD** state.

If mismatch detected, abort and notify Scribe.

## Detection

Scribe should flag in post-merge review:
- Commits on unexpected branches.
- File diffs crossing agent scopes (e.g., Gorman's PR includes `.squad/` agent history changes).

## Recurring Risk

- **R6:** Design-spec leak (shared-checkout collision).
- **R8:** Gorman #278 → Hudson `squad/284` branch (same race pattern).

**Action:** Log this as a standing hazard. Each agent's pre-commit `git status` verification should reduce recurrence.

---

## Decision: Round 15 — Hudson final #12 (verbatim spec strings), Hicks pedant CR #13 (init-state tests tooling gap)

**Date:** 2026-05-29  
**Authors:** Hudson (fix-up), Hicks (re-review), Vasquez (pending tiebreaker)  
**Status:** PR #12 Merged, PR #13 Open + REQUEST_CHANGES  

### Summary

- **Hudson PR #12 final fix:** ✅ MERGED.
  - **Spec strings inlined by coordinator:** Verbatim per-button hyphenated hints from `docs/design/printer-controls-section.md` now coded: "Double-tap to home printer", "Double-tap to home XY", "Double-tap to home Z".
  - **Disabled-state pattern finalized:** `resolvedAccessibilityLabel` appends `", unavailable during print"` when disabled; `resolvedAccessibilityHint` returns `""` (empty string) when disabled. Both computed properties used directly by `.accessibilityLabel()` / `.accessibilityHint()`.
  - **Test layer non-tautological pattern:** Helpers construct real `HomeButton` and call `resolved*` computed properties — same properties the view uses. If view strings change, test assertions change automatically; test cannot pass with stale spec.
  - **Commit:** `533b86f`
  - **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570269998

- **Hicks PR #13 re-review:** ❌ REQUEST_CHANGES (pedantic).
  - **Issue:** Init-state tests instantiate `JogSubgroup` and inspect `*ForTesting` test hooks rather than routing through SwiftUI render lifecycle.
  - **Rationale:** No ViewInspector or equivalent SwiftUI introspection library available in project; true "render-lifecycle" tests require UI framework integration not present.
  - **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570184502
  - **Status:** Awaiting Vasquez tiebreaker (spawned as R16 task).

### Tooling Gap Identified

**Project limitation:** Absence of ViewInspector (or equivalent SwiftUI introspection lib) prevents render-lifecycle tests on init state. Post-init `@State` reads via `*ForTesting` extensions are the practical equivalent for verifying init logic without adding a test dependency.

### Durable Decision Rules Captured

4. **SwiftUI introspection tooling threshold rule (effective immediately):**
   - **If ViewInspector or equivalent SwiftUI introspection lib is unavailable,** accept `*ForTesting` extensions that expose post-init `@State` values as the practical equivalent of render-lifecycle tests.
   - Do not ratchet code-review expectations beyond available tooling; test-hook reads of `@State` post-init are valid for init-logic verification.
   - Rationale: Full lifecycle tests require UIKit integration or framework support not present in this project. Don't require the impossible.
   - Applies to: All SwiftUI subgroup init-state testing going forward.

---

## Decision: Round 16 — iOS controls v1 stack APPROVED end-to-end

**Date:** 2026-05-29
**Authors:** Bishop (third-review APPROVE #12), Vasquez (tiebreaker APPROVE #13), Hicks (CR re-check, scope-creep acknowledged)
**Status:** ✅ APPROVED (all three PRs cleared)

### Summary

**PR #11 (preheat, #284):** ✅ **Bishop APPROVE**
- Cool Down preset label fixed (removed hardcoded "Off" ternary; format string now produces "0° / 0°" uniformly).
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/11#issuecomment-4570039961

**PR #12 (home, #285):** ✅ **Vasquez APPROVE + Bishop APPROVE**
- Verbatim spec strings inlined per-button ("Double-tap to home printer" / "Double-tap to home XY" / "Double-tap to home Z"). Disabled-state pattern: `resolvedAccessibilityLabel` appends `", unavailable during print"`; `resolvedAccessibilityHint` returns `""` when disabled.
- Both computed properties used directly by `.accessibilityLabel()` / `.accessibilityHint()` — test cannot pass if view strings change (non-tautological pattern verified).
- Commit: `533b86f`
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570211958 (third review), https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570269998 (Bishop final)

**PR #13 (jog, #286):** ✅ **Vasquez APPROVE** (tiebreaker; Hicks pedant CR overruled)
- Test-hook pattern (`.hasAnyJogCapabilityForTesting`, etc.) matches established HomeSubgroup/PreheatSubgroup project convention.
- Hicks objection: "True render-lifecycle tests require ViewInspector" — valid but scope-creep. No ViewInspector in project; accept test-hook reads of post-init `@State` per Rule 4 (tooling-threshold).
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570216262 (Vasquez tiebreaker)

### Durable Decision Rules Captured

5. **Scope-creep early-stop rule (effective immediately):**
   - When second-voice reviewer requests adding a test framework or major tooling (e.g., ViewInspector, screenshot testing) for a single PR, that is scope creep if:
     - The project has no established precedent for that tool.
     - The pattern (test-hooks, mock views, manual renders) already covers the requirement.
     - PR author's implementation matches project convention.
   - **Remedy:** Tiebreaker votes may override tooling blockers when convention suffices.
   - **Exception:** Never waive security, safety, or coverage gaps — only tool/framework choice.

6. **Multi-review consensus rule (standing):**
   - When two-of-three reviewers APPROVE (with tiebreaker rationale documented), PR is approved.
   - Third dissent is honored but does not block if rationale falls outside project scope or misses established convention.
   - Document dissent in decision log and agent history for future reference.

---

**Next Steps:**
1. Merge PR #321 once CI passes
2. If this pattern repeats quarterly, consider implementing `sync-main-to-dev.sh` skill

---

## Decision Record: dev→main Sync PR — 2026-05-29

**Author:** Parker
**Date:** 2026-05-29
**Status:** ⚠️ PR ready locally, push blocked — needs `workflow` scope

### Summary

Prepared a clean sync of `development` → `main` to pick up 536 commits including Dependabot security fixes for 49 flagged vulnerabilities (2 critical, 15 high, 31 moderate, 1 low).

### What Was Accomplished

- **Branch created:** `sync/dev-to-main-2026-05-29` off `origin/main`
- **Commits merged:** 536 (all of development since the last main sync)
- **Commit SHA:** `d4d8b4a1e`
- **Forbidden paths stripped from index:** All `.squad/`, `.ai-team/`, `.ai-team-templates/`, `team-docs/`, `docs/proposals/` — confirmed 0 forbidden paths in staged index
- **Conflicts resolved (16):**
  - `.squad/*` modify/delete conflicts (≈60 files) — resolved by `git rm --cached`
  - `.github/fact-checker-charter.md`, `.github/loop.md`, `.github/squad.agent.md.template` — git directory-rename heuristic misfire; removed
  - `.gitignore`, 5 `.github/workflows/squad-*.yml`, `mobile/scripts/release-beta.sh`, `scripts/sync-monorepo-version.sh`, 5 `.csproj` files — resolved using development's version

### Blocker

Push rejected: `refusing to allow an OAuth App to create or update workflow ... without 'workflow' scope`.

**Resolution required:** Jeff must run `gh auth refresh --scopes workflow` (browser one-time code), then run:
```bash
cd /Users/jpapiez/s/PFarm1
git push -u origin sync/dev-to-main-2026-05-29
gh pr create --base main --head sync/dev-to-main-2026-05-29 \
  --title "chore: sync development → main (Dependabot + accumulated)" \
  --body "Brings main current with development (536 commits). Picks up Dependabot security fixes for the 49 vulnerabilities flagged on the default branch.

Squad metadata (.squad/, .ai-team/, team-docs/, docs/proposals/) explicitly excluded per repo policy. The squad-main-guard.yml workflow will verify."
```

The local branch `sync/dev-to-main-2026-05-29` is ready to push — no further merge or conflict resolution needed.

### CI Expectation

- `squad-main-guard.yml` — should PASS (0 forbidden paths in index, verified)
- All other checks (build, tests, compose validation) — expected green (same codebase as development which passed CI)

---

## Merged from Inbox: 2026-05-31T09:17:00-07:00

# Decision: gcode-preview v1 (no-worker) → v2 (worker) Throwaway Risk

**Date:** 2026-05-31  
**Requested by:** Brady (Jeff Papiez)  
**Scope:** Architecture decision for gcode-preview phases to minimize rework  

## TL;DR

**Throwaway risk: LOW for UI components (reuse 95%+), MODERATE for parser integration (~40–60% of parsing code survives).** Estimated throwaway delta: ~200–400 LOC (mostly invocation sites, state management). **Recommendation: Ship v1 now behind a service abstraction, go straight to v2 in next sprint.** The cost of v1 main-thread is <2 weeks lost productivity; the cost of delaying v1 is blocking 3D model preview UX for 4+ weeks.

## Research Findings

### 1. Does gcode-preview v2.18.x expose a worker-compatible API surface?

**Answer: NO native worker support in v2.18.0.** However:

- **Parser API is pure JS:** The library exports `GCodePreview` class with `processGCode(gcodeString)` method (single-pass, full-string parsing only).
- **No streaming parse:** v2.18.0 has no streaming or chunked-parse API. It loads the entire G-code string into memory and parses synchronously.
- **Rendering tightly coupled:** `processGCode()` directly updates Three.js scene geometry. **Cannot move parsing to worker without decoupling parser output from Three.js commands.**
- **Upstream v3 alpha signals intent:** The maintained xyz-tools fork (xyz-tools/gcode-preview, moved Nov 2024) lists "streaming" and "incremental updates" as roadmap items but NOT yet in v2.18.0.

### 2. If we wire WebGLPreview + layer slider + extruder colors + T-command filter on main thread in v1, how much survives?

**Answer: ~60–70% reuse potential.**

**Components that survive v1→v2 (reusable ~95%):**
- React wrapper component for canvas binding
- Layer slider
- Extruder color palette UI
- T-command filter toggle UI
- File drop zone

**Components that change (~40–50% rework):**
- Parser invocation site
- State management for parsed data
- Progress feedback loop
- Memory management for large file handling

**Estimated reuse: 250–300 LOC survive untouched; 150–200 LOC requires rewrite.**

### 3. Cheapest v1 architecture to minimize v2 throwaway

**Implement a parser service abstraction NOW:**

```typescript
// v1: Synchronous main-thread parse
export class GcodeParserService {
  parse(gcodeString: string): ParsedGcode {
    const preview = new GCodePreview(options);
    preview.processGCode(gcodeString);
    return {
      layers: preview.parser.layers,
      metadata: preview.parser.metadata,
      bounds: preview.parser.bounds,
    };
  }
}

// v2 upgrade: Replace with async worker-based parse
// async parse(gcodeString: string): Promise<ParsedGcode> { ... }
```

**Impact:** ONE file changes invocation logic; all UI components remain untouched.

## Decision

- ✅ **Proceed with v1 (no-worker, 10MB warning) in Phase 1.**
- ✅ **Implement `GcodeParserService` as abstraction layer.**
- ✅ **Schedule v2 (worker-based) for Sprint N+1.**
- ⚠️ **Risk:** If v2 upstream (xyz-tools/gcode-preview) ships a breaking parser API before v2 implementation, revisit. Monitor releases.

---

# Decision: bambuddy Settings UX Patterns & PrintFarmer Nav Consolidation

**Author:** Brett (Researcher)  
**Date:** 2026-05-31  
**Status:** Decision Proposal  

## Executive Summary

Consolidates 25+ scattered nav items into a unified Settings area with tab navigation, modeled on bambuddy's proven pattern. Keeps Printers, Queue, Projects, Analytics, Automation as top-level workflow destinations.

## Proposed Settings Tabs

| Tab Name | Purpose |
|---|---|
| **General** | Language, theme, display prefs, system status, tag/bed-type enums, custom fields |
| **Filament** | Filament library, Spoolman config, AMS display thresholds |
| **Slicing** | Slicer profiles, OrcaSlicer worker registration, default print options, staggered start, gcode injection |
| **Hardware** | Camera registration, NFC device pairing, smart plugs (future) |
| **Notifications** | Notification providers (Discord, email, webhook), message templates, notification log |
| **Integrations** | Webhooks, API keys, external URLs, MQTT config, Home Assistant, reverse proxy |
| **Data** | Export/import, backup, reset, data management |
| **Users** (with sub-tabs) | Local users, LDAP, OIDC, 2FA, login audit |

## Key Design Patterns

- **Settings search:** Cross-tab search with tab-aware indexing and keyword-based jump-to
- **Secrets handling:** Masked + revoke (never edit-in-place) for tokens and credentials
- **Progressive disclosure:** Collapsible cards prevent overwhelming users
- **Inline modals:** Smart plug add, notification provider add, user creation all use modals

## Non-Adoption

| Feature | Reason |
|---|---|
| Per-printer Settings pages | bambuddy doesn't expose this; farm defaults apply uniformly |
| Settings sidebar (vertical nav) | Tab-based keeps Settings compact; sidebar would expand nav depth |

## Recommended Path

Structure Settings using 8-tab model, implement cross-tab search, consolidate 15+ scattered admin pages into Settings. Keep Printers, Queue, Projects, Analytics, and Automation as top-level workflow destinations.

---

# Decision: bambuddy NFC UX Patterns for Spool Binding & Tag Management

**Author:** Brett (Researcher)  
**Date:** 2026-05-31  
**Status:** Decision Proposal  

## Executive Summary

bambuddy's NFC workflow pairs physical RFID/NFC tags with spools via a two-step modal flow. PrintFarmer has NFC devices registered but no user-facing tag-binding UX. Key patterns: search-first binding, WebSocket real-time sync, passive reads for known tags, and clear error recovery.

## Key Winning Patterns (Adoption Recommended)

- **Modal-Based Tag Linking:** LinkSpoolModal + AssignSpoolModal pattern keeps spool assignment in context
- **Search-First UX:** Don't force users to scroll; always start with a search box
- **WebSocket Real-Time Sync:** Broadcast tag-link events via SignalR for multi-session consistency
- **Tag Unrecognized Flow:** Unknown tag scanned → LinkSpoolModal with search immediately
- **Mismatch Detection:** When tag bound to spool X is scanned on tray with spool Y, warn user
- **Passive Reads:** Successful re-reads of known tags are silent (no modal spam)
- **One-Way Tag Creation:** Tags are written once during binding; no edit-in-place

## Error Handling Flows

- **Unrecognized tag:** LinkSpoolModal appears for binding or cancel
- **Tag bound to different printer:** Toast warning with relink option
- **Tag bound but spool removed:** Option to unlink this tag
- **Duplicate tag detection:** Error toast, system prevents accidental reassignment
- **Tag physically moved without unbinding:** Backend reports location mismatch

## Non-Adoption (Due to PrintFarmer Differences)

- SpoolBuddy hardware management (NFC hardware may be different)
- Spoolman-specific APIs (PrintFarmer may not use Spoolman)

## Implementation Roadmap

| Phase | Tasks | Duration |
|---|---|---|
| 1 | Modal UX, trigger modals on NFC events | Weeks 1-2 |
| 2 | Real-time WebSocket sync, inventory grid updates | Weeks 2-3 |
| 3 | Mismatch detection, error handling, edge cases | Weeks 3-4 |
| 4 | Polish, search optimization, i18n, a11y | Week 5 |

---

# Backlog: Electricity Cost Tracking via Smart Plugs

**Author:** Dallas
**Date:** 2026-05-31
**Status:** Proposed — pending Brady decisions
**Routes to:** Lambert (backend), Ripley (frontend)

## Problem Statement

`PrintJob` already stores `EnergyCostUsd`, but that value is calculated from static `Printer.Wattage` × print duration. This is an estimate, not a measurement. Smart plugs (Kasa, Tasmota, Shelly) provide real-time power readings for measured kWh instead.

## Architecture Sketch

### Ingest Model: Polling

- Background `PowerMonitorPollingService` calls each plug's local HTTP API on configurable interval (default 10 s)
- Polling is skippable per-printer when no job running

### Provider Abstraction

```csharp
public interface ISmartPlugProvider
{
    string ProviderType { get; } // "Kasa", "Tasmota", "Shelly"
    Task<PowerSample> ReadAsync(PowerMonitor monitor, CancellationToken ct);
    Task<bool> PingAsync(PowerMonitor monitor, CancellationToken ct);
}
```

**Phase 1 providers:**
- `KasaSmartPlugProvider` — local REST (TP-Link Kasa LAN API)
- `TasmotaSmartPlugProvider` — `GET /cm?cmnd=Status%208` JSON endpoint
- `ShellySmartPlugProvider` — Gen1/Gen2 meter endpoints

### Data Model

**New entity — `PowerMonitor`:**
```
PowerMonitor
  Id, PrinterId (unique), ProviderType, Endpoint, CredentialJson (encrypted), PollingIntervalSeconds, IsEnabled, CreatedAt/UpdatedAt
```

**New time-series table — `PowerReading`:**
```
PowerReading
  Id (long PK), PrinterId (FK), PrintJobId (FK?), WattsInstant, SampledAt (UTC)
```
Index on `(PrinterId, SampledAt DESC)` and `(PrintJobId)`.

**Hot path — existing `PrintJob` columns:**
- `EnergyCostUsd` — updated from actual kWh on job completion
- Add `KwhUsed (decimal?)` — the measured kWh for the job window

### Electricity Rate

Store at **printer level** as `Printer.ElectricityRatePerKwh (decimal?)`.  
If null, fall back to farm-wide `CostTrackingSettings.DefaultElectricityRatePerKwh`.

## Work Item Table

| # | Owner | Title | Size |
|---|-------|-------|------|
| 1 | Lambert | `ISmartPlugProvider` + `PowerMonitor` entity + migrations | M |
| 2 | Lambert | Kasa/Tasmota/Shelly providers | M |
| 3 | Lambert | `PowerMonitorPollingService` | M |
| 4 | Lambert | `PowerReading` writes + indexes | S |
| 5 | Lambert | Add `KwhUsed` to `PrintJob` + migrations | S |
| 6 | Lambert | `IPowerAggregationService` — job-window kWh aggregation | M |
| 7 | Lambert | Admin CRUD endpoints for power monitors | S |
| 8 | Lambert | `GET /api/printers/{id}/power-readings?from=&to=` paginated | S |
| 9 | Lambert | Add `ElectricityRatePerKwh` to `Printer` + migrations | S |
| 10 | Ripley | Printer settings form: power monitor section | M |
| 11 | Ripley | Print history: surface `KwhUsed` + `EnergyCostUsd` per job | S |
| 12 | Ripley | Per-printer power graph (line chart, time-range picker) | L |

**Estimate:** Backend ~5 days, Frontend ~4 days.

---

# Backlog: Printables.com Model Import

**Author:** Dallas
**Date:** 2026-05-31
**Status:** Proposed — pending Brady decisions
**Routes to:** Lambert (backend download service), Ripley (frontend modal)

## Problem Statement

Users currently upload 3MF/STL files manually. Printables.com is the dominant open-ecosystem model repository. Goal: "paste URL → import" flow that fetches model files, thumbnail, license, and attribution directly into PrintFarmer's 3D models library.

## Printables API

Printables.com exposes a **public GraphQL API** at `https://api.printables.com/graphql/` (no auth required for public model metadata reads). File download URLs served from CDN with no auth token required.

**No OAuth needed for public models.** A simple `HttpClient` call returns everything needed.

## Architecture Sketch

### Backend: `PrintablesImportService`

1. Accept a Printables model URL
2. Extract model ID from URL path
3. Query GraphQL API for metadata (title, license, creator, thumbnail, file list)
4. User selects which file to import if multiple available
5. Download via CDN URL
6. Hand off to existing `Model3DFileService.UploadFileAsync` pipeline
7. Persist attribution fields on `Model3DFile` entity

### New API Endpoints

```
POST /api/3d-models/import-url/preview
Body: { "url": "https://www.printables.com/model/..." }
Response: PrintablesModelPreviewDto

POST /api/3d-models/import-url
Body: { "url": "...", "fileIndex": 0 }
Response: Model3DFileDto
```

### Schema: Attribution Fields

Add to `Model3DFile` entity:
```
SourceUrl       string?   — canonical Printables URL
SourceLicense   string?   — license identifier (e.g. "CC BY 4.0")
SourceCreator   string?   — creator handle
ImportedAt      DateTime? — UTC timestamp
```

### Frontend: Import-by-URL Modal

Two-step modal:
1. **Preview:** User pastes URL → Fetch → show thumbnail, title, creator, license, file list with sizes
2. **Confirm:** "Import" button → calls import endpoint

License prominently displayed before confirm. Yellow banner for non-commercial or NoDerivatives licenses.

### MakerWorld (Deferred)

MakerWorld uses Bambu Cloud API token. PrintFarmer does not currently carry a Bambu Cloud token. **Hard blocker.** File separate issue when/if Brady wants to unblock.

## Work Item Table

| # | Owner | Title | Size |
|---|-------|-------|------|
| 1 | Lambert | `IPrintablesImportService` + GraphQL fetch + CDN download | M |
| 2 | Lambert | `POST /api/3d-models/import-url/preview` endpoint | S |
| 3 | Lambert | `POST /api/3d-models/import-url` endpoint | S |
| 4 | Lambert | Add `SourceUrl`, `SourceLicense`, `SourceCreator`, `ImportedAt` + migrations | S |
| 5 | Ripley | "Import from URL" button + two-step modal on ModelsPage | M |
| 6 | Ripley | License badge + NC/ND warning banner | S |
| 7 | Ripley | Surface `SourceUrl` / `SourceCreator` / `SourceLicense` in model detail | S |

**Estimate:** Backend ~2 days, Frontend ~2 days.

---

# Backlog: Passkey (WebAuthn) Login Support

**Author:** Dallas
**Date:** 2026-05-31
**Status:** Proposed — pending Brady decisions
**Routes to:** Lambert (backend ceremony + storage), Ripley (frontend enrollment + login)

## Problem Statement

PrintFarmer login is password-only. Passkeys (WebAuthn/FIDO2) are now platform default: Face ID, Touch ID, Windows Hello, YubiKey. Adding passkey support improves security (phishing-resistant) and UX (no password to remember).

Goal: passkeys as **additional** login method alongside passwords — not a replacement.

## Library Choice

**`Fido2NetLib`** — canonical .NET WebAuthn/FIDO2 library, actively maintained, targets `net6+`.

## User Flows

### Enrollment (from Account Settings)

1. User navigates to Account Settings → Security → "Add a Passkey"
2. Frontend calls `POST /api/auth/passkey/register/begin` → returns `CredentialCreateOptions`
3. Browser calls `navigator.credentials.create(options)` — platform shows biometric/PIN prompt
4. Frontend POSTs `AuthenticatorAttestationRawResponse` to `POST /api/auth/passkey/register/complete`
5. Server validates, stores credential, returns success
6. UI shows new passkey in "Passkeys" list

### Login (Passkey Path)

1. Login page shows "Use a Passkey" button
2. User clicks → frontend calls `POST /api/auth/passkey/login/begin`
3. Browser calls `navigator.credentials.get(options)` → platform selects matching passkey
4. Frontend POSTs `AuthenticatorAssertionRawResponse` to `POST /api/auth/passkey/login/complete`
5. Server validates, issues JWT token (same as password path)
6. `LoginAuditService` records passkey login

## Storage: `UserPasskeyCredential` Entity

New table in `AppDbContext`:

```
UserPasskeyCredential
  Id, UserId (FK), CredentialId (byte[], unique), PublicKey (byte[]), SignCount (long),
  AaGuid, DeviceName?, AttestationType, Transports?, CreatedAt, LastUsedAt, IsEnabled
```

Migrations: `Farm.Migrations.PostgreSQL` + `Farm.Migrations.SqlServer` with `AppDbContext`.

## API Surface

```
POST /api/auth/passkey/register/begin    → CredentialCreateOptions
POST /api/auth/passkey/register/complete → 201 Created
GET  /api/auth/passkey/credentials       → list of user's passkeys
DELETE /api/auth/passkey/credentials/{id} → 204 No Content (revoke)
POST /api/auth/passkey/login/begin       → AssertionOptions
POST /api/auth/passkey/login/complete    → AuthenticationResult
```

Challenge state stored server-side in distributed cache (30 s TTL).

## Browser Support (2026)

All platforms green — no polyfills needed:
- Chrome 108+, Safari 16+, Firefox 122+, Edge 108+
- Android fingerprint/face via Google Password Manager
- iOS 17+ iCloud Keychain passkey sync

## Work Item Table

| # | Owner | Title | Size |
|---|-------|-------|------|
| 1 | Lambert | Add `Fido2NetLib` NuGet + DI registration | S |
| 2 | Lambert | `UserPasskeyCredential` entity + repository + migrations | M |
| 3 | Lambert | `IPasskeyService` + `PasskeyService` (ceremonies, challenge cache) | M |
| 4 | Lambert | Register ceremony endpoints (`begin` + `complete`) | S |
| 5 | Lambert | Assertion ceremony endpoints (`login/begin` + `login/complete`) | M |
| 6 | Lambert | Credential management endpoints (list, revoke) | S |
| 7 | Lambert | `AuthMethod` field on login audit | S |
| 8 | Ripley | Add `@simplewebauthn/browser` npm + `usePasskeyRegistration` hook | S |
| 9 | Ripley | Account Settings → Security tab: passkey list + "Add Passkey" + "Revoke" | M |
| 10 | Ripley | Login page: "Use a Passkey" button + `usePasskeyLogin` hook | M |
| 11 | Ripley | Friendly device name prompt during enrollment | S |

**Estimate:** Backend ~4 days, Frontend ~3 days.

---

### 2026-05-31T09:12 PT: Bambuddy adoption plan — Brady sign-off

**By:** Brady (via Copilot)

**Sign-offs (5):**
1. ✅ gcode-preview WITH web workers (v1 throwaway research confirmed → proceed v1 + service abstraction)
2. ✅ Hide raw-param sliders behind "Advanced"
3. ✅ Notification providers (webhook + Discord + Telegram) ship as ONE PR
4. ✅ Filament cost source: Spoolman price first, per-material fallback
5. ✅ Quick Slice as modal (not page)

**New backlog items requested (planning in flight):**
- Electricity cost tracking via smart plugs (Brady has plugs available for test)
- Bambuddy NFC UX review (we have our own NFC tech — learn from their exposure pattern)
- Printables import (priority over MakerWorld; MakerWorld stretch)
- Passkey login support
- Settings system overhaul — consolidate nav links into Settings area, drawing on bambuddy review

**Why:** Unblocks Phase 1 dispatch + expands backlog with 5 net-new candidates.

---

### 2026-05-31T09:14 PT: Worktrees + reference scrubbing — Brady directives

**By:** Brady (via Copilot)

**Directive 1 — Worktrees mandatory:**

All work items must be executed in dedicated git worktrees (SQUAD_WORKTREES=1). One worktree per GitHub issue, path `{repo-parent}/PrintFarmer-{issue-number}`, branch `squad/{issue-number}-{slug}`. Reuse existing worktrees when an agent picks up the same issue. Clean up after PR merge.

**Directive 2 — No external-repo references in PrintFarmer artifacts:**

NEVER reference the bambuddy repo by name in ANY of: GitHub issues, GitHub PR titles/descriptions/comments, source code, code comments, commit messages, changelogs, or user-facing docs. If a feature was inspired by external research, refer to it generically ("external 3D-printer-management reference", "research source", or simply describe the feature without attribution). The `.squad/` internal team memory (decisions.md, history.md, log/) MAY reference the source for our own context.

**Coordinator enforcement:**
- Strip "bambuddy"/"maziggy" from any issue body or PR description before filing
- Squad-internal `.squad/` files are exempt (research notes can keep the citation)
- Scribe should add a final scrub pass to the merge step

**Why:** Hygiene + attribution boundary. Brady's call.

---

---

### 2026-05-31T17:17:43-07:00: Added Mobile section to copilot-instructions.md

**By:** Lambert (requested by Brady)

**What:** Added Mobile bullet to project overview listing SwiftUI iOS app in `mobile/`. Added Working Directories table row for Xcode/swift/fastlane work in `mobile/`. Created new "Mobile App" section (post-Local Development, pre-Architecture Invariants) covering SwiftUI/Xcode 26+/iOS 17+, API connection via `PRINTFARMER_API_URL` env var (default localhost:5000 → override to 5245), build/test commands, test suites, agents/squad config, and consolidated release pipeline. Updated Architecture Invariants bullet to note iOS app and React app both consume same API with camelCase JSON and string enums.

**Why:** `mobile/` was merged into repo from OlyForge3D/PFarm-Ios but copilot-instructions.md had no mention of it. Agents working on the repo had zero guidance for iOS work. Added concise, actionable guidance tied to existing structure (Working Directories table, Architecture Invariants, Serialization Rules).

**Impact:** ~25 lines added. Maintains style consistency with existing sections (short sentences, tables, fenced code blocks). Enables future agents to understand mobile directory conventions and API integration immediately.

---

## Notification Preferences — Architecture Decisions

**Issue:** #341  
**Author:** Ripley (Frontend)  
**Date:** 2025-05-31

### Context

Farm operators need notification delivery (email, web push, in-app) with per-user preferences.

### Decisions

1. **Backend already existed.** The `NotificationPreferences` entity, `NotificationService`, and `GET/PUT /api/notifications/preferences` were already implemented. No changes to the existing preference logic were needed.

2. **Push subscription model.** Added `PushSubscription` entity with `(UserId, Endpoint)` unique index. Supports multiple subscriptions per user (different browsers/devices). VAPID public key served from `GET /api/notifications/push-subscription/vapid-key` (reads from `VAPID_PUBLIC_KEY` env var).

3. **Service Worker.** Extended existing `sw.js` with `push` and `notificationclick` event handlers rather than creating a separate file. Keeps a single SW registration.

4. **Frontend pattern.** New `features/notifications/` module with TanStack Query hooks (`useNotificationPreferences`, `usePushSubscription`). Page at `/profile/notifications` — user-level, not admin-restricted.

5. **No email/push delivery wiring yet.** The `NotificationService.BroadcastJobNotificationAsync` currently only creates in-app DB records and fires SignalR. Actual email sending (SMTP) and web push dispatch (via WebPush library) are deferred to phase 2. The infrastructure (subscriptions, preferences) is ready.

### What's NOT included

- SMS, Slack, Discord channels
- Actual SMTP email sending
- Actual web push payload dispatch (needs WebPush NuGet + VAPID private key)
- `farm_alert` / low filament event types (only job events covered)

### Migration

- `AddPushSubscriptions` migration for both PostgreSQL and SqlServer
- Creates `PushSubscriptions` table with FK to `Users`

---

# Decision: Passkey Management UI (#356)

**Date:** 2025-01-31
**Author:** Ripley (Frontend)
**Status:** Implemented

## Context

Issue #356 requires a passkey management UI under profile settings. Users need to list, rename, and revoke registered passkey credentials.

## Decisions

1. **Route:** `/profile/passkeys` — consistent with existing `/profile/api-keys` pattern.
2. **Backend endpoints:** Added to `AuthController` under `passkey/credentials` path:
   - `GET /api/auth/passkey/credentials` — list
   - `DELETE /api/auth/passkey/credentials/{id}` — revoke
   - `PATCH /api/auth/passkey/credentials/{id}` — rename
3. **Service layer:** Extended `IPasskeyService` / `PasskeyService` with `ListCredentialsAsync`, `DeleteCredentialAsync`, `RenameCredentialAsync`.
4. **Frontend service:** Standalone `passkeyService.ts` (mirroring `apiKeysService.ts` pattern) using `apiClient.request()`.
5. **Add passkey button:** Currently links to `/profile/passkeys/register` — will be connected to enrollment ceremony from #355.
6. **No "last passkey" guard yet:** Issue mentions "cannot remove last passkey when no password set" — deferred until password-status API is available.

## Tradeoffs

- Kept backend additions minimal (no separate controller file) since they naturally belong with existing passkey endpoints in `AuthController`.
- Used `int` ID for credential operations since the entity uses surrogate `int` PK.

---

## Decision: Settings Frontend Architecture (Issue #360)

**Date:** 2025-07-22
**Author:** Ripley (Frontend)

### Context

Implementing frontend pages for the per-user vs farm-wide settings split (backend shipped in #359/PR #385).

### Decisions

1. **Separate inner form components** — FarmSettingsForm and UserSettingsForm are separate components that receive data as props, initializing `useState` from prop values. This avoids the `useEffect` → `setState` anti-pattern flagged by the ESLint `react-hooks/set-state-in-effect` rule.

2. **Route at `/preferences`** — The new page lives at `/preferences` (no role guard). Farm settings show a lock badge + read-only fields for non-admins using the `canWrite` flag from the API. The existing admin `/settings` route (metadata-driven) remains untouched.

3. **React Query hooks** — `useFarmSettings` / `useUpdateFarmSettings` / `useUserSettings` / `useUpdateUserSettings` use the public `apiClient.get<T>` / `apiClient.put<T>` methods. Optimistic cache update on mutation success via `queryClient.setQueryData`.

4. **Client-side validation mirrors backend** — Same min/max ranges. Toast errors for invalid input before sending request.

### Alternatives Considered

- Embedding in existing SettingsPage — rejected because that page is admin-only and metadata-driven. The new endpoints have a different shape and audience.
- `react-hook-form` — charter says controlled `useState` is the convention.

---

# Decision: Optimistic Concurrency for Settings Writes

**Author:** Ripley (Frontend, backend fix per lockout rule)  
**PR:** #385  
**Date:** 2025-05-31  

## Context

Multi-writer scenarios on settings endpoints (PUT /api/settings/user and PUT /api/settings/farm)
could silently overwrite changes made by concurrent writers — a classic lost-update problem.

## Decision

Add application-managed concurrency tokens (`RowVersion` byte[] column) to:
- `UserSettings` entity (per-user preferences)
- `AppSettingsEntity` (farm-wide settings key-value store)

### Mechanism

1. **Token generation:** `AppDbContext.SaveChanges()` stamps a new GUID-based `RowVersion` on every Added/Modified entity.
2. **EF Core config:** `IsConcurrencyToken()` — provider-agnostic (works with SQLite, Postgres, SqlServer).
3. **PUT enforcement:** Clients supply `rowVersion` in the request body or `If-Match` header. Stale tokens yield HTTP 409 Conflict.
4. **Backward compatibility:** If no `rowVersion` is supplied, the write proceeds without a concurrency check (graceful degradation for older clients).

### Why not `IsRowVersion()` / `[Timestamp]`?

`IsRowVersion()` relies on server-side value generation (SQL Server `rowversion`, Postgres `xmin`). This creates provider-specific migration differences and breaks SQLite (local dev + tests). Application-managed tokens are simpler and portable.

## Migrations

- `AddSettingsConcurrencyTokens` for both PostgreSQL and SqlServer providers.
- Adds `RowVersion BYTEA/VARBINARY` column to `UserSettings` and `AppSettingsEntities` tables.

## Alternatives Considered

- **ETag via `UpdatedAt` timestamp:** Lower precision, timestamp collisions possible.
- **Database-native `xmin`/`rowversion`:** Provider-specific, doesn't work with SQLite.
- **Pessimistic locking:** Overly restrictive for settings that change infrequently.

---

# Decision: Fix captive dependency in PowerMonitorPollingService

**Date:** 2025-07-14
**Author:** Ripley (frontend, acting on backend fix per lockout rule)
**PR:** #391
**Bead:** #347

## Context

`PowerMonitorPollingService` is a singleton `BackgroundService` that previously accepted
`IEnumerable<ISmartPlugProvider>` as a direct constructor dependency. PR #393 (HA integration)
registers `HomeAssistantSmartPlugProvider` as **scoped** (it depends on per-request HTTP clients
and HA session tokens).

When both PRs merge, this creates a **captive dependency** — a singleton holding a reference to a
scoped service. With `ValidateScopes=true` (ASP.NET Core Development mode), this causes a startup
crash. In production (without validation), the scoped provider silently becomes a de-facto
singleton, leaking state across requests.

## Decision

Replace the direct `IEnumerable<ISmartPlugProvider>` constructor injection with per-iteration
scope resolution:

1. Remove `IEnumerable<ISmartPlugProvider>` from the constructor parameters.
2. In each poll iteration, resolve `IEnumerable<ISmartPlugProvider>` from the already-existing
   `AsyncServiceScope` via `scope.ServiceProvider.GetServices<ISmartPlugProvider>()`.
3. Pass the resolved providers to `PollMonitorsAsync` as a parameter.

## Validation

- Integration test `PowerMonitorPollingServiceScopeTests` verifies:
  - Startup succeeds with `ValidateScopes = true` and a scoped provider registered.
  - Each scope resolves a distinct provider instance (no captive reference).
- Full solution build: 0 errors.
- All tests pass.

## Consequences

- Any `ISmartPlugProvider` can now be registered with any DI lifetime (singleton, scoped, transient).
- Zero behavioral change for existing singleton providers.
- PR #393 can merge without modification.

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

# Bishop — History

## Core Context

- Code Reviewer on PrintFarmer project
- Uses GPT-5.4 model for review perspective diversity
- Part of triple-model pre-commit review gate (with Hicks and Vasquez)
- Project: C# .NET 10 API + React 19 TypeScript frontend for 3D printer management
- Owner: Jeff Papiez

## Learnings

_(append new learnings below this line)_

## Round 22-24: Cross-Layer Architectural Review — PR #318 (2026-05-21 to 2026-05-29)

**PR:** feat(backends): propagate firmware-409 from Moonraker/SDCP/FlashForge plugins  
**Status:** OPEN, all CI checks passing. Final two-reviewer APPROVE (Bishop + Hicks, round 24).

### Round 22: Caught Two Critical Blockers

REQUEST_CHANGES on initial approval. Hicks approved the PR based on plugin-layer tests alone, missing two critical architectural bugs:

1. **HTTP Status Mapping Bug**: `PrintersController.MapControlOutcome()` returns HTTP 502 (Bad Gateway) for `PrinterBackendBusyException`, not HTTP 409 (Conflict). This breaks the contract with downstream callers — 502 signals a retriable infrastructure failure; 409 signals a non-retriable printer-device state. Wrong status code poisons the print queue scheduler.

2. **Over-Broad Error Detection**: Moonraker treats all HTTP 503 responses as printer-busy, without inspecting response body. Conflates infrastructure timeouts with actual device-state errors.

**Finding:** Single-layer review is insufficient for cross-layer changes. Plugin logic alone ≠ end-to-end correctness.

### Round 23: Substring Match Over-Broad

After Lambert fixed 502→409 and narrowed 503 via body inspection, both Bishop AND Hicks blocked re-review. Bare substring match (`"busy"` anywhere in body) still produces false-positives on `"Klippy is busy initializing"` (firmware startup state, not printer-busy condition).

### Round 24: Final Approval

Lambert tightened with phrase-based allowlist. Bishop verified end-to-end:
- Phrase semantics explicit: `"printer is printing"`, `"printer is busy"`, `"sd busy"` vs. `"Klippy is busy initializing"` correctly classified as false.
- Case-insensitivity handled.
- Full request path: firmware 503 → Moonraker body inspection → phrase allowlist → `PrinterBackendBusyException` → controller `Conflict()` → API 409.
- **APPROVE round 24** (with Hicks).

### Key Learnings

1. **For cross-layer backend changes (controller ↔ service ↔ plugin), trace the complete request path end-to-end in code review.** Don't trust intermediate stages alone. Verify:
   - HTTP request enters the plugin correctly.
   - Plugin returns typed exception or domain result.
   - Service/controller maps that to the correct HTTP status.
   - Downstream consumers (UI, queue, scheduler) receive the correct signal.

2. **Phrase-based classification beats substring matching.** External error bodies are ambiguous. Explicit phrase allowlists are more durable than regex or substring scans.

3. **Cross-layer PRs require paired review.** Future requirement: pair Bishop+Hicks (or Bishop+Vasquez) on all backend cross-layer changes, with at least one reviewer documenting end-to-end path verification in review notes.

### Pattern

- All future cross-layer backend PRs: require evidence of end-to-end path verification in review comments.
- Error-body classification rule (added to squad decisions): phrase-based allowlists with explicit semantics, not bare substring matches.

## Round 25-26: PR #16 Review — PrinterControlsSection Async Loading (2026-06-10 to 2026-06-12)

**PR:** `squad/287-integrate-controls-section` (OlyForge3D/PrintFarmerMobile)
**Status:** Fully approved (round 26). Stacked on unmerged controls v1 base chain.

### Round 25: COMMENT (Not Blocking)

Bishop reviewed Hudson's PrinterControlsSection integration:
- Composition logic clean, layout variance sound, capability gating correct (12 tests).
- **Flagged:** Loading-state test weak. Mock returns immediately, so test only verifies endpoints (start: false, end: false). Cannot observe `isLoadingCapabilities == true` mid-flight.

Vasquez APPROVE (controls composition sound despite test gap).

### Round 26: Re-APPROVE After Fix

Hudson implemented `HoldablePrinterService` using `withCheckedThrowingContinuation` to suspend mid-fetch:
- Two new tests assert `isLoadingCapabilities == true` mid-flight.
- One confirms false after resolve.
- 14 tests total.

Bishop verified fix and re-APPROVE. Question answered: **the test now observes the transition, not just endpoints**.

### Key Learnings

1. **For async view-state tests, ask: "Can this test assert the in-flight state, or just the endpoints?"** Immediate-return mocks prove nothing about transitions. Continuation-based holds enable real async pause points for in-flight assertions.

2. **Loading-state + capability-gating + multi-layout designs compound test surface.** Preemptively design for async holds when laying out feature specs.

3. **Test rigor = end-to-end view behavior.** If a test can only see start and end, it's not testing the actual user-visible transition (the loading spinner, disabled controls, etc.).

### Pattern

- All async loading UI tests: require continuation-based hold-point to assert mid-flight state.
- Test review rule: ask "what does this test actually observe?" If only endpoints, request continuation-based redesign.

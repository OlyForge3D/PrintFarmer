# RALPH Log — Squad Activity Summary

## PR #318: Firmware 409 Propagation — Rounds 22-24

**Title:** feat(backends): propagate firmware-409 from Moonraker/SDCP/FlashForge plugins

**PR Link:** [OlyForge3D/PrintFarmer#318](https://github.com/OlyForge3D/PrintFarmer/pull/318)

**Status:** OPEN with all CI checks passing.  
**Final State:** Two-reviewer APPROVE (Bishop + Hicks, round 24).

### Timeline

**Round 22 (2026-05-21)**
- **Parker**: Triaged 9 Dependabot PRs into 3 buckets (auto-merge / verify-then-merge / GH Actions majors). Artifact: `.squad/parker/triage-2026-05-21.md`.
- **Bishop**: REQUEST_CHANGES on PR #318 — caught 2 critical architectural blockers Hicks missed:
  1. `PrintersController.MapControlOutcome()` returning HTTP 502 (not 409) for `PrinterBackendBusyException`.
  2. Moonraker treating all HTTP 503 as printer-busy — too broad, no body inspection.

**Round 23 (2026-05-23 to 2026-05-27)**
- **Lambert**: Fixed both blockers (commit `51d1bb9c3`):
  - Controller now returns `Conflict()` (409) for BackendBusy.
  - Moonraker narrowed via body inspection (substring match on `"busy"`).
  - Test port allocator hardened with rebind+retry (10 attempts).
  - 2 controller + 5 Moonraker tests added.
- **Bishop + Hicks**: Both BLOCK re-review — substring match still over-broad. False-positive on `"Klippy is busy initializing"` (should be false, not throw busy).

**Round 24 (2026-05-27 to 2026-05-29)**
- **Lambert**: Tightened (commit `90699107b`) with phrase-based allowlist in `IsMoonrakerBusyPrintingBody()`:
  - Allowed phrases (case-insensitive): `"printer is printing"`, `"printer is currently printing"`, `"printer is busy"`, `"printer busy"`, `"sd busy"`.
  - Negative test: `"Klippy is busy initializing"` → correctly returns false.
  - 3 new tests (phrase allowlist + case-insensitivity). 35 Moonraker tests passing.
- **Bishop + Hicks**: Both APPROVE round 24. PR #318 fully approved.

### Key Decisions Recorded

**Decision #99:** Error-Body Classification Rule — phrase-based allowlists with explicit semantics, not bare substring matches. Prefer false-negative over false-positive.

**Decision #100:** End-to-End Review Rule for Cross-Layer Backend Changes — pair Bishop+Hicks (or Bishop+Vasquez) on all controller ↔ service ↔ plugin changes; require documented end-to-end path verification in review notes.

### Squad Learning Summary

1. **Error-body classification**: phrase-based allowlists > substring matching.
2. **Cross-layer review**: pair Bishop+Hicks; require end-to-end path verification (HTTP request → controller → service → plugin → exception → HTTP response).
3. **False-negatives preferred**: ambiguous cases should fail safe (return false), not poison downstream logic (print queue, device scheduler, system-state transitions).
4. **Plugin-layer logic alone is insufficient**: full wiring chain must be verified before approval.

### Artifacts

- **Agent history entries**: `.squad/agents/lambert/history.md`, `.squad/agents/bishop/history.md`, `.squad/agents/hicks/history.md`, `.squad/agents/parker/history.md`
- **Decision rules**: `.squad/decisions.md` (decision #99, #100)
- **Dependabot triage**: `.squad/parker/triage-2026-05-21.md`

---

## PR #16: PrinterControlsSection Integration — Rounds 25-26

**Title:** `squad/287-integrate-controls-section` (OlyForge3D/PrintFarmerMobile)

**PR Link:** [OlyForge3D/PrintFarmerMobile#16](https://github.com/OlyForge3D/PrintFarmerMobile/pull/16)

**Status:** Fully approved (round 26). Stacked on unmerged controls v1 base chain (#11/12/13).  
**Final State:** Two-reviewer APPROVE (Vasquez round 25 + Bishop round 26).

### Timeline

**Round 25 (2026-06-10)**
- **Hudson**: Shipped PR #16 integrating all three `PrinterControlsSection` subgroups (jog, feed rate, safety) into `PrinterDetailView`:
  - Phone layout: 1-column controls below printer status.
  - iPad layout: 2-column with sidebar controls.
  - Capability gating: hide controls if printer lacks required axes/e-stop.
  - 12 tests: view state transitions, layout variance, accessibility identifiers.
- **Bishop**: COMMENT (not blocking) — flagged weak loading-state test. Mock returns immediately, so test cannot observe `isLoadingCapabilities == true` mid-flight. Only sees start (false) → end (false), no transition observation.
- **Vasquez**: APPROVE — controls composition correct, capability gating sound, layout logic clean. Approved with note that test rigor will be addressed in round 26. Read assertions from view state, not through internals (no-ViewInspector ceiling applies).

**Round 26 (2026-06-12)**
- **Hudson**: Surgical fix (commit `3da6249`):
  - Implemented `HoldablePrinterService` (private to test file).
  - Uses `withCheckedThrowingContinuation` to suspend mid-fetch.
  - Two new tests assert `isLoadingCapabilities == true` mid-flight (before continuation releases).
  - One new test confirms `isLoadingCapabilities == false` after resolve.
  - 14 tests total (12 → 14).
- **Bishop**: Re-reviewed and re-APPROVE. Question answered: test now observes the transition, not just endpoints.

### Key Decisions Recorded

**Decision:** Async Loading-State Test Rule — when asserting `isLoading` transitions correctly, mock must support explicit hold-point (e.g., `CheckedContinuation`) so test observes in-flight state. Immediate-return mocks cannot prove transition. See `.squad/decisions.md` for full rule.

### Squad Learning Summary

1. **Async loading-state transitions require hold-point mocks.** Immediate-return mocks only verify endpoints; cannot observe mid-flight state (loading spinner, disabled controls). Use continuation-based holds for real async pause points.

2. **Capability gating + loading states compound test complexity.** Future feature gate work should preemptively design for testable async holds.

3. **Stacked PRs on unmerged base chains need clear approval signals.** Vasquez + Bishop marked approval despite merge queue blockage, helping unblock dependent work visibility.

4. **Test rigor = view behavior.** iOS view tests without ViewInspector must assert through @State-observable output, not internal rendering. Design tests around observable state.

### Artifacts

- **Agent history entries**: `.squad/agents/hudson/history.md`, `.squad/agents/bishop/history.md`, `.squad/agents/vasquez/history.md`
- **Decision rules**: `.squad/decisions.md` (Async Loading-State Test Rule)
- **Squad bottleneck status**: All P0/P1 squad PRs across both repos (PrintFarmer + PrintFarmerMobile) now approved. Bottleneck shifts to Jeff's merge queue (unmerged base chains, deployment scheduling).

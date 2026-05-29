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

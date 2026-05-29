# Hicks — History

## Core Context

- Code Reviewer on PrintFarmer project
- Uses Gemini 3 Pro Preview model for review perspective diversity
- Part of triple-model pre-commit review gate (with Bishop and Vasquez)
- Project: C# .NET 10 API + React 19 TypeScript frontend for 3D printer management
- Owner: Jeff Papiez

## Learnings

_(append new learnings below this line)_

## Round 22-24: Cross-Layer Review Miss & Recovery — PR #318 (2026-05-21 to 2026-05-29)

**PR:** feat(backends): propagate firmware-409 from Moonraker/SDCP/FlashForge plugins  
**Status:** OPEN, all CI checks passing. Final two-reviewer APPROVE (Bishop + Hicks, round 24).

### Round 22: Initial Approval Miss

Approved the PR without catching two critical architectural blockers that Bishop caught:
1. `PrintersController.MapControlOutcome()` returning HTTP 502 instead of 409 for `PrinterBackendBusyException`.
2. Moonraker treating all HTTP 503 as printer-busy — too broad without body inspection.

**Lesson:** Helper-level unit tests and plugin-layer logic are not sufficient for correctness. Must verify **full wiring chain**: controller → service → plugin → exception → HTTP response. Helper tests pass ≠ end-to-end behavior is correct.

### Round 23: Substring Over-Match Blocker

After Lambert fixed the 502→409 mapping and narrowed Moonraker 503 via body substring match, both Hicks AND Bishop blocked re-review. Bare substring match (`"busy"` anywhere in body) is still too broad — false-positive on `"Klippy is busy initializing"`.

**Shared Learning with Bishop:** Substring matching in external error-body classification is fragile. Simple text scans conflate unrelated error messages.

### Round 24: Final Approval

Lambert tightened with phrase-based allowlist. Hicks verified semantics:
- Phrase allowlist explicit: `"printer is printing"`, `"printer is busy"`, `"sd busy"` (printer-device states) vs. `"Klippy is busy initializing"` (firmware startup, not printer-busy).
- Case-insensitivity correct.
- Controller wiring verified: firmware 503 → plugin body inspection → exception → HTTP 409.
- **APPROVE round 24** (with Bishop).

### Key Learnings

1. **Don't rely on component-level tests alone.** Even if plugin tests pass and helper methods work correctly, integration across layers (controller → service → plugin → exception → HTTP) can still break. Require full end-to-end verification before approving cross-layer changes.

2. **Phrase-based classification, not substring matching.** External error bodies are ambiguous. Explicit phrase allowlists are more durable than regex or substring scans.

3. **Cross-layer PRs require paired review.** Future requirement: pair Bishop+Hicks (or Bishop+Vasquez) on all backend cross-layer changes, with at least one reviewer documenting end-to-end path verification in review notes.

### Pattern

- Always verify the complete request-response path for backend changes that span multiple layers.
- For backend cross-layer changes (controller ↔ service ↔ plugin): require pairing with Bishop or Vasquez and evidence of end-to-end path verification in review notes.

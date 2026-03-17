# Decision: Bed Pre-Clear Feature for Auto-Print

**Date:** 2026-03-20  
**Agent:** Lambert (Backend Developer)  
**Status:** Implemented ✅  
**Impact:** Medium — Improves auto-print workflow efficiency

## Context

Users wanted the ability to tell the system "the printer bed is already clear" BEFORE a job finishes, so when the job completes, the next job dispatches immediately without waiting for the manual "bed clear" confirmation (PendingReady state).

This is particularly useful when:
- Operator is monitoring the printer and knows they'll clear the bed immediately
- Multiple operators are available and one can clear the bed while another queues jobs
- Reducing friction in high-throughput print farm operations

## Decision

Implemented a **pre-confirmation flag** (`BedPreConfirmed`) on the `Printer` entity that allows operators to declare bed readiness ahead of time.

### Implementation Details

1. **Database Schema**
   - Added `BedPreConfirmed: bool` to `Printer` entity (defaults to `false`)
   - Created EF Core migrations for both PostgreSQL and SQL Server

2. **API Surface**
   - New endpoint: `POST /api/auto-print/{printerId}/pre-clear`
   - Returns `AutoPrintStatusDto` with `bedPreConfirmed` field
   - Validation guards:
     - Auto-print must be enabled
     - Printer must be idle (not actively printing)

3. **Workflow Integration**
   - **At job completion** (`TransitionToPendingReadyAsync`):
     - If `BedPreConfirmed == true` → skip PendingReady, go straight to Ready
     - Reset flag after using it
     - Trigger immediate dispatch
   - **At dispatch time** (`AutoDispatchBackgroundService`):
     - Allow dispatch if `AutoPrintState == Ready OR BedPreConfirmed == true`
     - Reset flag after successful dispatch

4. **State Lifecycle**
   - Flag is **single-use** — automatically reset after:
     - Job dispatch completes
     - Transition through PendingReady state
     - No queued jobs remaining
   - Prevents perpetual pre-clear state

## Alternatives Considered

1. **Auto-transition to Ready after N seconds** — Rejected: unsafe, no operator control
2. **Queue-level pre-clear (all jobs)** — Rejected: too coarse-grained, doesn't respect per-job bed clearing
3. **Camera-based bed detection** — Rejected: requires ML integration, out of scope

## Consequences

### Positive
- ✅ Zero friction for operators who know bed will be clear
- ✅ Reduces dispatch latency from ~30s (manual confirmation) to immediate
- ✅ Backwards compatible — existing auto-print workflow unchanged
- ✅ Flag automatically resets, no stale state risk

### Negative
- ⚠️ Operator could pre-clear when bed isn't actually clear (user error)
- ⚠️ Adds another button to UI (frontend team needs to design placement)

### Neutral
- Frontend work required to expose the pre-clear button
- Webhook event added: `printer.bed_pre_confirmed`

## Validation

- **Build:** Clean (0 errors, 0 warnings)
- **Tests:** All 2087 tests passing
- **Format:** Compliant with dotnet format
- **Migrations:** Created for both database providers

## Related Work

- **Frontend (pending):** UI to expose "Pre-Clear Bed" button
- **Documentation (pending):** User guide for pre-clear feature
- **Monitoring (future):** Track pre-clear usage metrics (is it being used?)

## Notes

This feature complements the existing auto-print workflow rather than replacing it. Operators can choose:
1. **Traditional flow:** Wait for job to complete → manual "Ready" confirmation → dispatch
2. **Pre-clear flow:** Mark bed pre-clear → job completes → immediate dispatch

The flag's automatic reset ensures the feature is safe and doesn't leave the system in an unexpected state.

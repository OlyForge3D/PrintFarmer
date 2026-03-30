---
name: "auto-dispatch-canonical-pendingready"
description: "Keep auto-dispatch PendingReady status, ready-gate checks, and cancel semantics aligned when persisted state lags behind the operator-facing workflow."
domain: "backend-logic"
confidence: "high"
source: "earned"
---

## Context
Use this when the backend exposes auto-dispatch status for printer cards or dashboards and a queued printer can surface stale persisted workflow state (`None`) even though the operator still needs to clear the bed before dispatch resumes.

## Patterns
- Build status DTOs from an **effective** workflow state, not only the persisted enum.
- Normalize stale `None` to `PendingReady` only when the printer is idle, available, auto-dispatch-enabled, not pre-cleared, and queued work exists.
- If operators can dismiss the prompt, persist a separate internal sentinel (for example `Dismissed`) so status normalization does not immediately recreate the banner after a cancel action.
- Fail the `Bed Clear Confirmed` gate only when operator confirmation is actually required; canonical `None` rows should not show a misleading red gate.
- Reuse the same effective-state logic in action endpoints like `MarkReadyAsync()` so the confirm action works against the same contract the UI rendered.

## Examples
- `src/infra/Domain/AutoDispatchState.cs`
- `src/infra/Services/AutoDispatch/AutoDispatchService.cs`
- `src/tests/Farm.Web.Api.Tests/Controllers/AutoDispatchPendingReadyTests.cs`
- `src/tests/Farm.Web.Api.Tests/Services/AutoDispatch/AutoDispatchReadyGateServiceTests.cs`

## Anti-Patterns
- Returning `state = None` together with a failed/red `Bed Clear Confirmed` gate for an actionable queued printer.
- Teaching the UI to guess whether a failed bed-clear gate is actionable from queue depth alone.
- Using a single persisted `None` value for both “nothing to do” and “operator explicitly dismissed the gate.”

---
name: "actionable-attention-copy"
description: "Turn vague frontend attention states into explicit issue + operator action messaging using backend reason fields and local UI context."
domain: "frontend-state"
confidence: "high"
source: "earned"
---

## Context
Use this when a frontend surface shows a status like `Attention`, `Needs attention`, `Degraded`, or `Unhealthy`, but the user still cannot tell what is wrong or what to do next. This applies especially to monitoring overlays, camera health cards, and other dense operational UIs.

## Patterns
- Treat the backend reason field (`reason`, `healthMessage`, etc.) as the source of truth for “what’s wrong.”
- Derive a separate frontend `Action:` sentence based on the reason text and local UI state (selected mode, fallback availability, whether monitoring is active).
- Keep dense layouts compact by using a short status header plus a second line of issue/action copy.
- If the compact surface already opens a modal or detail pane, the trigger should still communicate the failure mode (`Monitor error`, `Needs setup`) instead of a vague label.
- When preview/runtime failures happen in the UI itself (`imageError`, missing URLs), combine that local failure state with backend health data so the operator gets both the broken behavior and the recovery path.

## Examples
- `src/Web/ReactApp/src/features/printers/utils/failureDetectionStatus.ts`
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringOverlay.tsx`
- `src/Web/ReactApp/src/features/cameras/utils/cameraAttention.ts`
- `src/Web/ReactApp/src/features/cameras/pages/CamerasPage.tsx`
- `src/Web/ReactApp/src/test/features/cameras/cameraAttention.test.ts`

## Anti-Patterns
- Rendering `Needs attention` as the only operator-facing message
- Hiding all useful context in a secondary modal while leaving the primary surface vague
- Inventing specific failure reasons when the payload only contains a coarse enum
- Repeating raw backend reason text without adding the next-step action

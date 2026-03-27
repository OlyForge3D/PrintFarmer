---
name: "monitoring-lifecycle-badges"
description: "Keep frontend monitoring badges aligned with whether monitoring is actually active, not just with raw subsystem error states."
domain: "frontend-state"
confidence: "high"
source: "earned"
---

## Context
Use this when the UI shows a secondary subsystem status badge (camera AI, health monitoring, background scanning, etc.) alongside a primary entity state. These badges often regress when the frontend renders raw error states even though the subsystem is not actively running yet.

## Patterns
- Check for an explicit lifecycle signal like `isPrinting`, `isActive`, or `isMonitoring` before surfacing attention/error UI.
- Centralize normalization in a shared helper so header badges, overlays, tooltips, and detail panels stay consistent.
- Treat pre-start or inactive error payloads as neutral/checking display state unless the feature is actively expected to be running.
- Keep the raw backend payload available for debugging, but derive operator-facing labels from normalized display state.

## Examples
- `src/Web/ReactApp/src/features/printers/utils/failureDetectionStatus.ts`
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringBadge.tsx`
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringOverlay.tsx`
- `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringBadge.test.tsx`

## Anti-Patterns
- Rendering `error` badges directly from raw status payloads without checking whether monitoring is active
- Fixing only one surface (for example, the camera overlay) while leaving summary badges inconsistent
- Hiding real active-monitoring failures by collapsing all error states to neutral

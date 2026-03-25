---
name: "compact-status-detail-modal"
description: "Teach agents when dense status chips should launch a shared details modal instead of inlining issue text"
domain: "frontend-ui"
confidence: "high"
source: "earned"
---

## Context
Use this when a dashboard card, preview overlay, or other dense UI surface needs to show an alert/status affordance without turning the compact chip into a paragraph.

## Patterns
- Keep the compact surface glanceable: status label plus icon is usually enough.
- Make the status affordance itself clickable and open a shared modal for operator-facing detail.
- Put the actionable content in the modal: why the state is showing, what the operator should do next, and any supporting facts like timestamps or snapshot links.
- Pass contextual fallbacks such as `printerName` explicitly so neutral/loading states still have accessible trigger labels.
- Reuse one modal component across badge and overlay variants so copy and metadata stay consistent.

## Examples
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringBadge.tsx`
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringOverlay.tsx`
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionStatusModal.tsx`

## Anti-Patterns
- Expanding a compact badge/overlay with multi-line remediation copy that competes with the rest of the layout.
- Giving badge and overlay variants different detail copy for the same backend status.
- Omitting accessible trigger text when the detail affordance appears before printer-specific status has loaded.

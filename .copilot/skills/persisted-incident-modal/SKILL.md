---
name: "persisted-incident-modal"
description: "Blend live SignalR incidents with persisted history in a details modal while keeping card summaries focused on current operator action"
domain: "frontend-ui"
confidence: "high"
source: "earned"
---

## Context
Use this when a dashboard card already has a compact live summary, but a new backend history endpoint exists for deeper incident drill-down. Operators need honest recent history without turning the card itself into a timeline.

## Patterns
- Keep compact and detailed cards focused on live state: current coverage, latest result, and operator action.
- Fetch persisted history only for the drill-down seam (`useFailureDetectionHistory()` in the modal trigger path), not for every card body by default.
- Merge persisted history with live SignalR incidents so just-detected events appear immediately even before the history poll catches up.
- Deduplicate merged incidents with a shared helper keyed by printer, timestamp, confidence, pause outcome, snapshot URL, and optional job/file context.
- Use job/file context chips in the modal list when the backend provides them; this improves operator trust without adding a separate timeline page.

## Examples
- `src/Web/ReactApp/src/common/hooks/useApi.ts`
- `src/Web/ReactApp/src/services/api.ts`
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionStatusModal.tsx`
- `src/Web/ReactApp/src/features/printers/utils/failure-detection-incidents.ts`

## Anti-Patterns
- Showing session-only incident counts on cards after persisted history exists.
- Adding a standalone history page when operators already have a badge → modal drill-down.
- Letting modal history lag behind live incidents when a fresh SignalR event has already fired.

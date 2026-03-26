---
name: "operational-monitoring-summary"
description: "Turn a small monitoring badge into a reusable operator summary panel that carries live status, action guidance, and session incidents."
domain: "frontend-ui"
confidence: "high"
source: "earned"
---

## Context
Use this when a compact dashboard badge is good for glanceability, but operators also need a persistent in-surface summary of what the monitoring subsystem is doing right now. This fits printer monitoring, camera health, worker coverage, or any runtime that has a tiny header affordance plus richer live state.

## Patterns
- Keep the compact badge for quick state recognition and modal access; do not overload it with paragraphs.
- Add a shared summary panel on the main card surface that answers four operator questions fast: **Is coverage active? What happened last? What should I do? What has happened this session?**
- Feed the summary from the existing polling status plus any in-memory realtime events. In PrintFarmer, `useFailureDetectionAlert()` now supplies both the transient live event and a short session incident ledger.
- Use industrial, high-signal styling: dense metadata tiles, uppercase section labels, explicit action copy, and strong state color without looking ornamental.
- When backend history does not exist yet, make the limitation explicit in naming and keep the session ledger local to the current frontend lifetime.

## Examples
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringSummary.tsx`
- `src/Web/ReactApp/src/features/printers/components/CompactPrinterCard.tsx`
- `src/Web/ReactApp/src/features/printers/components/DetailedPrinterCard.tsx`
- `src/Web/ReactApp/src/features/printers/hooks/useFailureDetectionAlert.ts`
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionStatusModal.tsx`

## Anti-Patterns
- Replacing the badge entirely with a bulky inline paragraph.
- Hiding operator action behind a modal while leaving the card surface vague.
- Showing only the most recent transient alert and dropping all session context once the toast expires.
- Pretending local session incidents are permanent history when they reset on refresh/navigation.

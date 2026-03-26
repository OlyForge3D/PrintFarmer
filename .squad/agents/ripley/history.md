# Ripley History

## Core Context

Ripley is the frontend architect and API integration specialist. Key retained context:
- Owns printer-card UX, BedClearBanner behavior, and frontend cache/signal updates for auto-dispatch state.
- Prefers centralizing transport compatibility in `src/Web/ReactApp/src/services/` wrappers so product language can stay clean in hooks/components.
- Uses focused React integration tests to protect compact-card, banner, and SignalR merge seams where stale partial payloads can hide operator actions.
- Consolidates repeated status affordances into a single predictable surface when duplicate UI adds cognitive load.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history
- 2026-03-25: Finalized icon-only failure-detection badge behavior, removed redundant camera overlays, and documented the header-badge-as-single-source pattern.
- 2026-03-25: Landed PendingReady compact-card fallback + live merge protections so failed bed-clear gates stay visible across stale bulk snapshots and partial SignalR payloads.
- 2026-03-25 to 2026-03-26: Completed frontend transport alignment toward canonical auto-dispatch naming while preserving a safe adapter strategy during transition.

## 2026-03-25: PendingReady compact-card fallback fix → LANDED

**Role:** Frontend Dev  
**Status:** ✅ Complete — commit e807133d landed on development

- Fixed `CompactPrinterCard` / `BedClearBanner` handling so a failed bed-clear gate with queued work still surfaces actionable Pending Ready UI even when the flattened bulk state is stale.
- Protected the live-update seam by preserving prior optional ready-gate detail when partial SignalR payloads omit it.
- Focused validation stayed green: 44/44 React tests in the targeted slice.

**Key files:**
- `src/Web/ReactApp/src/common/utils/printerStateDisplay.ts`
- `src/Web/ReactApp/src/features/printers/hooks/useAutoDispatch.ts`
- `src/Web/ReactApp/src/features/printers/__tests__/BedClearBanner.test.tsx`
- `src/Web/ReactApp/src/test/features/printers/compact-printer-pendingready-live.test.tsx`

## 2026-03-27: Failure Detection Timeline Decision — NO TIMELINE VIEW

**Role:** Frontend affected  
**Status:** Recommendation from Dallas (Lead) — Ready for implementation

From Dallas decision: Failure detection is a real-time monitoring state machine, not a persisted historical audit log. Recommendation is to **NOT implement a timeline view**. Current modal + header badge pattern is fit-for-purpose.

**Next steps for Ripley:**
- Finalize badge + modal pattern. No timeline pagination or scroll within modal.
- Call complete when modal shows all current state fields: coverage source, snapshot URL, last scan, last outcome, last failure, auto-pause action, next step.
- See decision entry in `.squad/decisions.md` (entry 4) for full rationale.

## Learnings

- 2026-03-26: The spaghetti-detection modal is presentational only. The live data path is `CompactPrinterCard` / `DetailedPrinterCard` → `usePrinterFailureDetectionStatus` → `apiClient.getFailureDetectionStatus()` → `GET /api/failure-detection/status`, then the hook filters `printers[]` by `printerId` before passing `status` into `FailureDetectionMonitoringBadge` / `FailureDetectionStatusModal`.
- 2026-03-26: `FailureDetectionStatusModal.tsx` does not issue its own request or send a payload; if the modal shows a transport error, inspect the upstream card hook and `/api/failure-detection/status` contract first.
- 2026-03-26: `useFailureDetectionAlert()` is now the frontend session-memory seam for failure incidents. It still exposes the transient 60-second `event`, but also keeps up to five recent `FailureDetected` SignalR events per printer so cards and modals can show session-level incident history without a backend history endpoint.
- 2026-03-26: The operator-facing failure-detection pattern is now `header icon badge for compact state` + `card-level operational summary panel for live session context`. Compact and detailed printer cards both reuse `FailureDetectionMonitoringSummary.tsx`, while `FailureDetectionStatusModal.tsx` accepts `recentEvents` for richer drill-down.
- 2026-03-27: Failure detection is live monitoring, not historical audit. Modal is the right interaction depth; no timeline needed.

## 2026-03-27: Failure Detection UX — Scope Clarification (Cross-Agent)

**Input:** Dallas decision memo on failure-detection timeline UX scope  
**Status:** Pending team decision

Failure detection UX scope clarified: Badge + modal pattern is recommended. No timeline/historical event list. Current modal shows state, coverage, last scan, last outcome—sufficient for operators. Awaiting team approval to finalize badge/modal implementation.

## 2026-03-26: Failure Detection UX — Two-Layer Surface → LANDED

**Role:** Frontend Dev  
**Status:** ✅ Complete — Orchestration log: 20260325-193351-ripley.md

- Implemented shared failure-detection summary panel (`FailureDetectionMonitoringSummary.tsx`) for both compact and detailed printer cards
- Panel shows live coverage state, latest result, monitoring target, operator action, and in-session incident memory
- Enhanced `useFailureDetectionAlert.ts` to track and expose up to 5 recent incidents per printer (session-scoped memory)
- Updated `FailureDetectionStatusModal.tsx` to carry recent incidents for drill-down
- Kept header badge as compact glanceability affordance and modal trigger
- Prevents header noise while giving operators quick access to failure-detection context without modal fatigue

**Validation:**
- 23 targeted failure-detection frontend tests passed
- Production React build passed with 0 new TypeScript errors
- ESLint passed with 0 new errors

**Key integration:**
- Merged with Lambert's backend job-context enrichment (`jobName`/`fileName` on API/SignalR payloads)
- In-session incident history enables drill-down without requiring backend history endpoint
- Pattern consistent across both card types reduces cognitive load

**Known gap:** Long-term incident history remains a backend follow-up (descoped from current work)

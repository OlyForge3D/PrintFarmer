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
- 2026-03-27: Persisted failure-detection history now belongs in the modal-first drill-down, not the card body. Frontend path is `apiClient.getFailureDetectionHistory()` → `useFailureDetectionHistory()` → `FailureDetectionStatusModal.tsx`, where persisted incidents are merged with live SignalR incidents via `features/printers/utils/failure-detection-incidents.ts` so operators see fresh alerts plus honest recent history.
- 2026-03-27: `FailureDetectionMonitoringSummary.tsx` should stay live-and-action focused. Keep card surfaces centered on current coverage, last result, and operator action; move multi-incident history detail into the modal so cards do not imply durable timeline storage.
- 2026-03-27: The best v1 print-session timeline lives inside `FailureDetectionStatusModal.tsx`, not on a separate page. Frontend can already assemble it from persisted/live `FailureDetectionEvent.jobId` context plus `apiClient.getAnalyticsJobStateHistory()` rendered through `PrintSessionTimeline.tsx`, while incidents without `jobId` should stay in the incident list with an explicit limitation message.
- 2026-03-27: `FailureDetectionMonitoringSummary` is now print-active only — hidden when printer is idle/offline/standby. Both `CompactPrinterCard` and `DetailedPrinterCard` gate on `(isPrinting || isPaused)`. Header badge remains the sole FD indicator at rest. At-rest states ("Standing by", "Off") duplicated info the badge already shows; during printing the summary shows unique operational context (scan results, confidence, actions, snapshots).
- Date range filters are now centralized in `TimePeriodFilter` component (`@/common/components/ui/TimePeriodFilter.tsx`). Constants live in `timePeriodOptions.ts` to satisfy react-refresh rules. Standard options: 7d/30d/90d/1yr/All time plus Custom. All three stats pages (Statistics, Analytics, Cost) use this shared component.
- Cost hooks (`useCostSummary`, `useCostsByPrinter`, `useCostsByMaterial`) accept `(days?, startDate?, endDate?)`. Query keys are now functions: `queryKeys.costSummary(days, startDate, endDate)`, etc. The apiClient methods pass either `days` or `startDate/endDate` as query params — never both.
- `TimePeriodFilterValue` is a discriminated union: `{ type: 'preset'; days: number | undefined } | { type: 'custom'; startDate: string; endDate: string }`. Pages manage this as state and derive `days`/`startDate`/`endDate` for hook consumption.
- Statistics hooks in `useStatistics.ts` also accept `(days?, startDate?, endDate?)` using `buildStatsParams()` helper to construct query strings.
- `ScheduleModal` fetches available jobs via `useQuery` + `apiClient.getJobQueue()` and presents them in a `Select` dropdown filtered to Queued/Assigned status. The old raw text input for Job ID is gone.
- `QueueJobsTable` has an `onSchedule` callback prop; the Schedule button appears for Queued/Assigned jobs alongside existing actions. `PrintQueueDashboardPage` wires this to open the `ScheduleModal` with the job pre-selected.
- The Settings page is **metadata-driven**: backend classes with `[AppSetting]` + `[SettingDisplay]` attributes are auto-discovered by `SettingsService` and rendered dynamically by `SettingsPagelet`. No per-section frontend code is needed — adding a new settings section only requires backend attributes. CostTrackingSettings already has all required attributes and renders automatically under the "Operations" sidebar group.
- `CostTrackingSettings` TypeScript interface added to `@/types/api` with typed convenience methods `apiClient.getCostTrackingSettings()` / `apiClient.updateCostTrackingSettings()` for direct access from cost features. The generic `getSettings<T>("CostTracking")` also works.
- `AddPrinterModal` and `EditPrinterModal` both have a "Cost Settings" section with Wattage (W) and Machine Hourly Rate ($) fields. Empty values submit as `undefined` (backend treats as null), numeric values pass through. EditPrinterModal pre-populates from `printerDetails.wattage` / `printerDetails.machineHourlyRate` and includes both fields in dirty-state change detection.
- Backend `PrinterDetailsDto` now returns `Wattage` and `MachineHourlyRate` from the Printer entity, and the TypeScript `PrinterDetails` interface mirrors them as `wattage?` and `machineHourlyRate?`.
- `PrinterModelsCatalog` shows `defaultWattage` as a badge in the Features column when set on the model (e.g., "250W").

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

## 2026-03-26: Persisted Failure-Detection History → Modal Integration → LANDED

**Role:** Frontend Dev  
**Status:** ✅ Complete — Orchestration log: 2026-03-26T02-58-26Z-ripley.md

- Loaded persisted failure-detection incidents from backend history endpoint in `FailureDetectionStatusModal.tsx`
- Merged persisted history with live SignalR events so fresh incidents appear immediately
- Shared utility `failure-detection-incidents.ts` handles the merge logic
- Modal now displays job/file context and snapshot links from persisted records
- Printer cards remain focused on live monitoring (coverage, latest result, action)

**Validation:**
- 23 targeted React tests passed
- `npm run build` ✅ (0 TypeScript errors)
- `npm run lint` ✅

**Architectural note:** Modal-first design keeps the feature scoped. Backend persists incidents; frontend merges with live events in the drill-down modal. Cards stay lean and live-focused.

**Decisions merged:** #9 (backend persistence), #10 (frontend integration)

## Session: Print Session Timeline v1 Frontend — Complete (2026-03-27)

**Role:** Frontend implementation lead  
**Status:** COMPLETE — All artifacts delivered, tests pass

### Work Completed

- **Integration:** Timeline tab embedded in `FailureDetectionStatusModal.tsx`
- **Workflow:** Use latest incident's `jobId` to reconstruct session context
- **UX:** Chronological rendering of mixed event types (state_change + failure_incident)
- **Fallback:** Plain message "Timeline unavailable for this record" when incident lacks jobId
- **Tests:** 3/3 focused component tests PASS
- **Build:** Production React build passes, ESLint clean, 0 errors

### Orchestration Log

Published: `.squad/orchestration-log/20260326-031539-ripley.md`

### Key Design Decision

**Timeline stays inside modal; no standalone page.** Modal-first design keeps printer card live/current and timeline contextual to incident drill-down.

### Test Coverage

- Chronological rendering of mixed event types ✅
- Auto-pause and snapshot affordances displayed ✅
- Empty state handling ✅

**Handed off to:** Kane for validation gate
- **2026-03-27: Multi-toolhead filament tracking frontend (Phase 6b-6e):** Added `useSetToolheadSpool()` and `useClearToolheadSpool()` hooks to `useApi.ts`. Created `ToolheadSpoolPicker` component for assigning spools to individual toolheads (reuses existing `SpoolPickerModal`). Added `toolheadUsages` field to `JobDetails` type. Enhanced `JobDetailsSection` to display per-toolhead filament usage in job history with material, weight, cost breakdown and totals. Descoped multi-spool display in `CompactPrinterCard` to avoid N+1 queries — more appropriate in detail views. All changes compile and lint cleanly (0 TypeScript errors, 1 pre-existing ESLint warning).
- **2026-01-11: Multi-toolhead spool integration (ToolheadSpoolPicker in PrinterDetailsSidebar):** Integrated the orphaned `ToolheadSpoolPicker` component into `PrinterDetailsSidebar.tsx`. Added `usePrinterDetails` hook call to fetch printer toolhead configuration. Implemented conditional rendering: multi-toolhead printers (2+ toolheads) show `ToolheadSpoolPicker` with individual toolhead spool assignments; single-toolhead printers show the existing single `LoadedFilamentCard`. Section title changes from "Spool" to "Spools" for multi-toolhead. Header actions (Eject/Change buttons) only appear for single-toolhead mode since multi-toolhead has per-toolhead controls. Key files: `PrinterDetailsSidebar.tsx`, `ToolheadSpoolPicker.tsx`, `useApi.ts` (`usePrinterDetails`, `useSetToolheadSpool`, `useClearToolheadSpool`). Build, lint, and all 1659 tests pass (0 errors).
- **MMU spool unification:** MMU printers (QidiBox, HappyHare, AFC) expose multi-material gates via `Printer.mmuStatus.gates[]` (live SignalR), not `PrinterDetails.toolheads[]` (config API). The spool section now detects MMU gates as a fallback multi-spool source. `mmuGatesToToolheads()` in `features/printers/utils/mmuGatesToToolheads.ts` converts `MmuGate[]` → `ToolheadDto[]`. Priority: use `printerDetails.toolheads` if backend has synced virtual MmuGate entries; otherwise convert live gate data. Also fixed `ToolheadSpoolPicker` to use `toolhead.index` (actual DB index) instead of array position for API calls. Key files: `PrinterDetailsSidebar.tsx`, `DetailedPrinterCard.tsx`, `ToolheadSpoolPicker.tsx`, `mmuGatesToToolheads.ts`. Backend toolhead spool endpoints (`PUT/DELETE /printers/{id}/toolheads/{index}/spool`) work for MmuGate toolheads since `SyncMmuVirtualToolheads` creates virtual Toolhead records at matching indices.
- **2026-01-11: Per-toolhead filament tracking in job history**: Extended `QueueHistoryEntryDto` and `HistoryJob` types to include `toolheadUsages?: PrintJobToolheadUsage[]`. Updated `HistoryJobCard` to display per-toolhead filament usage with color indicators, material names, usage in grams, and costs with totals for multi-toolhead prints. Updated `HistoryJobTable` to add Filament and Cost columns showing aggregated totals with tooltips breaking down per-toolhead details. Pattern follows `JobDetailsSection.tsx` implementation. All changes compile clean (0 TypeScript errors) and all 1659 tests pass. Key files: `src/types/api.ts` (QueueHistoryEntryDto), `src/types/queue.ts` (HistoryJob), `src/features/queue/components/HistoryJobCard.tsx`, `src/features/queue/components/HistoryJobTable.tsx`, `src/features/queue/components/QueueHistoryTab.tsx`.

## 2026-04-01: Per-Toolhead Filament/Material/Cost Display (PFarm1-j9u3)

**Role:** Frontend Dev  
**Status:** ✅ Complete  
**Tests:** 1659 passing

Extended HistoryJobCard and HistoryJobTable to display per-toolhead filament usage, material type, color indicators, and cost breakdowns.

**Key deliverables:**
- HistoryJobCard: Full per-toolhead breakdown inline (index, color, material, grams, cost)
- HistoryJobTable: Aggregated "Filament" and "Cost" columns with per-toolhead tooltips
- Graceful fallback: Components omit sections if toolheadUsages unavailable
- Type-safe: TypeScript optional chaining, proper imports

**Integration point:** Backend must populate `toolheadUsages` on QueueHistoryEntryDto when returning history via `/job-queue-analytics/history`

## Learnings — Tailwind v4 Configuration (PFarm1-rdp)

**Issue**: VS Code Tailwind IntelliSense was showing stale v3 plugin-based rewrite hints despite Tailwind v4 being installed.

**Discovery**: The project had a stale `postcss.config.js` file that was confusing the extension. Tailwind v4 uses **CSS-first configuration** — all settings live in CSS `@theme` blocks, not in a PostCSS plugin config.

**Resolution**: Removed the stale `postcss.config.js`. Vite + `@tailwindcss/postcss` handles everything automatically without needing an explicit config file.

**Key Insight**: When upgrading to Tailwind v4, delete any `postcss.config.js`, `tailwind.config.js`, or `.tailwindrc` files. The CSS file is the single source of truth for configuration.

**Impact**: The Tailwind IntelliSense extension now correctly understands the CSS-first setup and won't suggest outdated rewrites. Custom `@theme` color tokens work as expected.

- **2026-04-01: OrcaSlicer Frontend Audit** — Completed full gap analysis of `features/slicer/`. Key findings: (1) Two competing slice job pages exist (OrcaSlicerPage is 3D-first, NewSliceJobPage is form-first) — need consolidation. (2) 3D viewer renders bed+models but all manipulation tools (move/rotate/scale/arrange) are console.log stubs. (3) Slice job results/download flow is broken — no auto-navigation, no live progress, no download button. (4) No slice job history page despite service methods existing. (5) Profile management (SlicerProfilesPage) is production-grade. (6) Test coverage is ~15-20% of slicer code — only utilities, legacy modal, and one page have tests. Full report at `.squad/decisions/inbox/ripley-orcaslicer-frontend-audit.md`.
- Slicer services are split three ways: legacy `slicerService`, current `sliceJobService`, and `apiClient.getSlicerJobStatus()`. Consolidate to `sliceJobService` only.
- `SlicerHubService` covers worker events but NOT slice job progress events. No SignalR subscription exists for job lifecycle updates.
- `ImportOfficialProfilesPage` is complete but not wired into `App.tsx` routes — dead code.
- Feature gating is double-layered: `FeatureGate feature="slicing"` + `SlicerGate` (runtime worker check). Both must pass for slicer routes to render.
- **2026-01-12: Slice Jobs Page v1 — Built `/slice-jobs` dashboard**: Created `SliceJobsPage.tsx` with TanStack Query adaptive polling (5s active / 30s idle), status filter dropdown with counts, table+card dual view via `useViewModePreference`, inline row-expand for job details, cancel/download actions, and empty state with CTA. Route added at `/slice-jobs` with `FeatureGate feature="slicing"` (no `SlicerGate` — history viewing doesn't need live workers). Nav link placed in Operations section after "Slice". All data flows through existing `sliceJobService` methods. TypeScript and ESLint both pass clean.

## Learnings

- When ESLint flags `react-hooks/set-state-in-effect`, the fix is to lift form state into a child component that mounts/unmounts with the modal's `isOpen` prop. The child resets state naturally via React's mount lifecycle. This avoids `useEffect` for state reset entirely — cleaner and lint-safe. Pattern: `<Modal isOpen={isOpen}>{isOpen && <FormContent />}</Modal>`.
- `Checkbox` component at `@/common/components/ui/Checkbox` has a `label` prop that renders its own `<label>` wrapper. Never use raw `<input type="checkbox">` — ESLint custom rule `local/pf-no-raw-html-controls` catches it.
- `sliceJobService` uses `apiClient.request<T>({ url, method, data })` pattern (AxiosRequestConfig) — returns `response.data` directly. Match this for new service methods.
- `SendToPrinterModal` (P3 slicer feature) uses `sendToPrinter(jobId, printerId, startPrint)` → `POST /api/slice/{id}/send-to-printer`. Modal filters `usePrintersFast()` to online-only printers. 8 tests cover the full flow.

## 2026-01-12: P5 — Onboarding Polish for NewSliceJobPage

**Role:** Frontend Dev  
**Status:** ✅ Complete

- Added onboarding empty state to `NewSliceJobPage.tsx`: when `listExtended()` returns zero machine profiles, shows a welcoming banner with "Get started with slicing" message and two CTAs (Import Official Profiles → `/slicer/import-official`, Browse Profiles → `/admin/slicer-profiles`)
- Wired `ImportOfficialProfilesPage` into `App.tsx` at `/slicer/import-official` with `FeatureGate feature="slicing"` and lazy loading — page existed but had no route
- Created 4 focused tests in `src/test/features/slicer/pages/NewSliceJobPageOnboarding.test.tsx`: banner presence when no profiles, form presence when profiles exist, and navigation for both buttons
- TypeScript compiles clean (0 errors), all 4 tests pass
- Skipped SliceJobsPage banner (item 3) per instructions to avoid merge conflicts with concurrent agent work

**Key files:**
- `src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx`
- `src/Web/ReactApp/src/App.tsx`
- `src/Web/ReactApp/src/test/features/slicer/pages/NewSliceJobPageOnboarding.test.tsx`

## Learnings

- SignalR group memberships are connection-scoped. On reconnect the client gets a new connectionId and all server-side groups are lost. Any hub service must provide a reconnect callback mechanism so hooks can re-call `JoinUserGroupAsync` / `SubscribeToJobAsync` after reconnection.
- When `connection.start()` fails in a catch handler, both `this.connection` and `this.connectionPromise` must be nulled. Leaving `this.connection` non-null causes `start()` to return immediately on retry without actually connecting.
- After `ensureConnected()` resolves, always check `isConnected()` before setting UI state — the catch handler doesn't re-throw, so the promise resolves even on failure.

## Team Update: Slicer UI Fix (2026-04-05)

**Date:** 2026-04-05  
**Incident:** Slicer UI missing in Docker microservices deployment  
**Status:** ✅ RESOLVED

Participated in root-cause diagnosis for slicer UI visibility issue. Validated that frontend capability detection (`useSystemCapabilities`) was working correctly and isolated problem to backend `/api/system/capabilities` endpoint.

**Backend Fix:** Lambert updated `SystemCapabilitiesController.cs` to detect `DEPLOYMENT_MODE=microservices` environment variable and report `slicingEnabled=true` when in microservices deployment mode.

**Outcome:** Fix deployed via `pfdev redeploy api`. Frontend now sees correct capability flag and displays slicer UI in production deployment.


## Learnings

### Slicer UI Visibility Debug (2026-04-05)

- **Root cause**: `slicerEnabled` flag in `Program.cs:101` served dual purpose (module loading + capabilities reporting). In microservices mode (`DEPLOYMENT_MODE=microservices`), it hardcoded `false`, which made `SystemCapabilitiesController` return `slicingEnabled: false`, blocking all slicer UI.
- **Frontend gating chain**: Two independent gates — `FeatureGate` (App.tsx:129, checks `capabilities.slicingEnabled`) and `SlicerGate` (App.tsx:116, checks `useSlicer().isSlicerAvailable` from SlicerContext). Both must pass.
- **Nav sidebar gating**: `Layout.tsx:321` hides items with `requiresSlicingCapability: true` when `capabilities?.slicingEnabled === false`.
- **Capabilities caching**: `useSystemCapabilities()` uses `staleTime: Infinity` — once fetched, never re-fetched until page refresh. If backend returns wrong value, user must hard-refresh.
- **Nginx trailing-slash pattern**: Slicer nginx locations use trailing slashes (`/api/workers/`). Frontend calls without slash (`/api/workers`). nginx issues 301 redirect. Browser follows transparently. If redirect fails, main API's stub returns 404 `SLICER_DISABLED` and SlicerContext falls back to `workers = []`.
- **Key endpoint**: `/api/system/capabilities` — the single source of truth for frontend feature gating. Any issue here hides entire feature sections.
- **Key files**: `src/api/Program.cs` (flag logic), `src/api/Controllers/SystemCapabilitiesController.cs` (endpoint), `src/Web/ReactApp/src/common/hooks/useSystemCapabilities.ts` (frontend hook), `src/Web/ReactApp/src/contexts/SlicerContext.tsx` (worker discovery), `src/Web/ReactApp/src/services/slicerRegistry.ts` (API call to `/api/workers`).

## Learnings

- 2026-07-25: Profile import consolidation — the working profile import path is `/profiles/import` (ProfileImportWizardPage, worker-backed). The old DB-backed path (`/slicer/import-official`, ImportOfficialProfilesPage) was broken. Both old routes (`/slicer/import-official`, `/admin/slicer-profiles`) now redirect to the wizard. The old page files remain on disk as dead code but are not loaded by any route.
- 2026-07-25: When removing a nav item from Layout.tsx, check if its icon import is still used elsewhere to avoid orphaned imports (SettingsIcon was still used for the user menu cog).

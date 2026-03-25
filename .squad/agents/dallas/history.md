# Dallas History

## Core Context

Dallas is the project lead & product architect. Key contributions:
- Feature prioritization & architecture oversight
- Location hierarchy system design (phase 1 approved)
- Auto-dispatch phase 1 & 2 architecture
- Competitive analysis & market differentiation
- Team coordination & decision governance
- Failure detection & UI polish sessions (2026-03-25)
- Auto-dispatch naming cleanup & consistency (2026-03-25)

Early entries (pre-2026-03-25) summarized for maintainability. See decisions-archive.md for historical context.

---

## Session: Failure Detection Badge Placement Review (2026-03-25)

**Role:** Lead decision reviewer  
**Status:** Recommendation formulated; ready for team approval

### Work Completed
- Analyzed operator workflow: card view (collapsed) vs. camera feed (expanded)
- Assessed UI redundancy: header badge vs. camera overlay (identical information)
- Evaluated visual noise and cognitive load impact
- Reviewed discoverability via modal pattern
- Consolidated findings with Ripley (Frontend Dev)

### Recommendation
**Keep header badge only; remove camera overlay.**

**Reasoning:**
1. Single source of truth eliminates confusion and sync issues
2. Camera overlay is redundant; operator sees header state before camera opens
3. Reduces visual distraction during video inspection
4. Header badge → modal flow provides full detail access
5. Follows PrintFarmer conventions (secondary status lives in header, not overlays)

### Decision Document
- Status: Recommendation ready for team decision
- File: `.squad/decisions/decisions.md` → merged from inbox
- Implementation path clear; backend-agnostic (UI only)

### Implementation Checklist
- [ ] Remove `FailureDetectionMonitoringOverlay` import from CompactPrinterCard.tsx (line 18)
- [ ] Remove overlay prop from PrinterCameraPreview call (lines 230–236)
- [ ] Optionally deprecate overlay component if unused elsewhere
- [ ] Validate pattern compliance (compact-status-detail-modal, monitoring-lifecycle-badges)

### Related Skills
- `compact-status-detail-modal`: Status affordances should be glanceable + launch modal for detail
- `monitoring-lifecycle-badges`: Monitoring status badges reflect active lifecycle, not raw errors

---

## Session: Auto-Dispatch Naming Cleanup (2026-03-25)

**Role:** Lead decision reviewer  
**Status:** Decision approved; implementation scope documented  
**Urgency:** High

### Work Completed

**Analysis:** Reviewed naming inconsistency between frontend (uses "AutoDispatch") and backend (uses "AutoPrint"):
- Frontend hooks: `useAutoDispatchStatus`, `useAutoDispatchGlobalStatus` ✅
- Frontend types: `AutoDispatchStatus`, `AutoDispatchReadyResult` ✅
- Backend routes: `POST /api/auto-print/...` ❌ Should be `/api/auto-dispatch/...`
- Backend controller: `AutoPrintController` ❌ Should be `AutoDispatchController`
- Backend service: `IAutoPrintService`, `AutoPrintService` ❌ Should be `IAutoDispatchService`, `AutoDispatchService`
- SignalR handler: `onAutoPrintStateChanged` ❌ Should be `onAutoDispatchStateChanged`

**Root Cause:** Product rename was incomplete. Frontend already migrated to "auto-dispatch" terminology, but backend implementation still uses "auto-print" naming.

**Impact:** 3-layer contract violation—frontend code expects auto-dispatch semantics, backend implements auto-print routes. Will cause confusion; harder to maintain.

### Decision

**Full rename required** (Option B: clean rename, not deprecation period):
- **Tier 1 (MUST rename):** Routes, controller, services, DTOs, SignalR handlers
- **Tier 2 (Recommended):** Domain properties (`Printer.AutoDispatchEnabled`), logging tags, enums
- **Tier 3 (DO NOT rename):** Database columns (stay as `AutoPrintState` for backward compatibility; use `[Column]` mapping in EF Core)

**Rationale:**
- Single coordinated change minimizes confusion window
- No deprecated code to maintain long-term
- Lower maintenance cost than compatibility layer
- Database columns protected via EF Core mapping (`[Column("AutoPrintState")]` on `Printer.AutoDispatchState`)

### Implementation Checklist

**Backend Phase 1:** Rename controller → `AutoDispatchController`, route → `api/auto-dispatch`, service folder & files, DTOs  
**Backend Phase 2:** Update domain properties, enums, logging tags, add `[Column]` mapping  
**Frontend Phase 1:** Update all `/auto-print/*` routes → `/auto-dispatch/*`, rename SignalR handler  
**All Tests:** API + React tests must pass  
**Docs:** Update `AUTO_DISPATCH.md` terminology  

### Files Written

- `.squad/decisions/inbox/dallas-auto-dispatch-rename-scope.md` — Full implementation scope, trade-off analysis, risk assessment, checklist, definition of done.

### Key Patterns Established

**Database Column Mapping:** When C# properties are renamed post-deployment but database columns must stay immutable, use EF Core `[Column("OriginalName")]` attribute. Zero-cost mapping; no migration needed.

**3-Layer Rename Coordination:** Backend route + service + DTO names must match frontend expectations. Break this contract only when backward compatibility is critical (public APIs). For internal-only APIs, prefer clean rename over deprecation period.

---

## Session: Obico Self-Hosted Contract Final Review (2026-03-25)

**Role:** Lead reviewer  
**Status:** Approved fix direction; follow-up narrowed

### Work Completed
- Reviewed the final Obico contract change across runtime client, admin validation probe, and focused backend tests.
- Confirmed the approved behavior-safe direction: upstream `GET /p/?img=...` first, legacy multipart `POST /p/` only as compatibility fallback.
- Explicitly narrowed the remaining work so the team does not reopen the route bug after the contract fix landed.

### Remaining Follow-Up
- Runtime reachability / `detectionTarget` verification from the real API environment
- Optional stronger health validation with a real reachable snapshot path instead of the current synthetic `img=` probe

---

## Learnings

- Obico self-hosted compatibility must be kept aligned in both the runtime client and the admin health probe: prefer `GET /p/?img=...`, then fall back to the legacy multipart `POST /p/` contract. If only the monitor is updated, settings validation still gives false confidence.
- Key review files for this contract are `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs`, `src/api/Controllers/ObicoServerController.cs`, `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs`, and `src/tests/Farm.Web.Api.Tests/Controllers/ObicoServerControllerTests.cs`.
- The current health validation uses a synthetic `img=` probe URL, so it proves route compatibility but not a real printer-camera fetch path. Treat runtime reachability issues separately from this contract fix.

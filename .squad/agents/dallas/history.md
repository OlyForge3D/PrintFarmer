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

## 2026-03-26: Obico ML Timeout Mismatch — Final Tradeoff Call

**Role:** Team Lead decision authority  
**Status:** ✅ Complete — Decision documented

**Context:** Parker (DevOps) evaluated whether Obico's self-hosted `ml_api` 0.1s snapshot fetch timeout is configurable. No upstream config knob exists. Three remediation paths available:
1. Deploy local proxy on pfarm2 to improve latency
2. Build custom ml_api image with higher timeouts
3. Live with intermittent Obico failures (status quo)

**Tradeoff Analysis:**
- **Custom ml_api image:** High maintenance burden (rebasing, security patches) for marginal benefit
- **Local proxy:** Adds operational complexity and diagnostic burden
- **Status quo:** Intermittent failures acceptable pending upstream fix or user-initiated remediation

**Decision:** Treat as upstream limitation. Choose the **simplest path with lowest maintenance burden** and document the workaround clearly.

**Operational Guidance (3-tier remediation order):**
1. Fix network latency to <100ms (preferred, no code changes)
2. Custom ml_api image (if network fix is impossible)
3. Request upstream config knob (longer-term)

**Rationale:** No immediate action needed. Users have clear escalation path. Documented guidance empowers operators without forcing a choice now.

**Files:** Recorded in orchestration logs and merged into decisions.md.


## Session: Failure Detection Timeline Scope Clarification (2026-03-27)

**Role:** Lead decision authority  
**Status:** ✅ RECOMMENDATION DOCUMENTED — ready for team approval  
**Urgency:** Medium (unblocks Ripley + Lambert)

### Work Completed

**Analysis:**
- Reviewed failure-detection UX and underlying data models
- Examined in-memory monitoring state (no persistence layer)
- Cross-referenced job history pattern (has timeline; has persistence)
- Assessed operator workflow for failure detection

**Key Findings:**
- Failure detection is a **real-time state machine**, not a historical audit log
- Backend tracks only the last result per printer (in-memory FailureDetectionPrinterStatusDto)
- Current modal design already surfaces all actionable state: current state, last scan, last failure, last auto-pause, coverage source, next step
- Building a timeline would require database persistence, API endpoint, and frontend pagination—no use case exists
- Job history HAS a timeline because state transitions are persistent; failure detection doesn't follow this model

### Decision

**Do NOT implement a timeline view.** Current badge + modal pattern is fit-for-purpose.

**Rationale:**
1. No data model supports persistence (would require schema change)
2. Operator workflow (glance badge → click for detail) doesn't require historical scrolling
3. Modal already shows all anchoring points (last scan, last failure, last auto-pause)
4. Aligns with PrintFarmer monitoring paradigm (live state, not audit logs)

### Scope Clarity for Teams

**Ripley (Frontend):**
- Modal + header badge pattern is the final design
- No timeline pagination or scrollable event list
- Implementation complete when modal shows all current state fields

**Lambert (Backend):**
- In-memory snapshot is sufficient
- No persistence layer needed (unless future audit/compliance requirement emerges separately)

### Decision Document

- File: `.squad/decisions/inbox/dallas-failure-detection-timeline-decision.md` — Full recommendation with implementation clarity and open questions

### Related Context

- Failure detection modal: `src/Web/ReactApp/src/features/printers/components/FailureDetectionStatusModal.tsx`
- Status DTO (in-memory only): `src/infra/Services/FailureDetection/FailureDetectionMonitorStatus.cs`
- Monitoring service: `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs`
- Job history timeline pattern (for contrast): `src/infra/Dtos/PrintQueue/PrintQueueDtos.cs` (TimelineEventDto)


## Learnings

- **Failure detection is NOT a timeline domain.** It's a real-time state machine with in-memory state. Operators care about "is this printer being watched NOW?" not "show me all scans from 2 hours ago." The absence of a persistence layer (in-memory FailureDetectionPrinterStatusDto) is intentional; it's designed for live monitoring, not audit logging.
- **Modal + badge is the operator workflow for failure detection:** Glance at badge for state → click for detail (coverage source, snapshot URL, last scan time, last failure time, auto-pause action, next step). No need to scroll past historical events.
- **Job history timeline pattern is NOT applicable here.** Job state transitions are persistent (TimelineEventDto in the queue schema). Failure detection scans are ephemeral. The difference is fundamental: historical audit log vs. live monitoring state.
- **Data-driven scope containment:** Before proposing new UX patterns (timeline, graphs, tables), verify the backend has a persistence layer. If not, the pattern doesn't fit.


## 2026-03-27: Failure Detection Timeline — Recommendation Against (Product)

**Time:** Async  
**Task:** Clarify UX scope: should failure detection have a timeline/event log view?  
**Outcome:** ✅ Recommendation ready for team decision

Analyzed the failure-detection UX design. Recommended AGAINST timeline view. Failure detection is a **live monitoring state machine**, not a historical audit log. No persistence layer exists. Modal + badge pattern is fit-for-purpose. Scope protection for Ripley (Frontend) and Lambert (Backend). Decision ready for team approval.


## Session: Print Session Timeline v1 — Scope Definition (2026-03-27)

**Role:** Lead decision authority  
**Status:** ✅ DECISION DOCUMENTED — ready for implementation  
**Urgency:** Medium (unblocks Lambert + Ripley)

### Work Completed

**Analysis:**
- Reviewed existing persisted data: `JobStateHistory` (state transitions) and `FailureDetectionIncident` (just completed)
- Both already have `JobId` foreign key — clean join point
- Existing job-level endpoint: `GET /api/analytics/jobs/{jobId}/state-history`
- Existing timeline endpoint: `GET /api/analytics/timeline` (cross-job, filterable)

**Key Finding:**
A "print session" IS a PrintJob. The JobId anchors both state transitions and failure incidents. No new schema needed.

### Decision

**V1 print-session timeline = UNION of two already-persisted event streams:**
1. `JobStateHistory` → state_change events
2. `FailureDetectionIncident` → failure_incident events

**Single new endpoint:**
`GET /api/jobs/{jobId}/session-timeline` → `List<SessionTimelineEventDto>`

**UX integration:** Add timeline to existing job detail view (contextual, not standalone page).

**Out of scope for V1:**
- Thermal anomaly events (no persistence layer)
- Manual operator notes (would require new entity)
- Paginated timeline (overkill for <50 events per job)
- Timeline graphs/visualizations

### Sequencing

**Backend first (Lambert):**
1. Create `SessionTimelineEventDto`
2. Create `SessionTimelineService` (merge logic)
3. Expose endpoint
4. Unit tests

**Frontend second (Ripley):**
1. Add TypeScript type
2. Add `useSessionTimeline(jobId)` hook
3. Add timeline component to job detail view

### Decision Document

- File: `.squad/decisions/inbox/dallas-print-session-timeline-v1.md` — Full scope, API surface, trade-offs, DoD

## Learnings

- **"Print session" = PrintJob:** Don't invent a new entity when the existing model already serves as the anchor. Job state history and failure incidents both link via JobId.
- **UNION before JOIN:** When multiple event streams need to appear in a unified timeline, prefer simple UNION-style merge (query both, sort by timestamp) over complex denormalized schemas.
- **Contextual UX integration:** Timelines work best when attached to the entity they describe (job detail view) rather than as standalone pages disconnected from operator workflow.
- **Backend-first sequencing for data UX:** When the frontend needs new API data, sequence backend completion first so frontend work has a stable contract.

## Session: Print Session Timeline v1 — Complete (2026-03-27)

**Role:** Lead, decision publisher, validation overseer  
**Status:** COMPLETE — All artifacts delivered, tests pass, no new schema

### Work Completed

- **Decision finalized:** V1 = UNION of `JobStateHistory` + `FailureDetectionIncident`
- **Scope locked:** Single endpoint, no new schema, modal-first UX
- **Sequencing delivered:** Backend first (Lambert), frontend second (Ripley)
- **Handoff validated:** Lambert completed backend, Ripley completed frontend integration, Kane validated gate

### Orchestration Log

Published: `.squad/orchestration-log/20260326-031539-dallas.md`

### Session Complete

- Backend endpoint: ✅ `GET /api/printers/{printerId}/session-timeline`
- Frontend modal integration: ✅ Timeline tab in `FailureDetectionStatusModal.tsx`
- Tests: ✅ 41/41 PASS (service, controller, component, regression)
- Build: ✅ Clean, 0 errors
- Format: ✅ dotnet format + ESLint clean

**Next:** Merge to main and resolve CI dependencies if any.

---

## Session: Printer Entity Decomposition Analysis (2026-03-31)

**Role:** Lead architect / analyst
**Status:** ✅ DECISION PROPOSAL DOCUMENTED — ready for team review
**Urgency:** High (concurrency conflicts in production)

### Work Completed

- Classified all 56 Printer entity properties into 5 buckets: Core Identity, Hardware Config, Connection Config, Operational Bookkeeping, Already Extracted
- Identified 4 fields written by background services that share the Printer row's `RowVersion`: `LastHistorySeedUtc` (every 15 min), `LastModelSyncAt` (~hourly), `LastCapabilityUpdate` (~hourly + user edits), `ObicoServerId` (on rebalance)
- Confirmed none of these 4 fields are directly exposed to the frontend (only `HasCatalogUpdate` computed bool uses `LastModelSyncAt`)
- Proposed new entity `PrinterServiceState` (1:1 with Printer) to hold all background-service-written fields
- Documented full migration impact: domain, EF config, repository, services, DTOs, tests

### Decision Proposal

- File: `.squad/decisions/inbox/dallas-printer-entity-refactor-analysis.md`
- Recommendation: Single migration extracting all 4 fields into `PrinterServiceState`
- Priority: `LastHistorySeedUtc` is highest (Jeff's explicit callout, worst offender at 15-min interval)

### Key Findings

- The Printer entity has 56 properties (including navigations and computed) — only 4 are written by background services
- `PrinterDispatchState` extraction (already done) was the right pattern — `PrinterServiceState` follows the same approach
- Hardware spec fields (MaxBuildVolume, HasHeatedBed, etc.) stay on Printer despite catalog sync writes because they're also user-editable and frontend-visible

## Learnings

- **Background service writes are the root cause of Printer concurrency conflicts.** The `RowVersion` token means ANY write to the Printer row (even to an unrelated field) creates contention with the PUT endpoint. Extracting background-written fields into separate 1:1 tables with their own `RowVersion` eliminates cross-concern contention entirely.
- **"Never read by frontend" is the strongest signal for extraction.** Fields like `LastHistorySeedUtc` and `LastCapabilityUpdate` are pure internal bookkeeping — they have no business living on the same row as user-facing configuration.
- **Dual-writer fields (BG + API) are the worst case.** `LastCapabilityUpdate` is written by both catalog sync (BG) and user edits (API). This creates the widest contention window. Extracting it into `PrinterServiceState` means the BG service and API write to different rows.
- **The `PrinterDispatchState` pattern is the template.** Same approach: 1:1 table, own `RowVersion`, FK to Printer. `PrinterServiceState` follows this established pattern exactly.

---

## Session: PrinterServiceState Extraction Implementation (2026-03-31)

**Role:** Lead implementer / architect
**Status:** ✅ COMPLETE — All 446 tests passing, zero build warnings

### Work Completed

#### 1. Entity & EF Configuration
- Created `PrinterServiceState` entity following `PrinterDispatchState` pattern
  - 1:1 relationship with Printer using PrinterId as both PK and FK
  - Separate `RowVersion` token for independent concurrency control
  - Fields: `LastHistorySeedUtc`, `LastModelSyncAt`, `LastCapabilityUpdate`, `ObicoServerId`
  - Navigation properties: `Printer` (back-reference) and `ObicoServer` (for failure detection)
- Created `PrinterServiceStateConfiguration` with FK constraints and indexes
- Updated `Printer` entity: removed 4 migrated fields, added `ServiceState` navigation
- Added `DbSet<PrinterServiceState>` to AppDbContext

#### 2. Repository Layer Updates
- Modified `EfPrintJobManagementRepository.UpdatePrinterLastHistorySeedAsync` to write to PrinterServiceStates DbSet
- Updated `EfPrintersRepository` methods to Include ServiceState:
  - `GetAllWithIncludesAsync`
  - `GetAllForTemplateUpdateAsync`
  - `FindByIdForTemplateUpdateAsync`
  - `FindByIdWithIncludesAsync`
- Changed ObicoServer Include path: `p.ObicoServer` → `p.ServiceState.ThenInclude(s => s!.ObicoServer)`

#### 3. Service Layer Updates
- **PrintersService.cs**:
  - Created `EnsureServiceState` helper method for safe on-demand creation
  - Updated `ApplyModelTemplateAsync` to write LastModelSyncAt and LastCapabilityUpdate via ServiceState
  - Updated `HasCatalogUpdate` calculations to check `p.ServiceState != null && p.Model.UpdatedAt > (p.ServiceState.LastModelSyncAt ?? DateTime.MinValue)`
- **CatalogUpdateDetectionService.cs**: Changed to check `ServiceState.LastModelSyncAt` for catalog drift
- **ObicoServerAssignmentService.cs**: Updated 3 locations to write/read ObicoServerId through ServiceState
- **PrintFailureMonitorService.cs**: Updated ResolveDetectionTarget to check `printer.ServiceState?.ObicoServerId`

#### 4. API Layer Updates
- **PrintJobManagementService.cs**:
  - Changed isInitialSeed check: `!printer.ServiceState?.LastHistorySeedUtc.HasValue ?? true`
  - Updated seedSinceUtc and latestJobTimestamp to use `printer.ServiceState?.LastHistorySeedUtc`
  - Removed direct write to printer.LastHistorySeedUtc (repository method handles it)
- **PrintersController.cs**:
  - Changed DTO mapping: `p.ServiceState?.ObicoServer?.Name`
  - Updated HasCatalogUpdate: `p.Model != null && p.ServiceState != null && p.Model.UpdatedAt > (p.ServiceState.LastModelSyncAt ?? DateTime.MinValue)`

#### 5. EF Core Migrations (Data Preservation)
- **PostgreSQL Migration** (`20260331172828_ExtractPrinterServiceState.cs`):
  - Up(): CREATE TABLE → INSERT data from Printers → DROP FK & columns from Printers
  - Down(): ADD columns → UPDATE from ServiceState → DROP TABLE
  - Uses PostgreSQL quoted identifiers
- **SQL Server Migration** (`20260331172836_ExtractPrinterServiceState.cs`):
  - Same structure with SQL Server bracketed identifiers
  - Preserves all historical timestamps during migration

### Technical Decisions

#### Null-Safe Navigation Pattern
All ServiceState references use null-conditional operators:
```csharp
printer.ServiceState?.LastHistorySeedUtc
printer.ServiceState?.ObicoServerId
p.ServiceState != null && p.ServiceState.LastModelSyncAt
```

This handles cases where ServiceState isn't loaded or doesn't exist yet.

#### EnsureServiceState Helper Pattern
```csharp
private static PrinterServiceState EnsureServiceState(Printer printer)
{
    if (printer.ServiceState is not null) return printer.ServiceState;
    printer.ServiceState = new PrinterServiceState { PrinterId = printer.Id };
    return printer.ServiceState;
}
```

Used in PrintersService to safely write to ServiceState, creating on-demand if needed.

#### Migration Data Preservation Strategy
1. CREATE TABLE PrinterServiceState first (with all columns and constraints)
2. INSERT all existing data from Printers to PrinterServiceState (preserves historical timestamps)
3. DROP FK constraint and columns from Printers
4. Down() method reverses exactly: ADD columns → UPDATE from ServiceState → DROP table

### Test Results

```
Test Run Successful.
Total tests: 446
     Passed: 446
 Total time: 1.4250 Minutes
```

All tests passing with zero failures!

### Build Results

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:18.04
```

Clean build with zero warnings (StyleCop compliant).

## Learnings

- **Migration data preservation requires careful sequencing.** Creating the table first, then inserting data, then dropping columns ensures no data loss. The Down() method must reverse this exactly in reverse order.

- **Null-safe navigation is critical when extracting optional state.** ServiceState may not be loaded (Include missing) or may not exist (new printers). Every reference must use `?.` or explicit null checks.

- **EnsureServiceState pattern prevents boilerplate.** Rather than checking `if (printer.ServiceState is null)` everywhere, the helper encapsulates the create-on-demand logic.

- **Repository layer must Include ServiceState explicitly.** EF Core won't auto-load navigation properties. All repository methods that return Printer must explicitly Include ServiceState if downstream code needs it.

- **Foreign key path changes cascade through queries.** Changing `p.ObicoServer` to `p.ServiceState.ThenInclude(s => s!.ObicoServer)` affects every query that needs ObicoServer data.

- **PrinterDispatchState extraction was the perfect template.** Following the exact pattern (1:1 entity, own RowVersion, FK, EF configuration) made this implementation straightforward and consistent.

- **Background service writes are now isolated.** Each background service (history seed, catalog sync, capability update, Obico assignment) now writes to its own row with its own RowVersion token. User edits to Printer config via API won't conflict anymore.

---

## Session: Obico Settings Consistency Audit (2026-03-26)

**Role:** Lead/Architect  
**Bead:** PFarm1-07s  
**Status:** COMPLETE — Migration validated

### Work Completed

**Audit Phase:**
- Identified ALL ObicoSettings consumers across codebase
- Found ONE remaining IOptions<ObicoSettings> consumer: PrintersController
- Confirmed failure detection services already migrated to ISettingsService (PFarm1-07r)

**Implementation Phase:**
- Migrated PrintersController from IOptions<ObicoSettings> to ISettingsService
- Updated constructor DI: removed IOptions<ObicoSettings>, added ISettingsService
- Updated usage site (line 1494): changed `_obicoSettings.Enabled` to `_settingsService.Get<ObicoSettings>().Enabled`
- Removed unused `Microsoft.Extensions.Options` import

**Validation:**
- API project builds cleanly (0 errors, 0 warnings)
- Test project builds cleanly
- Format check passes (no new violations)
- Full solution build has unrelated race condition errors (pre-existing)

### Architecture Finding

**ObicoSettings now uses ISettingsService exclusively for runtime configuration:**
- ✅ PrintFailureMonitorService → ISettingsService (via GetCurrentSettings helper)
- ✅ ObicoFailureDetectionService → ISettingsService
- ✅ PrintersController → ISettingsService (migrated today)

**IOptions<ObicoSettings> binding still exists in ServiceCollectionExtensions** for initial config load, but no runtime consumers remain. This is correct: ISettingsService reads persisted settings (database), while options binding provides bootstrap defaults.

### Key Files Modified
- `src/api/Controllers/PrintersController.cs` — Migrated to ISettingsService pattern

## Learnings

- **Settings service consistency prevents stale reads.** Failure detection was reading persisted settings while PrintersController was reading appsettings.json values. This creates skew when users modify settings via UI (database) but code reads config files (stale).

- **ISettingsService.Get<T>() is the runtime pattern.** Options binding is for initial bootstrap only. All runtime consumers should read from ISettingsService to respect user modifications.

- **Single usage audit is tractable with grep.** `grep -r "IOptions<ObicoSettings>"` found the single consumer immediately. No need to over-engineer the search.

- **Migration pattern is consistent: DI replacement + call-site update.** Replace IOptions<T> parameter → ISettingsService parameter. Replace `_settings.Value` → `_settingsService.Get<T>()`. No architectural changes needed.

## 2026-04-01: ObicoSettings Runtime Consistency Audit (PFarm1-07s)

**Role:** Lead / Backend Architect  
**Status:** ✅ Complete  
**Build:** Passes (0 errors, 0 warnings)

Audited all ObicoSettings consumers and enforced standardized injection pattern.

**Work completed:**
1. Identified inconsistency: some code reading from `IOptions<ObicoSettings>` (static config file), others from `ISettingsService` (persisted database)
2. Migrated PrintersController from `IOptions<ObicoSettings>` to `ISettingsService`
3. Validated consistency across all failure-detection code paths

**Final state (validated 2026-04-01):**
- ✅ PrintFailureMonitorService → ISettingsService
- ✅ ObicoFailureDetectionService → ISettingsService
- ✅ PrintersController → ISettingsService
- ✅ Options binding in ServiceCollectionExtensions (bootstrap only, correct)

**Pattern established:** All runtime settings now flow through single ISettingsService abstraction. User modifications via Settings UI immediately visible to all consumers.

**Impact:** Runtime consistency; no stale config file values during execution; foundation for future settings work.

---

## Session: Backend Emulator Feasibility Analysis (2026-04-01)

**Role:** Lead feasibility analysis  
**Status:** Analysis complete; recommendation documented

### Work Completed
- Analyzed backend plugin architecture (`IBackendClientPlugin`, `IExtendedBackendPlugin`)
- Reviewed capability interface system (27 `ISupports*` interfaces identified)
- Examined Moonraker/PrusaLink implementations as reference backends
- Mapped frontend API surface (~180 methods in ApiClient)
- Analyzed SignalR real-time event flow (PrinterUpdated, DiscoveryProgress, JobQueueUpdate)
- Investigated Spoolman integration requirements
- Assessed Playwright test configuration and existing E2E setup

### Key Findings

**Backend Plugin Surface:**
- Core interfaces: `IBackendClientPlugin` (metadata, registration), `IExtendedBackendPlugin` (status client, additional services)
- 27 capability interfaces covering: file operations, job control, cameras, movement, temperature, history, spoolman, composite status
- Moonraker: WebSocket subscription service for real-time updates
- PrusaLink/OctoPrint/SDCP: HTTP polling services (5-10s intervals)
- Discovery probes: HTTP-based network scanning per backend type

**Frontend Requirements:**
- ~180 API methods consumed by React UI
- Critical data flows: printer status, job progress, temperatures, camera URLs, file lists, history
- SignalR events: `printerupdated`, `discoveryprogress`, `discoveryprinterf`, `jobqueueupdate`, `failuredetectionevent`
- Spoolman integration: spool lists, active spool tracking, filament metadata

**Test Infrastructure:**
- Playwright configured with 8 viewport/browser combinations
- Existing visual regression tests for homepage/printers page
- `CustomWebApplicationFactory` for API integration tests (in-memory SQLite)

### Learnings
1. **Plugin architecture is well-abstracted** — backend emulators can implement `IBackendClientPlugin` like any real backend
2. **Capability system is explicit** — each `ISupports*` interface documents what a backend can do
3. **SignalR is central to UI responsiveness** — emulators must publish status updates via PrinterHub
4. **Discovery is backend-specific** — each backend has its own `INetworkDiscoveryProbe` implementation
5. **Frontend is data-shape agnostic** — API DTOs abstract backend differences (good for emulation)
6. **Spoolman is loosely coupled** — only Moonraker backend implements `ISupportsSpoolman`

### Decision Document
- Created: `.squad/decisions/inbox/dallas-emulator-feasibility.md`
- Recommendation: **Option D (Hybrid)** — Fake backend plugins + mock API for discovery/spoolman
- Rationale: Maximizes reuse of real backend infrastructure while allowing test-specific overrides

---

## Session: OrcaSlicer Feature Assessment & Work Plan (2025-07-16)

**Role:** Lead — feature assessment, priority assignment
**Status:** Work plan delivered to `.squad/decisions/inbox/dallas-orcaslicer-priorities.md`

### Work Completed
- Deep scan of slicer backend: `src/slicer/` (5 projects), `src/orcaslicer-worker/`, 40+ API endpoints across 8 controllers
- Deep scan of slicer frontend: 5 pages (NewSliceJobPage, OrcaSlicerPage, SlicerProfilesPage, WorkerManagementPage, ImportOfficialProfilesPage), 15+ components, 6 service files
- Assessed test coverage: 69 test files in Farm.Slicer.Module.Tests
- Produced 5-item prioritized work plan with assignments

### Key Finding
The slicer subsystem is **far more complete than assumed.** The core pipeline (submit→queue→dispatch→slice→artifacts) is fully implemented and tested on both backend and frontend. The gap is UX around the flow — no job dashboard, no live progress display, no slice-to-print bridge.

## Learnings
1. **Slicer backend is production-ready** — SlicerOrchestrator, JobDispatcherService, WorkerLifecycle, ProfilesService, ArtifactsService all fully implemented with metrics, circuit breakers, and retry policies
2. **Frontend end-to-end job submission works** — NewSliceJobPage has complete flow: engine→printer→profiles→model→submit with incremental profile loading
3. **SignalR progress infrastructure is built but not consumed** — SlicerProgressHub exists with job subscriptions but frontend only uses SlicerHub for worker events
4. **Two separate UI approaches exist** — NewSliceJobPage (form-based, complete) and OrcaSlicerPage (3D workspace, toolbar actions are placeholders). NewSliceJobPage is the functional path.
5. **Profile system is sophisticated** — 3123-line ProfilesService with hierarchical profiles, deduplication, cloning, compatibility validation. Worker caches profiles in SQLite.
6. **The biggest user-facing gap is job visibility** — Users can submit but can't see a queue, can't track progress in real-time, can't bridge to printing
7. **`feature/orcaslicer-full-ui-parity` branch has no unique commits** — all slicer work already landed on development

## Learnings

### 3D Models Upload/Display Bug Synthesis (2026-01-12)

**Context:** User reported STL uploads succeed but files not appearing on 3D Models page. Separate issue: selecting a model yields 404 from `/api/3d-models/file/{id}`.

**Evidence synthesis across team:**

1. **Ripley (Frontend):** Fixed query invalidation mismatch where `ModelUploadModal` was invalidating `['models-search']` but `FileBrowser` uses `['file-browser', viewMode, params]`. Fix: Remove manual invalidation, use `onUploadSuccess` callback that calls `fileBrowserRef.current?.refetch()`.

2. **Kane (QA):** Identified tag filtering bug — `Model3DFileService.QueryAsync` accepts `tagIds` parameter but never uses it. Repository layer has no tag filtering implementation. This is a *separate bug* from the 404 file endpoint issue.

3. **Lambert (Backend):** Fixed database schema initialization issue — `SlicerDbContext` was never initialized, causing uploads to fail silently. Also working on file path bug where `GetModelFilePathAsync` was returning relative paths instead of absolute paths, causing 404s when frontend tries to download files.

**Root cause assessment:**

**Two separate bugs:**
- **Bug A (Upload not showing):** Fixed by Ripley (frontend cache) + Lambert (SlicerDbContext init). Models now appear on page after upload.
- **Bug B (404 on file download):** Lambert's file path fix in progress — `GetModelFilePathAsync` and `GetModelThumbnailPathAsync` now return absolute paths (`Path.Combine(_modelsPath, model.FileName)`) instead of relative paths that included virtual directories.

**Tag filtering:** Separate feature gap (Kane's finding) — not causing current user-reported issues but should be fixed to prevent confusion when users try to filter by tags.

**Decision:** Ship Ripley's frontend fix immediately (it's a pure cache bug, low risk). Lambert's file path fix appears to address the 404 issue and should be tested + merged ASAP. Tag filtering can be queued as separate work item.

**Key insight:** This was *two independent bugs masquerading as one*:
1. Cache invalidation (frontend) → models not appearing
2. File path resolution (backend) → 404 on download

Both needed fixes, but they're orthogonal. The cache fix unblocked visibility, the path fix unblocks downloads.

---

## Session: Native Keys Migration Architecture (2025-07-11)

**Role:** Lead architect — work breakdown and ADR for slicer settings migration
**Status:** ADR written to `.squad/decisions/inbox/dallas-native-keys-migration.md`

### Work Completed
- Audited all 3 settings type files (process: 700+ lines, filament: 538, machine: 690)
- Audited `CamelToNativeKeyMap` (283 entries, lines 560-919 of HttpJobPollerService.cs)
- Mapped full blast radius: 18 frontend files, 70+ backend files
- Produced 5 architectural decisions (AD-1 through AD-5)
- Produced 12-item work breakdown (WI-01 through WI-12) with dependency graph
- Estimated 41-57 hours total effort across Ripley (frontend), Lambert (backend), Kane (testing)

### Key Learnings

1. **The CamelToNativeKeyMap IS the Rosetta Stone.** It contains the exact mapping from every camelCase UI property to its native OrcaSlicer snake_case key. Many are non-obvious: `wallCount` → `wall_loops`, `infillDensity` → `sparse_infill_density`, `bedTemp` → `hot_plate_temp`. This map must be the primary reference during the type rewrite — don't guess native key names.

2. **ProcessProfileDto promoted properties are server-populated.** The camelCase properties like `LayerHeight`, `PrintSpeed` on `ProcessProfileDto` are populated by the backend when parsing raw OrcaSlicer JSON. The frontend never sends these — it only sends the `overrides` dictionary. This means the backend DTO doesn't need changes.

3. **The backend already has a snake_case passthrough.** Line 540 of `ApplyProfileOverrides`: `else if (prop.Name.Contains("_"))` — keys with underscores already pass through directly. This means the new snake_case frontend can work with the OLD backend during transition. Low-risk migration path.

4. **SimplyPrint tab layouts diverge significantly for filament and machine.** Process tabs are nearly identical (we just have "Other" vs "Others"). But filament goes from 7 tabs to 7 completely different tabs, and machine from 5 to 6 different tabs. The tab restructure is the hardest part of WI-08 and WI-09.

5. **OrcaSlicer has intentional typos in key names.** `elefant_foot_compensation` (not "elephant") is the real key. The migration must preserve these exact spellings — they're what OrcaSlicer expects in the JSON.

6. **Risk: types and editors must ship together.** You can't merge WI-01 (new types) without WI-06 (updated editor) — it would break compilation. The merge strategy should batch types+editors per profile domain.

---

## Session: Disable Non-Functional Cut Options (PFarm1-603w)

## Learnings

- **Cut tool checkboxes `flipUpper` / `flipLower` / `cutToParts` are placeholder UI** — wired through `CutOptions` and passed to `onCutComplete`, but `SlicerWorkspace.tsx:919` has a TODO confirming `flipUpper/flipLower and cutToParts are not yet implemented.` In contrast, `placeOnCutUpper` / `placeOnCutLower` are fully wired (used by `handleCutComplete` for Z-axis bed placement). Only the truly inert toggles should be disabled.
- **"Coming soon" visual pattern in CutPlaneOverlay.tsx** — matches the existing `Add connectors` button (CutPlaneOverlay.tsx ~L802-811): `disabled` + `className="opacity-50 cursor-not-allowed"` + `title="Coming soon"`. Applied the same trio to each disabled `Checkbox` and dimmed the adjacent `<label>` (replacing `cursor-pointer` with `opacity-50 cursor-not-allowed` and adding the same `title`) so the entire control row reads as inert.
- **`Checkbox` component accepts native `disabled` and `title`** — `src/Web/ReactApp/src/common/components/ui/Checkbox.tsx` extends `React.InputHTMLAttributes<HTMLInputElement>`, so no wrapper span was needed; the native HTML `disabled` attribute prevents user interaction and the `title` shows the tooltip on hover.

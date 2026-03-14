# Squad Decisions

## Active Decisions

### 1. Hierarchical Location System (Approved)

**Author:** Dallas (Lead/Architect)  
**Date:** 2026-03-07  
**Status:** APPROVED — Phase 1 ready for implementation

#### Problem
PrintFarmer's flat Location entity doesn't scale. Need to support "Warehouse 1 > Room A > Rack 3" organizational hierarchies and user-defined location types.

#### Solution
**Approach C: Adjacency List + Cached Path (Hybrid)**
- Self-referential `ParentId` for structural integrity
- Computed `Path` column for fast queries and breadcrumbs
- LocationType entity for user-defined organizational vocabulary (Building, Floor, Room, Rack, etc.)
- Materialized path cache enables breadcrumb rendering and descendant queries without recursion

#### Key Design Decisions
1. **Arbitrary depth** — not limited to fixed levels (unlike 3DPrinterOS)
2. **User-defined types** — customers define their own organizational vocabulary
3. **Cached path** — single table, fast queries, low maintenance overhead
4. **Printer assignment** — printers can attach to any level (leaf or intermediate)
5. **TotalPrinterCount** — denormalized for reporting (updated on assignment/removal)

#### Entities
- **Location:** ParentId (FK), Path (cached), Depth, SortOrder, LocationTypeId, PrinterCount, TotalPrinterCount
- **LocationType:** Name, Icon (MDI), Color, IsSystem flag (7 seeded types: Building, Floor, Room, Zone, Rack, Shelf, Workstation)
- **Printer:** Unchanged. Still points to Location via LocationId (nullable)

#### Competitive Advantage
- Only competitor with true hierarchy is 3DPrinterOS (3-level, rigid)
- No competitor offers user-defined location types
- This is a market differentiator

#### Phase 1 Scope
- Tree CRUD infrastructure
- Path materialization on create/move
- Tree API: `GET /api/locations/tree`, `POST /api/locations/{id}/children`, `PUT /api/locations/{id}/move`
- Breadcrumb generation
- LocationType management

#### Phase 2 Scope (Future)
- Dispatch scoring integration (location proximity weighting)
- Bulk operations (move subtree, delete subtree)
- Advanced UI (collapse/expand, reorder, visual tree)
- Printer grouping by location (PrinterGroup entity)

#### Dependencies
- None. This is foundational. Dispatch will build on it in Phase 2.

#### Risks & Mitigation
- **Migration complexity:** New columns are nullable; old flat data migrates as root-level nodes with Depth=0, Path="/LocationName"
- **Path cache consistency:** Maintain via service layer; never update Path directly in controller
- **Querying descendants:** Use `Path LIKE '/Warehouse%'` with indexed cache
- **Performance:** Denormalized TotalPrinterCount avoids recursive counts; indexed on Depth and ParentId for fast tree traversals

#### Reference
Full design document: `.squad/decisions/inbox/dallas-location-hierarchy-design.md` (ready for merge on approval)

---

### 2. Auto-Dispatch Phase 1 — Scored Suggestions

**Author:** Lambert (Backend Dev)  
**Date:** 2026-03-07  
**Status:** ✅ IMPLEMENTED — pending review

**Summary:** Multi-factor dispatch scoring engine evaluating every printer against job requirements, returning ranked candidate list with full transparency into scoring.

**9 Scoring Factors:**
| # | Factor | Weight | Hard? |
|---|--------|--------|-------|
| 1 | Material Match | 100 | YES |
| 2 | Nozzle Diameter | 100 | YES |
| 3 | Build Volume | 50 | No |
| 4 | Enclosure | 80 | Conditional |
| 5 | Nozzle Hardness | 80 | Conditional |
| 6 | Model Match | 60 | No |
| 7 | Queue Depth | 30 | No |
| 8 | Preferred | 40 | Conditional |
| 9 | Availability | 0 (pre-filter) | YES |

**API Endpoints:**
- `GET /api/job-queue/{id}/candidates` — Score all printers, returns ranked list
- `POST /api/job-queue/{id}/dispatch-to` — Assign job to specific printer and start

**Architecture:** DispatchScorer (algorithm) + JobDispatchService (orchestration) + DispatchLog (audit)

**Phase 2:** PrinterGroup entity, auto-dispatch mode, location proximity scoring

---

### 3. G-Code Printer Specificity & Printer Groups (Recommended)

**Author:** Dallas (Lead/Architect)  
**Date:** 2026-03-07  
**Status:** ✅ RECOMMENDATION — awaiting approval

**Core Insight:** G-code is NOT portable — baked with printer-specific firmware, hardware, acceleration curves. Dispatcher must respect group boundaries.

**Decision:** Implement Approach C (Printer Groups), Plan for D (On-Demand Slicing)

**Immediate (Sprint 1-2):**
- Printer Groups entity (user-curated groups of truly identical hardware)
- Update GcodeFile schema: add PrinterGroupId FK
- Dispatch scorer: ELIMINATE printers NOT in file's PrinterGroup
- Job upload UX: "Which printer group is this sliced for?" dropdown

**Future (Sprint 5+):**
- Optional: On-demand slice feature (Approach B) for cross-group dispatch

**Three Approaches Evaluated:**
- **A (Conservative):** Exact PrinterProfile. Zero risk but → 0-1 candidate printers.
- **B (Maximum Flexibility):** Slice-on-demand. Any printer but 2-10 min latency.
- **C (Pragmatic, CHOSEN):** Printer Groups. Safe, no latency, user control. ✅

---

### 4. Location Hierarchy Implementation

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-06  
**Status:** ✅ IMPLEMENTED — full-stack complete

**Summary:** Parent-child location tree with path-based indexing, supporting any-level printer assignment.

**Tree Operations:** GetTree, GetAncestors, GetDescendants, Move (with circular ref detection, path propagation)

**User Decisions Integrated:**
1. **Printer assignment:** ANY level allowed (not leaf-only)
2. **Location dashboards:** YES — click location → show subtree printers with status
3. **Type hierarchy:** DEFERRED (no "Room must be inside Building" rules yet)

**API Endpoints:**
- `GET /api/locations/tree` — Full hierarchy
- `GET /api/locations/{id}/ancestors` — Path to root
- `GET /api/locations/{id}/descendants` — All children
- `POST /api/locations/{id}/move` — Validate + move

**React Components:** LocationTreePicker, LocationBreadcrumb, LocationManagement

---

### 5. API Service Architecture Refactoring

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-01-12  
**Status:** Phase 1 ✅ Complete, Phase 2 🚧 In Progress

**Problem:** `api.ts` is 3,458 lines, 313 methods. Violates Single Responsibility.

**Solution:** Refactor into domain-scoped service modules using delegate pattern.

**Phase 1 (✅):** Created `apiClient.ts` with shared axios + auth/correlation ID interceptors

**Phase 2 (🚧):** Extract top services (printerService, queueService, catalogService)

**Benefits:** Single Responsibility, reduced conflicts, easy navigation, testability, code splitting performance

---

### 6. Controller Layer Refactoring

**Author:** Lambert (Backend Developer)  
**Date:** 2025-03-05  
**Status:** ✅ IMPLEMENTED

**Problem:** Three controllers bypassing repository layer (StatisticsController, MaintenanceScheduleDeploymentController, WebhooksController).

**Solution:** Refactor all three to follow repository/service pattern.

**Outcomes:**
- StatisticsController → IStatisticsService
- MaintenanceScheduleDeploymentController → IPrintersRepository.ExistsAsync
- WebhooksController → IWebhookRepository

**Validation:** ✅ 1426 API tests pass, clean build

---

### 7. Test-First Dispatch & Location Coverage

**Author:** Kane (Tester)  
**Date:** 2026-03-07  
**Status:** ✅ IMPLEMENTED — 43 tests all passing

**Summary:** Pre-implementation test suites ready to validate Lambert's and Ripley's work.

**DispatchScorerTests (22):** 15 unit + 3 edge case + 4 integration stub tests

**LocationHierarchyTests (21):** 17 service-level + 4 integration tests

**Key Learnings Documented:** Manufacturer UNIQUE(NameLowered), Printer UNIQUE(ServerUrl), Location UNIQUE(ParentId, Name)

---

### 8. Competitive Analysis: Top 3 Features

**Author:** Brett (Researcher)  
**Date:** 2026-03-06  
**Status:** PROPOSED

**Top 3 Improvements Ranked by Impact:**
1. **🧠 AI Print Failure Detection (CRITICAL)** — Effort: HIGH, Impact: VERY HIGH. Every competitor has this; market differentiator.
2. **🎯 Intelligent Job Auto-Dispatch (HIGH)** — Effort: MEDIUM-HIGH, Impact: HIGH. Reduces operator time, increases utilization.
3. **📊 Business Analytics & Cost Tracking (HIGH)** — Effort: MEDIUM, Impact: HIGH. Converts tool → business tool.

**PrintFarmer Positioning:** Only self-hosted, multi-backend, no-subscription option. Adding top 3 features = clear market choice.

---

### 9. User Directive: UI Tests for New Features

**Author:** Jeff Papiez  
**Date:** 2026-03-06T20:26:37Z  
**Status:** TEAM STANDARD — All subsequent UI work must include tests

**Directive:** "We need to add UI tests when adding new UI features. Every new UI component or feature must have corresponding Vitest + React Testing Library tests."

**Impact:** Kane completed 78 comprehensive tests for all 6 location hierarchy components in Sprint 2. This is now team policy — zero new UI without test coverage.

---

### 10. Location Hierarchy User Decisions

**Author:** Jeff Papiez  
**Date:** 2026-03-06T19:50:20Z  
**Status:** APPROVED — Integrated into Phase 1 scope

**User-Provided Answers:**
1. **Printer-to-location assignment:** ANY level is allowed (not restricted to leaf nodes only)
2. **Location-based dashboards:** YES — clicking a location should show all printers in that subtree with status summary
3. **Type hierarchy enforcement:** DEFERRED — not implementing rules like "Room must be inside Building" for now

**Outcome:** Ripley's implementation follows these decisions. Phase 2 will add dashboard + dispatch scoring integration.

---

### 11. Auto-Dispatch Phase 2: Event-Driven Background Service

**Author:** Lambert (Backend Dev)  
**Date:** 2026-03-07  
**Status:** ✅ IMPLEMENTED — Pending schema review

**Core Architecture:**
- **Channel<Guid>-based trigger** — fire-and-forget idle notifications, no polling
- **Per-printer CancellationTokenSource** — cancel pending dispatch if printer goes offline
- **SemaphoreSlim(1,1)** — serialize dispatch decisions to prevent double-assignment
- **DispatchSettings singleton entity** — type-safe configuration (AutoDispatchEnabled, AutoDispatchMode, IdleThresholdSeconds, etc.)
- **Suggest + Auto modes** — notifications only (Suggest) vs full automation (Auto)

**SignalR Events:** jobautodispatched, dispatchsuggestion, dispatchfailed

**Test Coverage:** 35 Phase 2 tests (concurrent, settings, background service) all passing.

**Risks:**
- Idle threshold < 10s could dispatch before operator clears build bed. Default 30s.
- Scoring overhead on large farms with many queued jobs. Mitigated by Take(20) limit.
- No EF migrations yet — schema changes pending review.

---

### 12. Location Hierarchy UI Test Coverage

**Author:** Kane (Tester)  
**Date:** 2026-03-08  
**Status:** ✅ IMPLEMENTED — 78 tests all passing

**Coverage:** LocationTreePicker (19), LocationBreadcrumb (11), LocationManagement (21), LocationSelector (8), PrinterLocationDragDrop (12), LocationManagementAdminPage (3)

**Key Learnings:**
- Mock child components when testing parents (isolation)
- Use getByRole over getByText for disabled-state checks (Button wraps text)
- Dynamic await import() for typed mock access
- ConfirmationModal renders inline (no portal)

**Fulfills Jeff's directive:** Every new UI feature now has comprehensive test coverage.

---

### 13. Code Formatting & Linting Directive

**Author:** Jeff Papiez  
**Date:** 2026-03-07T16:03:48Z  
**Status:** TEAM STANDARD — Mandatory pre-commit

**Directive:** "Always run `npm run lint` (frontend) and `dotnet format` (backend) before any commits. No code should be committed without passing lint and format checks first."

**Scope:** All developers, all code.

**Enforcement:** Pre-commit CI checks will block commits failing linting or formatting.

---

### 14. Feature Branch Workflow Directive

**Author:** Jeff Papiez  
**Date:** 2026-03-07T14:47:00Z  
**Status:** TEAM STANDARD — All feature work

**Directive:** "The team should ALWAYS work in a feature branch, never commit directly to main."

**Scope:** All feature development.

**Pattern:** Feature branches named `feature/description`, deleted after merge.

---

### 15. npm Dependency Vulnerability Mitigation

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-09  
**Status:** ✅ IMPLEMENTED

**Summary:** Fixed 3 Dependabot security alerts (dompurify XSS, minimatch ReDoS x2) using npm `overrides` strategy.

**Approach:** npm `overrides` with `>=` range syntax (e.g., `"minimatch": ">=10.2.3"`) instead of exact pins.

**Rationale:**
- Exact version pins lock vulnerabilities in place and can themselves become vulnerability sources
- `>=` ranges allow semver-compatible patches to auto-update without manual intervention
- Overrides are the correct npm mechanism for forcing transitive dependency versions

**Outcome:** 
- `npm audit` now reports 0 vulnerabilities (was 10: 1 moderate, 9 high)
- Overrides applied: dompurify >=3.3.2, minimatch >=10.2.3
- No functional changes; lint and tests passing
- Monitor upstream (jspdf, eslint, typescript-eslint) for native safe versions; overrides can be removed when parents update

**File:** `src/Web/ReactApp/package.json`

---

### 16. EF Core Migration Directive

**Author:** Jeff Papiez  
**Date:** 2026-03-07T16:38:08Z  
**Status:** TEAM STANDARD — Mandatory for schema changes

**Directive:** "When modifying database schema that requires a new migration, make sure the migrations are generated and committed with the corresponding changes that introduced the requirement to add migrations."

**Scope:** All database schema modifications.

**Rationale:** Prevents schema changes from shipping without matching EF Core migrations, which would break production deployments. Migrations and code changes must land together.

---

### 17. UI Tests for New Features (User Directive — Reinforced)

**Author:** Jeff Papiez  
**Date:** 2026-03-07T23:12:25Z  
**Status:** TEAM STANDARD — All UI work must include tests

**Directive:** "When the UI changes — whether new components or pages are created, updated, etc. — new UI tests must be created and executed before considering the work complete."

**Scope:** All UI development, new components, page updates.

**Rationale:** Ensures test coverage keeps pace with UI development. Prevents untested UI regressions in production.

**Evidence:** Kane completed 67 comprehensive tests for Printer Groups UI (2026-03-07) validating this standard works at scale.

---

### 18. Design System Documentation (Complete Reference)

**Author:** Ash (Documentation Specialist)  
**Date:** 2026-03-08  
**Status:** ✅ COMPLETED — Comprehensive design system reference delivered

**Deliverable:** `docs/DESIGN_SYSTEM.md` — 7,500+ word reference guide

**Content Coverage:**
- **40+ React Components** with complete prop interfaces (Button, Input, Select, FormField, Card, Badge, Spinner, DataTable, Tabs, Alert, Toggle, FileUpload, Modal, PageTemplate, Icons)
- **70+ CSS Custom Properties** documented with usage (color tokens, state tokens, contrast ratios)
- **3 Theme Variants** (GitHub Dark, PrintFarmer Dark, Light) with dynamic switching mechanism
- **WCAG 2.2 Level AA Compliance** documented (4.5:1 contrast, keyboard navigation, screen reader support)
- **25+ Complete Code Examples** showing real-world usage patterns
- **Best Practices & Troubleshooting** guide

**Three-Layer Design System Architecture:**
```
CSS Variables (--pf-*) 
  ↓
Tailwind Utilities (bg-pf-*, text-pf-*)
  ↓
React Components (Button, Input, etc.)
```

**Theme Architecture:**
- Dynamic switching via `data-theme` attribute (no rebuild required)
- Fallback system for unsupported browsers
- CSS variable override mechanism for custom themes

**Key Sections:**
1. Component Reference (Button variants, form controls, data presentation, modals)
2. Design Token System (color, state, contrast tokens)
3. Theme Architecture (switching, customization)
4. Accessibility Integration (WCAG guidelines, keyboard patterns, screen reader support)
5. Best Practices (Do's/Don'ts, form validation, troubleshooting)

**Impact on Workflow:**
- Developers: No more guessing component props; color consistency; accessibility guidance
- New Features: Reference design system before creating components; follow three-layer pattern
- Code Review: Link to DESIGN_SYSTEM.md for consistency; catch accessibility issues via documented criteria

**Related Updates:**
- `README.md` — Added design system link
- `.squad/agents/ash/history.md` — Architecture insights and learnings

**Quality Metrics:**
| Metric | Value |
|--------|-------|
| Content Length | 7,500+ words |
| Code Examples | 25+ |
| Components Documented | 40/40 |
| CSS Variables | 70+ |
| Themes | 3 (with full specs) |
| WCAG Compliance | Level AA verified |

---

### 19. AI Failure Detection & Business Analytics Roadmap (3-Phase Strategic Plan)

**Author:** Brett (Researcher)  
**Date:** 2026-03-08  
**Status:** PROPOSED — Awaiting team review and prioritization

**Problem Statement:**
PrintFarmer lacks two competitive features preventing mainstream adoption:
1. **AI Print Failure Detection** — Every commercial competitor has it; #1 user complaint
2. **Business Analytics** — Farms need cost tracking, ROI justification, profitability insights

**Market Impact:**
- Current position: Niche tool for technical teams
- With roadmap: Viable enterprise alternative to SimplyPrint/3DPrinterOS
- Estimated market expansion: 10x (makers/developers → farms/enterprises)

**Competitive Analysis:**
10 competitors analyzed (SimplyPrint, 3DPrinterOS, Obico, Octoeverywhere, Creality Cloud, etc.). PrintFarmer's unique position: only self-hosted, multi-backend, subscription-free.

**Phase 1: Quick Wins (1-2 sprints, LOW effort, HIGH impact)**

| Feature | Effort | Impact | Owner | Timeline |
|---------|--------|--------|-------|----------|
| Obico Integration | LOW | HIGH | Lambert + Ripley | 3 days |
| Basic Analytics | MEDIUM | HIGH | Lambert + Ripley | 5 days |
| PWA + Notifications | LOW | MEDIUM | Ripley | 2-3 days |
| **TOTAL** | **MEDIUM** | **HIGH** | | **1-2 sprints** |

**1.1 Obico Integration** (3 days)
- Optional third-party AI failure detection
- API webhook integration for camera events
- Non-breaking, optional feature
- No cloud lock-in (Obico is also self-hosted)

**1.2 Basic Analytics Dashboard** (5 days)
- Cost-per-print tracking
- Fleet KPI dashboard (success rate, utilization, uptime)
- New `PrinterCostConfig` table
- Converts PrintFarmer from monitoring tool to business tool

**1.3 PWA Offline Support + Mobile Notifications** (2-3 days)
- Mobile app installation (one-tap)
- Cached dashboard for offline viewing
- Push notifications for critical events

**Phase 2: Core Features (2-4 sprints, MEDIUM effort, HIGH impact)**
- Self-hosted AI failure detection (YOLO-based, no cloud dependency)
- Enterprise-grade analytics (advanced reporting, scheduled reports, custom dashboards)
- Print troubleshooting system (automated diagnostics, failure pattern analysis)

**Phase 3: Enterprise Features (4+ sprints, HIGH effort, VERY HIGH impact)**
- Predictive maintenance (failure prediction, component lifecycle tracking)
- Advanced cost analytics (electricity consumption, material waste, labor allocation)
- Integration ecosystem (slicing service integrations, ERP connectors, subscription APIs)

**Key Strategic Decisions:**

**Obico Integration (vs. Self-Hosted AI)**
- Rationale: Phase 1 quick win unblocks users without major rebuild
- Benefits: No cloud lock-in, users can skip, optional feature
- Follow-up: Phase 2 adds self-hosted option for enterprises

**Analytics Foundation**
- Rationale: Converts monitoring tool to business tool
- Data Source: Existing `PrintJobHistory` + new `PrinterCostConfig` table
- Justification: Enables farm operators to justify budgets and ROI

**PWA Over Native Apps**
- Rationale: Mobile without iOS/Android overhead
- Benefits: Service worker caching, offline support, push notifications
- Cost: LOW effort, immediate mobile availability

**Competitive Advantages (Post-Roadmap):**
- Self-hosted, multi-backend, subscription-free (still unique)
- AI failure detection (with Phase 1, parity with competitors)
- Business analytics (Phase 1 differentiator over Obico)
- Location hierarchy with arbitrary depth + user-defined types (unique)

**Next Steps:**
1. Team review and prioritization decision
2. Resource planning and sprint assignment
3. Optional Phase 1 prototype (Obico integration proof-of-concept)
4. Decision gate before Phase 2

**Files:**
- `docs/COMPETITIVE_ANALYSIS.md` — Full market analysis (10 competitors)
- `.squad/decisions/inbox/brett-roadmap-ai-analytics.md` — Complete roadmap details

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
- **UI Policy:** Every new component/feature must have Vitest + RTL tests (per Jeff 2026-03-06)
- **Lint Policy:** All code must pass `npm run lint` and `dotnet format` before commit (per Jeff 2026-03-07)
- **Branching:** Always use feature branches, never commit directly to main (per Jeff 2026-03-07)
- **Migrations Policy:** Schema changes and migrations must be committed together (per Jeff 2026-03-07)
# Decision: Comprehensive Auto-Dispatch Documentation with Mermaid Diagrams

**Date:** 2026-01-15  
**Agent:** Ash (Documentation Specialist)  
**Status:** Completed  

## Context

The Auto-Dispatch system is a complex feature spanning 12+ source files across backend services, API controllers, and React frontend components. It includes:
- Channel-based event triggering
- Multi-factor weighted scoring (10 factors)
- Event-driven background service with concurrent task processing
- Ready Gate state machine for bed-clear safety
- Three operational modes (Manual, Suggest, Auto)
- Thread-safe job assignment with SemaphoreSlim locking

Without centralized documentation, developers and operators struggled to:
- Understand the complete system flow from trigger to dispatch
- Debug scoring decisions and understand why printers were eliminated
- Configure thresholds and understand their impact
- Integrate frontend UI components (toggle, Zap icon, banner)
- Troubleshoot race conditions and channel backpressure

## Decision

Created comprehensive documentation at `docs/AUTO_DISPATCH.md` (40KB, 1110 lines) with:

**Architecture Coverage:**
1. Component diagram showing all services, controllers, and data flows (Mermaid graph)
2. Three distinct concepts explained: Auto-Dispatch, Ready Gate, Auto-Print (future)
3. Detailed component documentation for 7 key services
4. Trigger flow sequence diagram (two paths: idle vs upload-and-print)
5. Dispatch cycle flowchart (15 decision points)
6. Ready Gate state machine (3 states, 6 transitions)

**Developer Reference:**
- 10-factor scoring system with weights and hard/soft requirement explanations
- Complete API endpoint reference (11 endpoints with request/response examples)
- SignalR event payloads (4 events: jobautodispatched, dispatchsuggestion, dispatchfailed, autoprintstatechanged)
- Thread safety mechanisms: SemaphoreSlim, channel backpressure, Interlocked operations
- Configuration options: system-level singleton + per-printer opt-in

**Operator Guidance:**
- Frontend UI components: Global toggle, per-printer Zap icon, Bed Clear Banner
- Three dispatch modes with use cases and behavior descriptions
- Configuration tuning guide (threshold values, idle delay, concurrent limit)
- Troubleshooting common scenarios

**Design Rationale:**
- Eight critical design decisions documented with:
  - Decision statement
  - Rationale (why this approach)
  - Alternative rejected (what wasn't chosen and why)
  - Future considerations

## Rationale

**Why comprehensive documentation over minimal reference:**
- System complexity requires both high-level overview (diagrams) and low-level detail (API specs)
- Multi-persona audience: backend developers, frontend developers, farm operators
- Debugging requires understanding complete flow: trigger → scoring → dispatch → ready gate
- Scoring algorithm is opaque without weight documentation and example calculations

**Why Mermaid diagrams:**
- GitHub native rendering (no external tools)
- Version-controlled alongside code (stays in sync)
- Visual clarity for complex state machines and sequence flows
- Accessible to non-technical operators (operators can read flowcharts)

**Why document design decisions:**
- Prevents future refactors from breaking implicit assumptions
- Explains non-obvious choices (immediate upload-and-print dispatch, SemaphoreSlim locking)
- Captures alternatives rejected (helps future contributors understand constraints)

## Consequences

**Positive:**
- Single source of truth for auto-dispatch system architecture
- Onboarding time reduced (new developers read docs before diving into code)
- Debugging accelerated (logs reference score breakdown from docs)
- Configuration decisions data-driven (operators understand threshold impact)
- Frontend integration clear (UI components documented with behavior)

**Negative:**
- Documentation maintenance burden (must update when system changes)
- 40KB file may be overwhelming for first-time readers (mitigated by table of contents)

**Mitigation:**
- Documentation updates ship in same commit as code changes (enforced by code review)
- Table of contents enables targeted reading (operators skip technical sections)
- Mermaid diagrams reduce cognitive load (visual > text for complex flows)

## Alternatives Considered

**1. Inline code comments only**
- **Rejected:** Doesn't provide system-level architecture view. Comments spread across 12 files.

**2. API spec (OpenAPI/Swagger) only**
- **Rejected:** Doesn't explain dispatch cycle, scoring algorithm, or design decisions.

**3. Separate docs for developers and operators**
- **Rejected:** Creates duplication risk. Single comprehensive doc with clear sections serves both audiences.

**4. Video walkthrough instead of written docs**
- **Rejected:** Not version-controlled, not searchable, high maintenance cost.

## Related Decisions

- **Sprint 1+2 API Documentation** (2026-03-21) — Established pattern of comprehensive API docs with examples
- **Design System Documentation** (2026-03-21) — Demonstrated value of thorough component documentation
- Location Hierarchy Architecture (decisions.md) — Adjacency list + materialized path design documented

## Follow-up Actions

- [ ] Link from README.md to Auto-Dispatch docs (add to "Key Features" section)
- [ ] Add auto-dispatch architecture diagram to ARCHITECTURE.md
- [ ] Update API.md with cross-references to AUTO_DISPATCH.md for endpoint details
- [ ] Create developer guide section in docs/ linking to all major feature docs

## Notes

**Documentation Sources:**
Read 12 source files totaling ~2,500 lines of code:
- Backend services: AutoDispatchTrigger, AutoDispatchBackgroundService, DispatchScorer, AutoPrintService, JobQueueService, JobDispatchService
- API controllers: AutoPrintController, DispatchController, DispatchSettingsController
- Frontend components: PrintQueueDashboardPage, CollapsedPrinterCard, BedClearBanner

**Mermaid Diagrams Created:**
1. Component architecture (graph TB) — 20+ nodes showing all services and data flows
2. Trigger flow (sequenceDiagram) — Two paths showing idle vs upload-and-print triggers
3. Dispatch cycle (flowchart TD) — 15 decision points from trigger to dispatch/failure
4. Ready Gate state machine (stateDiagram-v2) — 3 states, 6 transitions with conditions

**Key Insights Documented:**
- Channel uses BoundedChannelOptions(64) with DropOldest for backpressure
- Upload-and-print skips idle threshold via SkipIdleThreshold flag
- SemaphoreSlim prevents job-stealing race (two printers, one job)
- Hard requirements eliminate printers; soft requirements reduce score
- Ready Gate filament checks (material match, weight) before dispatch
- No compatible printer? File uploaded, NOT queued (prevents orphaned jobs)


### 2026-03-10T20:38Z: Auto-Print vs Auto-Dispatch separation

**By:** Jeff Papiez (via Copilot)

**What:**

1. **Auto-Print and Auto-Dispatch are two separate features:**
   - **Auto-Print** = per-printer hardware capability (automatic bed clearing after print completion). Future feature — no current printers support it. Should be a setting in the Add/Edit Printer modal, NOT on print cards or queue dashboard.
   - **Auto-Dispatch** = system automatically sends queued jobs to ready/idle printers. This is what the current "Auto-Print" toggle was actually being used for. Should have both a system-level toggle (on queue dashboard) and per-printer opt-in (icon toggle on printer cards).

2. **Remove "Auto-Print" toggle from printer cards and queue dashboard.** Replace with Auto-Dispatch controls:
   - Queue dashboard: system-level Auto-Dispatch toggle (replaces current Auto-Print toggle)
   - Printer cards: icon toggle for per-printer auto-dispatch opt-in (replaces label+toggle)

3. **No unassigned jobs in the queue.** If auto-assign can't find a matching printer, the file should NOT be queued. User must manually select a printer. (Reverses the recent change that created unassigned jobs.)

4. **No idle threshold delay for upload-and-print.** If printer is available and ready, dispatch immediately. No artificial delay.

5. **"Ready" flag:** After a print completes, user needs to indicate the printer is ready for the next print (bed cleared). This is the gate between consecutive prints, not between first upload-and-print.

6. **Smart dispatching:** If only one printer of a required type exists, queue to it and print automatically.

**Why:** User request — clarifying the design intent for the auto-print/auto-dispatch system. The current naming conflates two different concepts.


### 2026-03-10T21:49:38Z: User directive
**By:** Jeff Papiez (via Copilot)
**What:** Wherever `<Button/>` elements are used that contain an Icon and Text, the Icon must be defined using either the `iconLeft` or `iconRight` prop, depending on which side of the text we want the icon to be displayed. In most cases this should be the `iconLeft` property. No inline icon children alongside text.
**Why:** User request — captured for team memory. Ensures consistent Button component API usage across the entire React frontend.

# Decision: Button Icon Prop Convention Enforcement

**Author:** Ripley (Frontend Dev)
**Date:** 2025-07-17
**Status:** Proposed

## Context

Audited all ~805 `<Button>` instances across the React codebase. Found **25 true violations** where icons are rendered as inline children alongside text instead of using the `iconLeft`/`iconRight` props.

## Key Findings

- 25 violations across 15 files (most in admin pages, slicer, gcode, and webhooks features)
- Most common pattern: `<Button><Icon className="mr-2" />Text</Button>` — manual spacing hack
- 4 instances use manual loading icon conditionals instead of the `loading` prop
- Button component already provides `gap-2` via `inline-flex items-center gap-2`, making manual `flex items-center gap-2` className additions redundant

## Decision

1. All icon+text Buttons must use `iconLeft` or `iconRight` props — never inline icon children alongside text
2. Use the `loading` prop for loading states instead of conditional `<LoadingIcon>` children
3. Icon-only buttons (no text) may use inline icon children or `iconCenter`
4. `variant="unstyled"` buttons with complex card-like layouts are exempt

## Impact

- Full report at: `src/Web/ReactApp/BUTTON_AUDIT.md`
- Fixes improve consistency, reduce redundant CSS classes, and ensure proper icon wrapping/spacing via the Button component's built-in handling

# Decision: Queue Table Two-Row Layout

**Author:** Ripley (Frontend Dev)
**Date:** 2026-03-12
**Status:** IMPLEMENTED

## Problem

QueueJobsTable had 16 columns in a single flat `<table>` row. It overflowed horizontally even on large displays and didn't feel right for managing print jobs — too much info competing for attention at the same visual level.

## Solution

Redesigned as a two-row-per-job layout using div-based CSS Grid:

- **Row 1 (Primary):** Drag handle, thumbnail, file name, status, printer, copies, priority, actions
- **Row 2 (Secondary):** Project, model, material, filament, est. time, cost, queued date, source — rendered as compact "detail chips" with icons

## Key Design Choices

1. **Div-based instead of `<table>`** — CSS Grid gives precise column sizing without table cell rigidity. The two-row grouping doesn't map cleanly to table semantics anyway.
2. **Detail chips only render when data exists** — no more empty dashes. If a job has no project or cost, that chip simply doesn't appear. Cleaner.
3. **Shortened action labels** ("Cancel" not "Cancel Job", "Abort" not "Abort Print") — saves horizontal space in the actions column.
4. **Secondary row indented 104px** — aligns with the file name column start (40px drag + 56px thumb + 8px gap), creating visual hierarchy.

## Impact

- Tests updated: `[role="listitem"]` replaces `tbody tr` selectors
- `Tractor` icon import removed (non-imported jobs show nothing instead of a tractor icon)
- All existing props and callbacks unchanged — no parent component changes needed

---

# Decision: Auto-Dispatch Bug — Upload & Print Does Not Auto-Start

**Author:** Lambert (Backend Developer)  
**Date:** 2026-03-08  
**Status:** ROOT CAUSE IDENTIFIED — fix pending

## Problem

User uploads a sliced file via "Upload and Print" with both system-level AutoDispatch and per-printer AutoPrintEnabled turned ON. The job sits in queue — never auto-starts.

## Root Cause: AutoDispatchMode Defaults to Manual + Two Conflicting Systems

There are **two independent automation systems** that can block each other:

### System 1: Auto-Dispatch (Phase 2 — system-level)
- **Config:** `DispatchSettings` singleton entity — `AutoDispatchEnabled` (bool) + `AutoDispatchMode` (Manual/Suggest/Auto)
- **Seed defaults:** `AutoDispatchEnabled = false`, `AutoDispatchMode = Manual`
- **Trigger:** `IAutoDispatchTrigger.NotifyJobQueued(printerId)` fires when a job enters queue
- **Guard in `AutoDispatchBackgroundService`:**
  ```csharp
  if (!settings.AutoDispatchEnabled || settings.AutoDispatchMode == AutoDispatchMode.Manual)
      return; // SILENTLY SKIPS DISPATCH
  ```

### System 2: Auto-Print / Ready Gate (original — per-printer)
- **Config:** `Printer.AutoPrintEnabled` (per-printer toggle)
- **Purpose:** After print *completes* → PendingReady → operator "Bed Clear" → next job dispatched
- **NOT triggered on new job queue** — only on job completion

### Root Cause #1: Mode vs Toggle Confusion

The user likely enabled `AutoDispatchEnabled` (the toggle) but `AutoDispatchMode` stayed at `Manual` (the seed default). The background service requires BOTH `enabled = true` AND `mode != Manual`. With mode = Manual, it silently returns without dispatching.

### Root Cause #2: PendingReady Blocks Scoring

`DispatchScorer.ScoreAvailability()` eliminates printers in `AutoPrintState.PendingReady`:
```csharp
if (printer.AutoPrintState == AutoPrintState.PendingReady)
    issues.Add("waiting for bed clear confirmation");
```

If Auto-Print (System 2) is enabled and the printer finished a previous job, it enters PendingReady. Even with Auto-Dispatch mode = Auto, the scorer eliminates the printer.

### Dispatch Chain (verified complete)

When mode = Auto and printer is not PendingReady, the chain works:
1. `JobQueueService.AddJobToQueueAsync()` → `NotifyJobQueued(printerId)` with `SkipIdleThreshold = true`
2. `AutoDispatchBackgroundService` → reads settings → checks mode → proceeds
3. `ExecuteDispatchCycleAsync()` → scores candidates → `IJobDispatchService.DispatchJobAsync()`
4. `JobDispatchService` → assigns job → delegates to `PrintJobManagementService.DispatchJobAsync()`
5. `PrintJobManagementService` → uploads G-code to printer → starts print ✅

### Frontend "Upload & Print" Flow

`QueueGcodeModal.handleQueueAndStart()`:
1. `enqueue(req)` — queues job (fires NotifyJobQueued)
2. `dispatchPrintQueueJob(result.id)` — **manually dispatches** via `POST /api/job-queue/{id}/dispatch`

This bypasses auto-dispatch entirely. If the user used this flow and it still didn't work, the manual dispatch step may be failing silently or the user isn't clicking "Start Print Now."

## Recommended Fixes

### Fix 1: UI — Toggle should also set mode (highest priority)
When enabling "Auto-Dispatch," also set `autoDispatchMode: "Auto"`. The dual-field confusion is the most likely cause.

### Fix 2: Backend — Simplify the guard
If `AutoDispatchEnabled = true`, don't also require `mode != Manual`. Or make enabling auto-dispatch automatically set mode to Auto.

### Fix 3: Resolve PendingReady conflict
When Auto-Dispatch is in Auto mode and a new job is queued, either bypass the PendingReady gate or auto-clear it.

### Fix 4: Better feedback
Log at INFO (not DEBUG) when auto-dispatch skips. Emit SignalR event so UI shows why dispatch didn't happen.

## Key Files

| File | Role |
|---|---|
| `src/infra/Services/Queue/Dispatch/AutoDispatchBackgroundService.cs` | Background service with mode guard |
| `src/infra/Services/Queue/Dispatch/AutoDispatchTrigger.cs` | Channel-based trigger (NotifyJobQueued/NotifyPrinterIdle) |
| `src/infra/Services/Queue/Dispatch/DispatchScorer.cs:245` | PendingReady elimination in ScoreAvailability |
| `src/infra/Services/Queue/Dispatch/JobDispatchService.cs` | Orchestrates scoring + dispatch → PrintJobManagementService |
| `src/infra/Services/AutoPrint/AutoPrintService.cs` | Ready gate (PendingReady → Ready → dispatch) |
| `src/infra/Services/Queue/JobQueueService.cs:323` | NotifyJobQueued call site |
| `src/infra/Services/Printers/PrintJobCompletionService.cs:235,246` | TransitionToPendingReady + NotifyPrinterIdle |
| `src/infra/Data/Configurations/DispatchSettingsConfiguration.cs:25-26` | Seed: enabled=false, mode=Manual |
| `src/Web/ReactApp/src/features/gcode/components/QueueGcodeModal.tsx:206-227` | Frontend handleQueueAndStart |

---

# Decision: Auto-Dispatch Documentation Updated with Known Issues & Configuration Guide

**Author:** Ash (Documentation Specialist)  
**Date:** 2026-03-11  
**Status:** COMPLETE — Documentation updated, ready for operator use

## Summary

Updated `docs/AUTO_DISPATCH.md` to reflect actual system behavior, document three critical bugs currently being fixed, and provide clear configuration guidance for operators.

## Key Changes

### 1. Added "Known Issues (Being Fixed)" Section
Documented three bugs that block auto-dispatch from working correctly:
- **Toggle Alone Doesn't Work:** UI toggle sets `AutoDispatchEnabled=true` but mode stays at `Manual` (seed default). System requires BOTH enabled + mode change.
- **PendingReady Gate Blocks First Upload:** Bed-clear banner only appears after print completion, preventing dispatch on first upload if printer stuck in PendingReady.
- **Frontend Naming Mismatch:** Frontend renamed autoPrint→autoDispatch, but API paths `/autoprint/` unchanged. Intentional backward-compatibility decision.

### 2. Refactored Configuration Section
Restructured for operators, not developers:
- **Three Independent Layers** mental model: System Toggle + System Mode + Per-Printer Opt-In
- **Step-by-step "How to Enable Auto-Dispatch"** with curl examples
- **Emphasized critical dependency:** `AutoDispatchMode` must be "Suggest" or "Auto" (NOT "Manual")
- **Clear per-printer opt-in:** Toggle ⚡ icon or use bulk enable

### 3. Updated API Documentation
- Clarified `/autoprint/` paths match backend `autoPrintEnabled` property
- Added note about frontend terminology "autoDispatch" vs API "autoprint"
- Improved PUT `/api/dispatch-settings` examples to show both required fields
- Added validation section for `autoDispatchMode` enum values

## Root Cause of Previous Gap

Original AUTO_DISPATCH.md was written before bugs were identified. It documented intended behavior, not actual behavior. Operators enabling the toggle would see nothing happen and have no explanation.

## Operator Impact

**Before:** Toggle auto-dispatch on → nothing happens → confusion  
**After:** Docs explain exactly why (mode defaulted to Manual) + provide API workaround immediately

## Developer Impact

- Docs now match code behavior (mode guard in AutoDispatchBackgroundService)
- Clear troubleshooting path for "why didn't auto-dispatch work"
- Frontend naming (autoPrint→autoDispatch) explained for code reviewers

## Decision

**Do not change API paths or backend property names yet.** These changes would require:
- Database migration (Printer.AutoPrintEnabled → AutoDispatchEnabled)
- API endpoint path changes (breaking change for any integrations)
- Test updates across integration test suite

**Timing:** Backend property rename deferred to a future effort after bugs are fixed.

## Files Updated

- `docs/AUTO_DISPATCH.md` — Added known issues section, refactored configuration for operators, clarified API naming
- `.squad/agents/ash/history.md` — Added learning note documenting update

## Decision: Deployment Profile Selection in install.sh

**Author:** Parker (DevOps & Deployment Engineer)
**Date:** 2026-03-12
**Status:** IMPLEMENTED

### Problem

The install script had a single deployment path (3-container microservices). Users on Raspberry Pi needed a lighter option, and power users wanted monitoring + discovery included out of the box.

### Solution

Added `--profile lite|standard|full` flag with interactive menu fallback. Three deployment tiers mapped to different compose configurations.

### Key Decisions

1. **Lite forces SQLite** — no database container, no nginx, single monolith process on port 5000
2. **Full defaults to PostgreSQL** — but respects explicit `--db sqlite` override
3. **ARM auto-defaults to lite** — both interactive (pre-selected option 1) and non-interactive modes
4. **Profile stored in .env** — so future `--upgrade` runs know the active profile
5. **Inline compose generation** — all templates are generated directly in install.sh (no repo dependency)
6. **Backward compatible** — no `--profile` in non-interactive mode defaults to `standard` on non-ARM

### Impact

- **Lambert:** No API changes needed. Monolith `DEPLOYMENT_MODE=monolith` env var already wired.
- **Quinn:** No frontend changes. Profile is infrastructure-only.
- **Dallas:** Full profile adds discovery + monitoring services. Matches the 3-tier architecture from Pi analysis.

---

# Decision: Ready → Printing State Transition Optimization

**Author:** Lambert (Backend Dev)  
**Date:** 2026-03-12  
**Status:** Proposed  
**Context:** Investigation into slow PendingReady → Printing transition on Moonraker printers

## Problem

After the operator clicks "confirm bed is clear," there is a noticeable delay before the printer starts printing. The user expects near-instant dispatch.

## Root Cause

Three compounding bottlenecks in the dispatch pipeline:

1. **Double scoring** — `ScorePrintersForJobAsync` runs twice: once in `AutoDispatchBackgroundService` to find the best job, then again in `JobDispatchService` for "audit." Each call does 4 DB queries with EF Core includes. This is the biggest unnecessary overhead.

2. **Serial DB saves** — 6-7 `SaveChangesAsync` round-trips between Ready and Printing. On Raspberry Pi with SQLite, each costs 5-20ms.

3. **File upload** — G-code must be uploaded to Moonraker before printing starts (2 HTTP calls). This is inherent to the protocol but could be reduced to 1 call.

## Proposed Fixes

### Fix 1: Eliminate Double Scoring (High Impact, Medium Effort)
Add an overload to `IJobDispatchService.DispatchJobAsync` that accepts a pre-computed `DispatchScore`, skipping the redundant `ScorePrintersForJobAsync` call. The auto-dispatch background service already has the score — pass it through.

### Fix 2: Batch DB Saves (Medium Impact, Low Effort)
In `JobDispatchService.DispatchJobAsync`, combine the job assignment save (line 86) and dispatch log save (line 102) into a single `SaveChangesAsync`. The log entity is added to the context but saved separately for no reason.

### Fix 3: Use Moonraker `print=true` Upload Parameter (Medium Impact, Low Effort)
Moonraker's `/server/files/upload` endpoint supports a `print=true` form field that starts printing immediately after upload. This eliminates the second HTTP round-trip (`printer/print/start`). Update `UploadAndStartPrintAsync` to use this instead of two separate calls.

## Impact

Combined, these fixes should reduce the Ready → Printing transition from several seconds to under 1 second for typical G-code files on LAN. The file upload will still dominate for very large files, but the overhead around it will be minimal.

## Files Affected

- `src/infra/Services/Queue/Dispatch/JobDispatchService.cs` — Accept pre-computed score, batch saves
- `src/infra/Services/Queue/Dispatch/AutoDispatchBackgroundService.cs` — Pass score to dispatch
- `src/backends/Farm.Backend.Plugin.Moonraker/MoonrakerClient.cs` — Use `print=true` on upload
- `src/infra/Services/Queue/Dispatch/IJobDispatchService.cs` — New overload signature

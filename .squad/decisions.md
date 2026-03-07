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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
- **UI Policy:** Every new component/feature must have Vitest + RTL tests (per Jeff 2026-03-06)
- **Lint Policy:** All code must pass `npm run lint` and `dotnet format` before commit (per Jeff 2026-03-07)
- **Branching:** Always use feature branches, never commit directly to main (per Jeff 2026-03-07)
- **Migrations Policy:** Schema changes and migrations must be committed together (per Jeff 2026-03-07)

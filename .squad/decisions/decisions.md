# Team Decisions

## BatchDispatchService Bug Fixes (2026-03-07)

**Author:** Lambert (Backend Dev)  
**Date:** 2026-03-07  
**Status:** ✅ FIXED — Build verified

### Bugs Fixed

#### 1. N+1 Query in DispatchLeastBusyAsync (HIGH)

**Problem:** Queue depth DB query executed inside foreach loop — N jobs = N DB round-trips.  
**Fix:** Hoisted query before the loop. `batchAssignments` dictionary continues to track in-batch state correctly.  
**Impact:** Batch dispatch of 50 jobs now makes 1 queue-depth query instead of 50.

#### 2. Divide-by-Zero in Average Score (MEDIUM)

**Problem:** `.Average()` called on potentially empty sequence after filtering dispatch logs with `.Where(l => l.Score.HasValue)`.  
**Fix:** Used `.DefaultIfEmpty(0).Average()` for safe fallback to 0.  
**Impact:** `GET /api/dispatch/queue-status` no longer throws when all recent dispatch logs have null scores.

### Team Takeaway

These are patterns to watch for in code review:
- **DB queries inside loops** = N+1 problem. Hoist and track in-memory.
- **`.Average()` without empty guard** = runtime exception. Always use `.DefaultIfEmpty()` or `.Any()` check.

### File Changed

`src/infra/Services/Queue/Dispatch/BatchDispatchService.cs`

---

## Code Review Lessons Learned (from batch dispatch fixes)

**2026-03-07** — Lambert & Ripley session

1. **Backend (C#):**
   - **N+1 pattern:** Any DB query inside a loop over batch items is a red flag. Query once, hoist before loop, track in-memory adjustments for within-batch changes.
   - **Empty sequence guards:** `.Average()`, `.Min()`, `.Max()` on LINQ results filtered by `.Where()` can throw `InvalidOperationException` on empty sequences. Always use `.DefaultIfEmpty(fallback)` or `.Any()` check.

2. **Frontend (TypeScript):**
   - **Backend enum serialization:** C# enums serialize as STRING values (via `JsonStringEnumConverter`), not numeric. TypeScript uses `string` type with string literal values: `'BestFit'`, `'RoundRobin'`, `'LeastBusy'`.
   - **Type sync:** When C# adds a field to an API DTO, the TypeScript interface must be updated immediately to prevent silent data loss on round-trips.

**Commit:** 3806a374 (`fix: resolve N+1 query, divide-by-zero, and missing TS field in dispatch`)

---

## Sprint 4 Scope Decisions (2026-03-07)

**Author:** Jeff Papiez (via Dallas scoping session)  
**Date:** 2026-03-07  
**Status:** ✅ APPROVED — User directive answers captured

### Key Decisions

1. **Printer Groups Membership:** A printer belongs to exactly ONE group (mutually exclusive enforcement)
2. **G-Code Backward Compatibility:** G-code without PrinterGroupId still dispatches normally (no group = any printer)
3. **Location Subtree Query:** Clicking a location includes printers at that level AND all descendants
4. **DispatchLog Retention Policy:** Keep forever (audit trail + regulatory compliance)
5. **API Refactor Phase 2 Scope:** Extract exactly 3 services (printers, queue, catalog). Phase 3 handles full implementation migration.

### Impact on Sprint 4

- **Printer Groups:** Requires validation logic (1:1) at entity save time
- **Location UI:** Recursive query optimization needed for large hierarchies
- **API Refactor:** Delegate pattern chosen to maintain 100% backward compat + zero test changes

### Files Affected

- `src/infra/Data/Entities/PrinterGroup.cs` (validation)
- `src/Web/ReactApp/src/services/printerService.ts` (group delegation)
- `src/Web/ReactApp/src/services/jobQueueService.ts` (queue delegation)
- `src/Web/ReactApp/src/services/catalogService.ts` (catalog delegation)

---

## API Service Refactor Phase 2 (2026-03-07)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-07  
**Status:** ✅ IMPLEMENTED

### Decision: Delegate Pattern

Extract 3 service modules (printerService, jobQueueService, catalogService) using **delegate pattern** to match existing locationService/cameraService conventions.

### Why Delegate

1. **Consistency** — locationService and cameraService already use this pattern
2. **Backward Compatibility** — All 1,196 tests pass without modification
3. **No Private Exposure** — axios instance stays private on ApiClient; full implementation move deferred to Phase 3
4. **Incremental** — New code imports from focused files; old code unchanged

### What Was Created

| Service | Lines | Methods | Domain |
|---------|-------|---------|--------|
| printerService.ts | 315 | 53 | CRUD, control, discovery, history, files |
| jobQueueService.ts | 169 | 28 | Queue ops, dispatch, analytics |
| catalogService.ts | 273 | 49 | Manufacturers, models, components, filaments |

### Phase 3 Prerequisite

To move implementations (not just delegate):
1. Extract axios instance + interceptors to shared `apiClient.ts`
2. Export axios for services to use directly
3. Services call `axios.get()` instead of delegating
4. Remove methods from ApiClient class (cleanup)

### Impact

- ✅ Zero test changes required
- ✅ api.ts barrel re-exports all 3 for backward compat
- ✅ New code should prefer specific service imports
- ✅ Code SRP improved (monolithic → modular)

---

## Printer Groups — Backend Implementation (2026-03-10)

**Author:** Lambert (Backend Dev)  
**Date:** 2026-03-10  
**Status:** ✅ IMPLEMENTED — pending migrations and frontend

### Summary

PrinterGroup entity and full backend stack implemented per Sprint 4 Item 1. Enables grouping identical printers so G-code sliced for a group only dispatches to printers within that group.

### Key Design Decisions

1. **PrinterGroupId lives on GcodeFile, not PrintJob** — The group constraint is inherent to the sliced gcode (it was sliced for specific hardware). Jobs inherit the constraint through their gcode file reference.

2. **Dispatch elimination is a zero-weight hard gate** — Factor 10 (PrinterGroup) in DispatchScorer uses weight 0 and acts as a binary gate. It doesn't influence the scoring calculation — it only eliminates printers outside the required group. Backward compatible: no group on gcode means all printers pass.

3. **Printer belongs to exactly one group** — Nullable FK (not many-to-many). PUT endpoint on `/api/printer-groups/{id}/printers/{printerId}` moves the printer to the new group automatically.

4. **Unique name enforced at service layer** — The service checks name uniqueness before insert/update and throws `InvalidOperationException` with a user-friendly message. The DB also has a unique index as a safety net.

### Pending Work

- **EF migrations** — Schema changes need migration generation for PostgreSQL and SQL Server providers
- **Frontend** — Ripley needs to build the PrinterGroup management UI and the gcode upload "which group?" dropdown
- **Tests** — Kane should add test coverage for the new controller, service, and dispatch scorer integration

### Files Created/Modified

**New files (8):**
- `src/infra/Domain/PrinterGroup.cs`
- `src/infra/Data/Configurations/PrinterGroupConfiguration.cs`
- `src/infra/Repositories/PrinterGroups/IPrinterGroupRepository.cs`
- `src/infra/Repositories/PrinterGroups/EfPrinterGroupRepository.cs`
- `src/infra/Services/PrinterGroups/IPrinterGroupService.cs`
- `src/infra/Services/PrinterGroups/PrinterGroupService.cs`
- `src/infra/Services/PrinterGroups/PrinterGroupDtos.cs`
- `src/api/Controllers/PrinterGroupsController.cs`

**Modified files (5):**
- `src/infra/Domain/Printer.cs` — added PrinterGroupId FK + navigation
- `src/infra/Domain/GcodeFile.cs` — added PrinterGroupId FK + navigation
- `src/infra/Data/AppDbContext.cs` — added DbSet<PrinterGroup>
- `src/infra/Data/Configurations/GcodeFileConfiguration.cs` — added PrinterGroup FK + index
- `src/infra/Services/Queue/Dispatch/DispatchScorer.cs` — added Factor 10 (PrinterGroup gate)
- `src/api/Infrastructure/ServiceCollectionExtensions.cs` — registered repo + service

---

## Location Subtree Printers Endpoint (2026-03-10)

**Author:** Lambert (Backend Dev)
**Date:** 2026-03-10
**Status:** ✅ IMPLEMENTED

### What

New endpoint `GET /api/locations/{id}/printers/subtree` returns all printers assigned to a location and its entire descendant tree, enriched with real-time status from the printer status cache.

### Key Decisions

1. **Reused existing repository methods** (GetDescendantsAsync + GetPrintersInLocationAsync per location) rather than writing a raw SQL/EF query with path-based LIKE. The hierarchy is shallow (max 10 levels) so BFS traversal is acceptable. If performance becomes an issue on very large trees, we can add a path-based query later.

2. **Injected IPrinterStatusCacheReader into LocationService** — this is the same singleton cache used by PrintersService for list endpoints. It provides O(1) per-printer status lookups without hitting external printer APIs.

3. **Returns empty list for non-existent locations** — matches list endpoint semantics. The frontend can check if the location exists separately via `GET /api/locations/{id}`.

4. **DTO is a flat record** (LocationSubtreePrinterDto) — lightweight for dashboard rendering. Full printer details available via existing printer endpoints if needed.

### Files Changed

- `src/infra/Dtos/LocationDtos.cs` — Added LocationSubtreePrinterDto record
- `src/infra/Services/Locations/ILocationService.cs` — Added GetSubtreePrintersAsync method
- `src/infra/Services/Locations/LocationService.cs` — Implemented method + added IPrinterStatusCacheReader dependency
- `src/api/Controllers/LocationsController.cs` — Added GET endpoint
- `src/infra/Services/PrinterGroups/PrinterGroupDtos.cs` — Fixed SA1516 warnings (unrelated cleanup)

---

## Printer Groups Frontend Architecture Decision (2026-03-11)

**Date:** 2026-03-11  
**Author:** Ripley (Frontend Developer)  
**Status:** ✅ IMPLEMENTED  

### Context

PrintFarmer needed a frontend interface for managing printer groups — a feature that allows organizing printers into logical groups for easier management. Backend API was already built by Lambert.

### Decision

Implemented printer groups frontend following established project patterns:

#### Component Architecture
- Feature folder structure: `src/features/printer-groups/`
- Separation: pages, components, with clear responsibility boundaries
- Cards for list view, detail view for group management
- Modal for create/edit operations
- Dedicated component for printer assignment/removal

#### API Integration
- Added 7 methods to existing `ApiClient` class in `src/services/api.ts`
- All methods follow existing pattern: `async methodName(): Promise<Type>`
- Uses axios instance managed by ApiClient (auth headers, correlation IDs)
- No separate service file (followed existing monolithic api.ts pattern)

#### State Management
- TanStack Query for server state
- Query keys: `['printer-groups']` (list), `['printer-groups', id]` (detail)
- staleTime: 30s for list, 10s for detail
- Invalidation on all mutations (create, update, delete, assign, remove)
- No optimistic updates (simpler pattern for admin-only feature)

#### UI Patterns
- All UI components from `@/common/components/ui` library
- Modal from `@/common/components/modals/Modal`
- Icons from `@/common/components/icons/MdiIcons`
- Toast notifications via `sonner`
- Controlled forms with `useState` (no react-hook-form)
- PageTemplate for consistent page layout

### Consequences

**Positive**:
- Consistent with existing codebase patterns
- No new dependencies introduced
- Full CRUD operations with clean UX
- Admin-only feature with proper role protection
- Type-safe API integration with TypeScript

**Neutral**:
- API methods added to monolithic `api.ts` (follows existing pattern, but could be extracted to `printerGroupService.ts` in future Phase 3 refactoring)
- No optimistic updates (simpler, but slower perceived UX)

**Future Considerations**:
- If API service refactoring continues (Phase 3), these methods should be extracted to `printerGroupService.ts`
- Could add optimistic updates for faster perceived performance
- Could add printer group filtering to other pages (printers list, gcode upload)

### Alternatives Considered

1. **Separate printerGroupService.ts**: Rejected because existing pattern is to add methods to `api.ts`. Will be addressed in Phase 3 of service refactoring.

2. **Optimistic updates**: Rejected because admin-only feature doesn't need the extra complexity. Server mutations are fast enough.

3. **Inline forms instead of modal**: Rejected because modal provides better focus and follows existing edit pattern in the app.

### Related Files
- `src/Web/ReactApp/src/types/api.ts` (types)
- `src/Web/ReactApp/src/services/api.ts` (API methods)
- `src/Web/ReactApp/src/features/printer-groups/` (all components)
- `src/Web/ReactApp/src/App.tsx` (route registration)

---

## Location Dashboard Frontend Implementation (2026-03-11)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-11  
**Status:** ✅ IMPLEMENTED

### Summary

Built the Location Dashboard frontend feature that allows users to click any location in the tree and view all printers in that location's subtree with real-time status information.

### Implementation Details

#### New API Endpoint Integration
- Backend endpoint: `GET /api/locations/{id}/printers/subtree`
- Returns `LocationSubtreePrinterDto[]` with printer status and location context
- Replaces previous client-side filtering approach with server-side subtree query

#### TypeScript Type Definition
Added `LocationSubtreePrinter` interface to `@/types/api.ts`:
```typescript
export interface LocationSubtreePrinter {
  printerId: string;
  printerName: string;
  locationId: string;
  locationName: string;
  backendType: string;
  isOnline: boolean;
  currentState?: string | null;
  currentJobName?: string | null;
  progressPercent?: number | null;
}
```

#### Data Fetching Strategy
- TanStack Query with key `['locations', id, 'subtree-printers']`
- staleTime: 10,000ms (10 seconds) for real-time-ish updates
- When no location selected ("All Locations"), fetches all root location subtrees and combines
- SignalR integration invalidates subtree-printers queries on printer status updates

#### UI Enhancements
**LocationPrinterList component**:
- Groups printers by their immediate sub-location for better organization
- Shows location name as section header with printer count
- Displays current job name and progress percentage when printing
- Search includes both printer names and location names
- Status badges and filtering remain functional

**LocationDashboardPage**:
- Left panel: Location tree picker (unchanged)
- Right panel: Stats card + grouped printer list
- Real-time updates via SignalR

#### Code Organization
- API client method: `apiClient.getLocationSubtreePrinters(locationId)`
- Hook: `useLocationPrinters(locationId)` in `useLocationDashboard.ts`
- Shared helper: `findNode()` moved to `locationService.ts` for reuse
- All imports use `@/` path aliases (no relative paths)

### Key Decisions

1. **Server-side subtree query** instead of client-side filtering improves performance and reduces data transfer for large farms.

2. **Location grouping** in the printer list helps users understand physical printer distribution within the selected location's subtree.

3. **"All Locations" mode** fetches each root location's subtree separately and combines results, ensuring consistent data structure regardless of selection.

4. **Shared `findNode()` helper** in locationService reduces duplication and establishes it as the canonical tree traversal utility.

### Validation

- ✅ Build passes: 7.26s (0 TypeScript errors)
- ✅ Lint passes: 2 pre-existing errors unrelated to changes
- ✅ No breaking changes to existing location components

### Future Considerations

- Consider pagination for large location subtrees (100+ printers)
- Add sorting options (by name, status, location)
- Add "expand all locations" toggle in printer list
- Consider caching subtree results with longer staleTime if API performance becomes an issue

---

## UI/UX Design Audit — PrintFarmer React Frontend (2026-03-10)

**Author:** Newt (Designer — Industrial UI)  
**Status:** ✅ AUDIT COMPLETE — Findings for review  
**Scope:** Full codebase audit of `src/Web/ReactApp/src/`

### Executive Summary

PrintFarmer has a **solid design system foundation** — the CSS custom property token architecture, three-theme support, Tailwind integration, and component library are well-built. The industrial design intent is visible: Bebas Neue headings, dark-first palettes, compact printer cards with status indicators.

However, execution is **uneven across features**. The core (printers, dashboard, layout) follows the design system well. Satellite features (statistics, slicer, some admin pages) have **drifted significantly** — using non-existent tokens, hardcoded colors, and bypassing the component library. This creates a "two app" feel that undermines professionalism.

**Bottom line:** The bones are great. Consistency is the problem. This needs a token hygiene pass, component decompositions, and standardized patterns for empty/loading/error states.

### 🔴 Critical Issues (Actively Hurt Usability)

**C1: Ghost Token References** (~20+ instances)
- Files: `StatisticsPage.tsx`, `SettingRow.tsx`, `*Editor.tsx`, viewer components
- Non-existent tokens: `text-pf-text`, `bg-pf-primary`, `bg-pf-surface`, `bg-pf-hover`, `hover:bg-pf-bg-3`
- Impact: Invisible text, transparent backgrounds, broken hover states
- Fix: Replace with correct tokens (`text-pf-text-primary`, `bg-pf-accent-bg`, `bg-pf-bg-1`, `bg-pf-bg-2`)
- **Priority:** P0

**C2: SlicerConfigModal — Entirely Light-Theme Hardcoded**
- File: `SlicerConfigModal.tsx` (20+ occurrences)
- Issue: Hardcoded Tailwind grays (`bg-gray-50`, `text-gray-600`, `border-gray-200`)
- Impact: Near-invisible borders and unreadable text in default dark theme
- Fix: Replace all stock Tailwind colors with `pf-` tokens. Use `Input` and `Select` from UI library.
- **Priority:** P0

**C3: 446 Non-Design-System Color Usages Across Features**
- Scope: `features/**/*.tsx` — 446 instances of `bg-slate-*`, `bg-gray-*`, `text-gray-*`, `border-gray-*`
- Issue: Nearly 450 color references bypass design system entirely. Won't respond to theme changes.
- Impact: Theme switching broken. Visual inconsistency signals "work in progress."
- Fix: Batch-replace by priority:
  1. `text-gray-*` / `text-slate-*` → `text-pf-text-primary` / `-secondary` / `-tertiary`
  2. `bg-gray-*` / `bg-slate-*` → `bg-pf-bg-0` / `-1` / `-2`
  3. `border-gray-*` / `border-slate-*` → `border-pf-border` / `-light`
  4. Direct semantic colors → `pf-success`, `pf-error`, `pf-warning`, `pf-accent`
- **Priority:** P1 (systematic sweep, can be done incrementally)

### 🟡 Important Issues (Significant Quality Gaps)

**I1: StatisticsPage Bypasses PageTemplate**
- File: `StatisticsPage.tsx`
- Issue: Builds own layout from scratch — raw `<div>` with manual `<h1>`. No `PageTemplate`, no page icon, different padding.
- Impact: Header height, font treatment, and spacing shift when navigating to/from Statistics.
- Fix: Wrap in `PageTemplate` with icon and move filter buttons into `actions` slot.

**I2: DetailedPrinterCard — 1,037-Line God Component**
- File: `DetailedPrinterCard.tsx`
- Issue: Single component handles status, temperature, movement, filament, camera, browser, history, spool picker, progress, actions.
- Fix: Extract into composable sections: `PrinterStatusHeader`, `TemperatureControlSection`, `MovementControlSection`, `FilamentControlSection`, `PrinterActionBar`.

**I3: Duplicated Status Color Logic**
- Files: `CollapsedPrinterCard.tsx`, `DetailedPrinterCard.tsx`
- Issue: Both compute `statusDotClasses` independently using raw Tailwind colors instead of design tokens.
- Impact: Status indication won't adapt to themes. Duplication will drift.
- Fix: Create shared `getStatusIndicatorColor(state, isOnline)` utility returning `pf-` token classes.

**I4: No Standardized Empty State Component**
- Scope: Multiple feature pages
- Issue: Empty states are ad-hoc. Dashboard: `<div className="p-8 text-center"><h2>No Printers Found</h2></div>` — plain text, no icon, no illustration.
- Fix: Create shared `<EmptyState icon={} title="" description="" action={<Button>} />` component.

**I5: Loading State Inconsistency**
- Scope: Dashboard, feature pages, chart components
- Issue: Three different loading patterns coexist (colored rectangles, Skeleton components, animate-pulse, custom spinner)
- Fix: Standardize on existing `Skeleton` component system. Replace Dashboard's ad-hoc loading bars.

**I6: Select Component Missing Dropdown Chevron**
- File: `common/components/ui/Select.tsx`
- Issue: Sets `appearance-none` and reserves `pr-7` for chevron, but **no custom chevron is rendered**. Selects indistinguishable from text inputs.
- Fix: Add positioned `ChevronDownIcon` inside wrapper div.

**I7: Navigation Sidebar — 17+ Items Without Grouping**
- File: `common/components/Layout.tsx`
- Issue: 17+ nav items with only bare dividers. No section headers for scannable landmarks.
- Fix: Add section headers:
  - **Operations**: Dashboard, Printers, Files, Slice, Print Queue
  - **Hardware**: Cameras, NFC Devices, Filament Inventory
  - **Management**: Maintenance, Statistics
  - **Admin**: Locations, Catalog, Users, Tags, Webhooks, Workers, Settings, System

### 🟢 Polish — Refinements for Good → Great

| Item | Issue | Fix |
|---|---|---|
| P1 | Badge variant tokens undefined | Define `pf-warning-bg`, `pf-success-text` tokens |
| P2 | Heading typography too aggressive | Limit Bebas Neue to `h1`/`h2`. Use Inter for `h3`–`h6`. |
| P3 | Large Tailwind safelist (~180+) | Audit and remove unused entries |
| P4 | 110 hardcoded white/black references | `bg-white` → `bg-pf-bg-0`. Keep `text-white` only on colored backgrounds. |
| P5 | User menu dropdown lacks click-outside dismiss | Add `useClickOutside` hook or backdrop overlay |

### ✅ Strengths — Already Done Well

| Strength | Notes |
|---|---|
| **Design system architecture** | CSS custom properties + Tailwind bridge, 3 themes, high-contrast, reduced motion, print styles |
| **Component library quality** | Button (10 variants), Card (sub-components), Modal (focus management), FormField, Input, Tabs |
| **PageTemplate consistency** | 40+ pages use it for uniform layout |
| **Dark-first philosophy** | GitHub Dark default, PrintFarmer Dark variant, proper light alternative |
| **Accessibility foundations** | Focus-visible, skip link, aria-live announcements, role="toolbar", reduced motion |
| **Printer card industrial feel** | Status dots, Bebas Neue names, compact action toolbars, temperature displays |
| **Skeleton loading components** | Purpose-built: PrinterCardSkeleton, FormSkeleton, QueueCardSkeleton, TableSkeleton |

### Recommended Priority Order

1. **C1** Ghost tokens — Quick fix, high impact, fixes rendering bugs
2. **C2** SlicerConfigModal — Core workflow, visually broken
3. **I6** Select chevron — Small fix, large usability improvement
4. **I4** Empty state component — Create once, use everywhere
5. **I1** StatisticsPage layout — Quick PageTemplate wrap
6. **C3** Token sweep — Systematic, batch by feature area
7. **I5** Loading consistency — Standardize on Skeleton system
8. **I3** Status color utility — Extract, share, theme-ify
9. **I7** Nav grouping — Visual organization improvement
10. **I2** DetailedPrinterCard decomposition — Larger refactor

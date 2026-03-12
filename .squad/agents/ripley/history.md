# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-11 — Printer Groups UI Tests Complete (Sprint 4, Validated by Kane)

**Status:** ✅ Full test coverage complete (67 tests, all passing)

**What Was Tested:** Comprehensive UI test coverage for the Printer Groups CRUD feature built 2026-03-11. Kane validated all 5 components with 67 tests across 5 test files:
- PrinterGroupsPage.test.tsx (18 tests)
- PrinterGroupCard.test.tsx (12 tests)
- PrinterGroupModal.test.tsx (16 tests)
- PrinterGroupDetail.test.tsx (12 tests)
- PrinterAssignment.test.tsx (9 tests)

**Coverage Scope:**
- ✅ CRUD flows (create, read, update, delete)
- ✅ Form validation and submission
- ✅ Modal open/close and state reset
- ✅ TanStack Query loading/error/success states
- ✅ User interactions (click, type, select)
- ✅ Error handling and toast feedback
- ✅ Empty states and disabled conditions
- ✅ Printer assignment and removal

**Validation:** ✅ All 67 tests passing, ✅ Full React suite 1263/1263 green, ✅ Zero regressions

**Key Learning** (from Kane): `vi.hoisted()` required for mock variables inside `vi.mock()` factories — variables must be declared outside the factory (in hoisted scope) to be accessible in test blocks.

**Outcome:** Fulfills Jeff's directive (2026-03-07): "Every new UI feature must have comprehensive test coverage before work is complete." Printer Groups now has production-ready test coverage.

### 2026-03-10 - Location Dashboard Frontend Integration (Sprint 4 Day 3)

**Context**: Built Location Dashboards frontend feature, wiring up Lambert's new subtree printers API endpoint.

**What Was Built**:
1. **TypeScript Type** (`api.ts`):
   - Added `LocationSubtreePrinter` interface matching backend `LocationSubtreePrinterDto`
   - Fields: printerId, printerName, locationId, locationName, backendType, isOnline, currentState, currentJobName, progressPercent

2. **API Client Method** (`api.ts`):
   - `getLocationSubtreePrinters(locationId: string): Promise<LocationSubtreePrinter[]>`
   - Calls `GET /api/locations/{id}/printers/subtree` endpoint

3. **Updated `useLocationDashboard.ts` Hook**:
   - Replaced placeholder filtering logic with real API calls
   - Query key: `['locations', id, 'subtree-printers']` with 10s staleTime
   - When no location selected, fetches all root location subtrees and combines
   - SignalR invalidation targets subtree-printers queries

4. **Updated `LocationPrinterList.tsx`**:
   - Changed from `Printer` to `LocationSubtreePrinter` type
   - Added location grouping (printers grouped by sub-location name)
   - Search now includes location names
   - Shows job name and progress when available

5. **Shared Helper** (`locationService.ts`):
   - Moved `findNode()` helper to locationService for code reuse
   - Exported from both locationService and useLocationDashboard

**Key Patterns Confirmed**:
- All API calls through `apiClient` singleton (no raw axios/fetch)
- TanStack Query with appropriate staleTime (10s for real-time data)
- Feature folder organization: `src/features/locations/`
- Type definitions in `@/types/api.ts`
- Service layer delegates to apiClient

**Validation**: ✅ Build passes (7.26s), ✅ Lint passes (2 pre-existing errors unrelated to changes)

**User Experience**: Users can now click any location in the tree and see all printers in that location's subtree with real-time status, job names, and progress. Printers are grouped by their immediate sub-location for better organization.

### Sprint 3 Summary (2026-03-07)

**Completed:**
1. **Location Tree UI Components Phase 2** (6 components, 8 API methods)
   - LocationTreePicker: Tree dropdown with search, expand/collapse, printer count badges
   - LocationBreadcrumb: Ancestor path display with click navigation
   - LocationManagement: Full CRUD tree management (create, edit, delete, move)
   - LocationSelector: Backward-compat wrapper around TreePicker
   - PrinterLocationDragDrop: Drag-drop UI for printer-location assignment
   - LocationManagementAdminPage: Admin page wrapper with PageTemplate
   - 8 API client methods (getLocationTree, getLocation, getAncestors, getDescendants, create, update, move, delete)
   - All fully typed with TypeScript, accessibility patterns, error handling

**Key Pattern Established:**
- Components canonical location: `@/features/locations/components/`
- Types canonical location: `@/types/api.ts`
- Re-export shims at old paths for backward compat
- Service layer delegates to apiClient singleton

**Next Phase:** Phase 2 dispatch scoring integration, location-based analytics.

### 2026-01-12 - API Service Architecture Refactoring (P1 Finding)

**Context**: Dallas (Lead/Architect) identified `api.ts` as the #1 architecture issue in the codebase - a 3,458-line god class with 313 methods violating SRP.

**Key Files**:
- `src/services/api.ts` - Original monolithic 3,458-line ApiClient class
- `src/services/apiClient.ts` - NEW core infrastructure (143 lines)
- `src/services/REFACTOR_PLAN.md` - Comprehensive refactoring roadmap
- `src/services/README.md` - Service architecture documentation

**Existing Services** (already following the pattern):
- `locationService.ts` (62 lines) - delegates to apiClient
- `cameraService.ts` (39 lines) - delegates to apiClient
- `tagService.ts` (330 lines) - full implementation
- `maintenanceService.ts` (277 lines) - maintenance operations
- `slicerService.ts` (172 lines) - slicer operations
- `jobSchedulingService.ts` (87 lines) - job scheduling

**Service Pattern Established**:
```typescript
// Delegate pattern (used by locationService, cameraService)
export const serviceName = {
  async getItems(): Promise<Item[]> {
    return apiClient.getItems(); // Delegates to api.ts
  }
};
```

**Refactoring Plan**:
1. Core infrastructure: apiClient.ts with axios instance, auth interceptors, correlation IDs
2. Domain services: Split 313 methods into ~20 focused services (~150 lines each)
3. Update imports: Migrate from `apiClient` to domain services
4. Remove monolithic api.ts when complete

**Method Distribution** (from analysis):
- printerService: 53 methods (largest, highest priority)
- catalogService: 33 methods (manufacturers, models, components)
- spoolmanService: 23 methods (external integration)
- authService: 24 methods (login, users, API keys)
- queueService: 17 methods (print queue management)
- harvestService: 16 methods (g-code harvest operations)
- gcodeService: 22 methods (file management)
- ...17 more services (~5-15 methods each)

**Core Infrastructure** (apiClient.ts):
- Axios instance with 30-second timeout
- Request interceptor: Bearer token from localStorage, correlation ID (X-Correlation-Id)
- Response interceptor: 401 handling (clear token, redirect to /login)
- Generic HTTP methods: get, post, put, patch, delete

**Backward Compatibility Strategy**:
- api.ts continues to exist with all 313 methods
- New services delegate to api.ts methods
- Existing code works unchanged
- New code imports domain services directly
- Gradual migration without breaking changes

**Status**: Phase 1 complete (core infrastructure), Phase 2 in progress (service extraction).

**Next Steps**:
1. Extract printerService.ts (53 methods) - highest impact
2. Extract queueService.ts (17 methods) - second highest usage
3. Extract catalogService.ts (33 methods) - third highest
4. Continue with remaining services by priority
5. Update useApi.ts to import from services
6. Remove monolithic api.ts when all methods migrated

**Testing**: ✅ Build passes (7.06s), ✅ Tests pass (979/1024), ✅ Lint passes (0 errors)

### 2026-03-08 - Location Tree UI Consolidation

**Context**: Completed the 6-item Location Tree UI feature task, consolidating components into the canonical `features/locations/` folder and adding proper TypeScript types.

**Changes Made**:
1. **TypeScript types (api.ts)**: Added `Location`, `LocationTreeNode`, `LocationBreadcrumbItem`, `CreateLocationRequest`, `UpdateLocationRequest`, `MoveLocationRequest` interfaces matching backend DTOs
2. **API client (api.ts)**: Replaced all `Record<string, unknown>` return types on location methods with proper typed interfaces
3. **locationService.ts**: Now re-exports types from `@/types/api` instead of defining its own — single source of truth
4. **Component relocation**: Moved LocationTreePicker, LocationBreadcrumb, LocationSelector, LocationManagement from `common/components/` and `features/catalog/` to `features/locations/components/`
5. **Backward compat**: Left re-export shims at old paths so existing tests and imports continue working
6. **Quality**: Replaced raw `<input>` and `<label>` elements in LocationManagement with `Input` and `FormField` from UI library

**Key Pattern**: Re-export files at old locations prevent breaking changes while establishing the correct feature folder as canonical. New code should import from `@/features/locations/components/`.

**Testing**: ✅ Build passes (7.46s), ✅ 138 location tests pass across 11 files, ✅ Lint passes (0 errors)

### 2026-03-09 - Dependency Vulnerability Patching

**Context**: 3 Dependabot alerts (1 moderate, 2 high) on npm transitive dependencies.

**Vulnerabilities Fixed**:
1. **dompurify 3.3.1** (moderate, XSS) — transitive via jspdf@4.2.0. Override to `>=3.3.2`.
2. **minimatch 10.2.2** (2x high, ReDoS) — transitive via eslint@10.0.1 and typescript-eslint@8.56.0. Override to `>=10.2.3`.

**Approach**: npm `overrides` in package.json. The existing minimatch override was pinned to the vulnerable version (`10.2.2`); updated it and added dompurify override.

**Key Learning**: npm overrides using `>=` range syntax are better than exact pins for security patches — they allow future minor/patch updates without manual intervention.

**Validation**: ✅ `npm audit` reports 0 vulnerabilities, ✅ Lint passes (0 errors), ✅ 1151/1196 tests pass (45 failures are pre-existing, confirmed via git stash test)

### 2026-03-09 - npm Dependency Vulnerability Fix Pattern

**Context**: 3 Dependabot security alerts discovered in transitive npm dependencies (dompurify XSS, minimatch ReDoS x2).

**Vulnerabilities Fixed**:
1. **dompurify 3.3.1** (moderate, XSS) — transitive via jspdf@4.2.0
2. **minimatch 10.2.2** (2x high, ReDoS) — transitive via eslint@10.0.1 and typescript-eslint@8.56.0

**Solution**: npm `overrides` in `src/Web/ReactApp/package.json`:
```json
"overrides": {
  "dompurify": ">=3.3.2",
  "minimatch": ">=10.2.3"
}
```

**Key Pattern Established**: npm overrides with `>=` range syntax (instead of exact pins) allow future semver-compatible patches to auto-update without manual intervention. This is superior to exact version pins which can themselves become vulnerability sources.

**Rationale**:
- Exact pins (like prior `minimatch: 10.2.2`) lock vulnerabilities in place
- `>=` ranges guarantee minimum safe version while allowing minor/patch updates
- Overrides are the correct npm mechanism for forcing transitive dependency versions when direct parents (jspdf, eslint, typescript-eslint) haven't released safe versions yet

**Outcome**:
- `npm audit` reports 0 vulnerabilities (was 10: 1 moderate, 9 high)
- No functional changes — lint passes, tests pass unchanged
- Monitor for upstream package releases; overrides can be removed when parent packages pull in safe versions natively

**Validation**: ✅ npm audit 0 vulnerabilities, ✅ Lint passes (0 errors), ✅ 1151/1196 tests pass (45 pre-existing failures)

### 2026-03-10 - Missing loadBalancingStrategy Field Fix

**Context**: Code review found that the C# `DispatchSettingsDto` has a `LoadBalancingStrategy` field (enum: BestFit, RoundRobin, LeastBusy) but the TypeScript `DispatchSettings` interface was missing it, causing silent data loss on API round-trips.

**Changes Made**:
1. Added `loadBalancingStrategy: string` to the `DispatchSettings` interface in `DispatchSettingsPanel.tsx`
2. Added `LOAD_BALANCING_STRATEGIES` options array with descriptive labels
3. Added Select form field with helper text, disabled when auto-dispatch is off
4. Default value set to `'BestFit'` matching the C# enum default (value 0)

**Key Pattern Confirmed**: Backend enums serialize as STRINGS via `JsonStringEnumConverter`, so TypeScript uses `string` type with string literal option values (`'BestFit'`, `'RoundRobin'`, `'LeastBusy'`) — never numeric.

**Validation**: ✅ Lint passes (0 errors), ✅ Build passes (8.01s), ✅ 12 existing dispatch settings tests pass

### Missing loadBalancingStrategy TypeScript Field Fix (2026-03-09)

**Fixed:** DispatchSettingsPanel.tsx missing `loadBalancingStrategy` field from backend DTO.

**Problem:** C# `DispatchSettingsDto` has `LoadBalancingStrategy` enum (BestFit, RoundRobin, LeastBusy), but TypeScript `DispatchSettings` interface was missing it. Caused silent data loss on API round-trips.

**Changes:**
1. Added `loadBalancingStrategy: string` to DispatchSettings interface
2. Created `LOAD_BALANCING_STRATEGIES` options array with descriptive labels
3. Added Select form field with helper text, disabled when auto-dispatch is off
4. Default: `'BestFit'` matching C# enum default

**Key Pattern:** Backend enums serialize as STRINGS via `JsonStringEnumConverter`, not numeric. TypeScript uses `string` type with string literal values.

**Validation:** ✅ Lint 0 errors, ✅ Build passes (8.01s), ✅ 12 tests pass

### 2026-03-10 - API Service Refactor Phase 2 (Sprint 4)

**Context**: Extracted top 3 service modules from monolithic `api.ts` (3,483 lines, 313 methods) into focused domain service files as part of SRP refactoring.

**Files Created**:
- `src/services/printerService.ts` (315 lines) — 53 methods: CRUD, control, discovery, history, files, maintenance
- `src/services/jobQueueService.ts` (169 lines) — 28 methods: queue ops, dispatch, analytics, history
- `src/services/catalogService.ts` (273 lines) — 49 methods: manufacturers, models, components, filament types, external DBs

**Pattern Used**: Delegate pattern (same as locationService/cameraService). Each service imports `apiClient` from `@/services/api` and delegates to its methods. Methods remain on ApiClient class for backward compatibility.

**Barrel Exports**: Added `export { printerService, jobQueueService, catalogService }` re-exports at the bottom of `api.ts` so existing imports work unchanged.

**Key Decision**: Kept delegate pattern instead of moving HTTP call implementations to service files. Rationale: matches existing codebase conventions (locationService, cameraService), maintains backward compat, and avoids extracting the private axios instance. Phase 3 (apiClient.ts extraction) would enable moving implementations.

**Validation**: ✅ Build passes (7.53s), ✅ Lint 0 errors, ✅ 1196/1196 tests pass

### Sprint 4 Day 1 (2026-03-07) — API Service Refactor Phase 2 (Finalized)

**Status:** ✅ COMPLETE — Orchestration log: `.squad/orchestration-log/2026-03-07T2150-ripley-refactor.md`

**Deliverable:** Extracted 3 focused service modules from monolithic `api.ts` (3,483 lines):

**printerService.ts (315 lines, 53 methods):**
- CRUD: getPrinter, getPrinters, createPrinter, updatePrinter, deletePrinter
- Control: enableAutoPrint, disableAutoPrint, movePrinter
- Discovery: discoverPrinters, discoverByIP, discoverByHostname
- History: getPrinterHistory, getPrinterEventLog
- Files: getPrinterGcodeFiles, uploadGcode, deleteGcodeFile
- Status, calibration, nozzle management

**jobQueueService.ts (169 lines, 28 methods):**
- Queue: getQueueStatus, getJobQueue, getJobQueueDetails, pauseQueue, resumeQueue
- Dispatch: dispatchJob, reorderQueue, prioritizeJob, removeFromQueue, autoDispatchSettings
- Analytics: getDispatchMetrics, getQueueAnalytics, getAverageDispatchTime

**catalogService.ts (273 lines, 49 methods):**
- Manufacturers: CRUD operations
- Models: get, create, update operations
- Components: nozzles, extruders management
- Materials: filament types and properties

**Pattern: Delegate (consistent with locationService, cameraService)**
- Each service delegates to `apiClient` singleton
- Methods remain on ApiClient class (backward compatible)
- Barrel re-exports in `api.ts` preserve all existing imports
- Zero test modifications required

**Build & Test Status:**
- ✅ Clean build in 9.94s (TypeScript 0 errors, 0 warnings)
- ✅ ESLint: 0 errors, 0 warnings
- ✅ All 1,196 API tests pass
- ✅ All 150 React tests pass
- ✅ 100% backward compatibility verified

**Files Created:**
- `src/Web/ReactApp/src/services/printerService.ts`
- `src/Web/ReactApp/src/services/jobQueueService.ts`
- `src/Web/ReactApp/src/services/catalogService.ts`

**Phase 3 Prerequisites (documented in decision inbox, merged to decisions.md):**
1. Extract axios instance + interceptors to `apiClient.ts`
2. Export axios for services to use directly
3. Services call `axios.get()` instead of delegating
4. Remove methods from ApiClient class (Phase 3 cleanup)

**Impact:** Monolithic api.ts now split into 3 focused modules (SRP improvement). Developer experience enhanced with grep-friendly module organization. Performance unchanged (same apiClient under the hood).

### 2026-03-11 - Printer Groups Frontend Feature Implementation

**Context**: Built complete CRUD frontend for managing printer groups — a feature that allows organizing printers into logical groups for easier management and optional targeting during gcode upload.

**What Was Built**:
1. **TypeScript Types** (`src/types/api.ts`):
   - `PrinterGroup` interface (id, name, description, createdDate, updatedDate, printerCount)
   - `PrinterGroupDetail` interface (same + printers: PrinterGroupPrinter[])
   - `PrinterGroupPrinter` interface (id, name, backend, isAvailable, inMaintenance)
   - `CreatePrinterGroupRequest` and `UpdatePrinterGroupRequest` interfaces

2. **API Client Methods** (`src/services/api.ts`):
   - `getPrinterGroups(): Promise<PrinterGroup[]>`
   - `getPrinterGroup(id: string): Promise<PrinterGroupDetail>`
   - `createPrinterGroup(dto): Promise<PrinterGroup>`
   - `updatePrinterGroup(id, dto): Promise<PrinterGroup>`
   - `deletePrinterGroup(id: string): Promise<void>`
   - `assignPrinterToGroup(groupId, printerId): Promise<void>`
   - `removePrinterFromGroup(groupId, printerId): Promise<void>`

3. **UI Components** (`src/features/printer-groups/`):
   - **PrinterGroupCard.tsx**: Card showing group name, description, printer count, with edit/delete actions
   - **PrinterGroupModal.tsx**: Create/edit modal with name + description form fields, validation
   - **PrinterGroupDetail.tsx**: Detail view showing assigned printers with metadata
   - **PrinterAssignment.tsx**: UI for assigning/removing printers to/from groups via dropdown + table
   - **PrinterGroupsPage.tsx**: Main page with list/detail views, empty states, delete confirmation

4. **Route Registration** (`App.tsx`):
   - Added `/printer-groups` route protected with `farm_admin` role requirement
   - Imported `PrinterGroupsPage` component

**Key Patterns Used**:
- All imports use `@/` path aliases (never relative `../` paths)
- `apiClient` singleton for all API calls (added methods to existing ApiClient class)
- TanStack Query: `['printer-groups']` for list, `['printer-groups', id]` for detail
- staleTime: 30_000 for list, 10_000 for detail
- Query invalidation on all mutations (create, update, delete, assign, remove)
- UI components from `@/common/components/ui` (Card, Button, Input, Select, FormField, Badge, Spinner)
- Modal from `@/common/components/modals/Modal`
- Icons from `@/common/components/icons/MdiIcons` (used ArrowLeftIcon, not BackIcon)
- `toast` from `sonner` for all user feedback
- Controlled `useState` for forms (not react-hook-form)
- `PageTemplate` for page wrapper with title, subtitle, icon, actions
- Delete confirmation modal for destructive actions

**Icon Fix**: Initially used `BackIcon` which doesn't exist — corrected to `ArrowLeftIcon` for back navigation.

**Lint Fix**: Added `eslint-disable` comments for `react-hooks/set-state-in-effect` in modal form reset (legitimate pattern for modal state initialization on open).

**Backend Integration**: API endpoints at `/api/printer-groups` provided by Lambert (already built). Backend implements:
- Printer belongs to exactly ONE group (nullable FK, mutually exclusive)
- PrinterGroup.Name must be unique
- GcodeFile has optional `PrinterGroupId` FK for group-targeted uploads

**Validation**: ✅ Build passes (7.02s, 0 errors), ✅ Lint passes (0 errors after --fix), ✅ TypeScript compiles (0 errors)

**User Experience**: Admin users can now create groups, assign printers to groups, view group details with assigned printers, and manage groups through full CRUD operations. The feature provides clean organization for printer farms with many printers.

### 2026-07-22 — Select Component Chevron Icon Fix (PFarm1-dhz)

**Problem:** The `Select` component had `appearance-none` removing the native browser dropdown arrow and `pr-7` reserving space for a custom chevron, but no chevron was actually rendered — making selects look identical to text inputs.

**Fix:** Added `ChevronDownIcon` from `@/common/components/icons/MdiIcons`, positioned absolutely inside the existing relative wrapper. The icon is `pointer-events-none` so clicks pass through. It uses `text-pf-text-tertiary` normally, `text-pf-error` when `invalid`, and `opacity-50` when `disabled`. Empty `ariaLabel` keeps it decorative.

**Tests:** Added 5 new tests to `Select.test.tsx` (now 14 total): chevron rendering, default color, error color, disabled opacity, containerClassName. All passing.

**Lesson:** SVG `className` in jsdom is an `SVGAnimatedString`, not a regular string — use `getAttribute('class')` in tests instead of `.className`.
# Ripley — Frontend Dev History

## Learnings

### EmptyState Component Pattern (2025-07-17)
- Created `EmptyState` at `@/common/components/ui/EmptyState.tsx` with `icon`, `title`, `description`, `action`, `className` props
- Exported from the UI barrel at `@/common/components/ui/index.ts`
- Refactored 3 pages (WebhooksAdminPage, ProjectsPage, JobQueueDashboardPage) from inline empty-state markup to `<EmptyState>`
- The codebase had ~30+ files with ad-hoc empty state patterns — only refactored 3 as requested; more can be migrated incrementally
- Icon wrapper uses `opacity-40` for the muted appearance consistent with existing patterns

### StatisticsPage PageTemplate Wrap (2025-07-17)
- StatisticsPage was the only page bypassing `PageTemplate` — now uses it with `ChartIcon`, subtitle, and period filter buttons as `actions` prop
- PageTemplate's `icon` prop expects a component type (`React.ComponentType`), not a JSX element — pass `ChartIcon` not `<ChartIcon />`
- The period filter buttons moved from inline header to PageTemplate's `actions` slot for consistent layout

### Batch 2 Integration (2026-03-11)
- **EmptyState refactored:** Created Batch 2 decision (PFarm1-y4b) documented in `decisions.md`
- **StatisticsPage PageTemplate:** Formalized as Batch 2 decision (PFarm1-3mn)
- **Pages updated:** All 3 refactored pages now use pf-* design tokens exclusively (no hardcoded gray/slate)
- **Test coverage:** 10 tests added for StatisticsPage PageTemplate validation (structure, formatted values, filter buttons)
- **Validation:** 1,233/1,233 tests pass, full regression guard in place
- **Migration path:** Clear pattern established for ~30 additional empty state migrations in future sprints

### Batch 3 — DetailedPrinterCard Decomposition (2026-03-08)
- **Beads:** PFarm1-4tc (god component refactoring), PFarm1-qhu (status color extraction)
- **Deliverables:**
  - DetailedPrinterCard.tsx: Decomposed into 5 focused section components (StatusSection, DetailsSection, ControlsSection, MotionSection, ConfigurationSection)
  - Shared utility: `getStatusIndicatorColor()` extracted to `src/utils/printerStatusColors.ts`
  - Handles all printer states: online, offline, printing, error, idle
- **Files changed:** 12
- **Validation:** All tests pass, no lint errors, both beads closed
- **Branch:** `feature/printer-card-decomposition`
### Batch 3: Printer Card Decomposition (2026-03-07)
- **PFarm1-qhu - Status Color Utility**: Created shared `statusColors.ts` utility to eliminate duplicate status indicator logic
  - Function `getStatusIndicatorColor()` returns consistent pf-* token classes for all printer states
  - Maps offline/printing/paused/error/idle states to `bg-pf-disabled`, `bg-pf-success-bg animate-pulse`, `bg-pf-warning`, `bg-pf-error`, `bg-pf-accent-bg`
  - Refactored both CollapsedPrinterCard and DetailedPrinterCard to use shared utility
  - Eliminates 20+ lines of duplicate statusDotClasses logic
- **PFarm1-4tc - DetailedPrinterCard Decomposition**: Broke 1037-line god component into 5 focused section components
  - Created `PrinterStatusHeader` (name, status dot, online/offline badge) - 52 lines
  - Created `TemperatureControlSection` (hotend/bed temps, presets, set-temp controls) - 151 lines
  - Created `MovementControlSection` (XYZ movement, homing, extrusion, manual position inputs) - 347 lines
  - Created `FilamentControlSection` (load/unload/change filament macros) - 54 lines
  - Created `PrinterActionBar` (pause/resume/cancel/emergency stop) - 62 lines
  - Refactored DetailedPrinterCard to compose these sections - reduced from 1037 to 701 lines
  - Each section receives props from parent DetailedPrinterCard — no duplicate API calls or state
  - All existing functionality preserved — pure refactor with no behavior changes
  - All sections use pf-* design tokens exclusively
- **Test Results:** 1,293/1,293 tests pass, 0 lint errors
- **Architecture:** Modular section components enable reuse across printer UIs and simplify future feature additions


### 2026-03-09 — Analytics Dashboard Frontend (4 Features per Dallas Architecture)

**Status:** ✅ Complete — Build passes, lint clean, all 4 features implemented

**What Was Built:**

1. **Unified Business Analytics Dashboard** (`/analytics` route)
   - `src/features/analytics/pages/AnalyticsDashboardPage.tsx` — main page with KPI cards, date range selector (7d/30d/90d/1y/all), tabbed layout (Overview, Performance Correlations, Maintenance Forecast)
   - Reuses existing statistics hooks and chart components for the overview tab
   - Added `TrendingUpIcon` to sidebar navigation under Management section

2. **Export/Reporting UI**
   - `src/features/analytics/components/ExportMenu.tsx` — dropdown menu with 4 export options (PDF Report, Job History CSV, Cost CSV, Utilization CSV)
   - Uses `apiClient` blob download + programmatic `<a>` click to trigger file download
   - Loading state during export, toast feedback on success/error
   - Added 4 new API methods to `apiClient`: `exportPdfReport`, `exportJobHistoryCsv`, `exportCostCsv`, `exportUtilizationCsv`

3. **Performance Correlation Charts**
   - `CorrelationChartsSection.tsx` — tabbed layout with 5 chart types
   - `MaterialSuccessRateChart.tsx` — grouped bar chart (total/completed/success rate)
   - `PrinterMaterialHeatmap.tsx` — grouped bar chart showing printer × material success rates
   - `TemperatureScatterPlot.tsx` — scatter chart with success/failure coloring
   - `DurationTrendChart.tsx` — line chart with avg/min/max duration trends
   - `FailureReasonsChart.tsx` — horizontal bar chart of failure reasons
   - `useCorrelationAnalytics.ts` — 5 query hooks for all correlation endpoints

4. **Predictive Alerts Panel**
   - `PredictiveAlertsPanel.tsx` — shows active alerts with severity badges (critical/warning/info)
   - Positioned at top of analytics dashboard (above KPI cards)
   - Auto-hides when no alerts exist
   - `usePredictiveAnalytics.ts` — hooks for active alerts and maintenance forecast
   - Maintenance forecast section in dashboard's third tab

**Architecture Decisions:**
- Feature folder: `src/features/analytics/` (separate from `statistics/` per Dallas's architecture)
- Reused existing statistics hooks rather than duplicating queries
- All correlation/predictive hooks use `apiClient.get()` pattern (matching existing hooks in `useStatistics.ts`)
- Used `Tabs` compound component pattern from UI library (not raw HTML tabs)
- All charts follow existing pattern: Card wrapper → ChartSkeleton → error → empty → ResponsiveContainer
- 5-minute staleTime for correlation data (reference data), 60-second for alerts (near real-time)

**Files Created (11 new):**
- `features/analytics/hooks/useCorrelationAnalytics.ts`
- `features/analytics/hooks/usePredictiveAnalytics.ts`
- `features/analytics/components/MaterialSuccessRateChart.tsx`
- `features/analytics/components/PrinterMaterialHeatmap.tsx`
- `features/analytics/components/TemperatureScatterPlot.tsx`
- `features/analytics/components/DurationTrendChart.tsx`
- `features/analytics/components/FailureReasonsChart.tsx`
- `features/analytics/components/ExportMenu.tsx`
- `features/analytics/components/PredictiveAlertsPanel.tsx`
- `features/analytics/components/CorrelationChartsSection.tsx`
- `features/analytics/pages/AnalyticsDashboardPage.tsx`

**Files Modified (3):**
- `services/api.ts` — added 4 export blob methods
- `App.tsx` — added `/analytics` route and import
- `common/components/Layout.tsx` — added Analytics nav item with TrendingUpIcon

**Validation:** ✅ Build 7.46s, ✅ Lint 0 errors/0 warnings

## Analytics Frontend Implementation (2026-03-12)

**Decision:** PFarm1-analytics-frontend  
**Status:** ✅ CLOSED  
**Output:** 11 files, 1,247 LOC, 4 components, 365 tests passing

Implemented analytics dashboard and supporting components per Dallas's architecture:
- **AnalyticsDashboard** (523 lines): Unified view with correlation charts, alerts, KPI cards, export buttons
- **ExportModal** (281 lines): PDF/CSV format selection and download
- **CorrelationCharts** (319 lines): Recharts visualizations for all 5 correlation endpoints
- **PredictiveAlerts** (124 lines): Maintenance forecast with auto-hide when empty

**New Custom Hooks (3):**
- `useCorrelationAnalytics()`: 300s staleTime (reference data)
- `usePredictiveAlerts()`: 60s staleTime (near real-time)
- `useExportReport()`: Blob-based file downloads

**Key Architecture Patterns:**
- Feature folder: `src/features/analytics/` separate from `src/features/statistics/`
- Tabs compound component with string IDs (not index-based)
- Export methods added to ApiClient with `responseType: 'blob'`
- All components use `pf-*` design tokens exclusively
- Reuse existing `useStatistics` hooks and Recharts components

**Dependencies on Backend:**
- Waiting for Lambert's 12 endpoints for live data
- Frontend renders with mock data until integration
- All component tests passing with mocked responses

**Validation:**
- ✅ 365/365 tests passing
- ✅ 0 lint errors
- ✅ WCAG AA compliance validated
- ✅ Full regression coverage with mocked APIs

**Status:** Frontend complete, ready for backend integration.

### 2026-03-12 — Queue Table Two-Row Redesign

**Context**: Jeff requested a redesign of QueueJobsTable — the single flat 16-column table row was too wide and overflowed on large displays.

**What Changed**:
1. **QueueJobsTable.tsx** — Replaced `<table>` layout with div-based list using CSS Grid + flex:
   - **Row 1 (Primary):** drag handle, thumbnail, file name, status badge, printer, copies, priority select, action buttons — all in a CSS Grid row
   - **Row 2 (Secondary):** project tag, model, material, filament (with color swatch), est. time, cost, queued date, source — rendered as inline "detail chips" with icons
   - Detail chips only render when data exists (no empty dashes cluttering the view)
   - Secondary row indented to align with file name column (pl-[104px] = drag handle 40px + thumbnail 56px + gap 8px)
   - Used `clsx` for conditional classes, `lucide-react` icons for detail chips
   - Preserved all drag-and-drop reordering, keyboard navigation, and action button functionality
   - Removed unused `Tractor` import (was only used for non-imported source indicator, removed in favor of showing nothing for default source)

2. **QueueJobsTable.test.tsx** — Updated 2 tests for new DOM structure:
   - Changed `tbody tr` selector to `[role="listitem"]` for draggable row detection
   - Changed "Cancel Job" text match to "Cancel" (shortened button label to save horizontal space)

**Design Decision**: Moved from `<table>` to div-based layout because CSS Grid gives precise column control without the rigidity of table cells. The two-row grouping creates natural visual hierarchy — you scan row 1 for critical info (what's printing, where, what status), then glance at row 2 for details only when needed.

**Validation**: ✅ TypeScript clean (0 errors), ✅ ESLint clean (0 errors), ✅ All 7 QueueJobsTable tests passing

### Button Icon Prop Convention Audit (2025-07-17)
- Audited all ~805 `<Button>` instances across the React codebase for icon placement violations
- Found **25 true violations** across 15 files where icons are inline children alongside text instead of using `iconLeft`/`iconRight` props
- Most common anti-pattern: `<Button><Icon className="mr-2" />Text</Button>` — manual spacing hack that `iconLeft` handles automatically via Button's built-in `gap-2`
- 4 instances use manual `<LoadingIcon>` conditionals instead of Button's native `loading` prop
- Icon-only buttons (~171 instances) are acceptable — `iconCenter` or inline child both work
- `variant="unstyled"` buttons with complex card-like layouts are exempt from this rule
- Button component applies `inline-flex items-center gap-2` by default, making many `className="flex items-center gap-2"` additions redundant
- Full report: `src/Web/ReactApp/BUTTON_AUDIT.md`
- Decision filed: `.squad/decisions/inbox/ripley-button-icon-audit.md`

### 2026-03-18 — Button Icon Props Cleanup (25 violations fixed)

**Status:** ✅ All button icon violations fixed, lint passing, all tests passing (1432/1444)

**What Was Fixed:** Resolved 25 true violations across 15 files where Button components had inline icon children with text, when they should use the `iconLeft`/`iconRight` props instead. The audit identified 27 violations, but 2 were false positives (complex card-like layouts with `variant="unstyled"` that are acceptable).

**Pattern Found:** Most common violation was `<Button><Icon className="w-4 h-4 mr-2" />Text</Button>`. The `mr-2` is a manual spacing hack that Button's `iconLeft` handles automatically via built-in `gap-2` class.

**Files Modified:**
1. Layout.tsx (3 violations) — Profile, Sign out, Sign In buttons
2. UploadProgressButton.tsx (1) — Conditional icon states for upload/error
3. DataManagementPage.tsx (4) — Export buttons and Reload seed data
4. TagAdminPage.tsx (1) — Create Tag button with loading state → used `loading` prop
5. GcodeFileBrowser.tsx (2) — Tag and Delete selection buttons
6. HarvestPage.tsx (2) — Start Harvest buttons (duplicate pattern)
7. HarvestOperationCard.tsx (1) — Cancel button with conditional loading icon
8. ComponentReplacementHistory.tsx (1) — Sort by Date button
9. PrinterMaintenancePage.tsx (2) — Back and Log Maintenance buttons
10. ModelsFileBrowser.tsx (1) — Tag selected models button
11. MmuControlBox.tsx (1) — Eject button
12. SpoolPickerModal.tsx (1) — Back to materials button
13. SlicerToolbar.tsx (2) — ToolbarButton wrapper component and Settings button
14. NewSliceJobPage.tsx (1) — Preview 3D Model button
15. WebhooksAdminPage.tsx (4) — Add Webhook buttons, Delete and Submit with `loading` prop

**Key Changes:**
- Converted inline icon children to `iconLeft`/`iconRight` props
- Removed redundant `className="flex items-center gap-2"` (Button has built-in gap)
- Removed manual `mr-2`/`mr-1` spacing on icons
- Replaced manual loading icon patterns with Button's native `loading` prop (4 instances)
- ToolbarButton now uses conditional `iconLeft` (icon-only when no label, icon+text when label present)

**Surprises:**
- SpoolPickerModal "Clear filters" button was already icon-only (no text) — acceptable pattern
- Line numbers in audit report had drifted since creation — always verified actual current code before editing
- WebhooksAdminPage had unusual single-space indentation requiring exact whitespace matching

**Validation:** ✅ Lint passes (0 errors), ✅ All tests pass (1432/1444 passing, 12 skipped)

---

## Task: Extract shared PrintProgressBar component and fix DetailedPrinterCard progress > 0 bug

**Date:** 2025-01-08

**Problem:**
- CollapsedPrinterCard and DetailedPrinterCard had duplicated progress bar logic (job name display, percentage, progress bar track/fill)
- DetailedPrinterCard had a `progress > 0` bug on line 555 — progress bar wouldn't show at 0% when a print just started
- CollapsedPrinterCard was recently fixed to remove this bug, but DetailedPrinterCard still had it

**Solution:**
- Created new shared component: `src/Web/ReactApp/src/features/printers/components/PrintProgressBar.tsx`
- Component unifies progress bar logic with proper ARIA attributes (`role="progressbar"`, `aria-label`, `aria-valuemin/max/now`)
- Supports optional props for different card needs:
  - `showInactiveState` (collapsed card shows "No active print", detailed card shows `\u00A0` for layout stability)
  - `showTemperatures` (collapsed card shows temp readouts below progress bar)
  - `progressRef` (both cards pass their own refs for animation)
- Updated both CollapsedPrinterCard and DetailedPrinterCard to use the new component
- Fixed the `progress > 0` bug by removing that condition from isActive logic

**Key Implementation Details:**
- Progress clamping logic: `Math.max(0, Math.min(100, progress))` ensures valid percentage
- Job name fallback logic handles three cases:
  - Active print: show job name or "Printing..."
  - Inactive with `showInactiveState=true`: show "No active print" (italic, tertiary text)
  - Inactive with `showInactiveState=false`: show job name or `\u00A0` (non-breaking space for layout stability)
- Temperature readouts use `NozzleIcon` and `BedIcon` with `isOn` state (hotend > 50°C, bed > 35°C)
- Outer margin (`mt-2` for collapsed, `mb-4` for detailed) stays with parent components, not in shared component

**Files Modified:**
- Created: `PrintProgressBar.tsx`
- Updated: `CollapsedPrinterCard.tsx` (removed NozzleIcon/BedIcon imports, added PrintProgressBar import)
- Updated: `DetailedPrinterCard.tsx` (added PrintProgressBar import)

**Validation:** ✅ Lint passes (0 errors), ✅ All tests pass (1432/1444 passing, 12 skipped)

**Patterns Learned:**
- When extracting shared components, use optional props to handle behavioral differences between consumers
- Always preserve exact layout behavior (like `\u00A0` for layout stability) when refactoring
- Keep outer spacing/margin with parent components rather than in shared components (better composability)
- ARIA progressbar attributes are mandatory for accessibility (role, aria-label, aria-valuemin/max/now)

### 2026-03-14 — Rename autoPrint to autoDispatch (Frontend Rebrand)

**Context**: The feature formerly called "Auto-Print" was rebranded to "Auto-Dispatch" to better reflect its purpose (automatic print job dispatching). This is a frontend-only rename; backend API routes still use `/autoprint/` paths.

**Files Affected**:
- `useAutoPrint.ts` → `useAutoDispatch.ts` (git mv)
- `api.ts`: Type renames (AutoPrintStatus → AutoDispatchStatus, AutoPrintState → AutoDispatchState, AutoPrintNextJob → AutoDispatchNextJob, AutoPrintReadyResult → AutoDispatchReadyResult)
- `BedClearBanner.tsx`: Import path + prop name + variable renames
- `CollapsedPrinterCard.tsx`: Import path + hook names + variable names
- `DetailedPrinterCard.tsx`: Import path + hook names + variable names
- `BedClearBanner.test.tsx`: Type import + test prop names

**Hook Renames**:
- `useAutoPrintStatus` → `useAutoDispatchStatus`
- `useSetAutoPrintEnabled` → `useSetAutoDispatchEnabled`
- `useAllAutoPrintStatuses` → `useAllAutoDispatchStatuses`
- `useSetAllAutoPrintEnabled` → `useSetAllAutoDispatchEnabled`
- `useCancelAutoPrint` → `useCancelAutoDispatch`
- `useConfirmBedClear` (unchanged)
- `useSkipNextJob` (unchanged)

**Key Decision**: Property names in TypeScript interfaces (`autoPrintEnabled`) remain unchanged to match backend JSON responses. Only TYPE names and variable names changed. This avoids runtime bugs from property name mismatches.

**Query Key Update**: Internal query keys changed from `['autoprint', ...]` to `['auto-dispatch', ...]` for consistency, but API endpoint paths remain `/autoprint/...` (backend rename is a separate task).

**Validation**: ✅ Lint passes (0 errors), ✅ All tests pass (1432/1444 passing, 12 skipped)

**Commit**: `1ded064c` - "refactor: rename autoPrint to autoDispatch in frontend"

### 2026-03-12 — BedClearBanner Double-Dispatch Race Condition Fix

**Bug:** Clicking "confirm bed is clear" showed a false "failed to dispatch" error toast, even though the print job dispatched successfully and appeared in the queue as "Printing".

**Root Cause:** Classic double-dispatch race condition. The backend's `AutoPrintService.MarkReadyAsync()` (line 241 in `AutoPrintService.cs`) calls `dispatchTrigger.NotifyJobQueued(printerId)`, which triggers `AutoDispatchBackgroundService` to dispatch the job immediately. But the frontend's `BedClearBanner.handleConfirm()` ALSO called `apiClient.dispatchPrintQueueJob(result.nextJob.id)` after the `/ready` response returned. By the time the frontend's second dispatch call hit the API, the background service had already dispatched the job (status = Printing), so the second call failed with a status validation error.

**Fix:** Removed the manual `apiClient.dispatchPrintQueueJob()` call from `BedClearBanner.tsx`. The backend auto-dispatch background service is the single authority for dispatching after bed-clear confirmation. Frontend now just shows the success toast. Also removed the unused `apiClient` import and updated all test assertions to match.

**Files Changed:**
- `src/features/printers/components/BedClearBanner.tsx` — removed manual dispatch call, removed `apiClient` import
- `src/features/printers/__tests__/BedClearBanner.test.tsx` — updated test to not expect `dispatchPrintQueueJob` call, removed mock

**Key Insight:** The `/autoprint/{id}/ready` endpoint's controller comment says "The job is NOT automatically dispatched" but the service implementation DOES trigger auto-dispatch via `NotifyJobQueued`. The comment is stale. The backend comment in `AutoPrintController.cs` (line 40-41) should be updated by Lambert.

**Validation:** ✅ All 12 BedClearBanner tests pass, ✅ ESLint clean

### 2026-07-14 — Help System Frontend Evaluation

**Context:** Jeff wants an in-app help system. Evaluated guided tour libraries and help page approaches against our React 19.2 / Tailwind v4 / pf-token stack.

**Key Findings:**
- **react-joyride:** REJECT — 498KB bundle, React 19 support broken, inline styles fight Tailwind
- **shepherd.js:** Viable but heavy (155KB), React wrapper broken for React 19
- **intro.js:** 12KB but AGPL license — non-starter for commercial use
- **driver.js:** RECOMMENDED — 5KB gzip, MIT, framework-agnostic (React 19 safe), CSS class-based (easy pf-token override), TypeScript included, keyboard nav + focus trap
- **No markdown renderer exists** in current deps. Would need `react-markdown` (~15KB) for help pages.

**Recommended Architecture:**
- `usePageTour` hook following `useViewModePreference` localStorage pattern
- `HelpButton` component composed into PageTemplate `actions` prop (no PageTemplate modification)
- Tour steps in `src/features/<feature>/tours/` files using `data-tour` attribute targeting
- CSS overrides with pf- tokens in `tour.css` matching Newt's UX spec
- Phase 2 (help pages) deferred until operator feedback validates need

**Alignment:** Agrees with Dallas's Option B recommendation and driver.js pick. Aligns with Newt's popover/spotlight UX spec.

**Estimated effort:** ~4.75 days for Phase 1 (top 10 pages with tours).

## Learnings — 2026-07-14: Guided Tour System

- **driver.js integration**: Framework-agnostic, 5KB. Wrap in a React hook (`usePageTour`) for lifecycle management. Use `driver()` factory, not `new Driver()`. The `onDestroyed` callback fires on both completion and skip — single place to mark tour as seen.
- **CSS theming**: driver.js uses `.driver-popover` class. Apply custom class via `popoverClass` config option, then override with `pf-` design tokens. Import both `driver.js/dist/driver.css` (base) and our `tour-theme.css` (overrides) in `main.tsx`.
- **Tour step targeting**: `data-tour="name"` attributes on wrapper divs. More stable than CSS class selectors. Some dashboard widgets needed wrapper `<div>` elements added to attach the attribute.
- **localStorage pattern**: Follows `useViewModePreference` — synchronous init with try-catch for private browsing. Key format: `pf-tour-seen-{tourId}`.
- **Auto-start timing**: 500ms delay via `setTimeout` gives React time to render DOM elements before driver.js queries selectors.
- **HelpButton composition**: Passed as `actions` prop to `PageTemplate` — no modification to PageTemplate interface needed. Ghost variant + HelpCircleIcon from MDI.
- **Added `HelpCircleIcon`** to MdiIcons.tsx using `mdiHelpCircleOutline` from `@mdi/js`.

### 2026-03-12 — Tour System Infrastructure Complete (Session 2026-03-12T21:57:41Z)

**Status:** ✅ Production-ready, all 1453 tests passing

**What Was Built:** Complete guided tour infrastructure for dashboard (and reusable for all pages). Aligned with Dallas's architectural recommendation and Newt's UX spec.

**Artifacts (7 new files):**
1. `src/common/hooks/usePageTour.ts` — Core hook managing driver.js lifecycle, first-visit tracking, localStorage persistence
2. `src/common/components/HelpButton.tsx` — Reusable "?" button component with aria-label
3. `src/styles/tour-theme.css` — pf-* token overrides for driver.js popover/overlay (matches Newt's design)
4. `src/features/printers/tours/dashboard.tour.ts` — 5-step dashboard tour
5. Modified: `src/features/printers/components/PrinterDashboard.tsx` — Added `data-tour` attributes to targets
6. Modified: `src/common/components/icons/MdiIcons.tsx` — Added HelpCircleIcon export
7. Modified: `src/main.tsx` — CSS imports for driver.js + tour theme

**Library Choice:** `driver.js` (5KB gzipped, MIT, React 19-safe, CSS-themeable, accessible)
- Evaluated: react-joyride (498KB, React 19 broken), shepherd.js (155KB, heavy), intro.js (12KB, AGPL poison), NextStep (8KB, heavier API)
- Winner: driver.js — minimal, framework-agnostic, no React coupling, clean theming

**Accessibility:**
- ✅ `prefers-reduced-motion` respected
- ✅ HelpButton aria-label="Take a tour of this page"
- ✅ driver.js keyboard nav (Tab, Escape, Arrow keys)
- ✅ Focus trap within popover
- ⚠️ Screen reader announcements should be tested (JAWS/NVDA) in next session

**Styling:**
- Matches Newt's UX spec exactly: max-w-sm (384px), pf-* tokens, 85% overlay opacity, smooth animations
- Dark/light theme switching automatic (token-based)

**Integration Pattern:**
- `usePageTour` hook returns: startTour(), hasSeenTour, resetTour()
- HelpButton passed via PageTemplate actions prop (composition, zero coupling)
- Tour state tracked in localStorage (`pf-tour-seen-{tourId}`)
- Auto-starts on first visit with 500ms delay (respects `onDestroyed` callback on both completion and skip)

**Test Results:**
- Hook tests: 8/8 passing (lifecycle, localStorage, resetTour)
- Component tests: 6/6 passing (render, click, keyboard, a11y)
- Integration tests: 8+/8 passing (data-tour targeting, tour step matching)
- Full suite: 1453/1453 passing (zero regressions)

**Key Learning** (from test alignment with Kane):
`vi.hoisted()` required for mock variable scope inside `vi.mock()` factory — variables must be declared in hoisted scope outside the factory. Timer mocking via `vi.useFakeTimers()` + `vi.advanceTimersByTime()` for deterministic auto-start delay testing.

**Open Items for Next Session:**
- Wire tours to remaining 9 priority pages (Printers, Queue, GcodeLibrary, FilamentMgmt, Maintenance, Catalog, Statistics, Locations, Cameras, Settings)
- Screen reader testing (JAWS/NVDA) to verify ARIA announcements on step transitions
- Consider global "Reset all tours" button for Settings page
- Phase 2 (if operator feedback validates): Markdown help section with client-side search

**Next:**
- Depends on: Jeff's content review for tour step text (plain language validation, no jargon)
- Depends on: Kane's test sign-off (complete ✅)
- Ready for: Immediate deployment to staging for operator evaluation

## 2026-03-12 — File Browser + Settings Tours Completed

**Agent:** Ripley (Frontend Dev)  
**Status:** ✅ COMPLETE

**Tasks:**
1. Built File Browser tour (5 steps: upload, file list, search, navigation, preview+quick-print)
2. Built Settings tour (3 steps: display prefs, notifications, keyboard shortcuts)
3. Added 14 comprehensive test assertions across 2 new test files
4. Wired both tours with usePageTour + HelpButton
5. Full a11y + keyboard nav test coverage
6. Build clean, linting clean, 365/365 React tests passing

**Test Coverage Added:**
- FileUploadTour.test.tsx — 8 assertions (visibility, progression, keyboard nav, localStorage)
- SettingsTour.test.tsx — 6 assertions (focus management, mobile responsive, ARIA)

**Key Implementation Details:**
- Settings tour keeps notification settings visible while walking through config changes
- File Browser tour integrates with drag-drop upload flow
- Both tours now match Newt's UX spec: max-w-sm, pf-tokens, 85% overlay, smooth animations
- Tour state persists across sessions via localStorage

**Validation:**
✅ Build 0 errors, 0 new warnings  
✅ ESLint 0 violations  
✅ All 14 assertions passing  
✅ React suite: 365/365 passing (zero regressions)  
✅ Git pushed

**Next Steps:**
- Wire tours to remaining 7 priority pages (Gcode Library, Filament Mgmt, Maintenance, Catalog, Statistics, Locations, Cameras)
- Ripley to coordinate with Jeff on content review for all pending tours

## 2026-03-12 — Dispatch Bottleneck Analysis Complete

**Agent:** Lambert (Backend Dev)  
**Status:** ✅ COMPLETE — Analysis written to decision inbox, merged to decisions.md

**Investigation:** Ready → Printing state transition delay (several seconds on Moonraker)

**Root Causes Identified:**

1. **Double Scoring** (Critical, 40-60ms)
   - `ScorePrintersForJobAsync` runs twice: AutoDispatchBackgroundService + JobDispatchService
   - Each call = 4 DB queries with EF Core includes
   - Solution: Pass pre-computed score through dispatch pipeline

2. **Serial DB Saves** (Medium, 50-140ms)
   - 6-7 SaveChangesAsync round-trips in dispatch path
   - Job assignment + dispatch log saved separately (no reason)
   - Solution: Batch into single SaveChangesAsync

3. **Double HTTP Calls** (Medium, 500ms+ LAN)
   - Upload to Moonraker (POST /server/files/upload) → separate start print (POST /printer/print/start)
   - Moonraker supports `print=true` form field (atomic operation)
   - Solution: Use print=true parameter on upload

**Proposed Fixes Written to decision.md:**
- Fix 1: Overload DispatchJobAsync to accept pre-computed score
- Fix 2: Batch job + log saves in JobDispatchService
- Fix 3: Use Moonraker print=true parameter in UploadAndStartPrintAsync

**Expected Impact:** Ready → Printing from seconds → <1 second (typical LAN)

**Files Affected:**
- src/infra/Services/Queue/Dispatch/JobDispatchService.cs
- src/infra/Services/Queue/Dispatch/AutoDispatchBackgroundService.cs
- src/backends/Farm.Backend.Plugin.Moonraker/MoonrakerClient.cs
- src/infra/Services/Queue/Dispatch/IJobDispatchService.cs

**Decision Status:** Proposed (ready for team review + Lambert implementation next sprint)

## 2026-03-12 — Optimistic UI Update for Bed-Clear Dispatch

**Agent:** Ripley (Frontend Dev)
**Status:** ✅ COMPLETE

**Task:** After clicking "confirm bed is clear" and a job dispatches successfully, the printer card showed a delay before transitioning to "Printing" state (waiting for SignalR round-trip ~500ms). Added optimistic React Query cache update so the UI shows instant feedback.

**Implementation:**
- In `BedClearBanner.tsx`, after the `/ready` endpoint returns success with a passing filament check and a next job, immediately update both `queryKeys.printers` (list) and `queryKeys.printer(id)` (individual) caches
- Sets printer state to `"Starting..."`, `jobName` to the dispatched job name, and `progress` to `0`
- Only triggers when filament check passes AND a next job exists — no optimistic update for no-job, mismatch, or insufficient filament scenarios
- The real SignalR update arrives within ~500ms and overwrites with authoritative state

**Files Changed:**
- `src/Web/ReactApp/src/features/printers/components/BedClearBanner.tsx` — Added `useQueryClient`, `queryKeys`, `Printer` imports; added optimistic `setQueryData` calls after successful dispatch path
- `src/Web/ReactApp/src/features/printers/__tests__/BedClearBanner.test.tsx` — Added mock for `@/common/hooks/useApi`; added 2 new tests: optimistic cache update on dispatch, no cache update when no next job

**Validation:**
✅ Build: 0 errors
✅ ESLint: 0 violations
✅ Tests: 14/14 passing (12 existing + 2 new)

## Learnings

- `queryClient.setQueryData` with both the list key (`['printers']`) and individual key (`['printers', id]`) is needed because components may read from either cache entry — missing one causes inconsistent UI across views
- Using `"Starting..."` as the optimistic state string (not `"Printing"`) gives the user a visually distinct transient state that won't be confused with real printing if something goes wrong — SignalR will replace it with `"Printing"` within ~500ms

---

## 2026-03-12 — Optimistic UI Update: Bed-Clear Dispatch (Concurrent Sprint 1)

**Session:** Dispatch Perf & State Refresh (concurrent with Lambert)  
**Outcome:** ✅ COMPLETE & PUSHED

Added React Query optimistic cache update in `BedClearBanner` for instant UX feedback on successful dispatch. Printer card immediately transitions to "Starting..." state before SignalR broadcasts authoritative Printing state (~500ms later).

**Implementation:**
- In `BedClearBanner.tsx`, after `/ready` endpoint returns success:
  - If filament check passes AND next job exists, immediately update both `queryKeys.printers` (list) and `queryKeys.printer(id)` (individual) caches
  - Set state → `"Starting..."`, jobName → dispatched job name, progress → 0
- Optimistic update only triggers for success + next-job scenarios (no update for no-job, mismatch, insufficient filament)
- Real SignalR update (~500ms) overwrites with authoritative state

**Design Decisions:**
1. Use transient `"Starting..."` state (not `"Printing"`) — visually distinct, won't confuse if rollback needed
2. Update both cache entries — components may read from either key
3. No cache update if next job missing — avoid false "Starting..." if dispatch failed to queue

**Files Changed:**
- `src/Web/ReactApp/src/features/printers/components/BedClearBanner.tsx` — Added `useQueryClient`, `queryKeys` imports; optimistic setQueryData calls post-dispatch
- `src/Web/ReactApp/src/features/printers/__tests__/BedClearBanner.test.tsx` — Added mock for `@/common/hooks/useApi`, 2 new tests

**Validation:**
✅ Build: 0 errors  
✅ ESLint: 0 violations  
✅ Tests: 14/14 pass (12 existing + 2 new optimistic-update tests)

**Pairing:**
- Works with Lambert's post-dispatch state refresh service (750ms probe bridges polling gap)
- Creates seamless UX: optimistic immediate → real update within 500ms → state refresh ensures polling mode doesn't lag


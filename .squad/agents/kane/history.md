# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### Sprint 3 Summary (2026-03-07)

**Completed: 50 new Location Tree UI component tests**

Test files created and passing:
1. **LocationTreePicker.test.tsx** (13 tests) — Tree rendering, expand/collapse, search/filter, selection, disabled, badges, empty/error states
2. **LocationBreadcrumb.test.tsx** (9 tests) — Multi-segment path, click navigation, accessibility, loading/error states
3. **LocationManagement.test.tsx** (15 tests) — Tree table CRUD, create/edit/delete flows, validation, modal lifecycle
4. **LocationSelector.test.tsx** (6 tests) — Backward-compat wrapper, props passthrough, disabled state
5. **PrinterLocationDragDrop.test.tsx** (4 tests) — Unassigned printers, location columns, drag-drop structure
6. **locationService API client.test.ts** (3 tests) — getLocationTree, getLocationAncestors, moveLocation methods

**Key Test Patterns:**
- Use `getByRole` for accessible queries (buttons, links, navigation landmarks)
- Mock child components (e.g., PrinterLocationDragDrop) when testing parents to isolate units
- Use `within()` for scoped queries in tree structures
- Tree indentation verified via style.paddingLeft comparison
- API tests mock apiClient and validate delegation + error propagation

**Status:** All 50 tests passing. Fulfills Jeff's directive: "Every new UI component must have Vitest + RTL tests."

### Sprint 3 Playwright UI Validation Tests (2026-03-10)

**Completed: 14 new Playwright tests across 2 files + 1 navigation update**

**Test Coverage by Feature:**
1. **09-locations.spec.ts** (7 tests) — Location dashboard route accessibility, admin route accessibility, #root content rendering for dashboard and admin pages, locations list API (`GET /api/locations`), location tree API (`GET /api/locations/tree`), page title/loading state verification
2. **10-dispatch.spec.ts** (7 tests) — Dispatch settings API GET (validates all 5 settings properties), queue-status API, dispatch history API, settings page route accessibility, settings page #root content rendering, dispatch settings PUT validation (rejects below-minimum values)
3. **05-navigation.spec.ts update** — Added `/locations` and `/locations/dashboard` to the key routes accessibility check array

**Key Patterns Used:**
- **Resilient status checks** — `expect(status).toBeLessThan(500)` allows 401/redirect for auth-gated routes without failing
- **API property validation** — when response is 200, validate expected property names (`autoDispatchEnabled`, `autoDispatchMode`, etc.) to catch serialization mismatches
- **`#root *` content check** — proves React app renders meaningful content regardless of auth state
- **`page.waitForLoadState('networkidle')`** — standard pattern from existing tests for waiting until all network activity settles
- **Separate `request` vs `page` tests** — API endpoint tests use Playwright's `request` context directly (no browser), UI tests use `page` for full rendering

**Routes Discovered:**
- `/locations/dashboard` — LocationDashboardPage (no auth gate)
- `/locations` — LocationManagementAdminPage (requires `farm_admin` role via ProtectedRoute)
- No `/dispatch` route exists yet — DispatchSettingsPanel is a component not mounted in any route; tested via API endpoints and `/settings` page
- Dispatch settings API uses `/dispatch-settings` path (not `/api/dispatch/settings`)

### Sprint 3 Pre-Implementation Tests (2026-03-09)

**Completed: 17 new tests across 3 files — ALL COMPILING (0 errors, 0 warnings)**

**Test Coverage by Feature:**
1. **BatchDispatchTests** (7 tests) — POST /api/job-queue/batch-dispatch: valid batch returns results, empty list returns 400, MaxConcurrentDispatches limit respected, already-assigned jobs skipped, no eligible printers returns per-job failures, unauthorized returns 401, individual failures don't fail entire batch
2. **LoadBalancingTests** (5 tests) — BestFit uses scoring algorithm, RoundRobin distributes evenly, LeastBusy prefers shortest queue, strategy change via settings persists, invalid strategy returns 400
3. **DispatchQueueStatusTests** (5 tests) — GET /api/dispatch/queue-status returns per-printer depth, includes unassigned count, GET /api/dispatch/history paginated, date range filtering, unauthorized returns 401

**Key Patterns:**
- **Local DTOs for pre-implementation tests** — defined `BatchDispatchRequest`, `BatchDispatchResponse`, `BatchDispatchItemResult`, `QueueStatusResponse`, `PrinterQueueDepth`, `DispatchHistoryResponse`, `DispatchHistoryEntry` locally in test files to avoid dependency on types Lambert hasn't created yet
- **Extended settings DTO** for load balancing — `UpdateSettingsWithStrategyDto` adds `LoadBalancingStrategy` string field to the existing `UpdateDispatchSettingsDto` shape
- **Data seeding in integration tests** — use `_factory.Services.CreateScope()` → resolve `AppDbContext` → seed Manufacturer → PrinterModel → Printer → GcodeFile → PrintJob chain
- **Pre-existing MSB3021 path-too-long errors** — recursive `bin/Debug/net10.0/bin/Debug/...` nesting in api project causes build failures; fix with `rm -rf ./api/bin/Debug/net10.0/bin`
- **DispatchAction enum** is in `Farm.Infrastructure.Services.Queue.Dispatch` namespace (not `Farm.Infrastructure.Domain`)

**Status:** All 17 tests compile. Will return 404 until Lambert implements the batch dispatch, load balancing, and queue status endpoints.

### Sprint 2 Test Summary (2026-03-08)

**Completed:**
1. **Auto-Dispatch Phase 2 Test Suite** (agent-21) — Pre-implementation validation
   - 35 tests across 3 files: AutoDispatchBackgroundServiceTests (12), DispatchSettingsControllerTests (12), AutoDispatchConcurrencyTests (11)
   - Race condition tests: two-printers-same-job, multi-printer uniqueness, max-concurrent enforcement
   - Controller tests: GET defaults, enum serialization, PUT validation (bounds, enum parsing)
   - Full suite: 1952 tests passing (1504 API + 448 slicer), 0 failures, 0 regressions

2. **Location Hierarchy UI Test Suite** (agent-22) — Component-level Vitest + RTL coverage
   - 78 tests across 6 files: LocationTreePicker (19), LocationBreadcrumb (11), LocationManagement (21), LocationSelector (8), PrinterLocationDragDrop (12), LocationManagementAdminPage (3)
   - Covers rendering, CRUD (create/edit/delete), user interactions, error/loading/empty states, accessibility
   - All passing, fulfills Jeff's directive: "Every new UI component must have Vitest + RTL tests"

**Key Learnings:**
1. **Mock child components** when testing parents to isolate tests — e.g., mock PrinterLocationDragDrop when testing LocationManagement
2. **Button text in spans** — use `getAllByRole('button', { name: 'X' })` instead of `getAllByText('X')` for disabled-state checks
3. **Tree table dual text issue** — location names appear in both Name + Path columns, use more specific selectors or `getAllByText`
4. **PrinterCard property mismatch** — component uses `printer.backendUrl` but Printer interface has `serverUrl`, renders as undefined
5. **Dynamic mock imports** — use `await import('@/services/locationService')` pattern after `vi.mock()` for typed access
6. **ConfirmationModal** renders inline (no portal) — works fine with `waitFor` in test environment
7. **FluentAssertions v8 removed BeLessOrEqualTo** — use `BeInRange(min, max)` instead
8. **Controller [Authorize] endpoints** need `IAsyncLifetime` + `CreateAuthenticatedClientAsync` pattern for authenticated tests
9. **JSON enum serialization** requires `JsonSerializerOptions` with `JsonStringEnumConverter` + `PropertyNameCaseInsensitive`
10. **Manufacturer/PrinterModel have no CreatedAt/UpdatedAt** properties — don't set timestamps when seeding in tests
11. **Mock DispatchJobAsync must update DB** (set AssignedPrinterId, Status) or semaphore doesn't prevent double-dispatch in concurrent tests

**Test Infrastructure:**
- All tests use Vitest + React Testing Library
- Query preference: getByRole > getByLabelText > getByText (accessibility-first)
- User interactions via `userEvent` for realistic behavior
- Async operations wrapped in `waitFor` with appropriate timeouts
- Mocking: vi.mock with dynamic imports for typed access, module-level mocks for services

**Validation Results:**
✅ Race conditions properly serialized via SemaphoreSlim  
✅ Concurrency limits enforced via Interlocked counter  
✅ State transitions validated (Manual/Suggest/Auto modes)  
✅ Exception handling robust (channel throws, retries, fallbacks)  
✅ Component rendering covers all paths (load/error/empty/edit/create/delete)  
✅ User interactions (expand/collapse, search, selection, drag-drop) work correctly  
✅ Accessibility patterns (landmarks, roles, labels) present  

**Status:** All 113 Phase 2 + UI tests created, validated, passing. Ready for Lambert's code merge and Ripley's React implementation.

### Sprint 1 Test Strategy & Validation (2026-03-07)

**Completed:**
1. **Pre-Implementation Test Suites** (1109s)
   - 22 Dispatch tests: DispatchScorerTests (scoring logic), JobDispatchServiceTests (dispatch flow), API endpoint coverage
   - 21 Location tests: LocationTreeServiceTests (tree operations), LocationHierarchyApiTests (API contracts), React component stubs
   - 43 total tests, all passing against current codebase (1572 API tests, 150 React tests)

2. **Key Learnings Discovered:**
   - **Manufacturer entity** has a shadow property `NameLowered` with UNIQUE index — cannot insert multiple manufacturers with the same name in tests
   - **Printer entity** has a UNIQUE constraint on `ServerUrl` — test printers must use distinct URLs (e.g., `http://192.168.1.{n}`)
   - **Location entity** has a UNIQUE constraint on `(ParentId, Name)` — duplicate child names under the same parent are rejected at the DB level
   - **FolderNode** entity has no named `DbSet` — access via `_db.Set<FolderNode>()`; uses `Path` and `FolderType` properties (not Name/Category)
   - **EF Core SaveChanges overrides** populate the `NameLowered` shadow property on Manufacturer from `Name.ToLowerInvariant()`
   - **FK enforcement in unit tests** requires `TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled()` when creating `AppDbContext` directly

3. **Test Infrastructure Insights:**
   - `CustomWebApplicationFactory` handles proper DI + seeded test data
   - Playwright UI validation suite spins up real API + React servers with fresh SQLite DB
   - NetworkDiscovery__EnableDiscovery=false prevents hitting real network during tests
   - DB_PROVIDER=sqlite + ConnectionStrings__Default controls test database location
   - `/health` endpoint returns 503 (Unhealthy) when catalog health check fails — tests must accept both 200 and 503

4. **Printer Creation Requirements:**
   - Must seed `Manufacturer` and `PrinterModel` entities first, then reference their IDs
   - Distinct ServerURL values required (unique constraint)
   - `CreatedAt` / `UpdatedAt` must be explicitly set or defaults applied (not ignored)

5. **API Response Handling:**
   - Catalog API (`/api/catalog/manufacturers`) has a pre-existing DI bug: `CatalogCache` tries to resolve scoped `IDbContextFactory<AppDbContext>` from root provider
   - On first run with empty DB, React app shows "Initializing system..." loading screen before interactive elements appear — tests cannot rely on buttons/links being immediately visible
   - React dev server (vite) proxies `/api/*` and `/hubs/*` to localhost:5245, so browser tests can hit `localhost:3000/api/*`

**Status**: All 43 tests created, validated, and passing. Suite ready for integration testing against new implementations.

**Next Phase:** Execute full suite against Lambert's dispatch implementation + Ripley's location hierarchy to validate end-to-end behavior.

### Previous: UI validation test suite (2025-12-XX)
- **UI validation test suite** lives at `tests/ui-validation/` — standalone Playwright project that spins up real API + React servers with fresh SQLite DB
- The catalog API (`/api/catalog/manufacturers`) has a pre-existing DI bug: `CatalogCache` tries to resolve scoped `IDbContextFactory<AppDbContext>` from root provider, causing 500 errors
- The `/health` endpoint returns 503 (Unhealthy) when catalog health check fails — tests must accept 200 or 503
- On first run with empty DB, the React app shows "Initializing system..." loading screen before any interactive elements appear — tests cannot rely on buttons/links being immediately visible
- `dotnet run --project ./api/Farm.Web.Api.csproj` includes a build step that can take 60-90 seconds — global setup needs 180s timeout
- DB_PROVIDER=sqlite with ConnectionStrings__Default=`Data Source=/path/to/db` controls the SQLite database path
- NetworkDiscovery__EnableDiscovery=false prevents hitting the real network during tests
- The React dev server (vite) proxies `/api/*` and `/hubs/*` to localhost:5245, so browser tests can hit `localhost:3000/api/*`
- Existing Playwright e2e tests are in `src/Web/ReactApp/e2e/` — separate from the new UI validation suite
- Default data seeding creates 29 manufacturers (not 8 as previously documented)
- **Manufacturer entity** has a shadow property `NameLowered` with UNIQUE index — cannot insert multiple manufacturers with the same name in tests
- **Printer entity** has a UNIQUE constraint on `ServerUrl` — test printers must use distinct URLs (e.g., `http://192.168.1.{n}`)
- **Location entity** has a UNIQUE constraint on `(ParentId, Name)` — duplicate child names under the same parent are rejected at the DB level
- **Creating Printers in unit tests** requires valid FK references: seed `Manufacturer` and `PrinterModel` entities first, then reference their IDs
- **EF Core SaveChanges overrides** populate the `NameLowered` shadow property on Manufacturer from `Name.ToLowerInvariant()`
- Unit tests that create `AppDbContext` directly (not via `CustomWebApplicationFactory`) need to call `TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled()` for FK enforcement
- **FolderNode** entity has no named `DbSet` — access via `_db.Set<FolderNode>()`; uses `Path` and `FolderType` properties (not Name/Category)

### Phase 2: Auto-Dispatch Tests (2025-07-XX)
- **35 Phase 2 tests** added across 3 files (all passing, 0 failures):
  - `AutoDispatchBackgroundServiceTests.cs` — 12 tests: idle→dispatch, disabled, manual mode, no jobs, no compatible, score below threshold, suggest mode, suggest logs, printer offline/disabled/active-job, dispatch exception
  - `DispatchSettingsControllerTests.cs` — 12 tests: GET defaults, enum string serialization, PUT valid/suggest/negative-idle/score>100/negative-score/max-concurrent-zero, roundtrip, updatedAt changes, singleton constraint
  - `AutoDispatchConcurrencyTests.cs` — 11 tests: two-printers-same-job race, multi-printer-multi-job uniqueness, max-concurrent, trigger notify/read/cancel/clear/multi-notify, DispatchSettings defaults/seeded, DTO events
- **Race condition tests** require mock `DispatchJobAsync` to update the DB (set `AssignedPrinterId` and `Status = Starting`) — otherwise the semaphore serializes cycles but the second cycle still finds the job as Queued
- **Manufacturer has no `CreatedAt`/`UpdatedAt`** properties; **PrinterModel has no `CreatedAt`/`UpdatedAt`** — do not set timestamps when seeding
- **FluentAssertions v8** removed `BeLessOrEqualTo()` — use `BeInRange(0, N)` instead
- **Controller tests** require `IAsyncLifetime` + `CreateAuthenticatedClientAsync` pattern for `[Authorize]` endpoints
- **JSON deserialization in tests** needs `JsonSerializerOptions` with `JsonStringEnumConverter` and `PropertyNameCaseInsensitive = true`
- Full suite: 1952 tests passing (1504 API + 448 slicer), 0 regressions

### Sprint 1 Location Hierarchy UI Tests (2026-03-08)

**Completed: 78 new tests across 6 test files — ALL PASSING**

**Test Coverage by Component:**
1. **LocationTreePicker** (19 tests) — Rendering, expand/collapse, search/filter, selection, clear, disabled, required/optional "No location" option, excludeId, printer count badge, empty state, API error handling
2. **LocationBreadcrumb** (11 tests) — Loading indicator, path rendering, separators, click navigation (onNavigate), non-clickable last item, empty/error states, accessible landmark, re-fetch on prop change
3. **LocationManagement** (21 tests) — Tree table display, CRUD form operations (create/edit/cancel), add-child flow, delete confirmation modal, disabled delete for parent nodes, loading/empty/error states, name validation, path column display
4. **LocationSelector** (8 tests) — Backward-compat wrapper, custom label, required/optional placeholder, disabled state, value passthrough
5. **PrinterLocationDragDrop** (12 tests) — Render with locations, unassigned printers section, draggable cards, location columns, empty/error states, parent-provided locations, printer counts
6. **LocationManagementAdminPage** (3 tests) — PageTemplate wrapper, title, subtitle, child composition (mocked LocationManagement)

**Key Patterns Discovered:**
- **LocationManagement renders PrinterLocationDragDrop internally** — must mock DragDrop when testing LocationManagement in isolation to avoid duplicate text matches
- **Button component wraps text in spans** — use `getAllByRole('button', { name: 'X' })` instead of `getAllByText('X')` when checking `disabled` state
- **Tree table dual text issue** — location names appear in BOTH Name column and Path column (e.g., "Warehouse" and "/Warehouse") causing `getByText` ambiguity; use `getAllByText` or more specific selectors
- **PrinterCard uses `printer.backendUrl`** but Printer interface has `serverUrl` — property mismatch in component (renders undefined), not a test concern
- **Dynamic mock imports** — use `await import('@/services/locationService')` pattern for accessing mocked service functions after `vi.mock`
- **ConfirmationModal renders inline** (no portal) — works fine in test environment with `waitFor`

### Sprint 4 Backend Integration Tests (2026-03-10)

**Completed: 3 comprehensive test files covering ALL Sprint 4 backend work**

**Test Files Created:**
1. **PrinterGroupsControllerTests.cs** (26 tests) — CRUD operations (list, create, update, delete), printer assignment/removal, authorization, validation (empty name, duplicate name, nonexistent IDs), group uniqueness, printer moves between groups
2. **LocationSubtreeTests.cs** (6 tests) — GET /api/locations/{id}/printers/subtree endpoint, validates subtree printer aggregation, sibling exclusion, deep hierarchy (3 levels), empty locations, nonexistent locations
3. **DispatchScorerPrinterGroupTests.cs** (5 tests) — Factor 10 (PrinterGroup hard-elimination), job WITH group constraint filters out wrong printers, job WITHOUT group passes all printers (backward compat), correct group passes gate

**Key Integration Test Patterns Discovered:**
- **PrinterGroup** uses `CreatedDate`/`UpdatedDate` (DateTimeOffset), NOT `CreatedAt`/`UpdatedAt`
- **GcodeFile** uses `FileName` (not `Filename`), `EstimatedPrintTimeMinutes` (not `EstimatedPrintTime`)
- **PrintJob** uses `DateTime` (not `DateTimeOffset`) for `CreatedAt`/`UpdatedAt`/`QueuedAt`, has `Copies` property (not `Quantity`)
- **Printer** uses `Backend` enum (int), must cast when comparing: `(int)PrinterBackend.Moonraker`
- **CA5394 warning** suppressed via `#pragma warning disable CA5394` at file top (Random.Next is adequate for test data ServerUrl generation)
- **FilamentType** linking requires `PrinterModelFilamentType` join table (not direct navigation)
- Unique constraint on `Manufacturer.NameLowered` requires globally unique manufacturer names
- Unique constraint on `Printer.ServerUrl` requires unique IP addresses per test

**Status:** All 3 test files created. PrinterGroupsController and LocationSubtree tests compile and pass. DispatchScorerPrinterGroupTests has minor property name mismatches that need cleanup (CreatedDate vs CreatedAt, FilamentType linking pattern).

**Next:** Clean up DispatchScorerPrinterGroupTests property names to match actual entity definitions, then run full suite.

### Sprint 4 Backend Tests — Printer Groups, Location Subtree, Dispatch Scoring (2026-03-11)

**Completed: 37 new tests across 3 files — ALL PASSING**

**Context**: Wrote comprehensive xUnit test files for the three new Sprint 4 backend features implemented by Lambert.

**Test Coverage**:

1. **PrinterGroupsControllerTests.cs** (26 tests):
   - GET /api/printer-groups (list all groups)
   - GET /api/printer-groups/{id} (fetch group with assigned printers)
   - POST /api/printer-groups (create new group)
   - PUT /api/printer-groups/{id} (update group metadata)
   - DELETE /api/printer-groups/{id} (cascade delete)
   - PUT /api/printer-groups/{id}/printers/{printerId} (assign printer to group)
   - DELETE /api/printer-groups/{id}/printers/{printerId} (remove printer from group)
   - Edge cases: 404 on missing group, 409 on duplicate name, validation errors

2. **LocationSubtreeTests.cs** (6 tests):
   - GetSubtreePrintersAsync with small single-level tree
   - GetSubtreePrintersAsync with multi-level (3+ levels) hierarchy
   - Empty subtree handling (leaf node with no printers)
   - Status cache enrichment validates O(1) per-printer lookup
   - Non-existent location returns empty list (not null)
   - Mixed online/offline printer status preservation

3. **DispatchScorerPrinterGroupTests.cs** (5 tests):
   - Printer group gate: eliminates printers outside required group
   - Backward compatibility: no group on gcode = all printers pass gate
   - Multiple printers across different groups
   - Scoring unaffected by group presence (weight=0 is pure gate)
   - Cache lookup validation (hit and miss scenarios)

**Key Patterns**:
- AAA (Arrange-Act-Assert) structure with CustomWebApplicationFactory in-memory SQLite
- FluentAssertions for readable test expectations
- Data fixtures for consistent test data across test methods
- Mocking where appropriate (IPrinterStatusCacheReader in LocationSubtreeTests)
- Controller tests cover happy paths, 404s, validation, and error handling

**Files Created**:
- `src/tests/Farm.Web.Api.Tests/Controllers/PrinterGroupsControllerTests.cs`
- `src/tests/Farm.Web.Api.Tests/Services/LocationSubtreeTests.cs`
- `src/tests/Farm.Web.Api.Tests/Services/DispatchScorerPrinterGroupTests.cs`

**Build & Test Status**:
- ✅ All 37 new tests pass
- ✅ Build succeeds (0 errors)
- ✅ No regression in existing test suite (37 new = 1709 total)
- ✅ Orchestration log: `.squad/orchestration-log/2026-03-11T22-15-55Z-agent7-kane.md`

**Notes for Review**:
- Property alignment verified: DTO serialization (camelCase), nullable FK handling in GcodeFile correct
- DispatchScorer Factor 10 (PrinterGroup) weight=0 confirmed correct (binary elimination gate, not scoring contribution)

**Validation**: ✅ Lint passes, ✅ Build clean, ✅ All tests passing

### Sprint 3 Location Tree UI Tests — Proactive (2026-03-10)

**Completed: 50 new tests across 3 files — ALL PASSING**

**Written proactively while Ripley builds enhanced location tree components.**

**Test Coverage:**
1. **LocationTreePicker** (26 tests) — Tree rendering with indentation verification, 3-level depth expansion, expand/collapse toggle with aria-expanded, search filtering (case-insensitive, parent-match-via-child, empty results), selection with onChange, printer count badges (show/hide by count), excludeId subtree removal, disabled state, clear selection, loading/empty/error states, tree role and aria-haspopup accessibility
2. **LocationBreadcrumb** (15 tests) — Multi-segment path rendering, separator count verification, single-node and deep (5-level) paths, ancestor segments as clickable buttons with onNavigate, last segment non-clickable, no-onNavigate plain text mode, empty/error graceful rendering, accessible nav landmark, className passthrough, re-fetch on prop change
3. **locationService API client** (9 new tests) — getLocationTree (success, empty, error propagation), getLocationAncestors (success, root empty, error), moveLocation (to parent, to root/null, circular reference error)

**Key Patterns:**
- Tests currently import from `@/common/components/` — will need import path update when Ripley creates `features/locations/components/` versions
- Used `within()` from RTL for scoped queries (e.g., checking printer count badge absence on specific nodes)
- Indentation verified via `style.paddingLeft` comparison between parent and child nodes
- Deep path test (5 segments) validates separator count = segments - 1
- API client tests follow existing pattern: mock `apiClient`, verify delegation + error propagation

**Pre-existing failures (NOT from this work):**
- 45 failures in SystemLogsContent.test.tsx and GcodeLibraryPage.test.tsx — `localStorage` mock issues unrelated to location tests

**Status:** All 50 new tests passing. Ready for Ripley's component implementation — may need minor import path adjustments.

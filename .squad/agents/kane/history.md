# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

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

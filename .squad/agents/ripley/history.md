# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

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

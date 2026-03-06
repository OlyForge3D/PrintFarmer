# API Service Architecture Refactoring

**Date**: 2026-01-12  
**Decided by**: Ripley (Frontend Dev)  
**Context**: P1 architecture finding by Dallas (Lead)  
**Status**: ✅ Phase 1 Complete, Phase 2 In Progress

## Problem

`api.ts` is a 3,458-line god class containing 313 methods across 20+ domains, violating Single Responsibility Principle and creating a major maintainability risk.

**Impact**:
- Merge conflicts on every feature change
- Impossible to navigate (3,458 lines)
- Unclear method ownership
- Difficult to test in isolation
- Performance impact from loading entire class

## Decision

**Refactor `api.ts` into domain-scoped service modules using the delegate pattern.**

### Architecture

**Core Infrastructure** (`apiClient.ts`):
- Axios instance with 30-second timeout
- Request interceptor: Auth (Bearer token from localStorage), Correlation ID (X-Correlation-Id)
- Response interceptor: 401 handling (clear token, redirect to /login)
- Shared HTTP methods (get, post, put, patch, delete)

**Domain Services** (17+ modules):
Each service delegates to apiClient methods until migration is complete:
```typescript
export const printerService = {
  async getPrinters(): Promise<Printer[]> {
    return apiClient.getPrinters(); // Delegates to api.ts during migration
  }
};
```

**Service Distribution**:
- printerService: 53 methods (Printer CRUD, control, history)
- catalogService: 33 methods (Manufacturers, models, components)
- spoolmanService: 23 methods (External Spoolman integration)
- queueService: 17 methods (Print queue management)
- harvestService: 16 methods (G-code harvest operations)
- gcodeService: 22 methods (File management, upload, library)
- authService: 24 methods (Login, users, API keys)
- ...10 more services (~5-15 methods each)

### Backward Compatibility

- api.ts continues to exist with all 313 methods
- New services delegate to api.ts methods
- Existing code works unchanged via imports from `@/services/api`
- New code imports domain services directly: `import { printerService } from '@/services/printerService'`
- Gradual migration without breaking changes

### Existing Services

Several services already follow this pattern:
- ✅ `locationService.ts` (62 lines) - delegates to apiClient
- ✅ `cameraService.ts` (39 lines) - delegates to apiClient
- ✅ `tagService.ts` (330 lines) - full implementation with hooks
- ✅ `maintenanceService.ts` (277 lines)
- ✅ `slicerService.ts` (172 lines)
- ✅ `jobSchedulingService.ts` (87 lines)

## Benefits

1. **Single Responsibility** - Each service has one clear domain
2. **Reduced Conflicts** - Changes isolated to specific services
3. **Easy Navigation** - Find printer methods in printerService.ts (~400 lines vs 3,458)
4. **Testability** - Test services independently
5. **Type Safety** - Strong domain-specific types
6. **Performance** - Load only needed services via code splitting

## Migration Path

**Phase 1: Core Infrastructure** ✅ COMPLETE
- [x] Create apiClient.ts with shared axios instance
- [x] Implement auth, correlation ID, 401 error interceptors
- [x] Document architecture (README.md, REFACTOR_PLAN.md)

**Phase 2: Service Extraction** 🚧 IN PROGRESS
- [ ] Extract printerService.ts (53 methods) - highest impact
- [ ] Extract queueService.ts (17 methods) - second highest
- [ ] Extract catalogService.ts (33 methods) - third highest
- [ ] Continue with remaining 14+ services by priority/usage

**Phase 3: Import Migration** 📋 PLANNED
- [ ] Update useApi.ts to import from domain services
- [ ] Update component imports (104 files affected)
- [ ] Maintain backward compatibility via re-exports

**Phase 4: Cleanup** 📋 PLANNED
- [ ] Remove monolithic api.ts when all methods migrated
- [ ] Verify all tests pass
- [ ] Update documentation

## Validation

✅ **Build**: Passes in 7.06s (no TypeScript errors)  
✅ **Tests**: 979/1024 pass (pre-existing failures unrelated)  
✅ **Lint**: 0 errors (ESLint rules updated for apiClient.ts)

## Documentation

- `src/services/README.md` - Service architecture overview
- `src/services/REFACTOR_PLAN.md` - Comprehensive migration plan
- `src/services/apiClient.ts` - Core HTTP client (143 lines)
- `.squad/agents/ripley/history.md` - Implementation notes

## Risks & Mitigation

**Risk**: Breaking existing code during migration  
**Mitigation**: Maintain backward compatibility via re-exports, gradual migration

**Risk**: Inconsistent service patterns  
**Mitigation**: Documented pattern in README.md, existing services as reference

**Risk**: Incomplete migration leaving tech debt  
**Mitigation**: Clear roadmap in REFACTOR_PLAN.md, phased approach with validation gates

## Follow-Up Actions

1. Extract top 3 services by priority (printer, queue, catalog)
2. Update useApi.ts hooks to use new services
3. Document service extraction pattern for other developers
4. Monitor for merge conflicts during migration period

## Team Impact

**Lambert (Backend)**: No changes needed - API contracts unchanged  
**Ripley (Frontend)**: Executing refactor, maintaining compatibility  
**Dallas (Lead)**: Architectural approval, validation

---

**Status**: Phase 1 complete, Phase 2 in progress. Core infrastructure validated and working.

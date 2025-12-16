# API Architecture Refactoring Plan

## Vision
Move all pure business logic to the Infrastructure assembly, leaving the API project to focus solely on web-specific concerns (Controllers, HTTP handling, SignalR notifications).

This enables reusability across multiple UI platforms: Web API, WPF, CLI, Mobile, etc.

---

## Phase 1: Pure Business Logic Migration (Estimated: 2-3 hours)

### Objective
Move services with **zero web dependencies** to Infrastructure. These are straightforward migrations requiring only namespace/project reference updates.

### 1.1 Catalog Services
**Status**: Ready to move immediately

**Files to move**:
- `src/api/Services/Catalog/ICatalogService.cs`
- `src/api/Services/Catalog/CatalogService.cs`

**New location**: `src/infra/Services/Catalog/`

**Dependencies**:
- ✅ ICatalogRepository (already in Infrastructure)
- ✅ INormalizationEventLogger (already in Infrastructure)
- ❌ ICatalogCache (API-specific) - **MUST ABSTRACT**
- ✅ Normalization services (available in Infrastructure)
- ✅ IUnifiedLoggingService (already in Infrastructure)

**Blocking Issue**: `ICatalogCache` is API-specific caching. Solution:
1. Create `IManufacturerCacheProvider` abstraction in Infrastructure
2. Keep caching implementation in API as adapter
3. CatalogService depends on abstraction, not concrete cache

**Effort**: 2-3 hours (includes cache abstraction)

**Test Impact**: CatalogService uses ICatalogRepository - all existing tests should pass

---

### 1.2 Default Catalog Service
**Status**: Ready to move immediately

**Files to move**:
- `src/api/Services/DefaultCatalogService.cs`

**New location**: `src/infra/Services/DefaultCatalogService.cs`

**Dependencies**:
- ✅ ICatalogRepository (Infrastructure)
- ✅ PrinterModelRepository (Infrastructure)
- ✅ IUnifiedLoggingService (Infrastructure)

**Blocking Issues**: None

**Effort**: 15 minutes (pure copy + namespace update)

**Test Impact**: Zero - this is initialization logic

---

### 1.3 Authentication & Authorization Services
**Status**: Ready to move immediately

**Files to move**:
- `src/api/Services/Authentication/IPasswordHashingService.cs`
- `src/api/Services/Authentication/PasswordHashingService.cs`
- `src/api/Services/Authentication/ITokenRevocationService.cs`
- `src/api/Services/Authentication/TokenRevocationService.cs`
- `src/api/Services/Authentication/AuthAuditService.cs`
- `src/api/Services/Authentication/IAuthAuditService.cs`

**Dependencies**:
- ✅ All use BCrypt, EntityFramework, logging (Infrastructure-compatible)
- ❌ IAuthenticationService (uses Controllers.Requests) - **PARTIAL**

**Partial Move Strategy**:
- Move services that don't depend on request DTOs
- PasswordHashingService, TokenRevocationService, AuthAuditService → Infrastructure
- IAuthenticationService → Keep in API (depends on request models)

**New location**: `src/infra/Services/Authentication/`

**Effort**: 1-2 hours

**Test Impact**: Low - mostly unit tests

---

### 1.4 Gcode Harvest Service (Partial)
**Status**: Partially ready

**Current Issue**: Uses `IHubContext<HarvestHub>` for SignalR updates

**Strategy**:
1. Extract core harvest logic → Infrastructure service
2. Keep SignalR notifications in API service
3. API service decorates Infrastructure service to add notifications

**Files to move** (core logic):
- Create `IGcodeHarvestService` in Infrastructure
- Create `GcodeHarvestService` in Infrastructure (harvest logic only)
- Move file discovery, processing, metadata extraction

**Files to keep** (web-specific):
- `GcodeHarvestService` in API becomes `GcodeHarvestNotificationService`
- Wraps Infrastructure service, adds SignalR notifications

**Dependencies to resolve**:
- Remove `IHubContext<HarvestHub>`
- Remove `AutoMapper` dependency (move to API adapter)

**Effort**: 2-3 hours (requires refactoring)

**Test Impact**: Medium - need to test both core and API notification layers

---

### Phase 1 Summary
| Service | Status | Effort | Blocking Issues |
|---------|--------|--------|-----------------|
| Catalog Services | ✅ Ready | 2-3h | Cache abstraction needed |
| DefaultCatalogService | ✅ Ready | 15m | None |
| Auth Services | ✅ Ready | 1-2h | IAuthenticationService dependency |
| Gcode Harvest | ⚠️ Partial | 2-3h | SignalR coupling |
| **Phase 1 Total** | | **6-9 hours** | Manageable |

---

## Phase 2: Mixed Web/Business Logic Refactoring (Estimated: 4-6 hours)

### Objective
Separate concerns in services that mix business logic with web-specific features. Create core Infrastructure services, then API adapters that add web features.

### 2.1 Printer Services (Core Business Logic)
**Status**: High priority, significant refactoring needed

**Current Structure**:
```
PrintersService (in API)
├── Business logic: CRUD, discovery, status querying
├── Web logic: SignalR notifications via IHubContext<PrinterHub>
└── Dependencies: Factories, repositories, circuit breaker
```

**Target Structure**:
```
Infrastructure:
  ├── IPrintersService (pure interface)
  └── PrintersService (core logic, no SignalR)

API:
  ├── PrinterNotificationService (wraps Infrastructure)
  └── PrintersController (uses notification service)
```

**Refactoring Steps**:
1. Create `IPrintersService` in Infrastructure with all current methods
2. Create `PrintersService` in Infrastructure (copy current, remove `IHubContext`)
3. Create `IPrinterNotificationService` in API
4. Create `PrinterNotificationService` in API that:
   - Injects Infrastructure `IPrintersService`
   - Wraps all methods to emit SignalR events
   - Delegates business logic to Infrastructure service
5. Update DI registration in API

**Files Affected**:
- Move: `PrintersService.cs`, `IPrintersService.cs` → Infrastructure
- Create: `PrinterNotificationService.cs` in API
- Update: ServiceCollectionExtensions.cs DI registration
- Update: All controllers using PrintersService

**Dependencies to handle**:
- ❌ `IHubContext<PrinterHub>` → Remove from core, add to API adapter
- ✅ All backend factories (already in Infrastructure)
- ✅ Circuit breaker (Infrastructure)
- ⚠️ `AutoMapper` → Use in API adapter
- ⚠️ HTTP factories → Keep in Infrastructure, parameterize

**Effort**: 3-4 hours

**Testing Strategy**:
1. Existing tests validate Infrastructure service independently
2. New tests for notification service integration
3. Controllers remain functional

**Breaking Changes**: Controllers receive `IPrinterNotificationService` instead of `IPrintersService`
- Update: PrintersController
- Update: Discovery services that use it

---

### 2.2 Printer Status Services
**Status**: Needs refactoring

**Services involved**:
- `PrinterStatusFallbackService`
- `MultiPrinterStatusCoordinator`  
- `IPrinterBackendCapabilitiesService` / `PrinterBackendCapabilitiesService`

**Current Issue**: 
- Some use SignalR (`IHubContext`)
- Some are pure but tightly integrated with `PrintersService`

**Strategy**:
1. Analyze each for SignalR usage
2. Extract pure logic → Infrastructure
3. Create adapters in API for notification wrapping
4. Usually can move as-is since they delegate to PrintersService

**Likely outcome**:
- `PrinterStatusFallbackService` → Infrastructure (no SignalR)
- `MultiPrinterStatusCoordinator` → Infrastructure (delegates to PrintersService)
- `PrinterBackendCapabilitiesService` → Infrastructure (pure query logic)

**Effort**: 1-2 hours

---

### 2.3 Slicing Services
**Status**: Complex, medium refactoring

**Services**:
- `ISlicersService` / `SlicersService` (job submission)
- `ISlicingSubmissionService` / `SlicingSubmissionService`
- `SliceJobEventService` (SignalR notifications)

**Current Structure**:
- `SlicersService`: Pure slicer discovery and capability checking
- `SlicingSubmissionService`: Pure job preparation logic
- `SliceJobEventService`: SignalR event emissions

**Target Structure**:
- Infrastructure: Core slicing logic (discovery, capability checking, job prep)
- API: Job submission with SignalR notifications

**Refactoring**:
1. Keep `SlicersService` core logic in Infrastructure
2. Extract `SlicingSubmissionService` core to Infrastructure
3. Create `SlicingNotificationService` in API that wraps core + emits SignalR
4. Keep `SliceJobEventService` in API (pure SignalR utility)

**Blocking Issues**:
- Need to understand SignalR event flow first
- Multiple hubs involved (SlicingHub, PrinterHub, etc.)

**Effort**: 2-3 hours (after understanding flow)

**Risk**: High - complex domain with multiple concerns

---

### 2.4 GCode File Services
**Status**: Needs analysis

**Services**:
- `GcodeFilesService` / `IGcodeFilesService`
- `GcodeLibraryService` / `IGcodeLibraryService`

**Likely Issues**:
- File I/O operations (should move)
- HTTP file upload handling (keep in API)

**Strategy**:
1. Extract file operations → Infrastructure
2. Keep HTTP upload/download handlers in API

**Effort**: 1-2 hours

---

### Phase 2 Summary
| Service | Status | Effort | Complexity |
|---------|--------|--------|-----------|
| Printer Services | ⚠️ Core | 3-4h | High |
| Printer Status Services | ✅ Ready | 1-2h | Medium |
| Slicing Services | ❌ Complex | 2-3h | High |
| Gcode File Services | ⚠️ Partial | 1-2h | Medium |
| **Phase 2 Total** | | **7-11 hours** | Challenging |

---

## Phase 3: Final Polish (Estimated: 1-2 hours)

### 3.1 Dependency Injection Cleanup
- Consolidate service registration
- Create `InfrastructureServiceCollectionExtensions` in Infrastructure
- Keep only API-specific registrations in API

### 3.2 Project References
- Verify no circular dependencies
- Clean up unused using statements
- Document API ↔ Infrastructure boundary

### 3.3 Documentation
- Update architecture documentation
- Document reusable services for WPF/other UIs
- Create adapter pattern examples

---

## Implementation Sequence

### Week 1: Phase 1 (Pure Business Logic)
- **Day 1-2**: Catalog & DefaultCatalog services (3-4h)
- **Day 2-3**: Authentication services (1-2h)
- **Day 3-4**: Gcode Harvest partial extraction (2-3h)
- **Testing**: Run full test suite daily

### Week 2: Phase 2 (Web/Business Logic Separation)
- **Day 1-2**: Printer Services core extraction (3-4h)
- **Day 2-3**: Printer Status Services (1-2h)
- **Day 3-4**: Slicing Services (after understanding) (2-3h)
- **Day 4-5**: Gcode File Services (1-2h)

### Week 3: Phase 3 (Polish & Validation)
- **Day 1**: DI cleanup and documentation
- **Day 2-3**: Full regression testing
- **Day 3+**: Code review and refinement

---

## Success Criteria

### Phase 1 Success
- ✅ All Phase 1 services moved to Infrastructure
- ✅ Full test suite passes
- ✅ No circular dependencies
- ✅ API project references Infrastructure

### Phase 2 Success
- ✅ All mixed-concern services separated
- ✅ Pure logic in Infrastructure, web logic in API
- ✅ Controllers still functional
- ✅ New adapter services tested

### Phase 3 Success
- ✅ Clean architecture enforced
- ✅ Easy to add new UI (WPF would reference Infrastructure services)
- ✅ Well-documented boundary between API and Infrastructure
- ✅ All tests passing

---

## Risk Mitigation

### High Risk: Breaking Changes
**Risk**: Refactoring breaks existing functionality
**Mitigation**:
- Git branch per phase
- Run tests after each service move
- Update controllers as services move
- Keep old services temporarily with deprecation warnings

### High Risk: Over-engineering
**Risk**: Abstraction layers become too complex
**Mitigation**:
- Start simple with adapter pattern
- Only add complexity when needed
- Review architecture weekly

### Medium Risk: Incomplete Testing
**Risk**: Tests don't cover moved logic
**Mitigation**:
- Preserve existing tests
- Add integration tests for adapters
- Run full test suite before merging

---

## Future Benefits

Once complete, the architecture will support:

### 1. WPF Desktop Application
```csharp
// WPF app can reference Infrastructure directly
var catalog = new CatalogService(...);
var printers = new PrintersService(...);
// No web framework needed
```

### 2. CLI Administration Tool
```csharp
// CLI tool reuses business logic
var authService = new AuthenticationService(...);
var harvestService = new GcodeHarvestService(...);
```

### 3. Mobile App API (different API framework)
```csharp
// Could use gRPC, GraphQL, or OpenAPI
// All reuse same business logic
var printersService = new PrintersService(...);
```

### 4. Job Scheduler Service
```csharp
// Background service reuses core logic
var slicingService = new SlicingSubmissionService(...);
var harvestService = new GcodeHarvestService(...);
```

---

## Next Steps

1. **Approve Phase 1**: Confirm scope and ordering
2. **Create feature branches**: One per service or phase
3. **Start with Catalog**: Lowest risk, highest value
4. **Build test framework**: Document patterns for adapters
5. **Review weekly**: Adjust scope based on learnings

---

## Appendix: Service Classification Matrix

| Service | Category | Phase | Complexity | Risk |
|---------|----------|-------|-----------|------|
| CatalogService | Pure | 1 | Low | Low |
| DefaultCatalogService | Pure | 1 | Low | Low |
| PasswordHashingService | Pure | 1 | Low | Low |
| TokenRevocationService | Pure | 1 | Low | Low |
| AuthAuditService | Pure | 1 | Low | Low |
| GcodeHarvestService (core) | Mixed | 1 | Medium | Medium |
| PrintersService | Mixed | 2 | High | High |
| PrinterStatusFallbackService | Mixed | 2 | Medium | Medium |
| MultiPrinterStatusCoordinator | Mixed | 2 | Medium | Medium |
| PrinterBackendCapabilitiesService | Mixed | 2 | Low | Low |
| SlicersService | Mixed | 2 | High | High |
| SlicingSubmissionService | Mixed | 2 | Medium | Medium |
| GcodeFilesService | Mixed | 2 | Medium | Medium |
| RateLimitService | Pure | 3 | Low | Low |
| GracefulShutdownService | Web | Keep | Medium | Medium |
| SignalRTestService | Web | Keep | Low | Low |
| WorkerAuthService | Web | Keep | Low | Low |


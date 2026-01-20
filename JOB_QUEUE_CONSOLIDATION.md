# Job Queue Services Consolidation

**Branch:** `refactor/consolidate-job-queue-services`  
**Created:** 2026-01-20  
**Status:** 🔄 In Progress

## Problem Statement

Four folders with overlapping responsibilities and 4+ interfaces doing similar things:

| Folder | Interface | Status |
|--------|-----------|--------|
| `Queue/` | `IQueueService` | ✅ Deleted - was duplicate of IJobQueueService |
| `Queue/` | `IJobQueueService` | ✅ Keep - core interface |
| `PrintQueue/` | `IPrintQueueService` | 🔄 Pending - extract analytics |
| `PrintJobQueue/` | `IPrintJobQueueService` | ✅ Deleted - unnecessary adapter |
| `PrintJobs/` | `IPrintApprovalService` | ✅ Keep - distinct purpose |

## Target Architecture

```text
src/api/Services/
├── Queue/                          # Consolidated queue services
│   ├── IJobQueueService.cs         # Core CRUD operations
│   ├── JobQueueService.cs          # Implementation
│   ├── IJobQueueAnalyticsService.cs # Stats, filtering (NEW - extracted)
│   ├── JobQueueAnalyticsService.cs  # Implementation (from PrintQueueService)
│   ├── IQueueDataService.cs        # Keep if used
│   └── QueueDataService.cs         # Keep if used
├── PrintJobs/                      # Job approval (unchanged)
│   ├── IPrintApprovalService.cs
│   └── PrintApprovalService.cs
```

**Delete:**

- ✅ `Queue/IQueueService.cs` (duplicate) - DELETED
- 🔄 `PrintQueue/` folder entirely - PENDING
- ✅ `PrintJobQueue/` folder entirely - DELETED

## Action Plan

### Phase 1: Analysis & Preparation ✅

- [x] Identify all interfaces and their purposes
- [x] Map current usage across controllers
- [x] Document all consumers of each interface

### Phase 2: Remove Unused Code ✅

- [x] Delete `IQueueService.cs` (confirmed duplicate of IJobQueueService)
- [x] No references to IQueueService found - was completely unused

### Phase 3: Eliminate PrintJobQueue Adapter Layer ✅

- [x] Find all consumers of `IPrintJobQueueService`
- [x] Update `EfPrintApprovalService` to use `IJobQueueService` directly
- [x] Remove unused using from `OctoPrintCompatController`
- [x] Delete `PrintJobQueue/` folder
- [x] Delete `PrintJobQueueController`
- [x] Remove DI registration from `ServiceCollectionExtensions.cs`
- [x] Delete test files for removed code
- [x] Update `PrintApprovalServiceTests` to remove unused test double

### Phase 4: Consolidate PrintQueueService

- [ ] Extract analytics methods into `IJobQueueAnalyticsService`
- [ ] Move unique functionality to `JobQueueService` or new analytics service
- [ ] Update `IPrintQueueService` consumers to use new interfaces
- [ ] Delete `PrintQueue/` folder

### Phase 5: Cleanup & Validation

- [ ] Update DI registrations in Program.cs and ServiceCollectionExtensions.cs
- [ ] Run full test suite
- [ ] Verify all controllers still work
- [ ] Remove unused DTOs

## Current Consumer Mapping

### IJobQueueService (Queue/) ✅

- `JobQueueController`
- `EfPrintApprovalService` (updated to use this directly)

### IPrintJobQueueService (PrintJobQueue/) ✅ DELETED

- ~~`PrintJobQueueController`~~ - DELETED
- ~~`EfPrintApprovalService`~~ - Updated to use IJobQueueService

### IPrintQueueService (PrintQueue/) - PENDING

- `JobQueueAnalyticsController`
- `RetriesController`

### IQueueService (Queue/) ✅ DELETED

- Was completely unused

## Progress Log

### 2026-01-20

- Created branch `refactor/consolidate-job-queue-services`
- Created this tracking document
- **Phase 2 Complete**: Deleted unused `IQueueService.cs`
- **Phase 3 Complete**: 
  - Updated `EfPrintApprovalService` to use `IJobQueueService` directly
  - Deleted `PrintJobQueue/` folder (adapter layer)
  - Deleted `PrintJobQueueController` (unused controller)
  - Removed DI registration from `ServiceCollectionExtensions.cs`
  - Deleted test files for removed code
  - Updated `PrintApprovalServiceTests` to remove unused test double
  - Build succeeded with 0 warnings, 0 errors
  - All tests passing
- **Naming & Cleanup Complete**:
  - Renamed `EfPrintApprovalService` → `PrintApprovalService` (Ef prefix only for repos)
  - Deleted unused `InMemoryPrintApprovalService` (leftover from earlier refactor)
  - Deleted duplicate `PrintApproval.cs` entity from API project (infra version is canonical)
  - Added `IPrintApprovalRepository` DI registration to `ServiceCollectionExtensions.cs`
  - Updated `PrintApprovalServiceTests` with `StubJobQueueService` test double
  - All 8 PrintApprovalService tests passing
  - Build: 0 warnings, 0 errors

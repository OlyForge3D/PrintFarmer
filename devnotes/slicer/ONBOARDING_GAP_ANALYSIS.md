# OrcaSlicer Onboarding Implementation Gap Analysis

**Generated**: October 19, 2025  
**Branch**: feature/orcaslicer-reimplementation  
**Analysis Scope**: Complete codebase evaluation against orcaslicer-onboarding-plan.md

## Executive Summary

**Overall Progress**: 3 out of 7 phases substantially complete (Phase 0, 1, 2)  
**Production Readiness**: Phase 2 (Job Dispatch) is 100% production-ready with comprehensive hardening  
**Critical Path**: Phase 4 (Job Processing & Artifacts) is the bottleneck for end-to-end functionality  
**Estimated Effort to MVP**: ~5-8 development days

---

## Phase-by-Phase Analysis

### ✅ Phase 0: Preparations (100% Complete)

**Status**: FULLY COMPLETE  
**Effort Invested**: 1 day

**Completed Items**:
- ✅ Feature branch created: `feature/orcaslicer-reimplementation`
- ✅ `orcaslicer-worker` project exists at `src/orcaslicer-worker/`
- ✅ `Dockerfile.orcaslicer` exists for containerization
- ✅ Onboarding plan documented in `docs/slicer/orcaslicer-onboarding-plan.md`

**Validation**: All prerequisite infrastructure in place.

---

### ✅ Phase 1: Registry & Discovery API (90% Complete)

**Status**: NEARLY COMPLETE - Missing SignalR hub and admin UI  
**Effort Invested**: ~2 days  
**Remaining Effort**: 0.5-1 day

#### Implemented ✅

**Database Entity** (`src/infra/Domain/SlicerService.cs`):
```csharp
public class SlicerService {
    public Guid Id { get; set; }
    public string Name { get; set; }
    public int SlicerType { get; set; }  // OrcaSlicer, PrusaSlicer, etc.
    public string? Version { get; set; }
    public string? Host { get; set; }
    public string? UiManifestUrl { get; set; }
    public string? CapabilitiesJson { get; set; }
    public int MaxConcurrentJobs { get; set; }
    public string? Status { get; set; }  // Online, Offline, Disabled
    public DateTime LastSeen { get; set; }
    public string? ApiKey { get; set; }
    public string? Tags { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

**API Endpoints** (`src/api/Controllers/SlicersController.cs`):
- ✅ `POST /api/slicers/register` - Register worker with auto-generated API key
- ✅ `GET /api/slicers` - List all registered workers
- ✅ `GET /api/slicers/{id}` - Get worker details
- ✅ `POST /api/slicers/{id}/heartbeat` - Update worker status and capacity
- ✅ `POST /api/slicers/{id}/deregister` - Remove worker from registry

**Service Layer** (`src/api/Services/Slicing/SlicersService.cs`):
- ✅ `ISlicersService` interface with full CRUD operations
- ✅ Database persistence with `AppDbContext`
- ✅ API key generation and validation

**Security**:
- ✅ `[RequireSlicerApiKey]` attribute for authentication
- ✅ API key validation in request headers (`X-Slicer-ApiKey`)

**Worker Client** (`src/orcaslicer-worker/Services/SlicerRegistrationClient.cs`):
- ✅ `ISlicerRegistrationClient` interface
- ✅ `RegisterAsync()` - Initial registration with API
- ✅ `HeartbeatAsync()` - Periodic status updates (configurable interval)
- ✅ `DeregisterAsync()` - Graceful shutdown cleanup

**Background Service** (`src/orcaslicer-worker/Services/RegistrationBackgroundService.cs`):
- ✅ Auto-registration on startup with 5-second delay
- ✅ Periodic heartbeat loop (default 30 seconds)
- ✅ Capacity reporting (free slots calculation)
- ✅ Graceful deregistration on shutdown with 5-second timeout
- ✅ Automatic re-registration on transient failures
- ✅ Status reporting ("Online", "Draining" during shutdown)

#### Missing ⏳

1. **SignalR Hub** (`/hubs/slicers`):
   - Events: `SlicerRegistered`, `SlicerHeartbeat`, `SlicerDeregistered`
   - Client notifications for real-time registry updates
   - **Estimated Effort**: 2-3 hours

2. **Admin UI** (`/settings/slicers`):
   - View registered workers with status badges
   - Enable/disable workers manually
   - View capacity, version, capabilities
   - Last seen timestamps
   - **Estimated Effort**: 3-4 hours (React components + API integration)

#### Acceptance Criteria Status

- ✅ Services can register and appear in `/api/slicers`
- ⏳ UI receives SignalR updates when services register/deregister (hub not implemented)
- ✅ Workers send periodic heartbeats
- ✅ Workers deregister gracefully on shutdown

#### Recommendations

1. **Low Priority**: SignalR hub can be deferred - polling `/api/slicers` works for MVP
2. **Medium Priority**: Admin UI provides operational visibility but not critical for worker functionality
3. **Production Consideration**: Add connection retry logic with exponential backoff in worker registration client

---

### ✅ Phase 2: Job API & Capability-aware Dispatching (100% Complete)

**Status**: PRODUCTION-READY with comprehensive hardening  
**Effort Invested**: ~3-4 days  
**Remaining Effort**: 0 days (optional enhancements only)

#### Comprehensive Implementation ✅

**Core Features**:
- ✅ SliceJob entity with capability JSON, priority, lifecycle timestamps
- ✅ Enqueue endpoint `POST /api/slice` with profile resolution
- ✅ Status endpoint `GET /api/slice/{id}`
- ✅ Cancellation endpoint `POST /api/slice/{id}/cancel`
- ✅ User jobs listing `GET /api/slice/my-jobs`
- ✅ Queue listing `GET /api/slice/queue` (admin-restricted)
- ✅ Worker capability registration (Orca & Prusa workers)
- ✅ Capability-aware filtering (`EfWorkerRepository.GetWorkersByCapabilitiesAsync`)
- ✅ Intelligent worker scoring (load, speed, success rate, capability bonus)
- ✅ SignalR lifecycle events (queued, started, progress, completed, failed, cancelled)

**Hardening & Observability** (Completed Oct 19 2025):
- ✅ **Retry Logic**: 3 attempts with exponential backoff (250ms base, 2x multiplier)
- ✅ **Configurable Retry**: `RetryOptions` class with `JobDispatchRetry` config section
- ✅ **Rate Limiting**: 20/hour, 200/day per user (externalized to appsettings.json)
- ✅ **Authorization**: `CanViewSliceQueue` policy requiring `farm_admin` role
- ✅ **Metrics Instrumentation**:
  - Counter: `slicing_jobs_dispatched`
  - Counter: `slicing_jobs_dispatch_failed` (with reason tags)
  - Histogram: `slicing_job_dispatch_duration_ms` (with outcome tags)
  - Gauge: `slicing_available_workers`
- ✅ **Prometheus Export**: `/metrics` endpoint via OpenTelemetry 1.10.0
- ✅ **Stale Worker Filtering**: `SLICER_WORKER_STALE_SECONDS` env (default 120s)
- ✅ **Capability Validation**: Max 32, distinct values, slug format regex
- ✅ **Worker Pull Model**: `POST /api/slice/claim` with lease management
- ✅ **Lease Tracking**: `ClaimedAt`, `LeaseExpiresAt` fields for job timeout handling
- ✅ **Comprehensive Tests**: 5/5 tests passing (dispatcher, retry, rate limit)

**Dual Dispatch Models**:
1. **Push Model**: Server actively dispatches jobs to workers via HTTP POST
2. **Pull Model**: Workers claim jobs via `POST /api/slice/claim` with lease semantics

#### Optional Enhancements (Deferred)

- ⏳ Circuit breaker for repeated worker failures
- ⏳ Audit logging infrastructure (compliance feature)
- ⏳ Negative tests for malformed capability JSON
- ⏳ ML-based worker selection algorithms

#### Acceptance Criteria Status

- ✅ API enqueues slice jobs with capability constraints
- ✅ Workers selected based on capabilities & scoring
- ✅ Lifecycle events broadcast over SignalR
- ✅ Retry logic handles transient failures
- ✅ Metrics exported for monitoring/alerting
- ✅ Pull model supports worker-initiated job claiming
- ✅ Configuration externalized for operational flexibility
- ✅ Build: 0 errors, tests: 5/5 passing

---

### ⏳ Phase 3: Worker Registration Integration (75% Complete)

**Status**: MOSTLY COMPLETE - Worker has full registration client, needs API server coordination  
**Effort Invested**: ~1.5 days  
**Remaining Effort**: 0.5-1 day

#### Implemented ✅

**Worker-Side Registration**:
- ✅ `SlicerRegistrationClient` with full API integration
- ✅ `RegistrationBackgroundService` with heartbeat loop
- ✅ Configuration-driven registration (appsettings.json)
- ✅ Graceful shutdown with deregistration
- ✅ Automatic re-registration on failures
- ✅ Status reporting ("Online", "Draining")
- ✅ Capacity reporting (free slots calculation)

**Configuration** (appsettings.json):
```json
{
  "SlicerRegistry": {
    "ApiBaseUrl": "http://api:5245",
    "ServiceName": "orcaslicer-worker",
    "Version": "1.0.0",
    "Host": "http://orcaslicer-worker:8080",
    "HeartbeatIntervalSeconds": 30,
    "ApiKey": "${SLICER_REGISTRATION_KEY}"
  },
  "Worker": {
    "MaxConcurrentJobs": 1
  }
}
```

#### Missing/Integration Needed ⏳

1. **Worker → SlicerService Synchronization**:
   - Currently workers register as `SlicerService` entities
   - Job dispatcher uses separate `Worker` entity
   - **GAP**: Need to synchronize `SlicerService` registrations to `Worker` table
   - **Options**:
     - A) Create background service to sync `SlicerService` → `Worker`
     - B) Merge entities (eliminate `SlicerService`, use `Worker` for registry)
     - C) Update dispatcher to query `SlicerService` instead of `Worker`
   - **Recommended**: Option B (merge entities) for simplicity

2. **API Server Worker Population**:
   - Heartbeat endpoint updates `SlicerService.LastSeen` and `Status`
   - Dispatcher needs `Worker` entity with same data
   - **Solution**: Middleware to auto-create/update `Worker` from `SlicerService` heartbeats

3. **Capability Synchronization**:
   - Worker sends `CapabilitiesJson` in registration
   - Dispatcher filters workers by capabilities
   - **Validation Needed**: Ensure capability format matches expectations

#### Acceptance Criteria Status

- ✅ Worker self-registers on startup
- ✅ Worker sends periodic heartbeats
- ✅ Worker deregisters gracefully on shutdown
- ⏳ Dispatcher sees registered workers (needs entity sync)
- ⏳ Jobs dispatched to registered workers (needs entity sync)

#### Recommendations

1. **Immediate**: Add `SlicerService` → `Worker` sync in heartbeat endpoint
2. **Refactor**: Consider merging `SlicerService` and `Worker` entities to eliminate redundancy
3. **Testing**: Verify end-to-end registration → job dispatch flow

---

### ⏳ Phase 4: Job Processing & Artifacts (40% Complete)

**Status**: PARTIALLY IMPLEMENTED - Workers poll Redis queue, missing HTTP claim model and artifact uploads  
**Effort Invested**: ~2 days  
**Remaining Effort**: 3-5 days

#### Implemented ✅

**Redis-Based Queue Consumer** (`src/worker-shared/BaseQueueConsumerService.cs`):
- ✅ Workers poll Redis lists (`RPOPLPUSH` pattern)
- ✅ Job processing with progress reporting
- ✅ State management (active job tracking)
- ✅ Error handling and job failure reporting
- ✅ Graceful cancellation on shutdown

**OrcaSlicer Pipeline** (`src/orcaslicer-worker/Services/OrcaSlicingPipelineService.cs`):
- ✅ Binary detection and validation
- ✅ Profile JSON conversion
- ✅ Command execution with output parsing
- ✅ Progress estimation (25-50-75-100% phases)

**Job Processing Flow**:
```
Redis Queue → BaseQueueConsumerService → OrcaSlicingPipelineService
    ↓               ↓                           ↓
Progress      Job Mutation               Slicer Execution
Reporter      (Status, WorkerId)          (Generate G-code)
```

#### Missing/Critical Gaps ⏳

1. **HTTP Claim Model Integration**:
   - Phase 2 implemented `POST /api/slice/claim` endpoint
   - Workers should call this instead of Redis polling for hybrid approach
   - **GAP**: Worker not updated to use claim endpoint
   - **Effort**: 2-3 hours to add HTTP claim client

2. **Artifact Upload System**:
   - ✅ G-code generation works locally
   - ❌ No upload to central storage (S3, blob storage, or file server)
   - ❌ No preview/thumbnail uploads
   - ❌ Result URLs not posted back to API
   - **Required Components**:
     - Storage client (S3/Azure Blob/local file server)
     - Upload service in worker
     - `POST /api/slice/{id}/result` endpoint for result submission
   - **Effort**: 1-2 days for storage integration + API endpoint

3. **Job Result Posting**:
   - Workers report to Redis/progress reporter
   - Need to call `POST /api/slice/{id}/result` with:
     - `resultFileUrl` (uploaded G-code)
     - `estimatedPrintTimeSeconds`
     - `filamentUsedGrams`
     - Preview image URLs
   - **Effort**: 3-4 hours

4. **GCode Resource Creation**:
   - Uploaded G-code should create `GCode` entity in database
   - Link to original job, user, printer
   - Extract metadata (layer count, print time, filament usage)
   - **Effort**: 4-6 hours

5. **Artifact Cleanup**:
   - Temporary files in worker (model downloads, generated G-code)
   - Retention policies for old artifacts
   - **Effort**: 2-3 hours

#### Current Architecture (Redis)

```
API Server                    Redis                  Worker
    │                           │                      │
    ├─ Submit Job              │                      │
    ├─ Push to Redis ─────────►│                      │
    │                           │◄───── Poll Queue ───┤
    │                           │                      │
    │◄────── Progress ──────────│◄──── Report ────────┤
    │                           │                      │
    ├─ SignalR Broadcast       │                      │
    └─ Update DB               │                      │
```

#### Target Architecture (HTTP Claim + Artifacts)

```
API Server                   Storage               Worker
    │                           │                    │
    ├─ Submit Job              │                    │
    │   (creates SliceJob)      │                    │
    │                           │                    │
    │◄──── Claim Job ───────────────────────────────┤
    ├─ Return Job Details      │                    │
    │                           │                    │
    │                           │◄── Upload G-code ─┤
    │◄──── Post Result ─────────────────────────────┤
    │   (resultFileUrl,         │                    │
    │    print time, etc.)      │                    │
    │                           │                    │
    ├─ Create GCode Entity     │                    │
    ├─ Mark Job Complete       │                    │
    └─ SignalR Broadcast       │                    │
```

#### Acceptance Criteria Status

- ⏳ Workers claim jobs from API (currently Redis-only)
- ✅ Workers execute slicer binaries (OrcaSlicer works)
- ⏳ Artifacts uploaded to storage (not implemented)
- ⏳ Results posted to API (Redis reporting only, no HTTP POST)
- ⏳ GCode entities created (not implemented)
- ✅ Job lifecycle completes (in Redis/memory, not persisted to API DB)

#### Recommendations

1. **Immediate Priority**: Implement artifact upload to S3/blob storage
2. **High Priority**: Add `POST /api/slice/{id}/result` endpoint
3. **Medium Priority**: Integrate HTTP claim model alongside Redis for hybrid approach
4. **Low Priority**: GCode entity creation (can be done post-MVP)

---

### ⏳ Phase 5: UI Integration (30% Complete)

**Status**: PARTIAL - API integration exists, missing slicer-specific UI  
**Estimated Effort**: 2-3 days

#### Implemented ✅

**React Slicer Service** (`src/Web/ReactApp/src/services/slicerService.ts`):
- ✅ `sliceModel()` - Submit slicing job
- ✅ `getSlicingJob()` - Get job status
- ✅ `cancelSlicingJob()` - Cancel job
- ✅ `subscribeToSlicingProgress()` - SSE progress updates
- ✅ Model upload/list/delete operations

**Existing Pages**:
- ✅ `NewSliceJobPage` - Job submission form
- ✅ `JobQueueDashboardPage` - Queue monitoring (admin)

#### Missing ⏳

1. **Worker Registry UI** (`/settings/slicers`):
   - View registered workers
   - Worker status (Online, Offline, Busy)
   - Capacity indicators (active jobs / total slots)
   - Enable/disable workers
   - View capabilities and versions
   - **Effort**: 4-6 hours

2. **Slicer Profile Manager**:
   - Import Orca JSON profiles
   - Profile library with search/filter
   - Profile editor (basic settings)
   - **Effort**: 1 day

3. **Embedded Worker UI** (Optional):
   - Mini-UI served by workers (HTML/React)
   - Worker-specific configuration
   - Local job queue view
   - **Effort**: 2-3 days (low priority)

---

### ⏳ Phase 6: Profile Import/Export (10% Complete)

**Status**: MINIMAL - Basic entity exists, no import/export logic  
**Estimated Effort**: 2-3 days

#### Implemented ✅

- ✅ `SlicerProfile` entity in database
- ✅ Profile association with jobs (`SlicerProfileId` field)

#### Missing ⏳

1. **Orca JSON Parser**:
   - Parse `.json` or `.zip` Orca profiles
   - Extract metadata (layer height, speed, temperature)
   - Validate required fields
   - **Effort**: 1 day

2. **Import Endpoint** (`POST /api/profiles/import`):
   - Accept file upload
   - Parse and validate
   - Store in database with hash
   - Return profile ID
   - **Effort**: 4-6 hours

3. **Export Endpoint** (`GET /api/profiles/{id}/export`):
   - Serialize profile to Orca JSON format
   - Return downloadable file
   - **Effort**: 2-3 hours

4. **Round-trip Validation**:
   - Import → Export → Import should be lossless
   - **Effort**: 2-3 hours testing

---

### ⏳ Phase 7: Hardening & Polish (20% Complete)

**Status**: BASIC - Phase 2 has production-ready observability, needs operational features  
**Estimated Effort**: 3-5 days

#### Implemented ✅

- ✅ Prometheus metrics export
- ✅ OpenTelemetry instrumentation
- ✅ Rate limiting
- ✅ Authorization policies
- ✅ Comprehensive logging

#### Missing ⏳

1. **Circuit Breaker**:
   - Stop dispatching to failing workers
   - Automatic recovery after cooldown
   - **Effort**: 4-6 hours

2. **Worker Health Monitoring**:
   - Dead worker detection
   - Auto-disable stale workers
   - Recovery workflow
   - **Effort**: 1 day

3. **Monitoring Dashboards**:
   - Grafana dashboard for metrics
   - Alert rules (high queue depth, worker failures)
   - **Effort**: 1 day

4. **Load Testing**:
   - Simulate 100+ concurrent jobs
   - Measure throughput and latency
   - Identify bottlenecks
   - **Effort**: 1 day

5. **Documentation**:
   - Deployment guide
   - Monitoring runbook
   - Troubleshooting guide
   - **Effort**: 1 day

---

## Critical Path to MVP

### Definition of MVP

**Goal**: End-to-end slicing workflow - submit STL, get G-code back, with basic monitoring

**Must-Have Features**:
1. Workers auto-register on startup ✅ (Phase 1 - done)
2. API accepts slice job submissions ✅ (Phase 2 - done)
3. Workers claim and process jobs ⏳ (Phase 4 - 40% done)
4. Artifacts uploaded and accessible ❌ (Phase 4 - critical gap)
5. Job results posted to API ❌ (Phase 4 - critical gap)
6. Basic monitoring (Prometheus metrics) ✅ (Phase 2 - done)

### Critical Blockers

**Blocker 1**: Artifact Upload System (Phase 4)
- **Impact**: No G-code accessible to users
- **Effort**: 1-2 days
- **Priority**: CRITICAL

**Blocker 2**: Job Result Posting (Phase 4)
- **Impact**: Jobs never marked complete in API database
- **Effort**: 4-6 hours
- **Priority**: CRITICAL

**Blocker 3**: Worker Entity Synchronization (Phase 3)
- **Impact**: Registered workers not visible to dispatcher
- **Effort**: 2-4 hours
- **Priority**: HIGH

### Fast-Track Path (5 Days)

**Day 1-2**: Implement artifact upload system
- Add S3/blob storage client to worker
- Create upload service
- Add `POST /api/slice/{id}/result` endpoint
- Test upload → download flow

**Day 3**: Complete Phase 3 integration
- Sync `SlicerService` → `Worker` entities
- Test registration → dispatch → execution flow

**Day 4**: HTTP claim model (optional enhancement)
- Add claim endpoint call to worker
- Test hybrid Redis + HTTP claim

**Day 5**: End-to-end testing & fixes
- Full workflow test: submit → claim → slice → upload → complete
- Fix integration bugs
- Smoke test with real STL files

---

## Implementation Priorities

### P0 (Critical - Required for MVP)

1. **Artifact Upload System** (2 days)
   - Phase 4 blocker
   - S3/blob storage integration
   - Upload service in worker

2. **Job Result Posting** (0.5 day)
   - Phase 4 blocker
   - `POST /api/slice/{id}/result` endpoint
   - Worker integration

3. **Worker Entity Sync** (0.5 day)
   - Phase 3 blocker
   - `SlicerService` → `Worker` synchronization
   - Heartbeat middleware

### P1 (High - Needed for Production)

4. **GCode Entity Creation** (0.5 day)
   - Phase 4 enhancement
   - Link uploaded G-code to database

5. **Worker Registry UI** (0.5 day)
   - Phase 5 feature
   - Operational visibility

6. **Circuit Breaker** (0.5 day)
   - Phase 7 hardening
   - Worker failure protection

### P2 (Medium - Nice to Have)

7. **Profile Import** (1 day)
   - Phase 6 feature
   - Orca JSON parser

8. **HTTP Claim Model** (0.5 day)
   - Phase 4 enhancement
   - Hybrid dispatch

9. **Monitoring Dashboards** (1 day)
   - Phase 7 feature
   - Grafana + alerts

### P3 (Low - Post-MVP)

10. **Embedded Worker UI** (2 days)
11. **Profile Editor** (1 day)
12. **Load Testing** (1 day)

---

## Risk Assessment

### High Risk

1. **Storage Integration Complexity**:
   - S3 SDK configuration
   - Credential management
   - Network latency impact
   - **Mitigation**: Use local file server for MVP, S3 for production

2. **Worker-API Coordination**:
   - Entity model mismatch (`SlicerService` vs `Worker`)
   - Registration race conditions
   - **Mitigation**: Merge entities or add sync middleware

### Medium Risk

1. **Binary Availability**:
   - OrcaSlicer binary detection in containers
   - License compliance
   - **Mitigation**: Pre-build Docker images with binaries

2. **Network Reliability**:
   - Worker-API connectivity
   - Upload failures
   - **Mitigation**: Retry logic with exponential backoff

### Low Risk

1. **Performance**: Current architecture should handle 10+ concurrent jobs easily
2. **Scalability**: Redis queue + horizontal worker scaling proven pattern

---

## Conclusion

**Current State**: Strong foundation with production-ready job dispatching (Phase 2), 90% complete registry (Phase 1), and functional worker registration (Phase 3).

**Immediate Focus**: Phase 4 artifact upload system is the critical blocker for end-to-end workflow.

**Path to MVP**: ~5-8 days of focused development to complete artifact uploads, job result posting, and entity synchronization.

**Strengths**:
- Excellent observability (Prometheus metrics, OpenTelemetry)
- Comprehensive testing (5/5 dispatcher tests passing)
- Dual dispatch models (push + pull) for flexibility
- Robust retry logic and rate limiting

**Weaknesses**:
- Missing artifact storage integration
- Entity model redundancy (`SlicerService` vs `Worker`)
- No end-to-end job completion in API database

**Recommendation**: Prioritize P0 items (artifact uploads, result posting, entity sync) to achieve functional MVP within 1 week. Defer UI polish and advanced features to post-MVP iterations.

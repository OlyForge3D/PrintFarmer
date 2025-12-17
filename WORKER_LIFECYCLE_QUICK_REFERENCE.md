# Worker Lifecycle - Quick Reference

**Last Updated**: December 17, 2025

## Component Locations

### 1. Entity Definition
- **File**: [src/infra/Domain/Worker.cs](src/infra/Domain/Worker.cs#L1-L166)
- **Key Properties**: 
  - `LastHeartbeat` (L73) - ⭐ CRITICAL for status tracking
  - `Status` (L38) - Online/Offline/Busy/Draining/Error
  - `ApiKey` (L93) - Unique per worker
  - `FreeSlots` (L40) - Calculated as TotalSlots - ActiveJobs

### 2. Worker Registration
- **File**: [src/api/Services/Slicing/SlicersService.cs](src/api/Services/Slicing/SlicersService.cs#L104-L220)
- **Method**: `RegisterAsync(RegisterSlicerDto dto)` (L104)
- **Creates**: Worker + SlicerService simultaneously
- **Initializes**:
  ```
  Status = Online
  LastHeartbeat = UtcNow
  RegisteredAt = UtcNow
  OnlineAt = UtcNow
  ```

### 3. Heartbeat Processing
- **Endpoint**: [src/api/Controllers/SlicersController.cs](src/api/Controllers/SlicersController.cs#L75-L87)
  ```
  POST /api/slicers/{id}/heartbeat
  ```
- **Service**: [src/api/Services/Slicing/SlicersService.cs](src/api/Services/Slicing/SlicersService.cs#L239-L307)
- **Method**: `HeartbeatAsync(Guid id, HeartbeatDto dto)` (L239)
- **Updates**:
  - LastHeartbeat = UtcNow
  - Status = from heartbeat
  - ActiveJobs = TotalSlots - FreeSlots
  - UpdatedAt = UtcNow

### 4. Stale Worker Detection
- **Service**: [src/api/Services/Workers/WorkerHealthMonitorService.cs](src/api/Services/Workers/WorkerHealthMonitorService.cs)
- **Schedule**: Every 30 seconds
- **Timeout**: 2 minutes (no heartbeat)
- **Action**: Mark as Offline
- **Query**: 
  ```sql
  WHERE Status='Online' AND LastHeartbeat < Now-2min
  ```
- **Database Index**: [src/infra/Data/AppDbContext.cs](src/infra/Data/AppDbContext.cs#L864)

### 5. Job Timeout Detection
- **Service**: [src/api/Services/Workers/JobTimeoutScannerHostedService.cs](src/api/Services/Workers/JobTimeoutScannerHostedService.cs)
- **Schedule**: Every 30 seconds
- **Purpose**: Find stuck jobs (expired leases or timeout)
- **Action**: Requeue or mark Failed

### 6. Circuit Breaker
- **Service**: [src/api/Services/Workers/WorkerCircuitBreakerService.cs](src/api/Services/Workers/WorkerCircuitBreakerService.cs)
- **Trigger**: Consecutive job failures
- **Action**: Disable worker automatically
- **States**: Closed → Open → HalfOpen → Closed

### 7. Worker Repository
- **Interface**: [src/infra/Repositories/Workers/IWorkerRepository.cs](src/infra/Repositories/Workers/IWorkerRepository.cs)
- **Implementation**: [src/infra/Repositories/Workers/EfWorkerRepository.cs](src/infra/Repositories/Workers/EfWorkerRepository.cs)
- **Key Methods**:
  - `GetStaleWorkersAsync(TimeSpan timeout)` (L117)
  - `UpdateHeartbeatAsync(id, freeSlots, totalSlots)` (L172)
  - `UpdateStatusAsync(id, status)` (L145)

### 8. Admin Endpoints
- **Controller**: [src/api/Controllers/Workers/WorkersController.cs](src/api/Controllers/Workers/WorkersController.cs)
- **Operations**:
  - `GET /api/workers` - List all [L44]
  - `GET /api/workers/{id}` - Get details [L60]
  - `GET /api/workers/available` - Online with capacity [L95]
  - `POST /api/workers/{id}/disable` - Admin only [L142]
  - `POST /api/workers/{id}/enable` - Admin only [L176]
  - `DELETE /api/workers/{id}` - Admin only [L200]
  - `PUT /api/workers/{id}/slots` - Update capacity [L222]

---

## Status Transitions

```
Registration → Status=Online, LastHeartbeat=Now

Heartbeat received (every few seconds)
→ LastHeartbeat=Now (resets timeout)
→ Status updated if changed

2 minutes without heartbeat (health monitor checks every 30s)
→ Status=Offline, OfflineAt=Now
→ WorkerHealthMonitorService.CheckWorkerHealthAsync()

5+ job failures in window (circuit breaker)
→ IsDisabled=true
→ DisabledReason="Circuit breaker: ..."
→ WorkerCircuitBreakerService.RecordJobFailureAsync()

Admin disable
→ IsDisabled=true
→ DisabledReason=custom reason
→ POST /api/workers/{id}/disable

Admin enable
→ IsDisabled=false
→ DisabledReason=null
→ POST /api/workers/{id}/enable

Admin delete
→ Remove from database
→ DELETE /api/workers/{id}
```

---

## Critical Query (Stale Detection)

```csharp
// EfWorkerRepository.cs L117-127
var staleWorkers = await _context.Workers
    .AsNoTracking()
    .Where(w => w.Status == WorkerStatus.Online 
           && w.LastHeartbeat < cutoffTime)  // cutoffTime = UtcNow - 2 minutes
    .ToListAsync();
```

---

## Fix Impact (deploy-docker.sh)

**Problem**: Each deployment regenerated API keys → duplicate inactive workers

**Solution**: 
1. Save API keys to `.deploy-config`
2. Load existing keys on redeploy
3. Reuse same `ApiKey` value
4. Worker found by `ServiceId` (one-to-one relationship)

**Result**:
- Single active worker per SlicerService
- No duplicate inactive workers accumulating
- Cleaner database after multiple deployments

---

## Testing References

- **Stale worker detection**: [JobDispatcherServiceIntegrationTests.cs#L275](src/tests/Farm.Web.Api.Tests/Integration/JobDispatcherServiceIntegrationTests.cs#L275)
- **Circuit breaker**: [WorkerCircuitBreakerTests.cs](src/tests/Farm.Web.Api.Tests/Services/WorkerCircuitBreakerTests.cs)
- **Heartbeat sync**: [SlicersControllerUnitTests.cs#L136](src/tests/Farm.Web.Api.Tests/SlicersControllerUnitTests.cs#L136)

---

## Key Insights

1. **LastHeartbeat is the foundation** - All stale detection depends on it being updated every heartbeat
2. **Two-minute timeout is generous** - Allows network delays, but marks non-responsive quickly
3. **Health monitor runs every 30s** - Detects offline workers within 2-3 minutes
4. **Circuit breaker is independent** - Doesn't rely on heartbeat; counts actual job failures
5. **ApiKey reuse prevents duplicates** - Same key = same worker (enforced at service level)

# Worker Lifecycle Management System - Complete Reference

**Document Status**: ✅ Comprehensive system mapping completed
**Date**: December 17, 2025
**Scope**: Full worker entity definition, registration, heartbeat, status tracking, and lifecycle management

---

## 1. Worker Entity Definition

### Location
[src/infra/Domain/Worker.cs](src/infra/Domain/Worker.cs#L1-L166)

### Entity Properties

```csharp
public class Worker
{
    // Identity
    public Guid Id { get; set; }                          // L11
    public string ServiceId { get; set; }                 // L17 (registry API ID)
    public string Name { get; set; }                      // L22
    public string EndpointUrl { get; set; }               // L27
    
    // Capabilities & Configuration
    public string CapabilitiesJson { get; set; }          // L32 (JSON array)
    public int TotalSlots { get; set; }                   // L44 (job capacity)
    public int ActiveJobs { get; set; }                   // L49
    public int FreeSlots => TotalSlots - ActiveJobs;      // L40 (calculated)
    
    // Job Statistics
    public int CompletedJobs { get; set; }                // L54
    public int FailedJobs { get; set; }                   // L59
    public double? AverageProcessingTimeSeconds { get; set; } // L64 (rolling avg)
    
    // Status & Timing
    public string Status { get; set; }                    // L38 (Offline/Online/Busy/Draining/Error)
    public DateTime? LastHeartbeat { get; set; }          // L73 ⭐ CRITICAL
    public DateTime RegisteredAt { get; set; }            // L78
    public DateTime? OnlineAt { get; set; }               // L83
    public DateTime? OfflineAt { get; set; }              // L88
    
    // Security & Admin
    public string? ApiKey { get; set; }                   // L93
    public string? Version { get; set; }                  // L98
    public bool IsDisabled { get; set; }                  // L108
    public string? DisabledReason { get; set; }           // L113
    
    // Metadata
    public string? MetadataJson { get; set; }             // L103
    public DateTime CreatedAt { get; set; }               // L118
    public DateTime UpdatedAt { get; set; }               // L123
    public int ArtifactsProduced { get; set; }            // L127
    public long ArtifactBytesProduced { get; set; }       // L132
}
```

### Worker Status Constants
[src/infra/Domain/Worker.cs](src/infra/Domain/Worker.cs#L139-L166)

```csharp
public static class WorkerStatus
{
    public const string Offline = "Offline";      // No recent heartbeat
    public const string Online = "Online";        // Available & healthy
    public const string Busy = "Busy";            // No free slots
    public const string Draining = "Draining";    // Rejecting new jobs
    public const string Error = "Error";          // Consistently failing jobs
}
```

---

## 2. Worker Registration Endpoint

### Location
[src/api/Controllers/Workers/WorkersController.cs](src/api/Controllers/Workers/WorkersController.cs)

### Registration Flow

**Endpoint**: `POST /api/workers` (implicit via SlicersController)
**Location**: [src/api/Services/Slicing/SlicersService.cs](src/api/Services/Slicing/SlicersService.cs#L104-L220)

**Process**:
1. SlicerService registers via `POST /api/slicers/register` ⭐
2. `SlicersService.RegisterAsync()` creates Worker entity
3. Worker synced to database with:
   - `Status = WorkerStatus.Online`
   - `LastHeartbeat = DateTime.UtcNow`
   - `RegisteredAt = DateTime.UtcNow`
   - `OnlineAt = DateTime.UtcNow`
   - `ApiKey = generated unique key`

**Key Code** [L127-L156]:
```csharp
Worker worker = new Worker
{
    Id = Guid.NewGuid(),
    ServiceId = svc.Id.ToString(),        // Links to SlicerService
    Name = svc.Name,
    EndpointUrl = svc.Host ?? string.Empty,
    CapabilitiesJson = svc.CapabilitiesJson ?? "[]",
    Status = WorkerStatus.Online,
    TotalSlots = maxConcurrentJobs,
    LastHeartbeat = DateTime.UtcNow,      // ⭐ Set at registration
    RegisteredAt = DateTime.UtcNow,
    OnlineAt = DateTime.UtcNow,
    ApiKey = svc.ApiKey,
    CreatedAt = DateTime.UtcNow,
    UpdatedAt = DateTime.UtcNow
};
await _workerRepo.AddAsync(worker);
```

---

## 3. Heartbeat Mechanism

### Heartbeat Endpoint

**Location**: [src/api/Controllers/SlicersController.cs](src/api/Controllers/SlicersController.cs#L75-L87)

```csharp
[HttpPost("{id}/heartbeat")]
[RequireSlicerServiceApiKey]
public async Task<IActionResult> HeartbeatAsync(Guid id, [FromBody] HeartbeatDto dto)
{
    bool ok = await _service.HeartbeatAsync(id, dto, ct);
    return ok ? NoContent() : NotFound();
}
```

### Service Implementation

**Location**: [src/api/Services/Slicing/SlicersService.cs](src/api/Services/Slicing/SlicersService.cs#L239-L307)

**HeartbeatDto Fields**:
- `Status`: Worker status (Online, Busy, etc.)
- `FreeSlots`: Available job slots

**Processing** [L244-L307]:

```csharp
public async Task<bool> HeartbeatAsync(Guid id, HeartbeatDto dto, CancellationToken ct)
{
    // 1. Update SlicerService table
    SlicerService? svc = await _repo.GetByIdAsync(id, ct);
    svc.LastSeen = DateTime.UtcNow;
    svc.Status = dto.Status ?? svc.Status;
    
    // 2. Sync to Worker table
    Worker? worker = await _workerRepo.GetByServiceIdAsync(id.ToString());
    if (worker != null)
    {
        worker.Status = MapStatus(dto.Status ?? "Online");
        worker.LastHeartbeat = DateTime.UtcNow;        // ⭐ CRITICAL
        worker.UpdatedAt = DateTime.UtcNow;
        
        // Update free slots if provided
        if (dto.FreeSlots.HasValue)
        {
            worker.ActiveJobs = Math.Max(0, worker.TotalSlots - dto.FreeSlots.Value);
        }
    }
    
    await _repo.SaveChangesAsync(ct);
    
    // 3. Broadcast via SignalR
    await _hub.Clients.All.SendAsync(SlicerHubEvents.SlicerHeartbeat, ...);
    
    return true;
}
```

### Database Index

**Location**: [src/infra/Data/AppDbContext.cs](src/infra/Data/AppDbContext.cs#L864)

```csharp
_ = b.HasIndex(w => w.LastHeartbeat);  // For stale worker queries
```

---

## 4. Worker Status Tracking

### Repository Methods

**Location**: [src/infra/Repositories/Workers/IWorkerRepository.cs](src/infra/Repositories/Workers/IWorkerRepository.cs)

#### Key Methods:

1. **GetByIdAsync(Guid id)** [L17]
   - Returns tracked entity (allows mutations)
   
2. **GetByStatusAsync(string status, int limit, int offset)** [L28]
   - Filter workers by status (Online/Offline/Busy/Draining/Error)
   
3. **GetAvailableWorkersAsync(int limit)** [L33]
   - Returns workers with Status=Online, FreeSlots > 0, !IsDisabled
   - Ordered by capacity (desc) then load (asc)
   
4. **GetStaleWorkersAsync(TimeSpan heartbeatTimeout)** [L48]
   - Returns workers with Status=Online AND LastHeartbeat < cutoffTime
   - ⭐ Used for detecting offline workers
   
5. **UpdateHeartbeatAsync(Guid id, int freeSlots, int totalSlots)** [L68]
   - Updates LastHeartbeat, FreeSlots, ActiveJobs, Status
   - Auto-transitions to Busy when FreeSlots=0
   
6. **UpdateStatusAsync(Guid id, string status)** [L61]
   - Direct status update
   - Sets OnlineAt/OfflineAt timestamps

### EF Core Implementation

**Location**: [src/infra/Repositories/Workers/EfWorkerRepository.cs](src/infra/Repositories/Workers/EfWorkerRepository.cs)

#### UpdateHeartbeatAsync [L172-L191]
```csharp
public async Task UpdateHeartbeatAsync(Guid id, int freeSlots, int totalSlots)
{
    Worker? worker = await _context.Workers.FindAsync(id);
    if (worker != null)
    {
        worker.LastHeartbeat = DateTime.UtcNow;           // ⭐ CRITICAL
        worker.TotalSlots = totalSlots;
        worker.ActiveJobs = totalSlots - freeSlots;       // ⭐ Calculated
        worker.UpdatedAt = DateTime.UtcNow;
        
        // Auto-transition status
        if (freeSlots > 0 && worker.Status != WorkerStatus.Draining)
        {
            worker.Status = WorkerStatus.Online;
        }
        else if (freeSlots == 0)
        {
            worker.Status = WorkerStatus.Busy;
        }
    }
}
```

#### UpdateStatusAsync [L145-L157]
```csharp
public async Task UpdateStatusAsync(Guid id, string status)
{
    Worker? worker = await _context.Workers.FindAsync(id);
    if (worker != null)
    {
        worker.Status = status;
        worker.UpdatedAt = DateTime.UtcNow;
        
        if (status == WorkerStatus.Online)
            worker.OnlineAt = DateTime.UtcNow;
        else if (status == WorkerStatus.Offline)
            worker.OfflineAt = DateTime.UtcNow;
    }
}
```

#### GetStaleWorkersAsync [L117-L127]
```csharp
public async Task<IReadOnlyList<Worker>> GetStaleWorkersAsync(TimeSpan heartbeatTimeout)
{
    DateTime cutoffTime = DateTime.UtcNow - heartbeatTimeout;
    
    return await _context.Workers
        .AsNoTracking()
        .Where(w => w.Status == WorkerStatus.Online && w.LastHeartbeat < cutoffTime)
        .ToListAsync();
}
```

---

## 5. Worker Cleanup & Maintenance

### 5.1 Health Monitor Service

**Location**: [src/api/Services/Workers/WorkerHealthMonitorService.cs](src/api/Services/Workers/WorkerHealthMonitorService.cs)

**Purpose**: Background service that marks stale workers as Offline

**Configuration**:
- Check interval: **30 seconds** [L20]
- Heartbeat timeout: **2 minutes** [L21]

**Process** [L56-L75]:
```csharp
private async Task CheckWorkerHealthAsync()
{
    // 1. Get workers with no heartbeat for 2+ minutes
    IReadOnlyList<Worker> staleWorkers = 
        await workerRepository.GetStaleWorkersAsync(_heartbeatTimeout);
    
    if (staleWorkers.Count > 0)
    {
        foreach (Worker worker in staleWorkers)
        {
            // 2. Mark as Offline
            await workerRepository.UpdateStatusAsync(
                worker.Id, 
                WorkerStatus.Offline);
        }
        await workerRepository.SaveChangesAsync();
    }
}
```

### 5.2 Job Timeout Scanner

**Location**: [src/api/Services/Workers/JobTimeoutScannerHostedService.cs](src/api/Services/Workers/JobTimeoutScannerHostedService.cs)

**Purpose**: Detects and handles slice jobs stuck in "In-Progress" state

**Configuration**:
- Scan interval: **30 seconds**
- Integrates with circuit breaker on failures

**Process** [L55-L75]:
1. Finds jobs with expired leases or exceeded timeout
2. Records failure with WorkerCircuitBreakerService
3. Requeues job or marks Failed (based on retry policy)

### 5.3 Circuit Breaker Service

**Location**: [src/api/Services/Workers/WorkerCircuitBreakerService.cs](src/api/Services/Workers/WorkerCircuitBreakerService.cs)

**Purpose**: Prevents wasted dispatch to consistently failing workers

**States**:
- **Closed**: Normal operation
- **Open**: Worker disabled (failed too many times)
- **HalfOpen**: Testing recovery

**Configuration** [L31-L48]:
```csharp
public async Task RecordJobFailureAsync(Guid workerId, IWorkerRepository workerRepo)
{
    WorkerCircuitState state = _circuitStates.GetOrAdd(workerId, ...);
    
    lock (state.Lock)
    {
        state.RecentFailures.Add(DateTime.UtcNow);
        
        // Remove old failures outside window
        DateTime cutoff = DateTime.UtcNow.AddSeconds(-_settings.WindowSeconds);
        state.RecentFailures.RemoveAll(t => t < cutoff);
        
        // Check threshold
        if (state.RecentFailures.Count >= _settings.FailureThreshold)
        {
            state.State = CircuitState.Open;
            state.OpenedAt = DateTime.UtcNow;
            
            // Disable worker
            await workerRepo.DisableWorkerAsync(workerId, reason);
            await workerRepo.SaveChangesAsync();
        }
    }
}
```

**Configurable Thresholds** [src/infra/Settings/CircuitBreakerSettings.cs]:
- WindowSeconds: Time window for counting failures
- FailureThreshold: Failures before opening circuit
- CooldownSeconds: Time before attempting half-open

---

## 6. Worker Management Endpoints

### Location
[src/api/Controllers/Workers/WorkersController.cs](src/api/Controllers/Workers/WorkersController.cs)

### Endpoints Summary

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `/api/workers` | GET | Yes | List all workers [L44-L53] |
| `/api/workers/{id}` | GET | Yes | Get worker details [L60-L71] |
| `/api/workers/by-status/{status}` | GET | Yes | Filter by status [L78-L88] |
| `/api/workers/available` | GET | Yes | Available online workers [L95-L104] |
| `/api/workers/{id}/jobs` | GET | Yes | Active jobs on worker [L111-L135] |
| `/api/workers/{id}/disable` | POST | **Admin** | Disable worker [L142-L169] |
| `/api/workers/{id}/enable` | POST | **Admin** | Enable worker [L176-L193] |
| `/api/workers/{id}` | DELETE | **Admin** | Delete worker [L200-L215] |
| `/api/workers/{id}/slots` | PUT | **Admin** | Update capacity [L222-L252] |

### Key Admin Operations

#### Disable Worker [L142-L169]
```csharp
[HttpPost("{id}/disable")]
[Authorize(Policy = "farm_admin")]
public async Task<IActionResult> DisableWorkerAsync(Guid id, [FromBody] DisableWorkerRequest request)
{
    Worker? worker = await _workerRepository.GetByIdAsync(id);
    if (worker == null) return NotFound();
    
    await _workerRepository.DisableWorkerAsync(id, request.Reason);
    await _workerRepository.SaveChangesAsync();
    return NoContent();
}
```

#### Delete Worker [L200-L215]
```csharp
[HttpDelete("{id}")]
[Authorize(Policy = "farm_admin")]
public async Task<IActionResult> DeleteWorkerAsync(Guid id)
{
    Worker? worker = await _workerRepository.GetByIdAsync(id);
    if (worker == null) return NotFound();
    
    await _workerRepository.DeleteAsync(id);
    await _workerRepository.SaveChangesAsync();
    return NoContent();
}
```

#### Update Capacity [L222-L252]
```csharp
[HttpPut("{id}/slots")]
[Authorize(Policy = "farm_admin")]
public async Task<IActionResult> UpdateWorkerSlotsAsync(Guid id, UpdateWorkerSlotsRequest request)
{
    if (request.TotalSlots < 1) return BadRequest();
    
    await _workerRepository.UpdateTotalSlotsAsync(id, request.TotalSlots);
    await _workerRepository.SaveChangesAsync();
    return Ok(MapToResponse(updatedWorker));
}
```

---

## 7. Worker Response DTOs

### Location
[src/infra/Models.cs](src/infra/Models.cs) + [src/api/Controllers/Workers/WorkersController.cs](src/api/Controllers/Workers/WorkersController.cs#L262-L290)

### WorkerResponse DTO

```csharp
public class WorkerResponse
{
    public Guid Id { get; set; }
    public string ServiceId { get; set; }
    public string Name { get; set; }
    public string EndpointUrl { get; set; }
    public string[] Capabilities { get; set; }
    public string Status { get; set; }
    public int FreeSlots { get; set; }
    public int TotalSlots { get; set; }
    public int ActiveJobs { get; set; }
    public int CompletedJobs { get; set; }
    public int FailedJobs { get; set; }
    public double? AverageProcessingTimeSeconds { get; set; }
    public DateTime? LastHeartbeat { get; set; }      // ⭐ Client-visible
    public DateTime RegisteredAt { get; set; }
    public DateTime? OnlineAt { get; set; }
    public DateTime? OfflineAt { get; set; }
    public string? Version { get; set; }
    public bool IsDisabled { get; set; }
    public string? DisabledReason { get; set; }
}
```

---

## 8. Database Schema

### Entity Configuration

**Location**: [src/infra/Data/AppDbContext.cs](src/infra/Data/AppDbContext.cs#L850-L865)

```csharp
modelBuilder.Entity<Worker>(b =>
{
    b.HasKey(w => w.Id);
    
    b.Property(w => w.ServiceId).IsRequired().HasMaxLength(36);
    b.Property(w => w.Name).IsRequired().HasMaxLength(255);
    b.Property(w => w.EndpointUrl).IsRequired().HasMaxLength(500);
    b.Property(w => w.CapabilitiesJson).IsRequired();
    b.Property(w => w.Status).IsRequired().HasMaxLength(50);
    b.Property(w => w.ApiKey).HasMaxLength(512);
    
    // ⭐ CRITICAL: Index for stale worker detection
    b.HasIndex(w => w.LastHeartbeat);
    
    b.ToTable("Workers");
});
```

### Migrations

**Status**: Uses `EnsureCreated()` during development
- **File**: [src/infra/Data/AppDbContext.cs](src/infra/Data/AppDbContext.cs)
- No explicit migrations required for development
- Production uses migration scripts

---

## 9. Testing References

### Integration Tests

**Location**: [src/tests/Farm.Web.Api.Tests/Integration/JobDispatcherServiceIntegrationTests.cs](src/tests/Farm.Web.Api.Tests/Integration/JobDispatcherServiceIntegrationTests.cs#L275-L310)

**Test**: `FindBestWorkerForJobAsync_IgnoresStaleWorkers`
- Creates stale worker with `LastHeartbeat = 10 minutes ago`
- Verifies dispatcher ignores it
- Confirms only online workers with recent heartbeats are selected

### Unit Tests

**Location**: [src/tests/Farm.Web.Api.Tests/Services/WorkerCircuitBreakerTests.cs](src/tests/Farm.Web.Api.Tests/Services/WorkerCircuitBreakerTests.cs)

**Tests**:
1. `RecordJobFailureAsync_OpensCircuitWhenThresholdExceeded` [L28-L55]
2. `RecordJobFailureAsync_TransitionsToHalfOpenAfterCooldown` [L57-L110]
3. `RecordJobFailureAsync_TransitionsToClosedOnSuccess` [L112-L165]

---

## 10. Lifecycle Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                    WORKER LIFECYCLE MANAGEMENT                       │
└─────────────────────────────────────────────────────────────────────┘

1. REGISTRATION (SlicersService.RegisterAsync)
   ├─ SlicerService registered via POST /api/slicers/register
   ├─ Worker entity created with:
   │  ├─ Status = Online
   │  ├─ LastHeartbeat = UtcNow
   │  ├─ RegisteredAt = UtcNow
   │  └─ OnlineAt = UtcNow
   └─ Worker inserted into database

2. HEARTBEAT (SlicersService.HeartbeatAsync)
   ├─ Worker sends POST /api/slicers/{id}/heartbeat
   ├─ SlicerService.HeartbeatAsync updates:
   │  ├─ LastHeartbeat = UtcNow              ⭐ CRITICAL
   │  ├─ Status = (from heartbeat)
   │  ├─ ActiveJobs = TotalSlots - FreeSlots
   │  └─ UpdatedAt = UtcNow
   └─ SignalR broadcasts SlicerHeartbeat event

3. STATUS TRACKING (WorkerHealthMonitorService - runs every 30s)
   ├─ Query stale workers:
   │  └─ WHERE Status=Online AND LastHeartbeat < (Now - 2 minutes)
   ├─ For each stale worker:
   │  ├─ UpdateStatusAsync(workerId, WorkerStatus.Offline)
   │  ├─ Status = Offline
   │  └─ OfflineAt = UtcNow
   └─ SaveChangesAsync()

4. JOB FAILURE (JobTimeoutScannerHostedService - runs every 30s)
   ├─ Detect stuck jobs (lease expired or timeout exceeded)
   ├─ Record failure with CircuitBreakerService
   ├─ Circuit breaker tracks failures:
   │  ├─ Window: ConfigurableWindowSeconds (default 60s)
   │  ├─ Threshold: ConfigurableFailureThreshold (default 5)
   │  └─ If failures >= threshold:
   │     ├─ DisableWorkerAsync(workerId, reason)
   │     ├─ IsDisabled = true
   │     ├─ DisabledReason = "Circuit breaker: ..."
   │     └─ Status = Error (implicitly)
   ├─ Requeue or fail job based on retry policy
   └─ SaveChangesAsync()

5. CLEANUP & DELETION
   ├─ Manual admin operation:
   │  ├─ DELETE /api/workers/{id}
   │  └─ DeleteAsync(workerId)
   ├─ Or disable for recovery:
   │  ├─ POST /api/workers/{id}/disable
   │  ├─ DisableWorkerAsync(workerId, reason)
   │  └─ IsDisabled = true
   └─ SaveChangesAsync()

6. RECOVERY (admin enabled)
   ├─ POST /api/workers/{id}/enable
   ├─ EnableWorkerAsync(workerId)
   ├─ IsDisabled = false
   ├─ DisabledReason = null
   └─ Worker can receive jobs again
```

---

## 11. Key Timestamps & Timeouts

| Property | Timeout | Purpose |
|----------|---------|---------|
| `LastHeartbeat` | 2 minutes | Detect offline workers |
| `RegisteredAt` | N/A | Track worker creation |
| `OnlineAt` | N/A | First "Online" status |
| `OfflineAt` | N/A | Last "Offline" transition |
| `CreatedAt` | N/A | Record creation |
| `UpdatedAt` | N/A | Track changes |
| Circuit breaker window | Configurable (default 60s) | Failure detection |
| Health check interval | 30 seconds | Stale detection |
| Job timeout scan | 30 seconds | Job timeout detection |

---

## 12. Critical Properties for Duplicate Prevention

✅ **The deploy-docker.sh fix preserves API keys by:**

1. **ServiceId** [Worker.cs L17]
   - Links Worker to SlicerService
   - One Worker per SlicerService
   - No duplicate workers if same ServiceId

2. **ApiKey** [Worker.cs L93]
   - Unique identifier for authentication
   - Generated at registration
   - Should be reused across deployments
   - **Currently** ⭐ Saved in .deploy-config to prevent regeneration

3. **Heartbeat Tracking**
   - LastHeartbeat prevents stale duplicates from accumulating
   - WorkerHealthMonitorService marks old ones Offline
   - JobDispatcherService ignores Offline workers

4. **Query Optimization** [EfWorkerRepository.cs L73-84]
   ```csharp
   public async Task<IReadOnlyList<Worker>> GetAvailableWorkersAsync()
   {
       return await _context.Workers
           .Where(w => w.Status == WorkerStatus.Online      // ← Only Online
               && (w.TotalSlots - w.ActiveJobs) > 0         // ← With capacity
               && !w.IsDisabled)                            // ← Not disabled
           .OrderByDescending(w => w.TotalSlots - w.ActiveJobs)
           .Take(limit)
           .ToListAsync();
   }
   ```

---

## 13. Summary

The PrintFarmer worker lifecycle is comprehensive with:

✅ **Registration**: Workers auto-registered when SlicerService registers  
✅ **Heartbeat**: 2-minute timeout with health monitor cleanup  
✅ **Status Tracking**: Online/Offline/Busy/Draining/Error states  
✅ **Fault Detection**: Circuit breaker disables consistently failing workers  
✅ **Job Cleanup**: Timeout scanner finds stuck jobs and requeues  
✅ **Admin Control**: Full CRUD + enable/disable/update capacity  
✅ **Database Index**: LastHeartbeat indexed for efficient stale detection  

**Deploy Script Fix Impact**:
- Prevents API key regeneration
- Eliminates duplicate inactive workers
- Maintains consistent worker identity
- Reduces database bloat

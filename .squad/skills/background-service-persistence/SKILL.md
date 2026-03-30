---
name: "background-service-persistence"
description: "Pattern for persisting events from background monitoring services with SignalR broadcasting"
domain: "backend-architecture"
confidence: "high"
source: "lambert-spaghetti-detection-analysis"
---

## Context

PrintFarmer uses BackgroundService workers for continuous monitoring (printer status, failure detection, etc.). These services need to:
1. Broadcast real-time events via SignalR
2. Persist events to database for history/audit trails
3. Maintain consistency between in-memory state and database
4. Avoid blocking the monitoring loop on database writes

## Pattern: Event Persistence + SignalR Broadcast

### Core Principle

**Write to database FIRST, then broadcast via SignalR.** This ensures that if SignalR fails, the event is still recorded for history queries.

### Implementation Steps

**1. Create Event Entity**
```csharp
public class MonitoringEvent
{
    public Guid Id { get; set; }
    
    // Core event metadata
    public Guid EntityId { get; set; }  // PrinterId, JobId, etc.
    public DateTime DetectedAt { get; set; }
    
    // User action tracking (nullable for unacknowledged events)
    public bool? UserAcknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    
    // Audit timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

**Key decisions:**
- Use nullable fields for user actions (events start as unacknowledged)
- Always include CreatedAt/UpdatedAt for audit trails
- Foreign keys to related entities (Printer, Job, User, etc.)

**2. Update Background Service**
```csharp
private async Task HandleEventAsync(
    Entity entity,
    Result result,
    AppDbContext dbContext,
    CancellationToken cancellationToken)
{
    // Step 1: Persist to database FIRST
    var evt = new MonitoringEvent
    {
        Id = Guid.NewGuid(),
        EntityId = entity.Id,
        DetectedAt = DateTime.UtcNow,
        // ... other fields
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
    
    dbContext.MonitoringEvents.Add(evt);
    await dbContext.SaveChangesAsync(cancellationToken);
    
    // Step 2: Broadcast via SignalR (non-blocking)
    var dto = new MonitoringEventDto
    {
        Id = evt.Id, // Include DB-generated ID for client-side tracking
        EntityId = entity.Id,
        DetectedAt = evt.DetectedAt
    };
    
    await _hub.Clients.All.SendAsync("EventDetected", dto, cancellationToken);
}
```

**3. Create History Endpoint**
```csharp
[HttpGet("history")]
public async Task<ActionResult<IEnumerable<MonitoringEventDto>>> GetHistoryAsync(
    [FromQuery] int pageSize = 50,
    [FromQuery] int page = 1,
    [FromQuery] Guid? entityId = null,
    CancellationToken ct = default)
{
    IQueryable<MonitoringEvent> query = _dbContext.MonitoringEvents
        .Include(e => e.Entity) // Eager load related entities
        .OrderByDescending(e => e.DetectedAt);
    
    if (entityId.HasValue)
        query = query.Where(e => e.EntityId == entityId.Value);
    
    List<MonitoringEvent> events = await query
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync(ct);
    
    return Ok(events.Select(ToDto));
}
```

**4. Add Acknowledge Endpoint**
```csharp
[HttpPost("{eventId:guid}/acknowledge")]
public async Task<ActionResult> AcknowledgeAsync(
    Guid eventId,
    [FromBody] AcknowledgeDto dto,
    CancellationToken ct = default)
{
    MonitoringEvent? evt = await _dbContext.MonitoringEvents
        .FindAsync([eventId], ct);
    
    if (evt == null)
        return NotFound();
    
    evt.UserAcknowledged = true;
    evt.AcknowledgedAt = DateTime.UtcNow;
    evt.AcknowledgedByUserId = GetCurrentUserId();
    evt.UpdatedAt = DateTime.UtcNow;
    
    await _dbContext.SaveChangesAsync(ct);
    
    return NoContent();
}
```

### Database Considerations

**Indexes:**
- `DetectedAt DESC` — Primary sorting for history queries
- `EntityId + DetectedAt` — Per-entity filtered views
- `UserAcknowledged` — Filter unacknowledged events (if nullable, create partial index)

**Migrations:**
- Always create migrations for BOTH providers (PostgreSQL + SQL Server)
- Use descriptive names: `AddFailureDetectionEvents`, `AddMaintenanceAlertEvents`, etc.

**Retention:**
- Consider archival strategy for old events (90 days? 1 year?)
- Use soft deletes or separate archive table if auditing is required

### EF Core Scoping (CRITICAL)

Background services run in a singleton scope. **Always create a new DbContext per operation:**

```csharp
private async Task RunMonitoringCycleAsync(CancellationToken cancellationToken)
{
    using IServiceScope scope = _scopeFactory.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
    // ... perform database operations
}
```

**Anti-pattern:**
```csharp
// ❌ NEVER inject DbContext directly into a BackgroundService constructor
// This creates a long-lived DbContext that leaks connections
public PrintFailureMonitorService(AppDbContext dbContext) // BAD!
```

### SignalR DTO Design

**Include the database-generated event ID in the broadcast DTO:**
```csharp
public class MonitoringEventDto
{
    public Guid Id { get; set; } // From database — enables client-side correlation
    public Guid EntityId { get; set; }
    public DateTime DetectedAt { get; set; }
    // ... other fields
}
```

Why? The frontend can use this ID to:
- Link real-time notifications to history table rows
- Implement "acknowledge" actions without re-querying
- Track which events have already been seen

## Examples

### Failure Detection (Implemented)
- **Service:** `PrintFailureMonitorService`
- **Entity:** `FailureDetectionIncident`
- **Endpoint:** `/api/failure-detection/history?printerId={guid?}&take={int?}`
- **SignalR:** `FailureDetected` event on `PrinterHub`
- **Reference:** `.squad/decisions/inbox/lambert-failure-detection-incident-history.md`

Current concrete slice keeps scope intentionally narrow:
- Persist only actual failure incidents, not every healthy scan
- Reuse operator-facing `jobName` / `fileName` context from `ResolveJobContext`
- Return the same `FailureDetectionDto` shape for history queries, including optional persisted `id`

### Future Use Cases
- **Maintenance Alerts:** Detect print bed leveling drift, nozzle wear, etc.
- **Filament Runout:** Track when printers pause due to runout
- **Print Progress Milestones:** Notify users at 25%, 50%, 75% completion
- **Temperature Anomalies:** Detect hotend/bed temp fluctuations

## Anti-Patterns

- **❌ Broadcast SignalR before database write** — If SignalR succeeds but DB write fails, event is lost
- **❌ Injecting DbContext into BackgroundService constructor** — Creates long-lived context, leaks connections
- **❌ Blocking the monitoring loop on database writes** — Use fire-and-forget or separate task
- **❌ No pagination on history endpoint** — Will timeout with large event counts
- **❌ Missing foreign keys** — Can't join to related entities or enforce referential integrity
- **❌ Not including event ID in SignalR DTO** — Frontend can't correlate real-time events with history

## Testing Strategy

**Unit Tests:**
- Test event creation logic in isolation
- Mock `IHubContext` for SignalR broadcasting
- Verify correct field mapping between entity and DTO

**Integration Tests:**
- Verify database write + SignalR broadcast sequence
- Test history endpoint filtering and pagination
- Test acknowledge endpoint authorization and updates

**Background Service Tests:**
- Use `CustomWebApplicationFactory` with test database
- Verify monitoring cycle detects events correctly
- Test error handling and retry logic

## References

- **PrintFailureMonitorService:** `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs`
- **FailureDetectionController:** `src/api/Controllers/FailureDetectionController.cs`
- **FailureDetectionDto:** `src/infra/Dtos/FailureDetectionDto.cs`
- **Decision Document:** `.squad/decisions/inbox/lambert-spaghetti-backend.md`

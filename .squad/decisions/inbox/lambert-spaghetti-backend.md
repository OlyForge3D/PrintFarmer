# Spaghetti Detection Backend Design

**Author:** Lambert  
**Date:** 2026-01-12  
**Status:** PROPOSED — Awaiting team review  
**Type:** Feature Design

## Problem Statement

The PrintFailureMonitorService currently broadcasts `FailureDetected` events via SignalR with no persistence. This makes it impossible to:
- Show users a history of past failures
- Track which failures were acted upon vs ignored
- Provide any meaningful UI beyond "something just failed right now"
- Audit detection accuracy over time
- Correlate failures with job outcomes

The `/api/failure-detection/history` endpoint returns HTTP 501 with a clear message: events are transient.

## Current Architecture (What Works)

**✅ Real-time Detection Pipeline**
- `PrintFailureMonitorService` → background worker, scans active prints every 30s
- `ObicoFailureDetectionService` → HTTP client to Obico ML API (confidence scores)
- `FailureDetectionDto` → SignalR broadcast with PrinterId, JobId, Confidence, DetectedAt, AutoPaused
- Works: Real-time alerting via WebSockets to connected clients

**✅ Domain Model Foundation**
- `PrintJob` entity: Already tracks Status, FailureReason, StartTime, EndTime, AssignedPrinter
- `ObicoServer` entity: Manages per-server assignments and load balancing
- `Camera` entity: Links printers to snapshot URLs for analysis

**⚠️ Missing: Persistence Layer**
- No `FailureDetectionEvent` table
- No status tracking (was this event acknowledged?)
- No outcome tracking (was the print actually a failure?)

## Phase 1 Design: Minimal Persistence for History UI

**Goal:** Add persistence with zero breaking changes to the existing SignalR broadcast workflow.

### New Entity: `FailureDetectionEvent`

```csharp
public class FailureDetectionEvent
{
    public Guid Id { get; set; }
    
    // Core detection metadata
    public Guid PrinterId { get; set; }
    public Printer? Printer { get; set; }
    
    public Guid? JobId { get; set; }
    public PrintJob? Job { get; set; }
    
    public decimal Confidence { get; set; }
    public DateTime DetectedAt { get; set; }
    public bool AutoPaused { get; set; }
    
    // User action tracking (Phase 1: nullable, Phase 2+: workflow states)
    public bool? UserAcknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public User? AcknowledgedBy { get; set; }
    
    // Outcome tracking (nullable: user can mark after print completes)
    public bool? WasActualFailure { get; set; }
    public string? UserNotes { get; set; }
    
    // Obico server tracking for debugging
    public Guid? ObicoServerId { get; set; }
    public ObicoServer? ObicoServer { get; set; }
    
    // Audit trail
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

**Key decisions:**
- `UserAcknowledged` + `AcknowledgedAt` → Did someone see this and click "dismiss" or "investigate"?
- `WasActualFailure` → Ground truth labeling for ML accuracy tracking (nullable: user can skip)
- Foreign keys to PrintJob, Printer, ObicoServer → enables filtering, reporting, and debugging
- No status enum yet: keep it simple, add workflow states later if needed

### Backend Changes (Minimal)

**1. Update `PrintFailureMonitorService.HandleFailureDetectedAsync`**
```csharp
private async Task HandleFailureDetectedAsync(
    Printer printer,
    FailureDetectionResult result,
    AppDbContext dbContext,
    CancellationToken cancellationToken)
{
    // Find current job
    PrintJob? currentJob = await dbContext.PrintJobs
        .Where(j => j.AssignedPrinterId == printer.Id && 
                    (j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting))
        .OrderByDescending(j => j.ActualStartTime ?? j.QueuedAt)
        .FirstOrDefaultAsync(cancellationToken);

    // NEW: Persist event to database
    var failureEvent = new FailureDetectionEvent
    {
        Id = Guid.NewGuid(),
        PrinterId = printer.Id,
        JobId = currentJob?.Id,
        Confidence = result.Confidence,
        DetectedAt = result.AnalyzedAt,
        AutoPaused = false, // TODO: Implement pause logic
        ObicoServerId = printer.ObicoServerId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
    
    dbContext.FailureDetectionEvents.Add(failureEvent);
    await dbContext.SaveChangesAsync(cancellationToken);

    // Existing SignalR broadcast (unchanged)
    var dto = new FailureDetectionDto
    {
        PrinterId = printer.Id,
        PrinterName = printer.Name,
        JobId = currentJob?.Id,
        Confidence = result.Confidence,
        DetectedAt = result.AnalyzedAt,
        AutoPaused = false
    };
    
    await _hub.Clients.All.SendAsync("FailureDetected", dto, cancellationToken);
}
```

**2. Update `FailureDetectionController.GetHistory()`**

Replace HTTP 501 with actual query:
```csharp
[HttpGet("history")]
[ProducesResponseType(typeof(IEnumerable<FailureDetectionEventDto>), 200)]
public async Task<ActionResult<IEnumerable<FailureDetectionEventDto>>> GetHistoryAsync(
    [FromQuery] int pageSize = 50,
    [FromQuery] int page = 1,
    [FromQuery] Guid? printerId = null,
    [FromQuery] bool? acknowledgedOnly = null,
    CancellationToken ct = default)
{
    IQueryable<FailureDetectionEvent> query = _dbContext.FailureDetectionEvents
        .Include(e => e.Printer)
        .Include(e => e.Job)
        .OrderByDescending(e => e.DetectedAt);

    if (printerId.HasValue)
        query = query.Where(e => e.PrinterId == printerId.Value);
    
    if (acknowledgedOnly.HasValue)
        query = query.Where(e => e.UserAcknowledged == acknowledgedOnly.Value);

    List<FailureDetectionEvent> events = await query
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync(ct);

    var dtos = events.Select(e => new FailureDetectionEventDto
    {
        Id = e.Id,
        PrinterId = e.PrinterId,
        PrinterName = e.Printer?.Name ?? "Unknown",
        JobId = e.JobId,
        JobName = e.Job?.Name,
        Confidence = e.Confidence,
        DetectedAt = e.DetectedAt,
        AutoPaused = e.AutoPaused,
        UserAcknowledged = e.UserAcknowledged,
        AcknowledgedAt = e.AcknowledgedAt,
        WasActualFailure = e.WasActualFailure
    });

    return Ok(dtos);
}
```

**3. Add Acknowledge Endpoint**
```csharp
[HttpPost("{eventId:guid}/acknowledge")]
public async Task<ActionResult> AcknowledgeEventAsync(
    Guid eventId,
    [FromBody] AcknowledgeEventDto dto,
    CancellationToken ct = default)
{
    FailureDetectionEvent? evt = await _dbContext.FailureDetectionEvents
        .FindAsync([eventId], ct);
    
    if (evt == null)
        return NotFound();

    evt.UserAcknowledged = true;
    evt.AcknowledgedAt = DateTime.UtcNow;
    evt.AcknowledgedByUserId = GetCurrentUserId(); // from JWT claims
    evt.WasActualFailure = dto.WasActualFailure;
    evt.UserNotes = dto.Notes;
    evt.UpdatedAt = DateTime.UtcNow;

    await _dbContext.SaveChangesAsync(ct);
    
    return NoContent();
}
```

### Migrations Required

**Both SQLite and PostgreSQL:**
```bash
cd /Users/jpapiez/s/PFarm1/src
DB_PROVIDER=postgres dotnet ef migrations add AddFailureDetectionEvents \
  --context AppDbContext \
  --project ../migrations/Farm.Migrations.PostgreSQL \
  --startup-project api

DB_PROVIDER=sqlserver dotnet ef migrations add AddFailureDetectionEvents \
  --context AppDbContext \
  --project ../migrations/Farm.Migrations.SqlServer \
  --startup-project api
```

**Schema:**
- Table: `FailureDetectionEvents`
- Columns: Id (PK), PrinterId (FK), JobId (FK nullable), ObicoServerId (FK nullable), Confidence (decimal), DetectedAt (datetime), AutoPaused (bool), UserAcknowledged (bool nullable), AcknowledgedAt (datetime nullable), AcknowledgedByUserId (FK nullable), WasActualFailure (bool nullable), UserNotes (nvarchar(500) nullable), CreatedAt, UpdatedAt
- Indexes: DetectedAt DESC (for history queries), PrinterId + DetectedAt (for per-printer views)

### DTOs for Frontend

**FailureDetectionEventDto** (extends current `FailureDetectionDto`):
```typescript
export interface FailureDetectionEventDto {
  id: string;
  printerId: string;
  printerName: string;
  jobId?: string;
  jobName?: string;
  confidence: number;
  detectedAt: string; // ISO 8601
  autoPaused: boolean;
  userAcknowledged?: boolean;
  acknowledgedAt?: string;
  wasActualFailure?: boolean;
  userNotes?: string;
}

export interface AcknowledgeEventDto {
  wasActualFailure?: boolean;
  notes?: string;
}
```

## What This Unlocks for Ripley (Frontend)

**Phase 1 UI Requirements:**
1. **History Table/List** → `GET /api/failure-detection/history?pageSize=50`
   - Columns: Printer, Job, Confidence, Detected At, Status (acknowledged/pending)
   - Filters: By printer, acknowledged status
   - Click row → modal with snapshot (if available), confidence %, acknowledge button

2. **Acknowledge Modal**
   - Show: Printer name, job name, confidence score, timestamp
   - Actions: "False Alarm" (wasActualFailure=false), "Confirmed Failure" (wasActualFailure=true), "Dismiss" (no feedback)
   - Optional: Notes text field

3. **Real-time Banner** (existing SignalR)
   - Keep current toast/notification on `FailureDetected` event
   - New: Show "unacknowledged events" count badge in nav

## Open Questions for Team Discussion

1. **Auto-pause implementation:** PrintFailureMonitorService logs "pause requires backend client integration". Do we implement this in Phase 1 or defer?
2. **Snapshot storage:** Should we save the analyzed snapshot URL with each event? (Pro: aids debugging, Con: storage overhead)
3. **Retention policy:** Archive/delete events older than 90 days? Or keep forever for ML training?
4. **Notification preferences:** Should failure detection respect user notification settings, or always alert?

## Decision Rationale

**Why this approach:**
- ✅ Non-breaking: SignalR broadcast unchanged, existing real-time UX preserved
- ✅ Incremental: Adds history endpoint without requiring full workflow states
- ✅ Auditable: Tracks who acknowledged events and their accuracy feedback
- ✅ Queryable: Enables per-printer, per-job, and time-range filtering
- ✅ ML-ready: `WasActualFailure` field supports future accuracy reporting

**Why NOT a separate event log table:**
- PrintFarmer doesn't have a generic event log system yet
- This is domain-specific (failure detection), not infrastructure
- Entity can evolve into workflow states later without breaking changes

**Alternatives considered:**
- **Option A: In-memory cache only** → Rejected: loses history on restart
- **Option B: SystemLog table** → Rejected: too generic, hard to query
- **Option C: Separate microservice** → Rejected: premature for Phase 1

## Impact & Risks

**Impact:**
- New table: ~1KB per event, estimate 10-50 events/day for 5 active printers = ~150KB/month
- Query performance: DetectedAt index ensures fast history fetches
- Migration: Required for both providers (tested in dev)

**Risks:**
- Low: Existing background service already handles database writes (JobStateHistory)
- Low: EF Core DbContext scoping already fixed in prior wave
- Medium: Frontend needs to handle pagination (50 events/page should cover most use cases)

## Next Steps

1. **Lambert:** Implement entity, migrations, controller endpoints
2. **Ripley:** Build history table + acknowledge modal UI
3. **Team:** Review auto-pause implementation plan
4. **Ash:** Document failure detection workflow in admin docs

---

**Reviewed by:** (pending)  
**Approved by:** (pending)  
**Implementation:** TBD

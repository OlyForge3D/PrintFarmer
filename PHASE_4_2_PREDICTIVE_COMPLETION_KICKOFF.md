# Phase 4.2: Predictive Completion Estimates - Implementation Kickoff

**Phase**: 4.2  
**Feature**: Predictive Completion Time Estimation  
**Duration**: 2 days (January 13-14, 2026)  
**Status**: 🚀 Ready to Implement  
**Depends On**: Phase 4.1 (Job Scheduling) ✅ COMPLETE

---

## 📋 Feature Overview

### What We're Building

A **predictive completion time system** that analyzes historical job data to estimate when print jobs will complete. The system learns from actual job durations and provides confidence levels based on historical data accuracy.

### User Value Proposition

- 📊 Know approximately when jobs will finish before they start
- 🎯 Plan around printer schedules with confidence-backed estimates
- 📈 Better insights into printer performance and material consistency
- ⚡ Queue planning with realistic time expectations

### Business Requirements

- **Accuracy Target**: ±15% average prediction error
- **Confidence Levels**: High (±10%), Medium (±20%), Low (±50%)
- **Learning**: System improves as more jobs complete
- **Filtering**: Estimates based on similar jobs (same model, material)

---

## 🏗️ Architecture Design

### Database Schema

**New Table: `PrintJobStatistics`**
```sql
CREATE TABLE "PrintJobStatistics" (
    "Id" uuid PRIMARY KEY,
    "PrintJobId" uuid NOT NULL UNIQUE,
    "ActualDurationMs" bigint,           -- Actual duration in milliseconds
    "EstimatedDurationMs" bigint,        -- Estimated duration from gcode
    "PrinterModelId" uuid,               -- For grouping similar printers
    "Material" varchar(100),             -- Material type (PLA, ABS, PETG, etc.)
    "NozzleTemperature" int,             -- Nozzle temp in Celsius
    "BedTemperature" int,                -- Bed temp in Celsius
    "SpeedPercentage" int DEFAULT 100,   -- Print speed (% of normal)
    "IsSuccess" bool DEFAULT false,      -- Whether job completed successfully
    "FailureReason" varchar(500),        -- Why it failed if IsSuccess=false
    "CompletedAtUtc" datetime,           -- When job actually completed
    "CreatedAtUtc" datetime NOT NULL,
    "UpdatedAtUtc" datetime NOT NULL,
    FOREIGN KEY ("PrintJobId") REFERENCES "PrintJobs"("Id") ON DELETE CASCADE,
    FOREIGN KEY ("PrinterModelId") REFERENCES "PrinterModels"("Id") ON DELETE SET NULL
);

CREATE INDEX idx_statistics_model_material ON "PrintJobStatistics"("PrinterModelId", "Material");
CREATE INDEX idx_statistics_success ON "PrintJobStatistics"("IsSuccess");
CREATE INDEX idx_statistics_completed ON "PrintJobStatistics"("CompletedAtUtc");
```

### Domain Model Changes

**New Entity: `PrintJobStatistics`**
```csharp
public class PrintJobStatistics
{
    public Guid Id { get; set; }
    public Guid PrintJobId { get; set; }
    
    // Duration tracking
    public long? ActualDurationMs { get; set; }        // Actual time taken (ms)
    public long? EstimatedDurationMs { get; set; }     // Time from gcode estimate
    
    // Job characteristics
    public Guid? PrinterModelId { get; set; }
    public string? Material { get; set; }              // PLA, ABS, PETG, TPU, etc.
    public int? NozzleTemperature { get; set; }        // Celsius
    public int? BedTemperature { get; set; }           // Celsius
    public int SpeedPercentage { get; set; } = 100;    // % of normal speed
    
    // Outcome
    public bool IsSuccess { get; set; }
    public string? FailureReason { get; set; }         // Why it failed
    public DateTime? CompletedAtUtc { get; set; }
    
    // Audit
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    
    // Navigation
    public PrintJob? PrintJob { get; set; }
    public PrinterModel? PrinterModel { get; set; }
}
```

**Update `PrintJob` Entity**: Add navigation property
```csharp
public class PrintJob
{
    // ... existing properties ...
    
    // New navigation
    public PrintJobStatistics? Statistics { get; set; }
}
```

### DTOs

**`CompletionPredictionDto`** - What the prediction returns
```csharp
public class CompletionPredictionDto
{
    public string JobId { get; set; } = string.Empty;
    public DateTime EstimatedCompletionTime { get; set; }
    public TimeSpan? EstimatedDuration { get; set; }
    public ConfidenceLevel Confidence { get; set; }
    public int SampleSize { get; set; }
    public double? VariancePercent { get; set; }
    public string? Note { get; set; }
}

public enum ConfidenceLevel
{
    High,      // ±10% accuracy, 10+ samples
    Medium,    // ±20% accuracy, 3-9 samples
    Low        // ±50% accuracy, 1-2 samples
}
```

**`DurationStatsDto`** - Analytics data
```csharp
public class DurationStatsDto
{
    public int TotalJobs { get; set; }
    public int SuccessfulJobs { get; set; }
    public double SuccessRate { get; set; }           // 0.0 to 1.0
    public TimeSpan AverageDuration { get; set; }
    public TimeSpan MedianDuration { get; set; }
    public TimeSpan MinDuration { get; set; }
    public TimeSpan MaxDuration { get; set; }
    public double StandardDeviation { get; set; }
    public double Variance { get; set; }
    public string? Material { get; set; }
    public string? PrinterModelName { get; set; }
}

public class PrintJobStatisticsDto
{
    public string JobId { get; set; } = string.Empty;
    public long? ActualDurationMs { get; set; }
    public long? EstimatedDurationMs { get; set; }
    public string? Material { get; set; }
    public int? NozzleTemperature { get; set; }
    public int? BedTemperature { get; set; }
    public int SpeedPercentage { get; set; }
    public bool IsSuccess { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
```

### Service Architecture

**`PredictionService.cs`** (New Service)

**Primary Methods**:
```csharp
/// <summary>
/// Predict job completion time based on historical data
/// </summary>
public async Task<CompletionPredictionDto> PredictCompletionTimeAsync(
    Guid jobId,
    CancellationToken cancellationToken = default);

/// <summary>
/// Record actual job completion for learning
/// </summary>
public async Task RecordJobCompletionAsync(
    Guid jobId,
    long actualDurationMs,
    bool isSuccess,
    string? failureReason = null,
    CancellationToken cancellationToken = default);

/// <summary>
/// Get statistics for a printer model/material combo
/// </summary>
public async Task<DurationStatsDto> GetDurationStatsAsync(
    Guid? modelId = null,
    string? material = null,
    DateTime? fromDate = null,
    int? minSampleSize = 3,
    CancellationToken cancellationToken = default);

/// <summary>
/// Get all recorded statistics for a job
/// </summary>
public async Task<PrintJobStatisticsDto?> GetJobStatisticsAsync(
    Guid jobId,
    CancellationToken cancellationToken = default);

/// <summary>
/// Bulk statistics for dashboard analytics
/// </summary>
public async Task<Dictionary<string, DurationStatsDto>> GetMaterialStatsAsync(
    Guid? printerId = null,
    CancellationToken cancellationToken = default);
```

**Internal Methods** (Helper logic):
```csharp
private ConfidenceLevel CalculateConfidenceLevel(int sampleSize)
{
    return sampleSize switch
    {
        >= 10 => ConfidenceLevel.High,
        >= 3 => ConfidenceLevel.Medium,
        _ => ConfidenceLevel.Low
    };
}

private TimeSpan CalculateAverageDuration(List<PrintJobStatistics> stats)
{
    if (stats.Count == 0) return TimeSpan.Zero;
    var avgMs = stats
        .Where(s => s.IsSuccess && s.ActualDurationMs.HasValue)
        .Average(s => s.ActualDurationMs.Value);
    return TimeSpan.FromMilliseconds(avgMs);
}

private double CalculateVariance(List<PrintJobStatistics> stats, TimeSpan average)
{
    if (stats.Count <= 1) return 0;
    var variance = stats
        .Where(s => s.IsSuccess && s.ActualDurationMs.HasValue)
        .Average(s => Math.Pow(s.ActualDurationMs.Value - average.TotalMilliseconds, 2));
    return Math.Sqrt(variance);
}

private double CalculateVariancePercent(ConfidenceLevel confidence)
{
    return confidence switch
    {
        ConfidenceLevel.High => 10,
        ConfidenceLevel.Medium => 20,
        _ => 50
    };
}
```

### Repository Architecture

**`IPrintJobStatisticsRepository`** Interface
```csharp
public interface IPrintJobStatisticsRepository : IRepository<PrintJobStatistics>
{
    Task<PrintJobStatistics?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default);
    
    Task<List<PrintJobStatistics>> GetByModelAndMaterialAsync(
        Guid? modelId,
        string? material,
        int limit = 100,
        CancellationToken cancellationToken = default);
    
    Task<List<PrintJobStatistics>> GetSuccessfulJobsAsync(
        DateTime? fromDate = null,
        int limit = 1000,
        CancellationToken cancellationToken = default);
}
```

**`EfPrintJobStatisticsRepository`** Implementation
- Use EF Core `IQueryable` for efficient filtering
- Index on (PrinterModelId, Material, IsSuccess) for prediction queries
- Index on CompletedAtUtc for time-range queries

---

## 🎯 API Endpoints

### Prediction Endpoints

**GET** `/api/jobs/{jobId}/completion-prediction`
- **Purpose**: Get predicted completion time for a job
- **Response**: `CompletionPredictionDto`
- **Status Codes**:
  - `200 OK` - Prediction returned
  - `404 Not Found` - Job not found
  - `400 Bad Request` - Cannot predict (not enough data)

**GET** `/api/jobs/{jobId}/statistics`
- **Purpose**: Get recorded statistics for a completed job
- **Response**: `PrintJobStatisticsDto`
- **Status Codes**:
  - `200 OK` - Statistics found
  - `404 Not Found` - Job not found

### Analytics Endpoints

**GET** `/api/predictions/stats/by-material`
- **Purpose**: Get duration statistics by material type
- **Query Parameters**:
  - `printerId` (optional) - Filter to specific printer
  - `material` (optional) - Filter to specific material
- **Response**: `Dictionary<string, DurationStatsDto>`
- **Status Codes**: `200 OK`

**GET** `/api/predictions/stats/by-model`
- **Purpose**: Get duration statistics by printer model
- **Query Parameters**:
  - `modelId` (optional) - Filter to specific model
- **Response**: `Dictionary<string, DurationStatsDto>`

### Admin Endpoints

**POST** `/api/predictions/record-completion`
- **Purpose**: Record job completion for learning
- **Request**: 
  ```json
  {
    "jobId": "uuid",
    "actualDurationMs": 3600000,
    "isSuccess": true,
    "failureReason": null
  }
  ```
- **Status Codes**:
  - `200 OK` - Recorded successfully
  - `404 Not Found` - Job not found

---

## 🎨 Frontend Components

### `CompletionPredictionCard.tsx`

**Location**: `src/Web/ReactApp/src/components/jobs/CompletionPredictionCard.tsx`

**Features**:
- Display estimated completion time
- Show confidence level with visual indicator (color-coded)
- Show sample size and variance percentage
- Loading skeleton while fetching
- Error handling for missing data

**Design System Usage**:
- Use CONTROLS_GUIDE.md button styles (`btn-primary`, `btn-secondary`)
- Use status badge styles for confidence level
- Use alert styles for warnings ("Not enough data")
- Color-coded confidence: 🟢 High, 🟡 Medium, 🔴 Low

**Props**:
```typescript
interface CompletionPredictionCardProps {
  jobId: string;
  onRefresh?: () => void;
  compact?: boolean;  // Minimal display for queue list
}
```

### `JobStatisticsPanel.tsx`

**Location**: `src/Web/ReactApp/src/components/analytics/JobStatisticsPanel.tsx`

**Features**:
- Display material statistics (avg duration, min/max, success rate)
- Display printer model statistics
- Show trend charts (optional: line chart of actual vs estimated)
- Filter by material, model, date range

**Design System Usage**:
- Card layout with section headers
- Table styling from CONTROLS_GUIDE.md
- Form inputs for filters

### `useCompletionPrediction.ts` Hook

**Location**: `src/Web/ReactApp/src/hooks/useCompletionPrediction.ts`

**Features**:
- React Query hook for prediction data
- Automatic refetch on job status changes
- Error boundary integration
- Caching strategy: staleTime 60s, cacheTime 5m

**Methods**:
```typescript
function useCompletionPrediction(jobId: string) {
  return useQuery({
    queryKey: ['prediction', jobId],
    queryFn: () => predictionService.getPrediction(jobId),
    staleTime: 60_000,
    cacheTime: 5 * 60_000,
    enabled: !!jobId,
  });
}
```

### `predictionService.ts` API Client

**Location**: `src/Web/ReactApp/src/services/predictionService.ts`

**Methods**:
```typescript
export const predictionService = {
  getPrediction: (jobId: string) => 
    api.get<CompletionPredictionDto>(`/jobs/${jobId}/completion-prediction`),
  
  getStatistics: (jobId: string) =>
    api.get<PrintJobStatisticsDto>(`/jobs/${jobId}/statistics`),
  
  getMaterialStats: (printerId?: string, material?: string) =>
    api.get<Record<string, DurationStatsDto>>('/predictions/stats/by-material', {
      params: { printerId, material }
    }),
  
  getModelStats: (modelId?: string) =>
    api.get<Record<string, DurationStatsDto>>('/predictions/stats/by-model', {
      params: { modelId }
    }),
};
```

---

## 📝 Implementation Checklist

### Phase 4.2 Tasks

**Day 1: Backend Implementation**
- [ ] Create `PrintJobStatistics` entity in `Domain/Entities.cs`
- [ ] Add `PrintJob.Statistics` navigation property
- [ ] Create EF Core DbContext configuration for relationships
- [ ] Create `IPrintJobStatisticsRepository` interface
- [ ] Create `EfPrintJobStatisticsRepository` implementation
- [ ] Create `PredictionService` with all methods
- [ ] Create DTOs: `CompletionPredictionDto`, `DurationStatsDto`, `PrintJobStatisticsDto`
- [ ] Create `PredictionController` with endpoints (GET/POST)
- [ ] Register service in `Program.cs` dependency injection
- [ ] ✅ Build success - 0 errors

**Day 2: Frontend & Integration**
- [ ] Create `CompletionPredictionCard.tsx` component
- [ ] Create `JobStatisticsPanel.tsx` component
- [ ] Create `useCompletionPrediction.ts` React Query hook
- [ ] Create `predictionService.ts` API client
- [ ] Create TypeScript types: `CompletionPredictionDto`, etc.
- [ ] Integrate `CompletionPredictionCard` into job detail view
- [ ] Integrate `JobStatisticsPanel` into analytics page
- [ ] Add design system compliance (CONTROLS_GUIDE.md)
- [ ] ✅ React build success - 0 errors
- [ ] ✅ All tests passing
- [ ] 📝 Update documentation

---

## 🚀 Getting Started

### Step 1: Review Architecture
Read through this kickoff document to understand:
- Database schema (PrintJobStatistics table)
- Service design (PredictionService)
- API endpoints (GET /jobs/{id}/completion-prediction)
- Frontend components (CompletionPredictionCard)

### Step 2: Implement Backend
1. Create domain model and DbContext configuration
2. Implement repository pattern
3. Build PredictionService with statistical logic
4. Create API controller endpoints
5. Register in dependency injection

### Step 3: Verify Backend
```bash
cd ./src
dotnet build ./farm-web.sln -c Release
dotnet test ./farm-web.sln -c Debug
```

### Step 4: Implement Frontend
1. Create React components
2. Create API service client
3. Create React Query hooks
4. Integrate into existing pages
5. Apply design system styling

### Step 5: Verify Everything
```bash
cd ./src/Web/ReactApp
npm run build
npm run test:run
```

### Step 6: Manual Testing
- [ ] Schedule a job
- [ ] After completion, verify statistics recorded
- [ ] Check prediction API returns data
- [ ] Verify confidence level calculated correctly
- [ ] Test with different materials
- [ ] Test with multiple jobs (build sample size)

---

## 📚 Key Concepts

### Confidence Levels

- **High** (±10%): 10+ similar historical jobs
  - Very reliable estimate
  - Show prominently to users

- **Medium** (±20%): 3-9 similar historical jobs
  - Reasonable estimate
  - Warn user of variance range

- **Low** (±50%): 1-2 similar historical jobs
  - Very rough estimate
  - Show with caution, encourage more data

### Statistical Filtering

**Similar Job Criteria**:
- Same printer model
- Same material type
- Successful completion only (exclude failures)
- Within last 6 months (configurable)

**Not Considered** (yet):
- Nozzle size differences
- Infill percentage
- Layer height
- Print speed variations
- Enclosure temperature

### Learning Mechanism

1. Job starts → Show prediction based on historical average
2. Job completes → Record actual duration
3. Next similar job → Prediction improves with larger sample size
4. System becomes more accurate over time

---

## ⚠️ Important Notes

### Design System Compliance

Use **CONTROLS_GUIDE.md** for all UI components:
- Buttons: `btn-primary`, `btn-secondary`, etc.
- Form inputs: `input-base`, `input-invalid`
- Status badges: Color-coded confidence levels
- Cards: `card-base`, `card-header`, `card-footer`
- Alerts: `alert-base`, `alert-warning` for low confidence

### No Migrations

Use `EnsureCreated()` pattern:
- Add entity configuration to `DbContext`
- Configure relationships and indexes
- Database schema created automatically on startup
- No migration files needed

### Database Access

- Always use `IRepository<T>` pattern
- Implement specific methods in repository interface
- Use `IQueryable` for efficient filtering
- Index on (ModelId, Material, IsSuccess) for performance

---

## 📞 Questions & Decisions

**Q: Should we track failed jobs in statistics?**
A: Record them but filter out from predictions. Failed jobs teach us about reliability.

**Q: How far back should we look for historical data?**
A: 6 months default, configurable. Older data less relevant due to calibration drift.

**Q: What if printer model changes?**
A: Group by PrinterModel entity, not hardware serial. Model captures behavioral characteristics.

**Q: Handle material variants (e.g., "Prusament PLA" vs generic PLA)?**
A: Store full material string, but aggregate similar ones (TBD with Material thesaurus).

---

## ✅ Success Criteria

Phase 4.2 is complete when:

✅ **Backend**:
- PredictionService calculates completion time with confidence level
- Statistics stored in `PrintJobStatistics` table
- Repository queries for similar historical jobs
- API endpoints return correct predictions
- `dotnet build` succeeds with 0 errors

✅ **Frontend**:
- `CompletionPredictionCard` displays in job detail view
- Confidence level shown with visual indicator
- `JobStatisticsPanel` shows material/model analytics
- Design system compliant (CONTROLS_GUIDE.md)
- `npm run build` succeeds with 0 errors

✅ **Quality**:
- All tests passing
- Error handling for missing data
- Loading states handled properly
- Design system compliance verified

✅ **Documentation**:
- Implementation summary updated
- API documentation complete
- README updated with feature overview

---

## 📅 Timeline

**Day 1** (January 13):
- Morning: Backend implementation (entities, service, controller)
- Afternoon: Build verification, debugging

**Day 2** (January 14):
- Morning: Frontend components and integration
- Afternoon: Design system compliance, testing, documentation

**Ready for Phase 4.3** (January 15): Notification System

---

## 🎓 Learning Resources

- [Statistics in C#](https://docs.microsoft.com/dotnet/api/system.collections.generic.list-1)
- [Entity Framework Core Relationships](https://docs.microsoft.com/ef/core/modeling/relationships)
- [React Query Documentation](https://tanstack.com/query/latest)
- [PrintFarmer CONTROLS_GUIDE.md](./docs/CONTROLS_GUIDE.md) - UI components
- [DEVELOPMENT.md](./docs/DEVELOPMENT.md) - Dev guidelines

---

**Status**: 🚀 Ready to implement  
**Next**: Update kickoff then begin Phase 4.2 implementation  
**Questions?** Review architecture section or check conversation history

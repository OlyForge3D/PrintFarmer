# Implementation Plan: Auto-Dispatch & Business Analytics

**Author:** Dallas (Lead/Architect)
**Date:** 2026-03-06
**Status:** APPROVED — ready for assignment
**Input:** Brett's competitive analysis (`brett-competitive-analysis-top3.md`)

---

## Preamble: What We Already Have

Before designing anything, I reviewed the existing codebase. We're in better shape than expected.

**Job Queue Infrastructure (already built):**
- `PrintJob` entity with `RequiredNozzleDiameter`, `RequiredMaterialType`, `RequiredCapabilities`, `PreferredPrinterIds`, `ExcludedPrinterIds`, `Priority`, `QueuePosition`
- `GcodeFile` entity with `RequiredNozzleDiameter`, `RequiredMaterial`, `PrinterModelId`, `EstimatedPrintTimeMinutes`, `EstimatedFilamentWeightG`, print temperatures
- `IQueueRepository` with `GetAvailablePrintersAsync()` and `GetCompatiblePrintersAsync()`
- `AutoPrintEnabled` / `AutoPrintState` fields on Printer (partially wired)
- `PrintJobCompletionService` already watches for job completion and triggers next actions

**Printer Capability Data (already built):**
- Printer entity: build volume (X/Y/Z), `MaxBedTemp`, `MaxPrintSpeed`, `HasHeatedBed`, `HasEnclosure`, `HasAutoLeveling`, `MultiMaterial`, `IsAvailable`, `InMaintenance`, `CurrentMaterial`, `CurrentSpoolId`
- Toolhead entity: `NozzleModelId` (with diameter, type, material), `SupportedMaterials` array
- PrinterModel: `SupportedFilamentTypes` collection, full hardware specs
- FilamentType: `IsAbrasive` (requires hardened nozzle), `NeedsEnclosure`
- Backend capabilities: full `ISupports*` interface matrix per backend

**Statistics Infrastructure (already built):**
- `StatisticsController` with 5 endpoints (summary, jobs-over-time, cost-over-time, filament-by-material, printer-utilization)
- `PrintJobStatistics` entity (one-to-one with PrintJob): duration, cost, material, temperatures, success/failure
- `PrinterStatistics` entity: cumulative hours, jobs, filament per printer
- `IPrintCostCalculator` service (cost estimation exists)
- Frontend statistics page with 8 KPI cards and 4 charts (Recharts)
- Spoolman integration for filament pricing data

**The takeaway:** We're not building from scratch. For auto-dispatch, we need a **scoring engine and dispatch service**. For analytics, we need **deeper aggregation, idle time tracking, and export capability**. The data model is largely in place.

---

# Feature #2: Intelligent Job Auto-Dispatch

## Architecture Design

### Where It Fits

```
┌─────────────────────────────────────────────────────────────────┐
│ API Layer                                                       │
│  JobQueueController ──► new endpoints:                          │
│    POST /api/job-queue/{id}/dispatch (manual trigger)            │
│    GET  /api/job-queue/{id}/candidates (scored printer list)     │
│    POST /api/job-queue/dispatch-settings (configure rules)       │
│    GET  /api/job-queue/dispatch-settings                         │
│  PrinterHub ──► new events:                                     │
│    JobDispatched, DispatchFailed, PrinterBecameIdle              │
├─────────────────────────────────────────────────────────────────┤
│ Service Layer (src/infra/Services/Queue/)                       │
│  IJobDispatchService ──► orchestrator                           │
│    - FindCandidates(jobId) → scored list                        │
│    - DispatchJob(jobId, printerId?) → assign + start            │
│    - DispatchNextForPrinter(printerId) → auto on idle           │
│  IDispatchScorer ──► scoring algorithm                          │
│    - ScorePrinterForJob(printer, job, gcode) → DispatchScore    │
│  IDispatchRuleEngine ──► configurable rules                     │
│    - material match, nozzle match, build volume, enclosure...   │
│  JobDispatchBackgroundService ──► watches for idle printers     │
│    - Subscribes to printer status changes                       │
│    - On idle + auto-dispatch enabled → dispatch next job        │
├─────────────────────────────────────────────────────────────────┤
│ Data Layer (src/infra/)                                         │
│  Existing: PrintJob, Printer, GcodeFile, Toolhead, FilamentType │
│  New: DispatchSettings entity (per-farm config)                 │
│  New: DispatchLog entity (audit trail of dispatch decisions)     │
│  Extended: PrintJob.DispatchedAt, PrintJob.DispatchScore        │
└─────────────────────────────────────────────────────────────────┘
```

### New Entities/Models

**DispatchSettings** (singleton, farm-wide config):
```
Id: Guid
AutoDispatchEnabled: bool (default: false — opt-in)
DispatchMode: enum (Manual=0, Suggest=1, Auto=2)
  - Manual: operator assigns manually (current behavior)
  - Suggest: system scores & suggests, operator confirms
  - Auto: system assigns automatically when printer idles
MaterialMatchWeight: int (default: 100 — hard requirement)
NozzleMatchWeight: int (default: 100 — hard requirement)
BuildVolumeWeight: int (default: 50)
QueueDepthWeight: int (default: 30)
PreferredPrinterWeight: int (default: 40)
EnclosureWeight: int (default: 80)
PrinterModelMatchWeight: int (default: 60)
RequireExactModelMatch: bool (default: false)
MaxConcurrentDispatches: int (default: 1)
IdleThresholdSeconds: int (default: 30 — how long idle before dispatch)
```

**DispatchLog** (audit trail):
```
Id: Guid
PrintJobId: Guid (FK)
PrinterId: Guid (FK)
Action: enum (Suggested, Dispatched, Rejected, Failed)
Score: double
ScoreBreakdown: string (JSON — individual factor scores)
Reason: string? (rejection/failure reason)
CreatedAtUtc: DateTime
```

**PrintJob extensions** (add to existing entity):
```
DispatchedAt: DateTime? (when auto-dispatched)
DispatchScore: double? (score of the winning printer)
DispatchMode: enum? (how it was dispatched: Manual, Suggested, Auto)
```

### Scoring Algorithm Design

The dispatch scorer evaluates each candidate printer against a job. Each factor produces a score 0–100, weighted by configuration. Hard requirements (score=0) eliminate the printer entirely.

```
Score = Σ(factor_score × factor_weight) / Σ(factor_weights)

Factors:
1. Material Match (weight: 100, HARD)
   - Printer's loaded filament matches job requirement → 100
   - Printer's toolhead supports the material but different spool loaded → 50
   - PrinterModel supports material → 25
   - No match → 0 (ELIMINATE)

2. Nozzle Diameter Match (weight: 100, HARD)
   - Exact match within ±0.01mm → 100
   - No compatible toolhead → 0 (ELIMINATE)

3. Build Volume Fit (weight: 50)
   - Job's gcode fits within printer's build volume → 100
   - Cannot determine (no gcode dimensions) → 80 (assume fits)
   - Does not fit → 0 (ELIMINATE)

4. Enclosure Requirement (weight: 80, HARD if material needs it)
   - Material.NeedsEnclosure && printer.HasEnclosure → 100
   - Material.NeedsEnclosure && !printer.HasEnclosure → 0 (ELIMINATE)
   - !Material.NeedsEnclosure → 100

5. Nozzle Hardness (weight: 80, HARD if material is abrasive)
   - Material.IsAbrasive && nozzle.IsHardened → 100
   - Material.IsAbrasive && !nozzle.IsHardened → 0 (ELIMINATE)
   - !Material.IsAbrasive → 100

6. Printer Model Match (weight: 60)
   - GcodeFile sliced for this exact PrinterModel → 100
   - GcodeFile sliced for same manufacturer → 50
   - No model data in gcode → 70 (neutral)
   - Different manufacturer → 30

7. Queue Depth (weight: 30)
   - No queued jobs → 100
   - 1-2 queued → 70
   - 3-5 queued → 40
   - 6+ queued → 10

8. Preferred Printer (weight: 40)
   - In PreferredPrinterIds → 100
   - Not in PreferredPrinterIds (but list exists) → 30
   - No preference list → 70 (neutral)
   - In ExcludedPrinterIds → 0 (ELIMINATE)

9. Printer Availability (HARD — pre-filter, not scored)
   - IsAvailable=true, InMaintenance=false, IsEnabled=true
   - IsOnline=true, State != "printing"/"error"
   - Backend supports ISupportsFileUpload + ISupportsStartPrint
```

### Database Schema

New table: `DispatchSettings`
```sql
CREATE TABLE DispatchSettings (
    Id TEXT PRIMARY KEY,
    AutoDispatchEnabled INTEGER NOT NULL DEFAULT 0,
    DispatchMode INTEGER NOT NULL DEFAULT 0,
    MaterialMatchWeight INTEGER NOT NULL DEFAULT 100,
    NozzleMatchWeight INTEGER NOT NULL DEFAULT 100,
    BuildVolumeWeight INTEGER NOT NULL DEFAULT 50,
    QueueDepthWeight INTEGER NOT NULL DEFAULT 30,
    PreferredPrinterWeight INTEGER NOT NULL DEFAULT 40,
    EnclosureWeight INTEGER NOT NULL DEFAULT 80,
    PrinterModelMatchWeight INTEGER NOT NULL DEFAULT 60,
    RequireExactModelMatch INTEGER NOT NULL DEFAULT 0,
    MaxConcurrentDispatches INTEGER NOT NULL DEFAULT 1,
    IdleThresholdSeconds INTEGER NOT NULL DEFAULT 30,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
```

New table: `DispatchLogs`
```sql
CREATE TABLE DispatchLogs (
    Id TEXT PRIMARY KEY,
    PrintJobId TEXT NOT NULL REFERENCES PrintJobs(Id) ON DELETE CASCADE,
    PrinterId TEXT NOT NULL REFERENCES Printers(Id) ON DELETE CASCADE,
    Action INTEGER NOT NULL,
    Score REAL NOT NULL,
    ScoreBreakdown TEXT,
    Reason TEXT,
    CreatedAtUtc TEXT NOT NULL
);
CREATE INDEX IX_DispatchLogs_PrintJobId ON DispatchLogs(PrintJobId);
CREATE INDEX IX_DispatchLogs_PrinterId ON DispatchLogs(PrinterId);
CREATE INDEX IX_DispatchLogs_CreatedAtUtc ON DispatchLogs(CreatedAtUtc);
```

Alter `PrintJobs`:
```sql
ALTER TABLE PrintJobs ADD COLUMN DispatchedAt TEXT;
ALTER TABLE PrintJobs ADD COLUMN DispatchScore REAL;
ALTER TABLE PrintJobs ADD COLUMN DispatchMode INTEGER;
```

---

## Implementation Phases

### Phase 1 — MVP: Scored Suggestions (Sprint 1-2)

**User value:** Operator clicks "Find Best Printer" on a queued job and gets a ranked list of compatible printers with scores. Operator picks one and confirms. Eliminates the mental matching work.

**Backend (Lambert):**
1. Create `DispatchScore` record and `IDispatchScorer` interface in `src/infra/Services/Queue/Dispatch/`
2. Implement `DispatchScorer` with factors 1-6, 8-9 from the algorithm above (skip queue depth for MVP)
3. Add `IJobDispatchService` with `FindCandidates(jobId)` method
4. Add new endpoints to `JobQueueController`:
   - `GET /api/job-queue/{id}/candidates` → returns `List<DispatchCandidateDto>` (printerId, printerName, score, scoreBreakdown, eliminationReasons)
   - `POST /api/job-queue/{id}/dispatch` with body `{ printerId: Guid }` → assigns job to printer and starts print
5. Add `DispatchLog` entity and EF configuration
6. Create EF migrations for both SQLite and PostgreSQL

**Frontend (Ripley):**
1. Add "Find Best Printer" button to job queue item (in existing queue page)
2. Create `DispatchCandidatesModal` — shows ranked printer list with scores, compatibility badges (✅ material, ✅ nozzle, ⚠️ model mismatch, etc.)
3. Each candidate row has a "Dispatch" button that calls the dispatch endpoint
4. Show score breakdown on hover/expand (material: 100, nozzle: 100, model: 50, etc.)

**Testing (Kane):**
1. Unit tests for `DispatchScorer`:
   - `ScorePrinter_ExactMaterialMatch_Returns100`
   - `ScorePrinter_WrongMaterial_Eliminates`
   - `ScorePrinter_NozzleTooSmall_Eliminates`
   - `ScorePrinter_AbrasiveMaterial_BrassNozzle_Eliminates`
   - `ScorePrinter_EnclosureRequired_NoEnclosure_Eliminates`
   - `ScorePrinter_PreferredPrinter_ScoresHigher`
   - `ScorePrinter_ExcludedPrinter_Eliminates`
   - `ScorePrinter_BuildVolumeExceeded_Eliminates`
   - `ScorePrinter_AllFactorsPass_CalculatesWeightedScore`
2. Integration tests for dispatch endpoints:
   - `GetCandidates_ReturnsRankedList`
   - `GetCandidates_NoPrintersAvailable_ReturnsEmpty`
   - `DispatchJob_ValidPrinter_AssignsAndStarts`
   - `DispatchJob_PrinterBusy_Returns409`
3. Edge cases:
   - Job with no material requirement (should match any printer)
   - Job with no gcode file (history-seeded jobs)
   - Printer with no toolheads configured
   - Multiple toolheads — pick best match

### Phase 2 — Auto-Dispatch on Idle (Sprint 3-4)

**User value:** When a printer finishes a job and goes idle, the system automatically dispatches the next compatible job from the queue. Operator sets it and walks away. This is the "4x productivity" feature.

**Backend (Lambert):**
1. Create `DispatchSettings` entity, EF config, and seeding (default settings)
2. Add `DispatchSettingsController` or extend `JobQueueController`:
   - `GET /api/dispatch-settings`
   - `PUT /api/dispatch-settings`
3. Create `JobDispatchBackgroundService` (IHostedService):
   - Subscribe to printer status changes (hook into existing `PrinterStatusService` or `PrintJobCompletionService`)
   - When printer transitions to idle state AND `AutoDispatchEnabled=true`:
     - Wait `IdleThresholdSeconds`
     - Query queued jobs sorted by Priority → QueuePosition
     - Score each job against the now-idle printer
     - If best score > minimum threshold → dispatch
     - Log decision to `DispatchLog`
   - Respect `MaxConcurrentDispatches` (don't overwhelm operator)
4. Add SignalR events through existing `PrinterHub`:
   - `JobAutoDispatched` (jobId, printerId, score)
   - `DispatchFailed` (jobId, reason)
5. Wire auto-dispatch into `PrintJobCompletionService` — after a job completes, check if same printer should receive next job
6. Add queue depth scoring (factor 7) now that auto-dispatch creates contention

**Frontend (Ripley):**
1. Create `DispatchSettingsPanel` component (settings page or queue page sidebar):
   - Toggle: Auto-dispatch on/off
   - Mode selector: Manual / Suggest / Auto
   - Slider controls for scoring weights (advanced, collapsed by default)
   - Idle threshold input
2. Add real-time dispatch notifications:
   - Toast: "Job 'benchy.gcode' auto-dispatched to Printer 3 (score: 92)"
   - Queue item shows dispatch status badge
3. Add dispatch history view (expandable panel showing DispatchLog entries)
4. Per-printer toggle: "Include in auto-dispatch" (maps to existing `IsAvailable` or new field)

**Testing (Kane):**
1. `JobDispatchBackgroundService` tests:
   - `OnPrinterIdle_AutoEnabled_DispatchesTopJob`
   - `OnPrinterIdle_AutoDisabled_DoesNothing`
   - `OnPrinterIdle_NoQueuedJobs_DoesNothing`
   - `OnPrinterIdle_NoCompatibleJobs_LogsAndSkips`
   - `OnPrinterIdle_MaxConcurrentReached_Waits`
   - `OnPrinterIdle_IdleThresholdNotMet_DoesNotDispatch`
2. Integration tests:
   - `AutoDispatch_EndToEnd_PrinterIdleTriggersJobStart`
   - `AutoDispatch_MultipleIdlePrinters_EachGetsJob`
   - `AutoDispatch_PreferredPrinter_GetsHigherPriority`
3. Race condition tests:
   - Two printers go idle simultaneously — same job shouldn't be assigned twice
   - Job cancelled while dispatch in progress
   - Printer goes offline during dispatch

### Phase 3 — Batch Dispatch & Load Balancing (Sprint 5-6)

**User value:** Operator queues 20 copies of a part and the system distributes them across 5 compatible printers (4 each). Load balancing maximizes throughput across the fleet.

**Backend (Lambert):**
1. Extend dispatch service with `DispatchBatch(jobId, printerIds[])` — split multi-copy job across printers
2. Add "scatter" dispatch mode: given N copies and M printers, create child jobs (N/M copies each) and dispatch
3. Queue priority system: high/normal/low with preemption rules (high priority job can bump a queued-but-not-started normal job)
4. Load balancing: factor in estimated completion time to balance finish times across printers
5. Dashboard API: `GET /api/dispatch/overview` — fleet-wide dispatch status (printers idle/busy, queue depth, estimated clear time)

**Frontend (Ripley):**
1. Batch dispatch UI: "Distribute across printers" button on multi-copy jobs
2. Dispatch overview dashboard widget: fleet utilization gauge, estimated queue clear time
3. Drag-and-drop job reordering in queue with real-time score recalculation

**Testing (Kane):**
1. `DispatchBatch_20Copies_5Printers_Distributes4Each`
2. `DispatchBatch_OddCopies_DistributesEvenly`
3. `PriorityPreemption_HighPriority_BumpsNormalInQueue`
4. `LoadBalancing_EstimatedTime_BalancesAcrossPrinters`

---

## Dependencies & Risks

**Dependencies:**
- Phase 1 has no blockers — can start immediately
- Phase 2 depends on Phase 1 (scoring engine must exist)
- Phase 3 depends on Phase 2 (auto-dispatch must work before batch)
- No dependency on Feature #3 (analytics) — these are independent

**Risks:**
1. **Stale printer status:** If printer status polling is slow, we might dispatch to a printer that just went offline. **Mitigation:** Check status immediately before dispatch (call `ISupportsStatus` directly), implement retry with fallback to next candidate.
2. **Race conditions in auto-dispatch:** Two printers idle at the same time, both try to claim the same job. **Mitigation:** Use optimistic concurrency on `PrintJob.Status` — first to update wins, second gets concurrency exception and retries with next job.
3. **Incomplete gcode metadata:** If the gcode file wasn't parsed (no material, no nozzle requirement), scoring degrades. **Mitigation:** Default to "neutral" scores (70) for unknown factors. Log a warning suggesting the operator add metadata.
4. **Spoolman unavailable:** If Spoolman is down, we can't verify loaded filament. **Mitigation:** Fall back to `Printer.CurrentMaterial` field (denormalized). Warn in dispatch log.

---

# Feature #3: Business Analytics Dashboard

## Architecture Design

### Where It Fits

```
┌─────────────────────────────────────────────────────────────────┐
│ API Layer                                                       │
│  StatisticsController ──► extended with new endpoints:          │
│    GET /api/statistics/cost-breakdown                            │
│    GET /api/statistics/printer-comparison                        │
│    GET /api/statistics/material-costs                            │
│    GET /api/statistics/utilization-timeline                      │
│    GET /api/statistics/failure-analysis                          │
│    GET /api/statistics/export/csv                                │
│    GET /api/statistics/export/pdf                                │
│  New: AnalyticsController (if StatisticsController gets bloated)│
├─────────────────────────────────────────────────────────────────┤
│ Service Layer (src/infra/Services/Statistics/)                   │
│  Extended: IStatisticsService                                   │
│    - GetCostBreakdownAsync(days, printerId?)                    │
│    - GetPrinterComparisonAsync(days)                            │
│    - GetMaterialCostAnalysisAsync(days)                         │
│    - GetUtilizationTimelineAsync(days, printerId?)              │
│    - GetFailureAnalysisAsync(days, printerId?)                  │
│  New: IAnalyticsExportService                                   │
│    - ExportCsvAsync(reportType, dateRange)                      │
│    - ExportPdfAsync(reportType, dateRange)                      │
│  New: IPrinterUptimeTracker (background service)                │
│    - Records periodic online/offline status snapshots           │
├─────────────────────────────────────────────────────────────────┤
│ Data Layer (src/infra/)                                         │
│  Existing: PrintJob, PrintJobStatistics, PrinterStatistics      │
│  New: PrinterUptimeSnapshot entity (time-series uptime data)    │
│  New: CostConfiguration entity (electricity, depreciation)      │
│  Extended: PrinterStatistics with utilization % fields          │
└─────────────────────────────────────────────────────────────────┘
```

### New Entities/Models

**PrinterUptimeSnapshot** (time-series data for utilization tracking):
```
Id: Guid
PrinterId: Guid (FK)
Timestamp: DateTime (UTC)
IsOnline: bool
State: string? ("idle", "printing", "error", "maintenance")
Duration: TimeSpan (time in this state since last snapshot)
```
*Captured every 5 minutes by background service. Enables accurate utilization % calculation.*

**CostConfiguration** (farm-wide cost settings):
```
Id: Guid
ElectricityCostPerKwh: decimal (default: 0.12)
DefaultPrinterWattage: int (default: 200)
DepreciationEnabled: bool (default: false)
DefaultPrinterCostUsd: decimal? (purchase price)
DefaultDepreciationYears: int? (default: 3)
LaborCostPerHour: decimal? (optional)
MarkupPercentage: decimal? (for pricing suggestions)
CreatedAtUtc: DateTime
UpdatedAtUtc: DateTime
```

**Per-printer cost overrides** (extend existing Printer entity):
```
PurchaseCostUsd: decimal? (this specific printer's cost)
WattageOverride: int? (this printer's power draw)
DepreciationYears: int? (override farm default)
```

### Key Analytics Calculations

**Cost-Per-Print Breakdown:**
```
MaterialCost = ActualFilamentUsage(g) × (SpoolPrice / SpoolWeight)
ElectricityCost = ActualPrintTime(h) × PrinterWattage(kW) × ElectricityCostPerKwh
DepreciationCost = (PrinterCost / (DepreciationYears × 365 × 24)) × ActualPrintTime(h)
LaborCost = (optional) ManualTime(h) × LaborCostPerHour
TotalCost = MaterialCost + ElectricityCost + DepreciationCost + LaborCost
```

**Printer Utilization %:**
```
UtilizationPct = (PrintingHours / AvailableHours) × 100
AvailableHours = TotalHours - MaintenanceHours - OfflineHours
IdlePct = (IdleHours / AvailableHours) × 100
EfficiencyScore = (SuccessfulPrintHours / TotalPrintHours) × 100
```

**Material Waste Analysis:**
```
WasteRate = FailedJobFilament / TotalFilamentUsed × 100
WasteCost = Σ(FailedJob.ActualCost)
MaterialEfficiency = SuccessfulFilament / TotalFilamentUsed × 100
```

### Database Schema

New table: `PrinterUptimeSnapshots`
```sql
CREATE TABLE PrinterUptimeSnapshots (
    Id TEXT PRIMARY KEY,
    PrinterId TEXT NOT NULL REFERENCES Printers(Id) ON DELETE CASCADE,
    Timestamp TEXT NOT NULL,
    IsOnline INTEGER NOT NULL,
    State TEXT,
    DurationTicks INTEGER NOT NULL,
    CONSTRAINT FK_PrinterUptimeSnapshots_Printers FOREIGN KEY (PrinterId)
        REFERENCES Printers(Id) ON DELETE CASCADE
);
CREATE INDEX IX_UptimeSnapshots_PrinterId_Timestamp
    ON PrinterUptimeSnapshots(PrinterId, Timestamp);
CREATE INDEX IX_UptimeSnapshots_Timestamp
    ON PrinterUptimeSnapshots(Timestamp);
```

New table: `CostConfiguration`
```sql
CREATE TABLE CostConfiguration (
    Id TEXT PRIMARY KEY,
    ElectricityCostPerKwh REAL NOT NULL DEFAULT 0.12,
    DefaultPrinterWattage INTEGER NOT NULL DEFAULT 200,
    DepreciationEnabled INTEGER NOT NULL DEFAULT 0,
    DefaultPrinterCostUsd REAL,
    DefaultDepreciationYears INTEGER DEFAULT 3,
    LaborCostPerHour REAL,
    MarkupPercentage REAL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
```

Alter `Printers`:
```sql
ALTER TABLE Printers ADD COLUMN PurchaseCostUsd REAL;
ALTER TABLE Printers ADD COLUMN WattageOverride INTEGER;
ALTER TABLE Printers ADD COLUMN DepreciationYears INTEGER;
```

---

## Implementation Phases

### Phase 1 — MVP: Enhanced Cost Analysis & Export (Sprint 1-2)

**User value:** Operator sees true cost-per-print (material + electricity + depreciation), can compare printer productivity, and export data to CSV for accounting.

**Backend (Lambert):**
1. Create `CostConfiguration` entity, EF config, seed with defaults
2. Add `CostConfigurationController`:
   - `GET /api/cost-configuration`
   - `PUT /api/cost-configuration`
3. Extend `StatisticsService` with new methods:
   - `GetCostBreakdownAsync(days, printerId?)` → returns per-job cost breakdown (material, electricity, depreciation)
   - `GetPrinterComparisonAsync(days)` → side-by-side: total cost, avg cost/print, success rate, total hours, jobs per printer
   - `GetMaterialCostAnalysisAsync(days)` → cost by material type, waste cost, efficiency %
4. Extend `StatisticsController` with new endpoints:
   - `GET /api/statistics/cost-breakdown?days=30&printerId=`
   - `GET /api/statistics/printer-comparison?days=30`
   - `GET /api/statistics/material-costs?days=30`
5. Create `IAnalyticsExportService` with CSV export:
   - `GET /api/statistics/export/csv?report=cost-breakdown&days=30` → downloads CSV file
   - `GET /api/statistics/export/csv?report=printer-comparison&days=30`
   - `GET /api/statistics/export/csv?report=job-history&days=30`
6. Enhance existing `IPrintCostCalculator` to use `CostConfiguration` for electricity and depreciation
7. Create EF migrations for both SQLite and PostgreSQL

**Frontend (Ripley):**
1. Create new `AnalyticsDashboard` page (add to routing alongside existing StatisticsPage):
   - Route: `/analytics`
   - Tab-based layout: Overview | Cost Analysis | Printers | Materials
2. **Overview tab:** Enhanced KPI cards with new metrics:
   - Avg Cost Per Print (total cost / completed jobs)
   - Material Waste Rate (%)
   - Fleet Utilization (% — placeholder until Phase 2)
   - Total Revenue (if markup configured)
3. **Cost Analysis tab:**
   - Cost breakdown chart (stacked bar: material vs electricity vs depreciation)
   - Cost-over-time trend (line chart, already exists — enhance with breakdown)
   - Per-job cost table with sort/filter
4. **Printers tab:**
   - Printer comparison table: name, total jobs, success rate, avg cost, total hours, filament used
   - Sortable columns, highlight best/worst performers
5. **Materials tab:**
   - Material cost breakdown (bar chart: PLA, PETG, ABS, etc.)
   - Waste analysis: successful vs failed material consumption
6. **Export buttons:** CSV download for each report view
7. Create `CostConfigurationModal` in settings page:
   - Electricity cost per kWh input
   - Default printer wattage
   - Depreciation toggle + inputs
   - Per-printer overrides (link to printer settings)

**Testing (Kane):**
1. `GetCostBreakdown_WithElectricityAndDepreciation_CalculatesCorrectly`
2. `GetCostBreakdown_NoCompletedJobs_ReturnsZeros`
3. `GetPrinterComparison_MultipleJobs_AggregatesCorrectly`
4. `GetPrinterComparison_FailedJobs_IncludedInWasteCost`
5. `GetMaterialCosts_GroupsByMaterial_SumsCorrectly`
6. `ExportCsv_CostBreakdown_ValidCsvFormat`
7. `ExportCsv_NoData_ReturnsEmptyWithHeaders`
8. `CostCalculation_WithDepreciation_IncludesAmortization`
9. `CostCalculation_WithoutDepreciation_ExcludesIt`
10. Frontend: `AnalyticsDashboard_Renders_WithMockData`
11. Frontend: `CostConfigurationModal_SavesSettings`
12. Edge cases:
    - Jobs with no filament usage data (history-seeded)
    - Jobs with no cost data (pre-cost-tracking)
    - Division by zero (0 completed jobs)
    - Printers with no jobs in date range

### Phase 2 — Printer Utilization & Trends (Sprint 3-4)

**User value:** Operator sees exactly how much each printer is being utilized (printing vs idle vs offline), with time-series trends. Answers "Which printers should I buy more of?" and "Which are underperforming?"

**Backend (Lambert):**
1. Create `PrinterUptimeSnapshot` entity and EF configuration
2. Create `PrinterUptimeBackgroundService` (IHostedService):
   - Every 5 minutes, capture current state of each printer (online/offline, state)
   - Calculate duration since last snapshot
   - Store to `PrinterUptimeSnapshots` table
   - Retention: auto-prune snapshots older than 90 days (configurable)
3. Extend `StatisticsService`:
   - `GetUtilizationTimelineAsync(days, printerId?)` → hourly/daily utilization data
   - `GetFleetUtilizationAsync(days)` → aggregate: fleet-wide idle/printing/offline %
   - `GetFailureAnalysisAsync(days, printerId?)` → failure reasons grouped, trending up/down
4. New endpoints on `StatisticsController`:
   - `GET /api/statistics/utilization-timeline?days=30&printerId=`
   - `GET /api/statistics/fleet-utilization?days=7`
   - `GET /api/statistics/failure-analysis?days=30&printerId=`
5. Add CSV export for utilization and failure reports

**Frontend (Ripley):**
1. **Overview tab enhancement:** Real utilization % from uptime data (replace placeholder)
2. **Printers tab enhancement:**
   - Utilization gauge per printer (printing % | idle % | offline %)
   - Utilization heatmap: 7-day grid showing hourly utilization
   - Printer ranking: sort by utilization, ROI, cost efficiency
3. **New Trends tab:**
   - Utilization over time (area chart: printing/idle/offline stacked)
   - Failure trend analysis (line chart: failure rate over time)
   - Top failure reasons (horizontal bar chart)
   - Comparative trend: this period vs last period
4. Add date range picker component (shared across all tabs):
   - Presets: 7d, 30d, 90d, YTD, All Time, Custom Range

**Testing (Kane):**
1. `UptimeTracker_CapturesOnlineOfflineTransitions`
2. `UptimeTracker_PrinterOffline_RecordsOfflineDuration`
3. `UtilizationTimeline_AggregatesHourly_CorrectPercentages`
4. `FleetUtilization_MixedStates_CalculatesCorrectly`
5. `FailureAnalysis_GroupsByReason_SortsByFrequency`
6. `UptimeRetention_PrunesOldSnapshots`
7. Edge cases:
   - Printer added mid-period (partial data)
   - Server restart (gap in snapshots)
   - All printers offline for extended period

### Phase 3 — Advanced Reporting & PDF Export (Sprint 5-6)

**User value:** Operator generates professional reports for business stakeholders, customers, or tax records. Scheduled weekly report emails. Custom date ranges. ROI calculator.

**Backend (Lambert):**
1. Add PDF generation service (using QuestPDF or similar .NET library):
   - `GET /api/statistics/export/pdf?report=monthly-summary&month=2026-03`
   - Includes: KPI summary, charts (server-rendered), tables
2. ROI calculator endpoint:
   - `GET /api/statistics/roi?printerId=` → time-to-payback, monthly profit/loss
3. Optional: scheduled report generation (background job that emails PDF)
4. Add per-project cost rollup if `ProjectId` is populated on jobs

**Frontend (Ripley):**
1. PDF export button alongside existing CSV
2. ROI calculator widget: input printer cost → shows break-even timeline
3. Report builder: select metrics, date range, printers → generate custom report
4. Print-friendly CSS for dashboard views

**Testing (Kane):**
1. `PdfExport_MonthlySummary_GeneratesValidPdf`
2. `RoiCalculator_WithRevenue_CalculatesPayback`
3. `RoiCalculator_NoCostData_ReturnsNotAvailable`

---

## Dependencies & Risks

**Dependencies:**
- Phase 1 has no blockers — can start immediately
- Phase 2 depends on Phase 1 (cost config and enhanced service layer must exist)
- Phase 3 depends on Phase 2 (utilization data needed for comprehensive reports)
- Auto-Dispatch (Feature #2) and Analytics (Feature #3) are **independent** — can be built in parallel by separate team members
- Analytics Phase 2 (uptime tracking) produces data that could improve Auto-Dispatch scoring in a future iteration (dispatch to the printer with lowest utilization)

**Risks:**
1. **Uptime data accuracy:** If the API server restarts, there's a gap in uptime snapshots. **Mitigation:** On startup, mark a "gap" in the timeline. Exclude gaps from utilization calculations. Show "data unavailable" indicator on frontend.
2. **Large datasets for analytics queries:** 90 days of uptime snapshots at 5-minute intervals = ~26k rows per printer. With 50 printers = 1.3M rows. **Mitigation:** Use indexed queries (already designed above). Aggregate at query time for daily views. Consider materialized daily rollups if query performance degrades.
3. **Cost data quality:** Many historical jobs may not have filament cost data (pre-Spoolman integration, or Spoolman was unavailable). **Mitigation:** Show "N/A" for cost fields when data is missing. Don't include uncostable jobs in averages. Show data completeness indicator (e.g., "Cost data available for 73% of jobs").
4. **PDF generation library:** Adding a new NuGet dependency. **Mitigation:** Evaluate QuestPDF (MIT license, .NET-native, no external dependencies). If too heavy, fall back to CSV-only in Phase 3 and defer PDF.

---

# Cross-Feature Considerations

## Shared Work

Both features need EF migrations. **Combine into a single migration** if phases overlap — don't create multiple conflicting migrations.

Both features extend `StatisticsController` or add new controllers. **Lambert should coordinate** to avoid merge conflicts in the controller layer.

## Implementation Order

**Recommended parallel tracks:**

| Sprint | Lambert (Backend) | Ripley (Frontend) |
|--------|------------------|-------------------|
| 1-2 | Auto-Dispatch Phase 1 (scorer + API) | Analytics Phase 1 (dashboard + cost config) |
| 3-4 | Auto-Dispatch Phase 2 (background service) + Analytics Phase 1 backend | Auto-Dispatch Phase 1 (dispatch UI) + Analytics Phase 1 frontend polish |
| 5-6 | Analytics Phase 2 (uptime tracking + utilization) | Auto-Dispatch Phase 2 (settings UI + notifications) |
| 7-8 | Auto-Dispatch Phase 3 (batch) | Analytics Phase 2 (trends + heatmaps) |
| 9-10 | Analytics Phase 3 (PDF + ROI) | Analytics Phase 3 (report builder) |

**Rationale:** Start analytics frontend early because it's the most visible deliverable (operators see value immediately). Start auto-dispatch backend early because the scoring engine is the hardest piece and needs thorough testing. This maximizes parallelism while respecting dependencies.

## Quality Gates

Every phase must:
1. Pass all existing tests (1709 API, 365 React)
2. Include new tests for all new service methods
3. Include at least one integration test per new endpoint
4. Run `dotnet format` and `npm run lint` clean
5. Create EF migrations for SQLite AND PostgreSQL

---

# Summary for Team

**Lambert:** You own the dispatch scoring engine and the analytics aggregation layer. Start with `IDispatchScorer` — it's the core algorithm. For analytics, extend `StatisticsService` with cost breakdown methods. Both are in `src/infra/Services/`.

**Ripley:** You own the analytics dashboard (new page) and the dispatch UI (modal + buttons on existing queue page). Start with the analytics dashboard — it delivers the most visible user value fastest. Use Recharts (already in the project) for new charts.

**Kane:** You own test coverage for both features. The dispatch scorer needs the most unit tests — it's the most logic-dense piece. Analytics tests are mostly about correct aggregation math and edge cases with missing data.

**Dallas (me):** I'll review PRs, resolve architectural conflicts, and adjust the plan as we learn. The scoring algorithm weights are my best guess — we'll tune them based on real farm operator feedback.

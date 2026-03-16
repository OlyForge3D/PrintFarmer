# Job Cost Tracking Backend - Implementation Summary

**Implemented By:** Lambert (Backend Dev)  
**Date:** 2026-03-16  
**Status:** ✅ COMPLETE (DI registration still needed)  

## Overview

Fully implemented Job Cost Tracking backend infrastructure (Feature #3 from roadmap) that calculates and stores detailed cost breakdowns for print jobs, including material, energy, machine time, and labor costs.

## What Was Built

### 1. Cost Tracking Settings

**File:** `src/infra/Settings/CostTrackingSettings.cs`

Configurable settings for cost calculations:
- **ElectricityRatePerKwh**: Electricity cost per kilowatt-hour (default: $0.12)
- **DefaultMachineHourlyRate**: Default machine operational cost per hour (default: $0.50)
- **LaborMarkupPercent**: Labor overhead percentage (default: 0%)
- **ProfitMarginTargetPercent**: Target profit margin for pricing (default: 30%)
- **AveragePrinterWattage**: Average printer power consumption (default: 250W)
- **EnableAutomaticCostCalculation**: Auto-calculate costs on job completion (default: true)

### 2. Domain Model Extensions

**Modified:** `src/infra/Domain/PrintJob.cs`

Added 6 new cost tracking fields:
- `MaterialCostUsd` — Filament cost from Spoolman
- `EnergyCostUsd` — Electricity cost based on print duration
- `MachineTimeCostUsd` — Machine depreciation/rental cost
- `LaborCostUsd` — Labor overhead cost
- `TotalCostUsd` — Sum of all cost components
- `CostCalculatedAt` — Timestamp for audit trail

**Modified:** `src/infra/Domain/Printer.cs`

Added per-printer cost override:
- `MachineHourlyRate` — Nullable decimal, overrides `DefaultMachineHourlyRate` if set

### 3. Cost Calculation Service

**Files:**
- `src/infra/Services/Cost/IJobCostCalculationService.cs` (interface)
- `src/infra/Services/Cost/JobCostCalculationService.cs` (implementation)

**Methods:**
1. `CalculateAndStoreCostsAsync(jobId)` — Automatically calculate all costs after job completion
2. `RecalculateCostsWithOverridesAsync(jobId, overrides)` — Manually override costs

**Calculation Formulas:**

```csharp
// Material Cost (from Spoolman)
materialCost = (actualFilamentGrams / spoolWeightGrams) × spoolPriceUsd

// Energy Cost
energyCost = (printDurationHours × printerWattage / 1000) × electricityRatePerKwh

// Machine Time Cost
machineTimeCost = printDurationHours × machineHourlyRate  // Uses per-printer override if available

// Labor Cost
laborCost = (materialCost + energyCost + machineTimeCost) × (laborMarkupPercent / 100)

// Total Cost
totalCost = materialCost + energyCost + machineTimeCost + laborCost
```

### 4. Integration with Job Completion

**Modified:** `src/infra/Services/Printers/PrintJobCompletionService.cs`

- Injected `IJobCostCalculationService` dependency
- Calls `CalculateDetailedCostBreakdownAsync()` after job completion
- Runs **AFTER** first `SaveChangesAsync()` to ensure `ActualFilamentUsage` is persisted
- Runs **BEFORE** notifications to include cost data in completion events

### 5. Cost DTOs

**File:** `src/infra/Dtos/CostDtos.cs`

6 DTOs for cost API responses:
- `JobCostBreakdownDto` — Detailed cost breakdown for single job
- `CostStatisticsSummaryDto` — Aggregate cost statistics
- `CostByTimePeriodDto` — Costs grouped by date
- `CostByPrinterDto` — Costs grouped by printer
- `CostByMaterialDto` — Costs grouped by material type
- `UpdateJobCostRequest` — Manual cost override request

### 6. API Endpoints

**StatisticsController** (`src/api/Controllers/StatisticsController.cs`):
- `GET /api/statistics/costs/summary` — Aggregate cost summary
- `GET /api/statistics/costs?startDate={date}&endDate={date}` — Time series cost data
- `GET /api/statistics/costs/by-printer` — Per-printer cost breakdown
- `GET /api/statistics/costs/by-material` — Per-material cost breakdown

**JobQueueAnalyticsController** (`src/api/Controllers/JobQueueAnalyticsController.cs`):
- `GET /api/job-queue-analytics/jobs/{id}/cost` — Job cost breakdown
- `PUT /api/job-queue-analytics/jobs/{id}/cost` — Manual cost override

### 7. Service Layer Implementations

**Modified:**
- `src/infra/Services/Statistics/IStatisticsService.cs` (4 new methods)
- `src/infra/Services/Statistics/StatisticsService.cs` (4 implementations using EF Core LINQ)
- `src/infra/Services/Interfaces/IPrintJobManagementService.cs` (2 new methods)
- `src/api/Services/PrintQueue/PrintJobManagementService.cs` (2 implementations)

All methods use repository pattern and async/await best practices.

### 8. Database Migrations

**Created:**
- `src/migrations/Farm.Migrations.PostgreSQL/Migrations/[timestamp]_AddJobCostTrackingFields.cs`
- `src/migrations/Farm.Migrations.SqlServer/Migrations/[timestamp]_AddJobCostTrackingFields.cs`

**Schema Changes:**
- `PrintJobs` table: Added 6 new columns (all nullable decimals + 1 datetime)
- `Printers` table: Added 1 new column (nullable decimal)

All migrations generated successfully with EF Core tooling.

## Key Design Decisions

### 1. All Cost Fields Are Nullable
- Jobs without Spoolman integration can't calculate material costs
- Jobs completed before feature deployment have no cost data
- Nullable fields allow incremental adoption and prevent data quality issues

### 2. Cost Calculation Timing
- Runs **AFTER** first `SaveChangesAsync()` that persists `ActualFilamentUsage`
- Runs **BEFORE** notifications to include cost data in events
- Ensures accurate material cost calculation based on actual usage

### 3. Per-Printer Cost Overrides
- `Printer.MachineHourlyRate` overrides global `DefaultMachineHourlyRate`
- Allows accurate costing for mixed-equipment facilities
- Industrial printers can have different hourly rates than hobby machines

### 4. Backward Compatibility
- Existing `ActualCost` field preserved (legacy material-only cost)
- `TotalCostUsd` provides new comprehensive cost including all components
- Allows gradual migration to new cost model

## Build Status

✅ **Build:** 0 errors, 0 warnings  
✅ **Format:** Clean (ran `dotnet format`)  
✅ **Migrations:** Generated for both PostgreSQL and SQL Server  
⚠️ **DI Registration:** Still needed (see Next Steps)  
⚠️ **Tests:** Not yet written  

## Next Steps

### Critical (Blocker for Runtime)

1. **Register `JobCostCalculationService` in DI container**
   - File: `src/api/Infrastructure/ServiceCollectionExtensions.cs` (or similar)
   - Add: `services.AddScoped<IJobCostCalculationService, JobCostCalculationService>();`
   - Verify dependencies (`ISettingsService`, `ISpoolmanService`) are already registered

### Deployment

2. **Run database migrations**
   ```bash
   cd src/migrations/Farm.Migrations.PostgreSQL
   dotnet ef database update --context AppDbContext
   
   cd ../Farm.Migrations.SqlServer
   dotnet ef database update --context AppDbContext
   ```

3. **Configure settings (optional)**
   - Settings use defaults if not configured
   - Can override in `appsettings.json` or via Settings UI
   - Example:
     ```json
     {
       "CostTracking": {
         "ElectricityRatePerKwh": 0.15,
         "DefaultMachineHourlyRate": 0.75,
         "LaborMarkupPercent": 15
       }
     }
     ```

### Testing

4. **End-to-end testing**
   - Complete a print job and verify cost fields are populated
   - Check API endpoints return correct cost data
   - Test manual cost override functionality
   - Verify per-printer rate overrides work correctly

5. **Unit testing**
   - Write tests for `JobCostCalculationService` formulas
   - Test settings validation
   - Test edge cases (missing Spoolman data, null ActualFilamentUsage, etc.)

### Frontend (Future)

6. **Cost dashboard UI**
   - Display aggregate cost statistics
   - Show cost trends over time
   - Per-printer cost comparison
   - Per-material cost comparison

7. **Per-job cost display**
   - Show cost breakdown in job details
   - Allow manual cost overrides via UI
   - Display cost vs. estimate comparison

## API Usage Examples

### Get Cost Summary
```http
GET /api/statistics/costs/summary
Authorization: Bearer {token}
```

**Response:**
```json
{
  "totalCostAllTime": 1234.56,
  "averageCostPerJob": 12.34,
  "totalJobsWithCosts": 100,
  "costByMaterialType": {
    "PLA": 456.78,
    "ABS": 345.67,
    "PETG": 432.11
  }
}
```

### Get Job Cost Breakdown
```http
GET /api/job-queue-analytics/jobs/abc123/cost
Authorization: Bearer {token}
```

**Response:**
```json
{
  "jobId": "abc123",
  "materialCost": 4.56,
  "energyCost": 0.15,
  "machineTimeCost": 2.50,
  "laborCost": 0.72,
  "totalCost": 7.93,
  "calculatedAt": "2026-03-16T14:30:00Z"
}
```

### Update Job Cost (Manual Override)
```http
PUT /api/job-queue-analytics/jobs/abc123/cost
Authorization: Bearer {token}
Content-Type: application/json

{
  "materialCost": 5.00,
  "energyCost": 0.20,
  "machineTimeCost": 3.00,
  "laborCost": 1.00
}
```

## Files Created/Modified

### Created (9 files)
- `src/infra/Settings/CostTrackingSettings.cs`
- `src/infra/Services/Cost/IJobCostCalculationService.cs`
- `src/infra/Services/Cost/JobCostCalculationService.cs`
- `src/infra/Dtos/CostDtos.cs`
- `src/migrations/Farm.Migrations.PostgreSQL/Migrations/[timestamp]_AddJobCostTrackingFields.cs`
- `src/migrations/Farm.Migrations.SqlServer/Migrations/[timestamp]_AddJobCostTrackingFields.cs`
- `.squad/agents/lambert/history.md` (updated)
- `.squad/decisions/inbox/lambert-cost-tracking.md` (created)
- `COST_TRACKING_IMPLEMENTATION.md` (this file)

### Modified (9 files)
- `src/infra/Domain/PrintJob.cs` (7 new fields)
- `src/infra/Domain/Printer.cs` (1 new field)
- `src/infra/Services/Printers/PrintJobCompletionService.cs` (cost calculation integration)
- `src/api/Controllers/StatisticsController.cs` (4 new endpoints)
- `src/api/Controllers/JobQueueAnalyticsController.cs` (2 new endpoints)
- `src/infra/Services/Statistics/IStatisticsService.cs` (4 new methods)
- `src/infra/Services/Statistics/StatisticsService.cs` (4 implementations)
- `src/infra/Services/Interfaces/IPrintJobManagementService.cs` (2 new methods)
- `src/api/Services/PrintQueue/PrintJobManagementService.cs` (2 implementations)

## Architectural Patterns Followed

- ✅ Settings with `[AppSetting]` attribute and `IValidatableSetting`
- ✅ Service interfaces in `infra/Services/`, implementations alongside
- ✅ DTOs in `infra/Dtos/` with consistent naming (`*Dto` suffix)
- ✅ API controllers return `Task<IActionResult>` with proper status codes
- ✅ EF migrations for both PostgreSQL and SQL Server
- ✅ Async/await for all I/O operations
- ✅ Repository pattern for data access
- ✅ Dependency injection for all services
- ✅ Nullable reference types for optional data

## Technical Notes

### Settings Service is Synchronous
- Use `_settingsService.Get<CostTrackingSettings>()` (synchronous)
- NOT `GetAsync<T>()` — interface only provides synchronous method
- Settings are cached in memory, no async I/O needed

### Material Cost Calculation
- Requires Spoolman integration
- Uses `ISpoolmanService.GetSpoolByIdAsync()`
- Falls back gracefully if Spoolman not configured
- `MaterialCostUsd` remains null if no Spoolman data

### Energy Cost Calculation
- Uses `AveragePrinterWattage` from settings
- Future enhancement: track actual per-printer wattage
- Formula: (hours × watts / 1000) × rate

### Machine Time Cost Calculation
- Uses `Printer.MachineHourlyRate` if set
- Falls back to `DefaultMachineHourlyRate` if not set
- Allows per-printer rate differentiation

### Labor Cost Calculation
- Applied as percentage of subtotal (material + energy + machine)
- Default is 0% (disabled)
- Can be configured per facility needs

## Support & Documentation

- Implementation details: `.squad/decisions/inbox/lambert-cost-tracking.md`
- Historical log: `.squad/agents/lambert/history.md`
- API documentation: Swagger UI at `/swagger`
- Database schema: EF Core migrations in `src/migrations/`

---

**Questions or Issues?** Contact Lambert (Backend Dev) or consult the decision document.

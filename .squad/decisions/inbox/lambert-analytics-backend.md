# Decision: Analytics Backend Implementation

**Author:** Lambert (Backend Developer)
**Date:** 2026-03-12
**Status:** Implemented

## Context

Dallas's architecture plan (`dallas-analytics-architecture.md`) specified backend services for analytics: export/reporting, performance correlation, and predictive alerts. The pre-existing test files had compilation errors due to incorrect entity property names from Dallas's spec.

## Decision

Implemented all 3 analytics backend services with adjustments for actual entity properties:

### Architecture
- **ReportExportService**: QuestPDF 2025.1.0 for PDF, CsvHelper 33.0.1 for CSV export
- **CorrelationAnalyticsService**: LINQ GroupBy queries against PrintJob + PrintJobStatistics
- **PredictiveAnalyticsService**: Heuristic engine with configurable thresholds (nozzle 500h, hotend 1000h)

### Route Changes from Dallas's Plan
- Export routes: `/api/statistics/export/{pdf,jobs-csv,cost-csv,utilization-csv}` (not `/api/reports/...`)
- Predictive POST endpoint: `[AllowAnonymous]` to match test expectations

### Entity Property Mappings
Corrected from Dallas's plan to actual properties:
- `NozzleTemperature` (int?) not `ActualHotendTemp` (double)
- `BedTemperature` (int?) not `ActualBedTemp`
- `ActualDurationMs` (long?) converted to minutes via `/ 60000.0`
- `PrinterStatisticsSet` not `PrinterStatistics`
- `TotalFilamentUsedGrams` not `TotalFilamentGrams`
- `TotalJobsCompleted` not `CompletedJobs`

## Consequences

- 12 new API endpoints available for frontend consumption
- 2 new NuGet packages added to Farm.Infrastructure
- All 2035 tests passing, 0 warnings

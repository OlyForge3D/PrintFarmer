# Analytics Architecture Plan — Four Missing Features

**Architect:** Dallas  
**Date:** 2026-03-09  
**Status:** READY FOR IMPLEMENTATION  

## Executive Summary

Brett's competitive analysis identified 4 analytics features present in competitors but missing from PrintFarmer:

1. **Export/Reporting** — PDF/CSV export of print reports
2. **Unified Business Analytics Dashboard** — Single comprehensive analytics view
3. **Performance Correlation Charts** — Material × printer × success rate analysis
4. **Predictive Alerts** — Job failure prediction, maintenance forecasting

**Current state:** PrintFarmer has solid analytics foundations (StatisticsService with 5 endpoints, 8 KPI cards, 4 Recharts visualizations). But data is fragmented across multiple pages and lacks export capability.

**Build dependencies:** All 4 features can be developed in parallel. No blocking dependencies.

**Chart library:** `recharts` (v3.6.0) is already installed and used extensively. Stick with it.

---

## Feature 1: Export/Reporting (PDF/CSV)

### Backend Implementation (Lambert)

#### 1.1 New Services

**File:** `src/infra/Services/Statistics/IReportExportService.cs`

```csharp
namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Service for exporting print statistics to PDF and CSV formats.
/// </summary>
public interface IReportExportService
{
    /// <summary>
    /// Generates a comprehensive print report in PDF format.
    /// </summary>
    Task<byte[]> GeneratePdfReportAsync(ReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Generates print job history CSV export.
    /// </summary>
    Task<byte[]> GenerateJobHistoryCsvAsync(ReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Generates cost breakdown CSV export.
    /// </summary>
    Task<byte[]> GenerateCostCsvAsync(ReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Generates printer utilization CSV export.
    /// </summary>
    Task<byte[]> GenerateUtilizationCsvAsync(ReportRequest request, CancellationToken ct = default);
}
```

**File:** `src/infra/Services/Statistics/ReportExportService.cs`

```csharp
using CsvHelper;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace Farm.Infrastructure.Services.Statistics;

public class ReportExportService(
    IStatisticsService statisticsService,
    AppDbContext db) : IReportExportService
{
    private readonly IStatisticsService _statisticsService = statisticsService;
    private readonly AppDbContext _db = db;

    public async Task<byte[]> GeneratePdfReportAsync(ReportRequest request, CancellationToken ct)
    {
        var summary = await _statisticsService.GetSummaryAsync(request.Days, ct);
        var jobsData = await _statisticsService.GetJobsOverTimeAsync(request.Days ?? 365, ct);
        var costData = await _statisticsService.GetCostOverTimeAsync(request.Days ?? 365, ct);
        var filamentData = await _statisticsService.GetFilamentByMaterialAsync(request.Days, ct);
        var utilization = await _statisticsService.GetPrinterUtilizationAsync(request.Days, ct);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(50);
                page.Header().Element(ComposeHeader);
                page.Content().Element(content => ComposeContent(content, summary, jobsData, costData, filamentData, utilization));
                page.Footer().AlignCenter().Text(text =>
                {
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }

    public async Task<byte[]> GenerateJobHistoryCsvAsync(ReportRequest request, CancellationToken ct)
    {
        var since = request.Days.HasValue ? DateTime.UtcNow.AddDays(-request.Days.Value) : (DateTime?)null;
        var query = _db.Set<PrintJob>().AsQueryable();
        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        var jobs = await query
            .OrderByDescending(j => j.QueuedAt)
            .Select(j => new JobHistoryCsvRow
            {
                JobName = j.Name,
                Status = j.Status.ToString(),
                QueuedAt = j.QueuedAt,
                StartedAt = j.ActualStartTime,
                CompletedAt = j.ActualEndTime,
                PrintTime = j.ActualPrintTime,
                FilamentGrams = j.ActualFilamentUsage,
                Cost = j.ActualCost,
                PrinterName = j.AssignedPrinter != null ? j.AssignedPrinter.Name : null,
            })
            .ToListAsync(ct);

        using var memoryStream = new MemoryStream();
        using var writer = new StreamWriter(memoryStream);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        
        await csv.WriteRecordsAsync(jobs, ct);
        await writer.FlushAsync();
        return memoryStream.ToArray();
    }

    public async Task<byte[]> GenerateCostCsvAsync(ReportRequest request, CancellationToken ct)
    {
        var costData = await _statisticsService.GetCostOverTimeAsync(request.Days ?? 365, ct);
        
        using var memoryStream = new MemoryStream();
        using var writer = new StreamWriter(memoryStream);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        
        await csv.WriteRecordsAsync(costData, ct);
        await writer.FlushAsync();
        return memoryStream.ToArray();
    }

    public async Task<byte[]> GenerateUtilizationCsvAsync(ReportRequest request, CancellationToken ct)
    {
        var utilization = await _statisticsService.GetPrinterUtilizationAsync(request.Days, ct);
        
        using var memoryStream = new MemoryStream();
        using var writer = new StreamWriter(memoryStream);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        
        await csv.WriteRecordsAsync(utilization, ct);
        await writer.FlushAsync();
        return memoryStream.ToArray();
    }

    private void ComposeHeader(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text("PrintFarmer Analytics Report").FontSize(20).Bold();
                column.Item().Text($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC").FontSize(10);
            });
        });
    }

    private void ComposeContent(IContainer container, StatisticsSummaryDto summary, 
        List<DailyJobCountDto> jobs, List<DailyCostDto> costs, 
        List<FilamentByMaterialDto> filament, List<PrinterUtilizationDto> utilization)
    {
        container.PaddingVertical(20).Column(column =>
        {
            column.Item().Text("Summary Statistics").FontSize(16).Bold();
            column.Item().PaddingTop(10).Element(c => ComposeSummaryTable(c, summary));

            column.Item().PaddingTop(20).Text("Job History").FontSize(16).Bold();
            column.Item().PaddingTop(10).Element(c => ComposeJobHistoryTable(c, jobs));

            column.Item().PaddingTop(20).Text("Printer Utilization").FontSize(16).Bold();
            column.Item().PaddingTop(10).Element(c => ComposeUtilizationTable(c, utilization));

            column.Item().PaddingTop(20).Text("Filament Consumption").FontSize(16).Bold();
            column.Item().PaddingTop(10).Element(c => ComposeFilamentTable(c, filament));
        });
    }

    // Table composition methods omitted for brevity — Lambert will implement
}
```

**New DTOs:**

**File:** `src/infra/Dtos/ReportDtos.cs`

```csharp
namespace Farm.Infrastructure.Dtos;

public record ReportRequest
{
    public int? Days { get; init; }
    public ReportFormat Format { get; init; } = ReportFormat.Pdf;
}

public enum ReportFormat
{
    Pdf,
    Csv
}

public record JobHistoryCsvRow
{
    public string JobName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime QueuedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public TimeSpan? PrintTime { get; init; }
    public double? FilamentGrams { get; init; }
    public decimal? Cost { get; init; }
    public string? PrinterName { get; init; }
}
```

#### 1.2 New Controller Endpoints

**File:** `src/api/Controllers/StatisticsController.cs` (Add these methods)

```csharp
/// <summary>
/// Exports comprehensive print report as PDF.
/// </summary>
[HttpGet("export/pdf")]
[ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
public async Task<IActionResult> ExportPdfReportAsync([FromQuery] int? days, CancellationToken ct)
{
    var report = await _reportExportService.GeneratePdfReportAsync(
        new ReportRequest { Days = days }, ct);
    
    var fileName = $"printfarmer-report-{DateTime.UtcNow:yyyy-MM-dd}.pdf";
    return File(report, "application/pdf", fileName);
}

/// <summary>
/// Exports job history as CSV.
/// </summary>
[HttpGet("export/jobs-csv")]
[ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
public async Task<IActionResult> ExportJobHistoryCsvAsync([FromQuery] int? days, CancellationToken ct)
{
    var csv = await _reportExportService.GenerateJobHistoryCsvAsync(
        new ReportRequest { Days = days }, ct);
    
    var fileName = $"job-history-{DateTime.UtcNow:yyyy-MM-dd}.csv";
    return File(csv, "text/csv", fileName);
}

/// <summary>
/// Exports cost data as CSV.
/// </summary>
[HttpGet("export/cost-csv")]
[ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
public async Task<IActionResult> ExportCostCsvAsync([FromQuery] int? days, CancellationToken ct)
{
    var csv = await _reportExportService.GenerateCostCsvAsync(
        new ReportRequest { Days = days }, ct);
    
    var fileName = $"cost-breakdown-{DateTime.UtcNow:yyyy-MM-dd}.csv";
    return File(csv, "text/csv", fileName);
}

/// <summary>
/// Exports printer utilization as CSV.
/// </summary>
[HttpGet("export/utilization-csv")]
[ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
public async Task<IActionResult> ExportUtilizationCsvAsync([FromQuery] int? days, CancellationToken ct)
{
    var csv = await _reportExportService.GenerateUtilizationCsvAsync(
        new ReportRequest { Days = days }, ct);
    
    var fileName = $"printer-utilization-{DateTime.UtcNow:yyyy-MM-dd}.csv";
    return File(csv, "text/csv", fileName);
}
```

**Constructor injection:** Add `IReportExportService reportExportService` parameter to `StatisticsController`.

#### 1.3 Dependencies to Add

**File:** `src/infra/Farm.Infrastructure.csproj`

```xml
<PackageReference Include="QuestPDF" Version="2024.12.4" />
<PackageReference Include="CsvHelper" Version="33.0.1" />
```

#### 1.4 Service Registration

**File:** `src/api/Program.cs` (Add to service registration section)

```csharp
builder.Services.AddScoped<IReportExportService, ReportExportService>();
```

### Frontend Implementation (Ripley)

#### 1.5 New API Client Methods

**File:** `src/Web/ReactApp/src/services/api.ts` (Add to ApiClient class)

```typescript
// Export Reports
async exportPdfReport(days?: number): Promise<Blob> {
  const params = days ? `?days=${days}` : '';
  const response = await this.axiosInstance.get(
    `/statistics/export/pdf${params}`,
    { responseType: 'blob' }
  );
  return response.data;
}

async exportJobHistoryCsv(days?: number): Promise<Blob> {
  const params = days ? `?days=${days}` : '';
  const response = await this.axiosInstance.get(
    `/statistics/export/jobs-csv${params}`,
    { responseType: 'blob' }
  );
  return response.data;
}

async exportCostCsv(days?: number): Promise<Blob> {
  const params = days ? `?days=${days}` : '';
  const response = await this.axiosInstance.get(
    `/statistics/export/cost-csv${params}`,
    { responseType: 'blob' }
  );
  return response.data;
}

async exportUtilizationCsv(days?: number): Promise<Blob> {
  const params = days ? `?days=${days}` : '';
  const response = await this.axiosInstance.get(
    `/statistics/export/utilization-csv${params}`,
    { responseType: 'blob' }
  );
  return response.data;
}
```

#### 1.6 Export Component

**File:** `src/Web/ReactApp/src/features/statistics/components/ExportMenu.tsx`

```tsx
import React, { useState } from 'react';
import { Button } from '@/common/components/ui';
import { DownloadIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';

interface Props {
  days?: number;
}

export const ExportMenu: React.FC<Props> = ({ days }) => {
  const [exporting, setExporting] = useState(false);

  const handleExport = async (type: 'pdf' | 'jobs-csv' | 'cost-csv' | 'utilization-csv') => {
    setExporting(true);
    try {
      let blob: Blob;
      let filename: string;
      
      switch (type) {
        case 'pdf':
          blob = await apiClient.exportPdfReport(days);
          filename = `printfarmer-report-${new Date().toISOString().split('T')[0]}.pdf`;
          break;
        case 'jobs-csv':
          blob = await apiClient.exportJobHistoryCsv(days);
          filename = `job-history-${new Date().toISOString().split('T')[0]}.csv`;
          break;
        case 'cost-csv':
          blob = await apiClient.exportCostCsv(days);
          filename = `cost-breakdown-${new Date().toISOString().split('T')[0]}.csv`;
          break;
        case 'utilization-csv':
          blob = await apiClient.exportUtilizationCsv(days);
          filename = `printer-utilization-${new Date().toISOString().split('T')[0]}.csv`;
          break;
      }

      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      window.URL.revokeObjectURL(url);
      document.body.removeChild(a);
      
      toast.success('Report exported successfully');
    } catch (error) {
      toast.error(`Failed to export: ${String(error)}`);
    } finally {
      setExporting(false);
    }
  };

  return (
    <div className="relative inline-block">
      <Button
        variant="secondary"
        iconLeft={<DownloadIcon />}
        loading={exporting}
        onClick={() => {
          // Show dropdown menu
        }}
      >
        Export
      </Button>
      {/* Implement dropdown menu with 4 options: PDF Report, Job History CSV, Cost CSV, Utilization CSV */}
    </div>
  );
};
```

#### 1.7 Update StatisticsPage

**File:** `src/Web/ReactApp/src/features/statistics/pages/StatisticsPage.tsx`

Add `<ExportMenu days={days} />` to the `actions` prop of `PageTemplate`.

---

## Feature 2: Unified Business Analytics Dashboard

### Backend Implementation (Lambert)

**No new backend endpoints required.** All data is already available via existing `StatisticsController` endpoints. Feature is primarily frontend consolidation.

### Frontend Implementation (Ripley)

#### 2.1 New Dashboard Page

**File:** `src/Web/ReactApp/src/features/statistics/pages/BusinessAnalyticsDashboard.tsx`

This page consolidates:
- Existing `StatisticsPage` KPIs and charts
- Job queue analytics from `JobQueueAnalyticsController`
- Real-time printer status summary

**Layout structure:**

```
┌─────────────────────────────────────────────┐
│ Business Analytics Dashboard                │
│ [Export] [30 days ▾]                       │
├─────────────────────────────────────────────┤
│ KPI Cards Row (8 cards)                     │
│ [Total Jobs] [Success Rate] [Cost] [Hours] │
│ [Completed] [Failed] [Cancelled] [Filament]│
├─────────────────────────────────────────────┤
│ Tab Navigation                              │
│ [Overview] [Jobs] [Costs] [Printers]      │
├─────────────────────────────────────────────┤
│                                             │
│ Tab Content (Charts + Tables)              │
│                                             │
└─────────────────────────────────────────────┘
```

**Key components to create:**

1. **BusinessAnalyticsDashboard.tsx** — Main page with tabs
2. **AnalyticsOverviewTab.tsx** — Summary view with all 4 charts
3. **JobAnalyticsTab.tsx** — Job-focused analytics (jobs-over-time + job queue table)
4. **CostAnalyticsTab.tsx** — Cost-focused analytics (cost-over-time + breakdown table)
5. **PrinterAnalyticsTab.tsx** — Printer-focused analytics (utilization + status table)

#### 2.2 Tab Component Pattern

**File:** `src/Web/ReactApp/src/features/statistics/components/AnalyticsTabs.tsx`

```tsx
import { Tabs, TabList, Tab, TabPanels, TabPanel } from '@/common/components/ui/Tabs';
import { AnalyticsOverviewTab } from './AnalyticsOverviewTab';
import { JobAnalyticsTab } from './JobAnalyticsTab';
import { CostAnalyticsTab } from './CostAnalyticsTab';
import { PrinterAnalyticsTab } from './PrinterAnalyticsTab';

interface Props {
  days?: number;
}

export const AnalyticsTabs: React.FC<Props> = ({ days }) => (
  <Tabs defaultIndex={0}>
    <TabList>
      <Tab>Overview</Tab>
      <Tab>Jobs</Tab>
      <Tab>Costs</Tab>
      <Tab>Printers</Tab>
    </TabList>
    <TabPanels>
      <TabPanel>
        <AnalyticsOverviewTab days={days} />
      </TabPanel>
      <TabPanel>
        <JobAnalyticsTab days={days} />
      </TabPanel>
      <TabPanel>
        <CostAnalyticsTab days={days} />
      </TabPanel>
      <TabPanel>
        <PrinterAnalyticsTab days={days} />
      </TabPanel>
    </TabPanels>
  </Tabs>
);
```

#### 2.3 Route Configuration

**File:** `src/Web/ReactApp/src/App.tsx`

Add new route:

```tsx
<Route path="/analytics/business" element={<BusinessAnalyticsDashboard />} />
```

Update navigation sidebar to include "Business Analytics" link under the Statistics section.

#### 2.4 Deprecate Old StatisticsPage?

**Decision:** Keep existing `/statistics` route for now as a simpler view. Business dashboard is for power users who want comprehensive view. Both pages can coexist.

---

## Feature 3: Performance Correlation Charts

### Backend Implementation (Lambert)

#### 3.1 New Service Interface

**File:** `src/infra/Services/Statistics/ICorrelationAnalyticsService.cs`

```csharp
namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Service for computing performance correlations across materials, printers, and print settings.
/// </summary>
public interface ICorrelationAnalyticsService
{
    /// <summary>
    /// Returns success rate breakdown by material type.
    /// </summary>
    Task<List<MaterialSuccessRateDto>> GetMaterialSuccessRatesAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns success rate breakdown by printer × material combination.
    /// </summary>
    Task<List<PrinterMaterialPerformanceDto>> GetPrinterMaterialPerformanceAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns temperature vs quality correlation data (completed jobs only).
    /// </summary>
    Task<List<TemperatureQualityCorrelationDto>> GetTemperatureQualityDataAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns print duration distribution by status.
    /// </summary>
    Task<List<DurationTrendDto>> GetDurationTrendsAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns failure reasons breakdown.
    /// </summary>
    Task<List<FailureReasonDto>> GetFailureReasonsAsync(int? days, CancellationToken ct = default);
}
```

#### 3.2 Service Implementation

**File:** `src/infra/Services/Statistics/CorrelationAnalyticsService.cs`

```csharp
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Statistics;

public class CorrelationAnalyticsService(AppDbContext db) : ICorrelationAnalyticsService
{
    private readonly AppDbContext _db = db;

    public async Task<List<MaterialSuccessRateDto>> GetMaterialSuccessRatesAsync(int? days, CancellationToken ct)
    {
        var since = days.HasValue ? DateTime.UtcNow.AddDays(-days.Value) : (DateTime?)null;
        var query = _db.Set<PrintJob>()
            .Where(j => j.RequiredMaterialType != null)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed);

        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        var grouped = await query
            .GroupBy(j => j.RequiredMaterialType!)
            .Select(g => new
            {
                Material = g.Key,
                Total = g.Count(),
                Completed = g.Count(j => j.Status == PrintJobStatus.Completed),
            })
            .ToListAsync(ct);

        return grouped.Select(g => new MaterialSuccessRateDto
        {
            Material = g.Material,
            TotalJobs = g.Total,
            CompletedJobs = g.Completed,
            SuccessRate = g.Total > 0 ? Math.Round((double)g.Completed / g.Total * 100, 1) : 0,
        })
        .OrderByDescending(d => d.TotalJobs)
        .ToList();
    }

    public async Task<List<PrinterMaterialPerformanceDto>> GetPrinterMaterialPerformanceAsync(int? days, CancellationToken ct)
    {
        var since = days.HasValue ? DateTime.UtcNow.AddDays(-days.Value) : (DateTime?)null;
        var query = _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId.HasValue)
            .Where(j => j.RequiredMaterialType != null)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed);

        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        var rawData = await query
            .Select(j => new
            {
                PrinterId = j.AssignedPrinterId!.Value,
                Material = j.RequiredMaterialType!,
                Status = j.Status,
            })
            .ToListAsync(ct);

        var grouped = rawData
            .GroupBy(j => new { j.PrinterId, j.Material })
            .Select(g => new
            {
                g.Key.PrinterId,
                g.Key.Material,
                Total = g.Count(),
                Completed = g.Count(j => j.Status == PrintJobStatus.Completed),
            })
            .ToList();

        var printerIds = grouped.Select(g => g.PrinterId).Distinct().ToList();
        var printerNames = await _db.Printers
            .Where(p => printerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        return grouped.Select(g => new PrinterMaterialPerformanceDto
        {
            PrinterId = g.PrinterId,
            PrinterName = printerNames.GetValueOrDefault(g.PrinterId, "Unknown"),
            Material = g.Material,
            TotalJobs = g.Total,
            CompletedJobs = g.Completed,
            SuccessRate = g.Total > 0 ? Math.Round((double)g.Completed / g.Total * 100, 1) : 0,
        })
        .OrderByDescending(d => d.TotalJobs)
        .ToList();
    }

    public async Task<List<TemperatureQualityCorrelationDto>> GetTemperatureQualityDataAsync(int? days, CancellationToken ct)
    {
        // Query PrintJobStatistics for temperature data (requires joining PrintJob → PrintJobStatistics)
        var since = days.HasValue ? DateTime.UtcNow.AddDays(-days.Value) : (DateTime?)null;
        
        var query = from job in _db.Set<PrintJob>()
                    join stats in _db.Set<PrintJobStatistics>() on job.Id equals stats.PrintJobId
                    where job.Status == PrintJobStatus.Completed
                    where stats.ActualHotendTemp.HasValue && stats.ActualBedTemp.HasValue
                    select new { job, stats };

        if (since.HasValue)
        {
            query = query.Where(x => x.job.QueuedAt >= since.Value);
        }

        var data = await query
            .Select(x => new TemperatureQualityCorrelationDto
            {
                JobId = x.job.Id,
                HotendTemp = x.stats.ActualHotendTemp!.Value,
                BedTemp = x.stats.ActualBedTemp!.Value,
                Material = x.job.RequiredMaterialType ?? "Unknown",
                DurationMinutes = x.stats.PrintDurationMinutes,
                Success = x.job.Status == PrintJobStatus.Completed,
            })
            .ToListAsync(ct);

        return data;
    }

    public async Task<List<DurationTrendDto>> GetDurationTrendsAsync(int? days, CancellationToken ct)
    {
        var since = days.HasValue ? DateTime.UtcNow.AddDays(-days.Value) : (DateTime?)null;
        var query = _db.Set<PrintJob>()
            .Where(j => j.ActualPrintTime.HasValue);

        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        var jobs = await query
            .Select(j => new
            {
                j.QueuedAt,
                j.ActualPrintTime,
                j.Status,
            })
            .ToListAsync(ct);

        var grouped = jobs
            .GroupBy(j => j.QueuedAt.Date)
            .Select(g => new DurationTrendDto
            {
                Date = g.Key.ToString("yyyy-MM-dd"),
                AverageDurationMinutes = Math.Round(g.Average(j => j.ActualPrintTime!.Value.TotalMinutes), 1),
                MinDurationMinutes = Math.Round(g.Min(j => j.ActualPrintTime!.Value.TotalMinutes), 1),
                MaxDurationMinutes = Math.Round(g.Max(j => j.ActualPrintTime!.Value.TotalMinutes), 1),
                JobCount = g.Count(),
            })
            .OrderBy(d => d.Date)
            .ToList();

        return grouped;
    }

    public async Task<List<FailureReasonDto>> GetFailureReasonsAsync(int? days, CancellationToken ct)
    {
        var since = days.HasValue ? DateTime.UtcNow.AddDays(-days.Value) : (DateTime?)null;
        var query = _db.Set<PrintJob>()
            .Where(j => j.Status == PrintJobStatus.Failed)
            .Where(j => j.FailureReason != null);

        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        var grouped = await query
            .GroupBy(j => j.FailureReason!)
            .Select(g => new FailureReasonDto
            {
                Reason = g.Key,
                Count = g.Count(),
            })
            .OrderByDescending(f => f.Count)
            .ToListAsync(ct);

        return grouped;
    }
}
```

#### 3.3 New DTOs

**File:** `src/infra/Dtos/CorrelationAnalyticsDtos.cs`

```csharp
namespace Farm.Infrastructure.Dtos;

public record MaterialSuccessRateDto
{
    public string Material { get; init; } = string.Empty;
    public int TotalJobs { get; init; }
    public int CompletedJobs { get; init; }
    public double SuccessRate { get; init; }
}

public record PrinterMaterialPerformanceDto
{
    public Guid PrinterId { get; init; }
    public string PrinterName { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public int TotalJobs { get; init; }
    public int CompletedJobs { get; init; }
    public double SuccessRate { get; init; }
}

public record TemperatureQualityCorrelationDto
{
    public Guid JobId { get; init; }
    public double HotendTemp { get; init; }
    public double BedTemp { get; init; }
    public string Material { get; init; } = string.Empty;
    public double DurationMinutes { get; init; }
    public bool Success { get; init; }
}

public record DurationTrendDto
{
    public string Date { get; init; } = string.Empty;
    public double AverageDurationMinutes { get; init; }
    public double MinDurationMinutes { get; init; }
    public double MaxDurationMinutes { get; init; }
    public int JobCount { get; init; }
}

public record FailureReasonDto
{
    public string Reason { get; init; } = string.Empty;
    public int Count { get; init; }
}
```

#### 3.4 New Controller

**File:** `src/api/Controllers/CorrelationAnalyticsController.cs`

```csharp
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides performance correlation analytics for materials, printers, and settings.
/// </summary>
[ApiController]
[Route("api/correlation-analytics")]
[Authorize]
public class CorrelationAnalyticsController(ICorrelationAnalyticsService correlationService) : ControllerBase
{
    private readonly ICorrelationAnalyticsService _correlationService = correlationService;

    /// <summary>
    /// Returns success rate breakdown by material type.
    /// </summary>
    [HttpGet("material-success-rates")]
    [ProducesResponseType(typeof(List<MaterialSuccessRateDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMaterialSuccessRatesAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetMaterialSuccessRatesAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns success rate breakdown by printer × material combination.
    /// </summary>
    [HttpGet("printer-material-performance")]
    [ProducesResponseType(typeof(List<PrinterMaterialPerformanceDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrinterMaterialPerformanceAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetPrinterMaterialPerformanceAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns temperature vs quality correlation data.
    /// </summary>
    [HttpGet("temperature-quality")]
    [ProducesResponseType(typeof(List<TemperatureQualityCorrelationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTemperatureQualityDataAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetTemperatureQualityDataAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns print duration trends over time.
    /// </summary>
    [HttpGet("duration-trends")]
    [ProducesResponseType(typeof(List<DurationTrendDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDurationTrendsAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetDurationTrendsAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns failure reasons breakdown.
    /// </summary>
    [HttpGet("failure-reasons")]
    [ProducesResponseType(typeof(List<FailureReasonDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFailureReasonsAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetFailureReasonsAsync(days, ct);
        return Ok(result);
    }
}
```

#### 3.5 Service Registration

**File:** `src/api/Program.cs`

```csharp
builder.Services.AddScoped<ICorrelationAnalyticsService, CorrelationAnalyticsService>();
```

### Frontend Implementation (Ripley)

#### 3.6 New React Hooks

**File:** `src/Web/ReactApp/src/features/statistics/hooks/useCorrelationAnalytics.ts`

```typescript
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';

export interface MaterialSuccessRate {
  material: string;
  totalJobs: number;
  completedJobs: number;
  successRate: number;
}

export interface PrinterMaterialPerformance {
  printerId: string;
  printerName: string;
  material: string;
  totalJobs: number;
  completedJobs: number;
  successRate: number;
}

export interface TemperatureQualityCorrelation {
  jobId: string;
  hotendTemp: number;
  bedTemp: number;
  material: string;
  durationMinutes: number;
  success: boolean;
}

export interface DurationTrend {
  date: string;
  averageDurationMinutes: number;
  minDurationMinutes: number;
  maxDurationMinutes: number;
  jobCount: number;
}

export interface FailureReason {
  reason: string;
  count: number;
}

export function useMaterialSuccessRates(days?: number) {
  return useQuery<MaterialSuccessRate[]>({
    queryKey: ['correlation-analytics', 'material-success-rates', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/material-success-rates${params}`);
      return response.data;
    },
  });
}

export function usePrinterMaterialPerformance(days?: number) {
  return useQuery<PrinterMaterialPerformance[]>({
    queryKey: ['correlation-analytics', 'printer-material-performance', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/printer-material-performance${params}`);
      return response.data;
    },
  });
}

export function useTemperatureQualityCorrelation(days?: number) {
  return useQuery<TemperatureQualityCorrelation[]>({
    queryKey: ['correlation-analytics', 'temperature-quality', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/temperature-quality${params}`);
      return response.data;
    },
  });
}

export function useDurationTrends(days?: number) {
  return useQuery<DurationTrend[]>({
    queryKey: ['correlation-analytics', 'duration-trends', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/duration-trends${params}`);
      return response.data;
    },
  });
}

export function useFailureReasons(days?: number) {
  return useQuery<FailureReason[]>({
    queryKey: ['correlation-analytics', 'failure-reasons', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/failure-reasons${params}`);
      return response.data;
    },
  });
}
```

#### 3.7 New Chart Components

**File:** `src/Web/ReactApp/src/features/statistics/components/MaterialSuccessRateChart.tsx`

```tsx
import React from 'react';
import { Card } from '@/common/components/ui/Card';
import { ResponsiveContainer, BarChart, Bar, XAxis, YAxis, Tooltip, CartesianGrid, Legend } from 'recharts';
import { ChartSkeleton } from '@/common/components/skeletons/ChartSkeleton';
import type { MaterialSuccessRate } from '../hooks/useCorrelationAnalytics';

interface Props {
  data: MaterialSuccessRate[];
  isLoading: boolean;
  error: Error | null;
}

export const MaterialSuccessRateChart: React.FC<Props> = ({ data, isLoading, error }) => (
  <Card title="Success Rate by Material" className="h-96">
    {isLoading ? (
      <ChartSkeleton />
    ) : error ? (
      <div className="text-pf-error-text">Error loading data</div>
    ) : data.length === 0 ? (
      <div className="flex h-full items-center justify-center text-pf-text-secondary">No data available</div>
    ) : (
      <ResponsiveContainer width="100%" height="90%">
        <BarChart data={data} margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="material" tick={{ fontSize: 12 }} />
          <YAxis label={{ value: 'Success Rate (%)', angle: -90, position: 'insideLeft' }} />
          <Tooltip />
          <Legend />
          <Bar dataKey="successRate" fill="#34D399" name="Success Rate (%)" />
        </BarChart>
      </ResponsiveContainer>
    )}
  </Card>
);
```

**File:** `src/Web/ReactApp/src/features/statistics/components/PrinterMaterialHeatmap.tsx`

```tsx
// Heatmap showing printer × material success rates
// Uses recharts ScatterChart with color-coded dots (success rate)
```

**File:** `src/Web/ReactApp/src/features/statistics/components/TemperatureScatterPlot.tsx`

```tsx
// Scatter plot: X-axis = hotend temp, Y-axis = bed temp, color = success/failure
// Uses recharts ScatterChart
```

**File:** `src/Web/ReactApp/src/features/statistics/components/DurationTrendChart.tsx`

```tsx
// Line chart showing average print duration over time
// Uses recharts LineChart with error bars (min/max)
```

**File:** `src/Web/ReactApp/src/features/statistics/components/FailureReasonsChart.tsx`

```tsx
// Bar chart or pie chart showing failure reason distribution
// Uses recharts BarChart or PieChart
```

#### 3.8 New Page

**File:** `src/Web/ReactApp/src/features/statistics/pages/CorrelationAnalyticsPage.tsx`

Layout similar to `StatisticsPage` but focused on correlation charts:

- Period selector (7/30/90/all days)
- Export button
- Grid of 5 charts:
  - Material Success Rate Chart
  - Printer × Material Heatmap
  - Temperature Scatter Plot
  - Duration Trend Chart
  - Failure Reasons Chart

Add route to `App.tsx`:

```tsx
<Route path="/analytics/correlations" element={<CorrelationAnalyticsPage />} />
```

---

## Feature 4: Predictive Alerts

### Backend Implementation (Lambert)

#### 4.1 New Service Interface

**File:** `src/infra/Services/Statistics/IPredictiveAnalyticsService.cs`

```csharp
namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Service for predictive analytics and alert generation.
/// </summary>
public interface IPredictiveAnalyticsService
{
    /// <summary>
    /// Calculates job failure likelihood based on historical patterns.
    /// </summary>
    Task<JobFailurePredictionDto> PredictJobFailureLikelihoodAsync(
        PredictionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Forecasts printer maintenance needs based on usage patterns.
    /// </summary>
    Task<List<MaintenanceForecastDto>> ForecastMaintenanceAsync(
        int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns active predictive alerts.
    /// </summary>
    Task<List<PredictiveAlertDto>> GetActiveAlertsAsync(CancellationToken ct = default);
}
```

#### 4.2 Service Implementation

**File:** `src/infra/Services/Statistics/PredictiveAnalyticsService.cs`

```csharp
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Statistics;

public class PredictiveAnalyticsService(AppDbContext db) : IPredictiveAnalyticsService
{
    private readonly AppDbContext _db = db;

    public async Task<JobFailurePredictionDto> PredictJobFailureLikelihoodAsync(
        PredictionRequest request, CancellationToken ct)
    {
        // Simple heuristic-based prediction (no ML yet)
        // Factors:
        // 1. Material historical success rate
        // 2. Printer historical success rate
        // 3. Duration (longer jobs = higher failure risk)
        // 4. Recent printer performance trend

        var materialSuccessRate = await GetMaterialSuccessRate(request.Material, ct);
        var printerSuccessRate = await GetPrinterSuccessRate(request.PrinterId, ct);
        var recentPrinterTrend = await GetRecentPrinterTrend(request.PrinterId, ct);

        // Duration risk factor (longer jobs = higher risk)
        var durationRiskFactor = request.EstimatedDurationMinutes > 300 ? 0.85 : 1.0;

        // Combined success probability
        var predictedSuccessProbability = materialSuccessRate * printerSuccessRate * recentPrinterTrend * durationRiskFactor;
        var failureLikelihood = 1.0 - predictedSuccessProbability;

        // Risk level classification
        var riskLevel = failureLikelihood switch
        {
            < 0.1 => "Low",
            < 0.25 => "Medium",
            < 0.5 => "High",
            _ => "Critical",
        };

        return new JobFailurePredictionDto
        {
            PrinterId = request.PrinterId,
            Material = request.Material,
            EstimatedDurationMinutes = request.EstimatedDurationMinutes,
            PredictedFailureLikelihood = Math.Round(failureLikelihood * 100, 1),
            RiskLevel = riskLevel,
            Factors = new List<PredictionFactorDto>
            {
                new() { Name = "Material Success Rate", Value = Math.Round(materialSuccessRate * 100, 1), Weight = 0.3 },
                new() { Name = "Printer Success Rate", Value = Math.Round(printerSuccessRate * 100, 1), Weight = 0.3 },
                new() { Name = "Recent Performance Trend", Value = Math.Round(recentPrinterTrend * 100, 1), Weight = 0.25 },
                new() { Name = "Duration Risk", Value = Math.Round(durationRiskFactor * 100, 1), Weight = 0.15 },
            },
        };
    }

    public async Task<List<MaintenanceForecastDto>> ForecastMaintenanceAsync(int? days, CancellationToken ct)
    {
        // Query PrinterStatistics for usage data
        // Forecast maintenance based on:
        // 1. Total print hours (nozzle replacement at 500h, hotend at 1000h)
        // 2. Total filament usage (cleaning at 10kg)
        // 3. Job count (belt tension check at 1000 jobs)
        // 4. Recent failure rate (if failures spike, suggest immediate inspection)

        var printers = await _db.Printers.ToListAsync(ct);
        var forecasts = new List<MaintenanceForecastDto>();

        foreach (var printer in printers)
        {
            var stats = await _db.Set<PrinterStatistics>()
                .FirstOrDefaultAsync(s => s.PrinterId == printer.Id, ct);

            if (stats == null) continue;

            var upcomingMaintenance = new List<MaintenanceTaskDto>();

            // Nozzle replacement forecast
            if (stats.TotalPrintHours > 400)
            {
                upcomingMaintenance.Add(new MaintenanceTaskDto
                {
                    TaskName = "Nozzle Replacement",
                    EstimatedDaysUntilDue = CalculateDaysUntilThreshold(stats.TotalPrintHours, 500, stats.TotalPrintHours / 30.0),
                    Priority = stats.TotalPrintHours > 480 ? "High" : "Medium",
                });
            }

            // Hotend replacement forecast
            if (stats.TotalPrintHours > 800)
            {
                upcomingMaintenance.Add(new MaintenanceTaskDto
                {
                    TaskName = "Hotend Replacement",
                    EstimatedDaysUntilDue = CalculateDaysUntilThreshold(stats.TotalPrintHours, 1000, stats.TotalPrintHours / 30.0),
                    Priority = stats.TotalPrintHours > 950 ? "High" : "Medium",
                });
            }

            if (upcomingMaintenance.Any())
            {
                forecasts.Add(new MaintenanceForecastDto
                {
                    PrinterId = printer.Id,
                    PrinterName = printer.Name,
                    UpcomingTasks = upcomingMaintenance,
                });
            }
        }

        return forecasts;
    }

    public async Task<List<PredictiveAlertDto>> GetActiveAlertsAsync(CancellationToken ct)
    {
        // Generate alerts based on current data patterns
        var alerts = new List<PredictiveAlertDto>();

        // Alert 1: High recent failure rate
        var recentFailureRate = await GetRecentFailureRate(7, ct);
        if (recentFailureRate > 0.2)
        {
            alerts.Add(new PredictiveAlertDto
            {
                AlertType = "HighFailureRate",
                Severity = "Warning",
                Message = $"Recent failure rate is {Math.Round(recentFailureRate * 100, 1)}% (last 7 days)",
                RecommendedAction = "Review recent failed jobs for common patterns. Check printer maintenance status.",
            });
        }

        // Alert 2: Printer(s) approaching maintenance threshold
        var maintenanceForecasts = await ForecastMaintenanceAsync(null, ct);
        foreach (var forecast in maintenanceForecasts)
        {
            foreach (var task in forecast.UpcomingTasks.Where(t => t.Priority == "High"))
            {
                alerts.Add(new PredictiveAlertDto
                {
                    AlertType = "MaintenanceDue",
                    Severity = "Warning",
                    Message = $"{forecast.PrinterName}: {task.TaskName} due in ~{task.EstimatedDaysUntilDue} days",
                    RecommendedAction = "Schedule maintenance to prevent quality degradation.",
                });
            }
        }

        return alerts;
    }

    // Helper methods
    private async Task<double> GetMaterialSuccessRate(string material, CancellationToken ct)
    {
        var last90Days = DateTime.UtcNow.AddDays(-90);
        var jobs = await _db.Set<PrintJob>()
            .Where(j => j.RequiredMaterialType == material)
            .Where(j => j.QueuedAt >= last90Days)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .ToListAsync(ct);

        if (jobs.Count == 0) return 0.8; // Default assumption
        var completed = jobs.Count(j => j.Status == PrintJobStatus.Completed);
        return (double)completed / jobs.Count;
    }

    private async Task<double> GetPrinterSuccessRate(Guid printerId, CancellationToken ct)
    {
        var last90Days = DateTime.UtcNow.AddDays(-90);
        var jobs = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= last90Days)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .ToListAsync(ct);

        if (jobs.Count == 0) return 0.85; // Default assumption
        var completed = jobs.Count(j => j.Status == PrintJobStatus.Completed);
        return (double)completed / jobs.Count;
    }

    private async Task<double> GetRecentPrinterTrend(Guid printerId, CancellationToken ct)
    {
        // Compare last 7 days to previous 7 days
        var last7Days = DateTime.UtcNow.AddDays(-7);
        var previous7Days = DateTime.UtcNow.AddDays(-14);

        var recent = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= last7Days)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .CountAsync(j => j.Status == PrintJobStatus.Completed, ct);

        var recentTotal = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= last7Days)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .CountAsync(ct);

        var previous = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= previous7Days && j.QueuedAt < last7Days)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .CountAsync(j => j.Status == PrintJobStatus.Completed, ct);

        var previousTotal = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= previous7Days && j.QueuedAt < last7Days)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .CountAsync(ct);

        if (recentTotal == 0) return 1.0; // No recent data, assume neutral
        var recentRate = (double)recent / recentTotal;
        if (previousTotal == 0) return recentRate; // No previous data, return recent rate

        var previousRate = (double)previous / previousTotal;
        var trend = recentRate / previousRate; // > 1.0 = improving, < 1.0 = degrading

        return Math.Clamp(trend, 0.5, 1.5); // Limit trend impact
    }

    private async Task<double> GetRecentFailureRate(int days, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var jobs = await _db.Set<PrintJob>()
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .ToListAsync(ct);

        if (jobs.Count == 0) return 0;
        var failed = jobs.Count(j => j.Status == PrintJobStatus.Failed);
        return (double)failed / jobs.Count;
    }

    private int CalculateDaysUntilThreshold(double currentValue, double threshold, double usagePerDay)
    {
        if (usagePerDay <= 0) return 999;
        var remaining = threshold - currentValue;
        return (int)Math.Ceiling(remaining / usagePerDay);
    }
}
```

#### 4.3 New DTOs

**File:** `src/infra/Dtos/PredictiveAnalyticsDtos.cs`

```csharp
namespace Farm.Infrastructure.Dtos;

public record PredictionRequest
{
    public Guid PrinterId { get; init; }
    public string Material { get; init; } = string.Empty;
    public double EstimatedDurationMinutes { get; init; }
}

public record JobFailurePredictionDto
{
    public Guid PrinterId { get; init; }
    public string Material { get; init; } = string.Empty;
    public double EstimatedDurationMinutes { get; init; }
    public double PredictedFailureLikelihood { get; init; }
    public string RiskLevel { get; init; } = string.Empty;
    public List<PredictionFactorDto> Factors { get; init; } = new();
}

public record PredictionFactorDto
{
    public string Name { get; init; } = string.Empty;
    public double Value { get; init; }
    public double Weight { get; init; }
}

public record MaintenanceForecastDto
{
    public Guid PrinterId { get; init; }
    public string PrinterName { get; init; } = string.Empty;
    public List<MaintenanceTaskDto> UpcomingTasks { get; init; } = new();
}

public record MaintenanceTaskDto
{
    public string TaskName { get; init; } = string.Empty;
    public int EstimatedDaysUntilDue { get; init; }
    public string Priority { get; init; } = string.Empty;
}

public record PredictiveAlertDto
{
    public string AlertType { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string RecommendedAction { get; init; } = string.Empty;
}
```

#### 4.4 New Controller

**File:** `src/api/Controllers/PredictiveAnalyticsController.cs`

```csharp
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides predictive analytics and alert generation.
/// </summary>
[ApiController]
[Route("api/predictive-analytics")]
[Authorize]
public class PredictiveAnalyticsController(IPredictiveAnalyticsService predictiveService) : ControllerBase
{
    private readonly IPredictiveAnalyticsService _predictiveService = predictiveService;

    /// <summary>
    /// Predicts job failure likelihood based on historical patterns.
    /// </summary>
    [HttpPost("predict-job-failure")]
    [ProducesResponseType(typeof(JobFailurePredictionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> PredictJobFailureAsync(
        [FromBody] PredictionRequest request, CancellationToken ct)
    {
        var prediction = await _predictiveService.PredictJobFailureLikelihoodAsync(request, ct);
        return Ok(prediction);
    }

    /// <summary>
    /// Forecasts printer maintenance needs.
    /// </summary>
    [HttpGet("maintenance-forecast")]
    [ProducesResponseType(typeof(List<MaintenanceForecastDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMaintenanceForecastAsync([FromQuery] int? days, CancellationToken ct)
    {
        var forecast = await _predictiveService.ForecastMaintenanceAsync(days, ct);
        return Ok(forecast);
    }

    /// <summary>
    /// Returns active predictive alerts.
    /// </summary>
    [HttpGet("active-alerts")]
    [ProducesResponseType(typeof(List<PredictiveAlertDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActiveAlertsAsync(CancellationToken ct)
    {
        var alerts = await _predictiveService.GetActiveAlertsAsync(ct);
        return Ok(alerts);
    }
}
```

#### 4.5 Service Registration

**File:** `src/api/Program.cs`

```csharp
builder.Services.AddScoped<IPredictiveAnalyticsService, PredictiveAnalyticsService>();
```

### Frontend Implementation (Ripley)

#### 4.6 New React Hooks

**File:** `src/Web/ReactApp/src/features/statistics/hooks/usePredictiveAnalytics.ts`

```typescript
import { useQuery, useMutation } from '@tanstack/react-query';
import { apiClient } from '@/services/api';

export interface PredictionRequest {
  printerId: string;
  material: string;
  estimatedDurationMinutes: number;
}

export interface JobFailurePrediction {
  printerId: string;
  material: string;
  estimatedDurationMinutes: number;
  predictedFailureLikelihood: number;
  riskLevel: string;
  factors: PredictionFactor[];
}

export interface PredictionFactor {
  name: string;
  value: number;
  weight: number;
}

export interface MaintenanceForecast {
  printerId: string;
  printerName: string;
  upcomingTasks: MaintenanceTask[];
}

export interface MaintenanceTask {
  taskName: string;
  estimatedDaysUntilDue: number;
  priority: string;
}

export interface PredictiveAlert {
  alertType: string;
  severity: string;
  message: string;
  recommendedAction: string;
}

export function usePredictJobFailure() {
  return useMutation<JobFailurePrediction, Error, PredictionRequest>({
    mutationFn: async (request) => {
      const response = await apiClient.post('/predictive-analytics/predict-job-failure', request);
      return response.data;
    },
  });
}

export function useMaintenanceForecast(days?: number) {
  return useQuery<MaintenanceForecast[]>({
    queryKey: ['predictive-analytics', 'maintenance-forecast', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/predictive-analytics/maintenance-forecast${params}`);
      return response.data;
    },
  });
}

export function useActiveAlerts() {
  return useQuery<PredictiveAlert[]>({
    queryKey: ['predictive-analytics', 'active-alerts'],
    queryFn: async () => {
      const response = await apiClient.get('/predictive-analytics/active-alerts');
      return response.data;
    },
    staleTime: 60_000, // Refetch every 60 seconds
  });
}
```

#### 4.7 New Components

**File:** `src/Web/ReactApp/src/features/statistics/components/AlertPanel.tsx`

```tsx
// Card displaying active predictive alerts
// Uses Alert component from UI library
// Grouped by severity (Critical, Warning, Info)
// Click alert for details and recommended actions
```

**File:** `src/Web/ReactApp/src/features/statistics/components/MaintenanceForecastPanel.tsx`

```tsx
// Table showing upcoming maintenance tasks per printer
// Grouped by printer
// Color-coded by priority (High = red, Medium = yellow)
// Sortable by days until due
```

**File:** `src/Web/ReactApp/src/features/statistics/components/JobRiskPredictor.tsx`

```tsx
// Modal/form for predicting job failure risk
// Inputs: printer dropdown, material dropdown, estimated duration
// Output: Risk level badge + factor breakdown chart
```

#### 4.8 Dashboard Integration

Add `<AlertPanel />` to the top of `BusinessAnalyticsDashboard` (above KPI cards).

Add "Predictive Insights" tab to `AnalyticsTabs` component:

```tsx
<Tab>Predictive Insights</Tab>
...
<TabPanel>
  <PredictiveInsightsTab />
</TabPanel>
```

**File:** `src/Web/ReactApp/src/features/statistics/components/PredictiveInsightsTab.tsx`

```tsx
// Layout:
// - Active alerts panel
// - Maintenance forecast panel
// - Job risk predictor tool
```

---

## Architecture Decisions Summary

### Reusability

- **Chart library:** `recharts` (already installed, extensively used). No new dependencies.
- **Export libraries:** Add `QuestPDF` and `CsvHelper` to backend.
- **Existing services:** All 4 features extend `StatisticsService` — no duplication.

### Build Order

All 4 features can be built in parallel:

1. **Export/Reporting** — Independent (new service + endpoints)
2. **Unified Dashboard** — Frontend consolidation (reuses existing endpoints)
3. **Correlation Charts** — Independent (new service + endpoints)
4. **Predictive Alerts** — Independent (new service + endpoints)

**Recommended order for Lambert (backend):**
1. Feature 3 (Correlation) — Builds on existing patterns
2. Feature 1 (Export) — Requires new library integration
3. Feature 4 (Predictive) — Most complex logic

**Recommended order for Ripley (frontend):**
1. Feature 2 (Unified Dashboard) — Consolidates existing components
2. Feature 3 (Correlation) — New charts using existing patterns
3. Feature 1 (Export) — Simple button + blob download
4. Feature 4 (Predictive) — New UI patterns (alerts, forecasts)

### File Structure Summary

**Backend files (Lambert):**

```
src/infra/Services/Statistics/
  ├── IReportExportService.cs (NEW)
  ├── ReportExportService.cs (NEW)
  ├── ICorrelationAnalyticsService.cs (NEW)
  ├── CorrelationAnalyticsService.cs (NEW)
  ├── IPredictiveAnalyticsService.cs (NEW)
  └── PredictiveAnalyticsService.cs (NEW)

src/infra/Dtos/
  ├── ReportDtos.cs (NEW)
  ├── CorrelationAnalyticsDtos.cs (NEW)
  └── PredictiveAnalyticsDtos.cs (NEW)

src/api/Controllers/
  ├── StatisticsController.cs (MODIFY - add export endpoints)
  ├── CorrelationAnalyticsController.cs (NEW)
  └── PredictiveAnalyticsController.cs (NEW)

src/api/Program.cs (MODIFY - add service registrations)

src/infra/Farm.Infrastructure.csproj (MODIFY - add QuestPDF, CsvHelper)
```

**Frontend files (Ripley):**

```
src/Web/ReactApp/src/features/statistics/
  ├── components/
  │   ├── ExportMenu.tsx (NEW)
  │   ├── MaterialSuccessRateChart.tsx (NEW)
  │   ├── PrinterMaterialHeatmap.tsx (NEW)
  │   ├── TemperatureScatterPlot.tsx (NEW)
  │   ├── DurationTrendChart.tsx (NEW)
  │   ├── FailureReasonsChart.tsx (NEW)
  │   ├── AlertPanel.tsx (NEW)
  │   ├── MaintenanceForecastPanel.tsx (NEW)
  │   ├── JobRiskPredictor.tsx (NEW)
  │   ├── AnalyticsTabs.tsx (NEW)
  │   ├── AnalyticsOverviewTab.tsx (NEW)
  │   ├── JobAnalyticsTab.tsx (NEW)
  │   ├── CostAnalyticsTab.tsx (NEW)
  │   ├── PrinterAnalyticsTab.tsx (NEW)
  │   └── PredictiveInsightsTab.tsx (NEW)
  ├── pages/
  │   ├── StatisticsPage.tsx (MODIFY - add export button)
  │   ├── BusinessAnalyticsDashboard.tsx (NEW)
  │   └── CorrelationAnalyticsPage.tsx (NEW)
  └── hooks/
      ├── useStatistics.ts (existing)
      ├── useCorrelationAnalytics.ts (NEW)
      └── usePredictiveAnalytics.ts (NEW)

src/Web/ReactApp/src/services/api.ts (MODIFY - add export methods)

src/Web/ReactApp/src/App.tsx (MODIFY - add routes)
```

---

## Testing Strategy

### Backend Testing (Lambert)

1. **Unit tests for services:**
   - `ReportExportServiceTests.cs`
   - `CorrelationAnalyticsServiceTests.cs`
   - `PredictiveAnalyticsServiceTests.cs`

2. **Integration tests for controllers:**
   - `StatisticsControllerTests.cs` (extend existing)
   - `CorrelationAnalyticsControllerTests.cs`
   - `PredictiveAnalyticsControllerTests.cs`

3. **Test data:**
   - Seed 100+ test print jobs with varied statuses, materials, printers
   - Seed PrintJobStatistics with temperature data
   - Seed PrinterStatistics with usage data

### Frontend Testing (Ripley)

1. **Component tests:**
   - All new chart components
   - Export menu
   - Alert panel
   - Tab components

2. **Hook tests:**
   - `useCorrelationAnalytics`
   - `usePredictiveAnalytics`

3. **Integration tests:**
   - Full page rendering
   - Export flow (mock blob download)
   - Alert display and dismissal

---

## Dependencies Between Features

**No blocking dependencies.** All features are independent and can be developed concurrently.

**Synergy opportunities:**

- Feature 2 (Unified Dashboard) can display alerts from Feature 4
- Feature 3 (Correlation Charts) can be added as tabs to Feature 2
- Feature 1 (Export) can export data from all other features

**Suggested integration flow:**

1. Build all 4 features independently first
2. Then integrate into unified dashboard:
   - Add correlation charts as new tabs
   - Add alert panel to dashboard header
   - Add export menu to all analytics pages

---

## Deployment Notes

### Backend Deployment

1. Add NuGet packages: `QuestPDF`, `CsvHelper`
2. Run `dotnet restore`
3. Register new services in `Program.cs`
4. Deploy API (no database migrations required — all features use existing schema)

### Frontend Deployment

1. No new npm packages required (`recharts` already installed)
2. Run `npm install` (verify dependencies)
3. Build: `npm run build`
4. Deploy React build

### Feature Flags (Optional)

Consider adding feature flags in `appsettings.json` to enable/disable features:

```json
{
  "Features": {
    "ExportReports": true,
    "CorrelationAnalytics": true,
    "PredictiveAlerts": true
  }
}
```

---

## Future Enhancements

### Export/Reporting
- Scheduled automated reports (email)
- Excel export format
- Custom report templates

### Correlation Analytics
- Machine learning model training for more accurate correlations
- Real-time correlation updates
- Custom correlation rules

### Predictive Alerts
- ML-based failure prediction (replace heuristics)
- Email/SMS alert notifications
- Alert history and analytics
- Custom alert thresholds per user

### Unified Dashboard
- Drag-and-drop dashboard layout customization
- User-specific dashboard preferences
- Multi-farm aggregated analytics
- Real-time streaming updates

---

## Estimated Effort

**Backend (Lambert):**
- Feature 1 (Export): 8 hours
- Feature 3 (Correlation): 12 hours
- Feature 4 (Predictive): 16 hours
- **Total: ~36 hours (~4.5 days)**

**Frontend (Ripley):**
- Feature 2 (Unified Dashboard): 16 hours
- Feature 3 (Correlation Charts): 12 hours
- Feature 1 (Export UI): 4 hours
- Feature 4 (Predictive UI): 12 hours
- **Total: ~44 hours (~5.5 days)**

**Testing & Integration:**
- Backend tests: 8 hours
- Frontend tests: 8 hours
- Integration & polish: 8 hours
- **Total: ~24 hours (~3 days)**

**Grand Total: ~104 hours (~13 days with parallelization)**

---

## Sign-Off Checklist

Before implementation:

- [ ] Lambert reviews backend architecture
- [ ] Ripley reviews frontend architecture
- [ ] Jeff approves feature scope and priorities
- [ ] Kane confirms testing strategy
- [ ] Team confirms build order and timelines

---

**End of Architecture Plan**

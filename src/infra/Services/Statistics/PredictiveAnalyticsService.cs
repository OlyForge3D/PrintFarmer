using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Heuristic-based predictive analytics and alert generation.
/// Analyzes historical failure patterns to produce actionable predictions.
/// </summary>
public class PredictiveAnalyticsService(AppDbContext db) : IPredictiveAnalyticsService
{
    private readonly AppDbContext _db = db;

    // Thresholds for maintenance forecasting
    private const double NozzleReplacementHours = 500;
    private const double NozzleWarningHours = 400;
    private const double HotendReplacementHours = 1000;
    private const double HotendWarningHours = 800;
    private const double HighFailureRateThreshold = 0.20;
    private const double DecliningPerformanceThreshold = 0.85;

    public async Task<JobFailurePredictionDto> PredictJobFailureLikelihoodAsync(
        PredictionRequest request, CancellationToken ct = default)
    {
        var materialRate = await GetMaterialSuccessRateAsync(request.Material, ct);
        var printerRate = await GetPrinterSuccessRateAsync(request.PrinterId, ct);
        var recentTrend = await GetRecentPrinterTrendAsync(request.PrinterId, ct);

        // Longer jobs carry higher risk
        double durationRiskFactor = request.EstimatedDurationMinutes > 300 ? 0.85 : 1.0;

        double predictedSuccess = materialRate * printerRate * recentTrend * durationRiskFactor;
        double failureLikelihood = 1.0 - Math.Clamp(predictedSuccess, 0, 1);

        string riskLevel = failureLikelihood switch
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
            Factors =
            [
                new() { Name = "Material Success Rate", Value = Math.Round(materialRate * 100, 1), Weight = 0.3 },
                new() { Name = "Printer Success Rate", Value = Math.Round(printerRate * 100, 1), Weight = 0.3 },
                new() { Name = "Recent Performance Trend", Value = Math.Round(recentTrend * 100, 1), Weight = 0.25 },
                new() { Name = "Duration Risk", Value = Math.Round(durationRiskFactor * 100, 1), Weight = 0.15 },
            ],
        };
    }

    public async Task<List<MaintenanceForecastDto>> ForecastMaintenanceAsync(int? days, CancellationToken ct = default)
    {
        var printers = await _db.Printers.ToListAsync(ct);
        var forecasts = new List<MaintenanceForecastDto>();

        foreach (var printer in printers)
        {
            var stats = await _db.Set<PrinterStatistics>()
                .FirstOrDefaultAsync(s => s.PrinterId == printer.Id, ct);

            if (stats is null)
            {
                continue;
            }

            var tasks = new List<MaintenanceTaskDto>();

            double dailyRate = stats.TotalPrintHours / Math.Max(1, (DateTime.UtcNow - stats.CreatedAt).TotalDays);

            if (stats.TotalPrintHours > NozzleWarningHours)
            {
                tasks.Add(new MaintenanceTaskDto
                {
                    TaskName = "Nozzle Replacement",
                    EstimatedDaysUntilDue = EstimateDaysRemaining(stats.TotalPrintHours, NozzleReplacementHours, dailyRate),
                    Priority = stats.TotalPrintHours > NozzleReplacementHours * 0.96 ? "High" : "Medium",
                });
            }

            if (stats.TotalPrintHours > HotendWarningHours)
            {
                tasks.Add(new MaintenanceTaskDto
                {
                    TaskName = "Hotend Replacement",
                    EstimatedDaysUntilDue = EstimateDaysRemaining(stats.TotalPrintHours, HotendReplacementHours, dailyRate),
                    Priority = stats.TotalPrintHours > HotendReplacementHours * 0.95 ? "High" : "Medium",
                });
            }

            if (tasks.Count > 0)
            {
                forecasts.Add(new MaintenanceForecastDto
                {
                    PrinterId = printer.Id,
                    PrinterName = printer.Name,
                    UpcomingTasks = tasks,
                });
            }
        }

        return forecasts;
    }

    public async Task<List<PredictiveAlertDto>> GetActiveAlertsAsync(CancellationToken ct = default)
    {
        var alerts = new List<PredictiveAlertDto>();

        // Alert: High recent failure rate
        double recentFailureRate = await GetRecentFailureRateAsync(7, ct);
        if (recentFailureRate > HighFailureRateThreshold)
        {
            alerts.Add(new PredictiveAlertDto
            {
                AlertType = "HighFailureRate",
                Severity = "Warning",
                Message = $"Recent failure rate is {Math.Round(recentFailureRate * 100, 1)}% (last 7 days)",
                RecommendedAction = "Review recent failed jobs for common patterns. Check printer maintenance status.",
            });
        }

        // Alert: Printers approaching maintenance thresholds
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

        // Alert: Printers with declining success rate
        var printers = await _db.Printers.Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        foreach (var printer in printers)
        {
            double trend = await GetRecentPrinterTrendAsync(printer.Id, ct);
            if (trend < DecliningPerformanceThreshold)
            {
                alerts.Add(new PredictiveAlertDto
                {
                    AlertType = "DecliningPerformance",
                    Severity = "Info",
                    Message = $"{printer.Name}: performance trending down ({Math.Round(trend * 100, 1)}% of baseline)",
                    RecommendedAction = "Inspect printer for mechanical issues. Review recent material changes.",
                });
            }
        }

        return alerts;
    }

    private async Task<double> GetMaterialSuccessRateAsync(string material, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-90);
        var total = await _db.Set<PrintJob>()
            .Where(j => j.RequiredMaterialType == material)
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .CountAsync(ct);

        if (total == 0)
        {
            return 0.8; // No history — assume reasonable baseline
        }

        int completed = await _db.Set<PrintJob>()
            .Where(j => j.RequiredMaterialType == material)
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Completed)
            .CountAsync(ct);

        return (double)completed / total;
    }

    private async Task<double> GetPrinterSuccessRateAsync(Guid printerId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-90);
        int total = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .CountAsync(ct);

        if (total == 0)
        {
            return 0.85;
        }

        int completed = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Completed)
            .CountAsync(ct);

        return (double)completed / total;
    }

    private async Task<double> GetRecentPrinterTrendAsync(Guid printerId, CancellationToken ct)
    {
        var last7Days = DateTime.UtcNow.AddDays(-7);
        var previous7Days = DateTime.UtcNow.AddDays(-14);

        int recentTotal = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= last7Days)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .CountAsync(ct);

        if (recentTotal == 0)
        {
            return 1.0; // No recent data — assume neutral
        }

        int recentCompleted = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= last7Days)
            .Where(j => j.Status == PrintJobStatus.Completed)
            .CountAsync(ct);

        int previousTotal = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= previous7Days && j.QueuedAt < last7Days)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .CountAsync(ct);

        double recentRate = (double)recentCompleted / recentTotal;

        if (previousTotal == 0)
        {
            return recentRate;
        }

        int previousCompleted = await _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId == printerId)
            .Where(j => j.QueuedAt >= previous7Days && j.QueuedAt < last7Days)
            .Where(j => j.Status == PrintJobStatus.Completed)
            .CountAsync(ct);

        double previousRate = (double)previousCompleted / previousTotal;

        if (previousRate == 0)
        {
            return recentRate > 0 ? 1.5 : 1.0;
        }

        return Math.Clamp(recentRate / previousRate, 0.5, 1.5);
    }

    private async Task<double> GetRecentFailureRateAsync(int days, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        int total = await _db.Set<PrintJob>()
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed)
            .CountAsync(ct);

        if (total == 0)
        {
            return 0;
        }

        int failed = await _db.Set<PrintJob>()
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Failed)
            .CountAsync(ct);

        return (double)failed / total;
    }

    private static int EstimateDaysRemaining(double currentValue, double threshold, double dailyRate)
    {
        if (dailyRate <= 0)
        {
            return 999;
        }

        double remaining = threshold - currentValue;
        return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining / dailyRate);
    }
}

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// High-level KPI summary values for dashboard display.
/// </summary>
public record StatisticsSummaryDto
{
    public int TotalJobs { get; init; }

    public int CompletedJobs { get; init; }

    public int FailedJobs { get; init; }

    public int CancelledJobs { get; init; }

    public double SuccessRate { get; init; }

    public decimal TotalCost { get; init; }

    public double TotalFilamentGrams { get; init; }

    public double TotalPrintHours { get; init; }
}

/// <summary>
/// Daily job counts grouped by status for chart display.
/// </summary>
public record DailyJobCountDto
{
    public string Date { get; init; } = string.Empty;

    public int Completed { get; init; }

    public int Failed { get; init; }

    public int Cancelled { get; init; }
}

/// <summary>
/// Daily cost totals for cost-over-time chart.
/// </summary>
public record DailyCostDto
{
    public string Date { get; init; } = string.Empty;

    public decimal Cost { get; init; }
}

/// <summary>
/// Filament consumption grouped by material type.
/// </summary>
public record FilamentByMaterialDto
{
    public string Material { get; init; } = string.Empty;

    public double Grams { get; init; }
}

/// <summary>
/// Per-printer utilization statistics.
/// </summary>
public record PrinterUtilizationDto
{
    public Guid PrinterId { get; init; }

    public string PrinterName { get; init; } = string.Empty;

    public int TotalJobs { get; init; }

    public int CompletedJobs { get; init; }

    public int FailedJobs { get; init; }

    public double TotalPrintHours { get; init; }

    public double SuccessRate { get; init; }
}

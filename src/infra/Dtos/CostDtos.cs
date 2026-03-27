namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Cost breakdown for a specific print job.
/// </summary>
public class JobCostBreakdownDto
{
    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public decimal? MaterialCostUsd { get; set; }

    public decimal? EnergyCostUsd { get; set; }

    public decimal? MachineTimeCostUsd { get; set; }

    public decimal? LaborCostUsd { get; set; }

    public decimal? TotalCostUsd { get; set; }

    public DateTime? CostCalculatedAt { get; set; }

    public TimeSpan? PrintDuration { get; set; }

    public double? FilamentUsageGrams { get; set; }

    public string? FilamentName { get; set; }

    public string? PrinterName { get; set; }
}

/// <summary>
/// Aggregate cost statistics for a time period.
/// </summary>
public class CostStatisticsSummaryDto
{
    public decimal TotalCostUsd { get; set; }

    public decimal AverageCostPerJobUsd { get; set; }

    public int JobsWithCostData { get; set; }

    public decimal TotalMaterialCostUsd { get; set; }

    public decimal TotalEnergyCostUsd { get; set; }

    public decimal TotalMachineTimeCostUsd { get; set; }

    public decimal TotalLaborCostUsd { get; set; }

    public string? MostExpensiveMaterial { get; set; }

    public decimal MostExpensiveMaterialCost { get; set; }
}

/// <summary>
/// Cost data grouped by time period.
/// </summary>
public class CostByTimePeriodDto
{
    public DateTime Date { get; set; }

    public decimal TotalCostUsd { get; set; }

    public decimal MaterialCostUsd { get; set; }

    public decimal EnergyCostUsd { get; set; }

    public decimal MachineTimeCostUsd { get; set; }

    public decimal LaborCostUsd { get; set; }

    public int JobCount { get; set; }
}

/// <summary>
/// Cost data grouped by printer.
/// </summary>
public class CostByPrinterDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public decimal TotalCostUsd { get; set; }

    public decimal AverageCostPerJobUsd { get; set; }

    public int JobCount { get; set; }

    public decimal MaterialCostUsd { get; set; }

    public decimal EnergyCostUsd { get; set; }

    public decimal MachineTimeCostUsd { get; set; }

    public decimal LaborCostUsd { get; set; }
}

/// <summary>
/// Cost data grouped by material type.
/// </summary>
public class CostByMaterialDto
{
    public string MaterialType { get; set; } = string.Empty;

    public decimal TotalCostUsd { get; set; }

    public decimal AverageCostPerJobUsd { get; set; }

    public int JobCount { get; set; }

    public double TotalFilamentUsageGrams { get; set; }
}

/// <summary>
/// Per-job cost breakdown for the "Costs by Job" analytics tab.
/// </summary>
public class CostByJobDto
{
    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public string? PrinterName { get; set; }

    public string? FilamentName { get; set; }

    public string? MaterialType { get; set; }

    public double? FilamentUsedGrams { get; set; }

    public decimal TotalCostUsd { get; set; }

    public decimal MaterialCostUsd { get; set; }

    public decimal EnergyCostUsd { get; set; }

    public decimal MachineTimeCostUsd { get; set; }

    public decimal LaborCostUsd { get; set; }

    /// <summary>Actual print duration in seconds.</summary>
    public double? PrintTimeSeconds { get; set; }

    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Request to update job cost with manual overrides.
/// </summary>
public class UpdateJobCostRequest
{
    public decimal? MaterialCostUsd { get; set; }

    public decimal? EnergyCostUsd { get; set; }

    public decimal? MachineTimeCostUsd { get; set; }

    public decimal? LaborCostUsd { get; set; }
}

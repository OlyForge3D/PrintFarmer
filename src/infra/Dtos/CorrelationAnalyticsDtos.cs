namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Success rate breakdown for a specific material type.
/// </summary>
public record MaterialSuccessRateDto
{
    public string Material { get; init; } = string.Empty;

    public int TotalJobs { get; init; }

    public int CompletedJobs { get; init; }

    public double SuccessRate { get; init; }
}

/// <summary>
/// Success rate for a specific printer × material combination.
/// </summary>
public record PrinterMaterialPerformanceDto
{
    public Guid PrinterId { get; init; }

    public string PrinterName { get; init; } = string.Empty;

    public string Material { get; init; } = string.Empty;

    public int TotalJobs { get; init; }

    public int CompletedJobs { get; init; }

    public double SuccessRate { get; init; }
}

/// <summary>
/// Temperature data point for quality correlation analysis.
/// </summary>
public record TemperatureQualityCorrelationDto
{
    public Guid JobId { get; init; }

    public int NozzleTemp { get; init; }

    public int BedTemp { get; init; }

    public string Material { get; init; } = string.Empty;

    public double DurationMinutes { get; init; }

    public bool Success { get; init; }
}

/// <summary>
/// Daily print duration trend aggregation.
/// </summary>
public record DurationTrendDto
{
    public string Date { get; init; } = string.Empty;

    public double AverageDurationMinutes { get; init; }

    public double MinDurationMinutes { get; init; }

    public double MaxDurationMinutes { get; init; }

    public int JobCount { get; init; }
}

/// <summary>
/// Aggregated failure reason with occurrence count.
/// </summary>
public record FailureReasonDto
{
    public string Reason { get; init; } = string.Empty;

    public int Count { get; init; }
}

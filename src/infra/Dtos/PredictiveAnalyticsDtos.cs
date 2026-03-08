namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Request parameters for job failure prediction.
/// </summary>
public record PredictionRequest
{
    public Guid PrinterId { get; init; }

    public string Material { get; init; } = string.Empty;

    public double EstimatedDurationMinutes { get; init; }
}

/// <summary>
/// Job failure prediction result with risk assessment and contributing factors.
/// </summary>
public record JobFailurePredictionDto
{
    public Guid PrinterId { get; init; }

    public string Material { get; init; } = string.Empty;

    public double EstimatedDurationMinutes { get; init; }

    public double PredictedFailureLikelihood { get; init; }

    public string RiskLevel { get; init; } = string.Empty;

    public List<PredictionFactorDto> Factors { get; init; } = [];
}

/// <summary>
/// Individual factor contributing to a failure prediction.
/// </summary>
public record PredictionFactorDto
{
    public string Name { get; init; } = string.Empty;

    public double Value { get; init; }

    public double Weight { get; init; }
}

/// <summary>
/// Maintenance forecast for a specific printer.
/// </summary>
public record MaintenanceForecastDto
{
    public Guid PrinterId { get; init; }

    public string PrinterName { get; init; } = string.Empty;

    public List<MaintenanceTaskDto> UpcomingTasks { get; init; } = [];
}

/// <summary>
/// Individual maintenance task prediction.
/// </summary>
public record MaintenanceTaskDto
{
    public string TaskName { get; init; } = string.Empty;

    public int EstimatedDaysUntilDue { get; init; }

    public string Priority { get; init; } = string.Empty;
}

/// <summary>
/// Active predictive alert generated from historical pattern analysis.
/// </summary>
public record PredictiveAlertDto
{
    public string AlertType { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string RecommendedAction { get; init; } = string.Empty;
}

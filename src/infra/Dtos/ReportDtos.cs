namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Request parameters for report generation.
/// </summary>
public record ReportRequest
{
    /// <summary>
    /// Optional number of days to filter. If null, returns all-time data.
    /// </summary>
    public int? Days { get; init; }
}

/// <summary>
/// CSV row representing a single print job in the history export.
/// </summary>
public record JobHistoryCsvRow
{
    public string JobName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateTime QueuedAt { get; init; }

    public DateTime? StartedAt { get; init; }

    public DateTime? CompletedAt { get; init; }

    public double? PrintTimeMinutes { get; init; }

    public double? FilamentGrams { get; init; }

    public decimal? Cost { get; init; }

    public string? PrinterName { get; init; }
}

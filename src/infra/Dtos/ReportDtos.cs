using CsvHelper;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Request parameters for report generation.
/// </summary>
public record ReportRequest
{
    /// <summary>
    /// Optional number of days to filter. If null, defaults to 365 days for exports (capped at 10,000 records).
    /// </summary>
    public int? Days { get; init; }
}

/// <summary>
/// CSV row representing a single print job in the history export.
/// </summary>
[CsvRecord]
public partial record JobHistoryCsvRow
{
    public string JobName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    [CsvFormat("G")]
    public DateTime QueuedAt { get; init; }

    [CsvFormat("G")]
    public DateTime? StartedAt { get; init; }

    [CsvFormat("G")]
    public DateTime? CompletedAt { get; init; }

    public double? PrintTimeMinutes { get; init; }

    public double? FilamentGrams { get; init; }

    public decimal? Cost { get; init; }

    public string? PrinterName { get; init; }
}

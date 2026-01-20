using System.Diagnostics.CodeAnalysis;

namespace Farm.Infrastructure;

// History Models (matching Moonraker structure)

/// <summary>
/// Paginated (or filtered) list response of historical jobs from Moonraker.
/// </summary>
public class HistoryListResponse
{
    public int Count { get; set; }

    public HistoryJob[] Jobs { get; set; } = [];
}

/// <summary>
/// Historical job entry mirroring Moonraker history schema.
/// </summary>
public class HistoryJob
{
    public string JobId { get; set; } = string.Empty;

    public bool Exists { get; set; }

    public double? EndTime { get; set; }

    public double FilamentUsed { get; set; }

    public string Filename { get; set; } = string.Empty;

    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO used for JSON serialization; setter required for deserialization")]
    public Dictionary<string, object> Metadata { get; set; } = [];

    public double PrintDuration { get; set; }

    public string Status { get; set; } = string.Empty;

    public double StartTime { get; set; }

    public double TotalDuration { get; set; }

    public string User { get; set; } = string.Empty;

    public AuxiliaryData[]? AuxiliaryData { get; set; }

    public string? ThumbnailUrl { get; set; }
}

/// <summary>
/// Additional provider-specific metadata associated with a history job.
/// </summary>
public class AuxiliaryData
{
    public string Provider { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public object Value { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    public string? Units { get; set; }
}

/// <summary>
/// Aggregate totals across historical jobs, including auxiliary data sums.
/// </summary>
public class HistoryTotals
{
    public JobTotals JobTotals { get; set; } = new();

    public AuxiliaryTotals[]? AuxiliaryTotals { get; set; }
}

/// <summary>
/// Aggregated job statistics (counts, durations, filament usage).
/// </summary>
public class JobTotals
{
    public int TotalJobs { get; set; }

    public double TotalTime { get; set; }

    public double TotalPrintTime { get; set; }

    public double TotalFilamentUsed { get; set; }

    public double LongestJob { get; set; }

    public double LongestPrint { get; set; }
}

/// <summary>
/// Aggregated auxiliary metric totals.
/// </summary>
public class AuxiliaryTotals
{
    public string Provider { get; set; } = string.Empty;

    public string Field { get; set; } = string.Empty;

    public double Maximum { get; set; }

    public double Total { get; set; }
}

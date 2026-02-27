using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Records job completion statistics for predictive modeling (Phase 4.2)
/// One-to-one relationship with PrintJob (optional, only filled after job completes)
/// </summary>
public class PrintJobStatistics
{
    public Guid Id { get; set; }

    public Guid PrintJobId { get; set; }

    public PrintJob PrintJob { get; set; } = null!;

    // Duration tracking
    public long? ActualDurationMs { get; set; }        // Actual time taken in milliseconds

    public long? EstimatedDurationMs { get; set; }     // Time from gcode estimate in milliseconds

    // Job characteristics
    public Guid? PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    public string? Material { get; set; }              // PLA, ABS, PETG, TPU, etc.

    public int? NozzleTemperature { get; set; }        // Celsius

    public int? BedTemperature { get; set; }           // Celsius

    public int SpeedPercentage { get; set; } = 100;    // % of normal speed

    // Outcome
    public bool IsSuccess { get; set; }

    public string? FailureReason { get; set; }         // Why it failed if IsSuccess=false

    // Cost tracking
    public decimal? EstimatedCost { get; set; }        // Cost estimated at queue time

    public decimal? ActualCost { get; set; }           // Cost calculated at completion

    public DateTime? CompletedAtUtc { get; set; }

    // Audit
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Phase 4.1: Job Execution Tracking
/// Tracks execution history for scheduled jobs (especially recurring ones)
/// </summary>
public class JobExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to JobSchedule
    /// </summary>
    public Guid JobScheduleId { get; set; }

    public JobSchedule JobSchedule { get; set; } = null!;

    /// <summary>
    /// When this execution was scheduled to run
    /// </summary>
    public DateTime ScheduledExecutionTime { get; set; }

    /// <summary>
    /// When this execution actually started (null if not started yet)
    /// </summary>
    public DateTime? ActualStartTime { get; set; }

    /// <summary>
    /// Execution status: Pending, Running, Completed, Failed, Cancelled
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// Result message or error details
    /// </summary>
    public string? Message { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

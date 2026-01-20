using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Phase 4.1: Job Scheduling
/// Represents scheduling configuration for a print job.
/// Separate table to keep PrintJob clean (only for scheduled jobs, not on-demand).
/// One-to-one relationship with PrintJob.
/// </summary>
public class JobSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to PrintJob
    /// </summary>
    public Guid PrintJobId { get; set; }

    public PrintJob PrintJob { get; set; } = null!;

    /// <summary>
    /// Scheduled start time in UTC
    /// </summary>
    public DateTime ScheduledStartTime { get; set; }

    /// <summary>
    /// Timezone for display/input (e.g., "America/New_York", "UTC")
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// Recurrence pattern if job should repeat (null = one-time)
    /// Values: "Daily", "Weekly", "Monthly", null
    /// </summary>
    public string? RecurrencePattern { get; set; }

    /// <summary>
    /// When recurrence should end (null = indefinite for recurring jobs)
    /// </summary>
    public DateTime? RecurrenceEndDate { get; set; }

    /// <summary>
    /// Is this scheduled job currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Is this scheduled job paused (can be resumed)
    /// </summary>
    public bool IsPaused { get; set; } = false;

    /// <summary>
    /// When the job was originally scheduled
    /// </summary>
    public DateTime ScheduledAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to execution history (for recurring jobs)
    /// </summary>
    public ICollection<JobExecution> Executions { get; set; } = new List<JobExecution>();
}
